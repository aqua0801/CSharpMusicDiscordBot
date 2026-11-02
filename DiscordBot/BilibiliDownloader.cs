using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DiscordBot;

public sealed partial class BilibiliDownloader
{
    private const string DefaultReferer = "https://www.bilibili.com";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";
    private const int RetryLimit = 2;

    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private readonly SemaphoreSlim _cookieInit = new(1, 1);
    private volatile bool _cookiesReady;

    private readonly SemaphoreSlim _wbiLock = new(1, 1);
    private string? _wbiMixinKey;
    private DateTime _wbiKeyTime;

    public BilibiliDownloader()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = _cookies,
            UseCookies = true,
        });

        var h = _http.DefaultRequestHeaders;
        h.UserAgent.ParseAdd(UserAgent);
        h.Referrer = new Uri("https://www.bilibili.com/");
        h.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        h.TryAddWithoutValidation("Accept-Language", "zh-TW,zh;q=0.9,en;q=0.8");
        h.TryAddWithoutValidation("Origin", "https://www.bilibili.com");
        h.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        h.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        h.TryAddWithoutValidation("sec-fetch-dest", "empty");
        h.TryAddWithoutValidation("sec-fetch-mode", "cors");
        h.TryAddWithoutValidation("sec-fetch-site", "same-site");
    }

    // ── Public API ─────────────────────────────────────────────

    public async Task<string?> DownloadAsync(string url, ExtensionOption extension)
    {
        try
        {
            var play = await FetchPlayInfoAsync(url);
            string baseName = SanitizeFileName(play.Title);

            if (extension == ExtensionOption.Audio)
            {
                string audioUrl = await ResolveFirstValidUrlAsync(play.Audio, ExtensionOption.Audio)
                    ?? throw new InvalidOperationException("Failed to find available audio source");

                string path = Path.GetFullPath(Path.Combine(GlobalVariable.DownloadFolderPath, $"{baseName}.m4a"));
                await DownloadToFileAsync(audioUrl, path);
                return path;
            }

            string videoUrl = await ResolveFirstValidUrlAsync(play.Video, ExtensionOption.Video)
                ?? throw new InvalidOperationException("Failed to find available video source");
            string audio = await ResolveFirstValidUrlAsync(play.Audio, ExtensionOption.Audio)
                ?? throw new InvalidOperationException("Failed to find available audio source");

            string outputPath = Path.GetFullPath(Path.Combine(GlobalVariable.DownloadFolderPath, $"{baseName}.mp4"));
            await MediaValidator.DownloadAndMergeMediaAsync(videoUrl, audio, outputPath, GetFfmpegHeaders());
            return outputPath;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to download bili url: {url}", ex);
            return null;
        }
    }

    public async Task<AudioInfo?> GetBilibiliStreamUrlAsync(string url)
    {
        for (int attempt = 1; attempt <= RetryLimit; attempt++)
        {
            try
            {
                var play = await FetchPlayInfoAsync(url);
                string? audioUrl = await ResolveFirstValidUrlAsync(play.Audio, ExtensionOption.Audio);
                if (audioUrl is null)
                {
                    Log.Warn($"Bilibili the {attempt}-th attempt: No available source {url}");
                    continue;
                }

                string headers = GetFfmpegHeaders();
                var duration = await MediaProcess.GetAudioDurationAsync(audioUrl, headers);

                return new AudioInfo
                {
                    Title = play.Title,
                    Creator = play.Author,
                    Url = audioUrl,
                    Duration = duration?.TotalSeconds ?? 0,
                    FfmpegHeaders = headers,
                };
            }
            catch (Exception ex)
            {
                Log.Warn($"Bilibili failed to parse source, the {attempt}-th attempt {url}", ex);
            }
        }

        return null;
    }


    private async Task DownloadToFileAsync(string mediaUrl, string filePath)
    {
        using var response = await _http.GetAsync(mediaUrl, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(fs);
    }

    private static async Task<string?> ResolveFirstValidUrlAsync(IReadOnlyList<DashStream> streams, ExtensionOption ext)
    {
        foreach (var candidate in streams.SelectMany(s => s.CandidateUrls()).Distinct())
        {
            if (await MediaValidator.IsValidMediaUrlAsync(candidate, ext, UserAgent, DefaultReferer))
                return candidate;
        }
        return null;
    }

    private string GetFfmpegHeaders() => MediaValidator.ToFfmpegHeaders(_http.DefaultRequestHeaders);

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = string.Concat(name.Select(c => invalid.Contains(c) ? ' ' : c)).Trim();
        if (cleaned.Length > 80) cleaned = cleaned[..80];
        return string.IsNullOrWhiteSpace(cleaned) ? Utils.GenerateHashCode(8) : cleaned;
    }


    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlInTextRegex();

    [GeneratedRegex(@"[?&]p=(\d+)")]
    private static partial Regex PageRegex();

    [GeneratedRegex(@"BV[0-9A-Za-z]{10}")]
    private static partial Regex BvRegex();

    [GeneratedRegex(@"(?:^|[/=])av(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex AvRegex();

    private static readonly string[] _shortHosts = ["b23.tv", "bili2233.cn", "bili22.cn", "bili33.cn", "bili23.cn"];

    private async Task<string> ExpandUrlAsync(string input)
    {
        var m = UrlInTextRegex().Match(input);
        string url = m.Success ? m.Value : input.Trim();
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            url = "https://" + url;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ArgumentException($"Invalid url: {input}");

        bool isShort = _shortHosts.Any(h => uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                                         || uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));
        if (!isShort) return url;

        using var req = new HttpRequestMessage(HttpMethod.Get, uri);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        var final = resp.RequestMessage?.RequestUri?.ToString() ?? url;

        Log.Info($"Short link expanded: {url} -> {final}");
        return final;
    }

    private readonly record struct VideoId(string Key, string Value, int Page)
    {
        public string Query => $"{Key}={Value}";
        public string RefererPath => Key == "bvid" ? Value : "av" + Value;
    }

    private static VideoId ParseVideoId(string url)
    {
        var pMatch = PageRegex().Match(url);
        int page = pMatch.Success && int.TryParse(pMatch.Groups[1].Value, out int p) ? p : 1;

        var bv = BvRegex().Match(url);
        if (bv.Success) return new VideoId("bvid", bv.Value, page);

        var av = AvRegex().Match(url);
        if (av.Success) return new VideoId("aid", av.Groups[1].Value, page);

        throw new ArgumentException($"Failed to get BV/av: {url}");
    }


    private async Task EnsureCookiesAsync()
    {
        if (_cookiesReady) return;

        await _cookieInit.WaitAsync();
        try
        {
            if (_cookiesReady) return;

            using (var resp = await _http.GetAsync("https://www.bilibili.com/"))
            {
                if (!resp.IsSuccessStatusCode)
                    Log.Warn($"Bili main page responded with {(int)resp.StatusCode}, considering insufficent cookie");
            }

            var site = new Uri("https://www.bilibili.com");
            var spi = await GetApiAsync<SpiData>("https://api.bilibili.com/x/frontend/finger/spi");

            if (_cookies.GetCookies(site)["buvid3"] is null && !string.IsNullOrEmpty(spi.Data?.B3))
                _cookies.Add(new Cookie("buvid3", spi.Data.B3, "/", ".bilibili.com"));
            if (!string.IsNullOrEmpty(spi.Data?.B4))
                _cookies.Add(new Cookie("buvid4", spi.Data.B4, "/", ".bilibili.com"));

            try
            {
                await AddBiliTicketAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to get bili_ticket", ex);
            }

            _cookiesReady = true;
        }
        finally
        {
            _cookieInit.Release();
        }
    }

    private async Task AddBiliTicketAsync()
    {
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sign = HMACSHA256.HashData("XgwSnGZ1p"u8, Encoding.UTF8.GetBytes($"ts{ts}"));
        var url = "https://api.bilibili.com/bapis/bilibili.api.ticket.v1.Ticket/GenWebTicket" +
                  $"?key_id=ec02&hexsign={Convert.ToHexStringLower(sign)}" +
                  $"&context[ts]={ts}&csrf=";

        using var resp = await _http.PostAsync(url, null);
        string body = await resp.Content.ReadAsStringAsync();
        resp.EnsureSuccessStatusCode();

        var json = Json.Deserialize<BiliResponse<TicketData>>(body, "bili_ticket");
        if (!string.IsNullOrEmpty(json.Data?.Ticket))
            _cookies.Add(new Cookie("bili_ticket", json.Data.Ticket, "/", ".bilibili.com"));
    }

    private static readonly int[] MixinKeyEncTab =
    [
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
        20, 34, 44, 52,
    ];

    private async Task<string> GetWbiMixinKeyAsync()
    {
        await _wbiLock.WaitAsync();
        try
        {
            if (_wbiMixinKey is not null && DateTime.UtcNow - _wbiKeyTime < TimeSpan.FromHours(6))
                return _wbiMixinKey;

            var nav = await GetApiAsync<NavData>("https://api.bilibili.com/x/web-interface/nav", checkCode: false);
            var img = nav.Data?.WbiImg
                ?? throw new InvalidDataException($"nav API not responding wbi_img (code={nav.Code} {nav.Message})");

            string raw = Path.GetFileNameWithoutExtension(img.ImgUrl) + Path.GetFileNameWithoutExtension(img.SubUrl);
            if (raw.Length < 64)
                throw new InvalidDataException($"Incorrect length of wbi_img key:{raw}");

            _wbiMixinKey = new string(MixinKeyEncTab.Select(i => raw[i]).ToArray())[..32];
            _wbiKeyTime = DateTime.UtcNow;
            return _wbiMixinKey;
        }
        finally
        {
            _wbiLock.Release();
        }
    }

    private async Task<string> SignWbiAsync(Dictionary<string, string> param)
    {
        var key = await GetWbiMixinKeyAsync();
        param["wts"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var query = string.Join("&", param
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p =>
            {
                var v = new string(p.Value.Where(c => !"!'()*".Contains(c)).ToArray());
                return $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(v)}";
            }));

        var hash = MD5.HashData(Encoding.UTF8.GetBytes(query + key));
        return $"{query}&w_rid={Convert.ToHexStringLower(hash)}";
    }


    private async Task<BiliResponse<T>> GetApiAsync<T>(string url, string? referer = null, bool checkCode = true)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer is not null) req.Headers.Referrer = new Uri(referer);

        using var resp = await _http.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} @ {url}{Environment.NewLine}{Json.Truncate(body, 300)}", null, resp.StatusCode);

        var result = Json.Deserialize<BiliResponse<T>>(body, url);
        if (checkCode && result.Code != 0)
            throw new InvalidOperationException($"Bili API error code={result.Code} message={result.Message} @ {url}");

        return result;
    }

    private sealed record PlayInfo(string Title, string Author, IReadOnlyList<DashStream> Video, IReadOnlyList<DashStream> Audio);

    private async Task<PlayInfo> FetchPlayInfoAsync(string url)
    {
        await EnsureCookiesAsync();

        url = await ExpandUrlAsync(url);
        var id = ParseVideoId(url);

        var view = await GetApiAsync<ViewData>(
            $"https://api.bilibili.com/x/web-interface/view?{id.Query}",
            referer: $"https://www.bilibili.com/video/{id.RefererPath}/");

        var data = view.Data ?? throw new InvalidDataException("view API contains no data");
        if (data.Pages.Count == 0)
            throw new InvalidDataException($"video do not contain pages {data.Bvid}");

        long cid = data.Pages[Math.Clamp(id.Page - 1, 0, data.Pages.Count - 1)].Cid;

        var query = await SignWbiAsync(new Dictionary<string, string>
        {
            ["bvid"] = data.Bvid,   
            ["cid"] = cid.ToString(),
            ["fnval"] = "16",
            ["fnver"] = "0",
            ["fourk"] = "1",
        });

        var play = await GetApiAsync<PlayUrlData>(
            $"https://api.bilibili.com/x/player/wbi/playurl?{query}",
            referer: $"https://www.bilibili.com/video/{data.Bvid}/");

        var dash = play.Data?.Dash
            ?? throw new InvalidDataException($"playurl not responding DASH stream (might be region-restricted) bvid={data.Bvid}");

        return new PlayInfo(
            string.IsNullOrWhiteSpace(data.Title) ? "未知" : data.Title,
            data.Owner?.Name ?? "未知",
            dash.Video ?? new List<DashStream>(),
            dash.Audio ?? new List<DashStream>());
    }

    // ── DTO ─────────────────────────────────────────────────

    private sealed record BiliResponse<T>
    {
        [JsonPropertyName("code")] public int Code { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("data")] public T? Data { get; init; }
    }

    private sealed record SpiData
    {
        [JsonPropertyName("b_3")] public string? B3 { get; init; }
        [JsonPropertyName("b_4")] public string? B4 { get; init; }
    }

    private sealed record TicketData
    {
        [JsonPropertyName("ticket")] public string? Ticket { get; init; }
    }

    private sealed record NavData
    {
        [JsonPropertyName("wbi_img")] public WbiImg? WbiImg { get; init; }
    }

    private sealed record WbiImg
    {
        [JsonPropertyName("img_url")] public string ImgUrl { get; init; } = "";
        [JsonPropertyName("sub_url")] public string SubUrl { get; init; } = "";
    }

    private sealed record ViewData
    {
        [JsonPropertyName("bvid")] public string Bvid { get; init; } = "";
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("owner")] public ViewOwner? Owner { get; init; }
        [JsonPropertyName("pages")] public List<ViewPage> Pages { get; init; } = [];
    }

    private sealed record ViewOwner
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    private sealed record ViewPage
    {
        [JsonPropertyName("cid")] public long Cid { get; init; }
    }

    private sealed record PlayUrlData
    {
        [JsonPropertyName("dash")] public DashInfo? Dash { get; init; }
    }

    private sealed record DashInfo
    {
        [JsonPropertyName("video")] public List<DashStream>? Video { get; init; }
        [JsonPropertyName("audio")] public List<DashStream>? Audio { get; init; }
    }

    private sealed record DashStream
    {
        [JsonPropertyName("base_url")] public string? BaseUrlSnake { get; init; }
        [JsonPropertyName("baseUrl")] public string? BaseUrl { get; init; }
        [JsonPropertyName("backup_url")] public List<string>? BackupUrlSnake { get; init; }
        [JsonPropertyName("backupUrl")] public List<string>? BackupUrl { get; init; }

        public IEnumerable<string> CandidateUrls()
        {
            if (!string.IsNullOrEmpty(BaseUrlSnake)) yield return BaseUrlSnake;
            if (!string.IsNullOrEmpty(BaseUrl)) yield return BaseUrl;
            if (BackupUrlSnake is not null)
                foreach (var u in BackupUrlSnake) if (!string.IsNullOrEmpty(u)) yield return u;
            if (BackupUrl is not null)
                foreach (var u in BackupUrl) if (!string.IsNullOrEmpty(u)) yield return u;
        }
    }
}

public static class MediaValidator
{
    private static readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false });

    private static readonly HashSet<string> _supportedVideoCodecs =
    [
        "h264", "hevc", "av1", "vp8", "vp9", "mpeg2", "mpeg4", "theora",
        "prores", "dnxhd", "jpeg", "h263", "wmv3", "flv1", "mvc",
    ];

    private static readonly HashSet<string> _supportedAudioCodecs =
    [
        "aac", "mp3", "opus", "ac3", "flac", "vorbis", "pcm", "alac",
        "wma", "speex", "amr-nb", "amr-wb",
    ];

    public static async Task<bool> IsValidMediaUrlAsync(string url, ExtensionOption extension, string? ua = null, string referer = "https://www.bilibili.com")
    {
        try
        {
            if (await TryValidateAsync(HttpMethod.Head, url, extension, ua, referer, rangeHeader: null))
                return true;

            return await TryValidateAsync(HttpMethod.Get, url, extension, ua, referer, rangeHeader: new RangeHeaderValue(0, 1));
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to validate media source ({ex.GetType().Name}: {ex.Message}) {Json.Truncate(url, 120)}");
            return false;
        }
    }

    private static async Task<bool> TryValidateAsync(
        HttpMethod method, string url, ExtensionOption ext,
        string? ua, string referer, RangeHeaderValue? rangeHeader)
    {
        using var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(ua)) request.Headers.UserAgent.ParseAdd(ua);
        request.Headers.Referrer = new Uri(referer);
        if (rangeHeader is not null) request.Headers.Range = rangeHeader;

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) return false;

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!IsMediaContentType(contentType)) return false;

        string? codec = await GetMediaCodecAsync(url, ext, ToFfmpegHeaders(request.Headers));
        var supported = ext == ExtensionOption.Video ? _supportedVideoCodecs : _supportedAudioCodecs;
        return codec is not null && supported.Contains(codec);
    }

    private static bool IsMediaContentType(string? contentType)
        => !string.IsNullOrEmpty(contentType) &&
           (contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ||
            contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase));

    private static async Task<string?> GetMediaCodecAsync(string url, ExtensionOption ext, string headers)
    {
        string stream = ext == ExtensionOption.Video ? "v:0" : "a:0";
        var args = new List<string> { "-v", "error", "-select_streams", stream, "-show_entries", "stream=codec_name", "-of", "default=noprint_wrappers=1:nokey=1" };
        if (!string.IsNullOrEmpty(headers)) args.AddRange(["-headers", headers]);
        args.Add(url);

        var result = await ProcessRunner.RunAsync(GlobalVariable.FfprobeExePath, args);
        if (result.ExitCode != 0 || !string.IsNullOrWhiteSpace(result.StdErr))
        {
            Log.Warn($"ffprobe failed to get codec (exit {result.ExitCode}): {Json.Truncate(result.StdErr.Trim(), 300)}");
            return null;
        }
        return result.StdOut.Trim();
    }

    public static async Task DownloadAndMergeMediaAsync(string videoUrl, string audioUrl, string outputPath, string headers = "")
    {
        var args = new List<string> { "-nostdin", "-y" };
        if (!string.IsNullOrEmpty(headers)) args.AddRange(["-headers", headers]);
        args.AddRange(["-i", videoUrl]);
        if (!string.IsNullOrEmpty(headers)) args.AddRange(["-headers", headers]);
        args.AddRange(["-i", audioUrl, "-c:v", "libx264", "-crf", "23", "-preset", "fast", "-c:a", "aac", "-b:a", "192k", "-f", "mp4", outputPath]);

        var result = await ProcessRunner.RunAsync(GlobalVariable.FfmpegExePath, args);
        result.EnsureSuccess("ffmpeg(merge)");
    }

    public static string ToFfmpegHeaders(HttpHeaders headers)
    {
        var sb = new StringBuilder();
        foreach (var h in headers)
            sb.Append($"{h.Key}: {string.Join(", ", h.Value)}\r\n");
        return sb.ToString();
    }
}
