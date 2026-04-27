using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DiscordBot;

public class BilibiliDownloader
{
    private readonly record struct UrlSourceInfo(
        string Title = "未知",
        string Author = "未知",
        string Url = "-1");

    private const string DefaultReferer = "https://www.bilibili.com";

    private readonly HttpClient _http;
    private readonly string _downloadPath = GlobalVariable.DownloadFolderPath;
    private readonly int _retryLimit = 2;

    private static readonly string[] _userAgents =
    {
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/112.0.0.0 Safari/537.36",
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Safari/537.36",
    };

    public BilibiliDownloader()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.GZip |
                                     System.Net.DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler);
        _http.DefaultRequestHeaders.Referrer = new Uri(DefaultReferer);
        RefreshUserAgent();
    }

    private void RefreshUserAgent()
    {
        _http.DefaultRequestHeaders.UserAgent.Clear();
        var ua = _userAgents[Random.Shared.Next(_userAgents.Length)];
        while (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(ua)) ;
    }

    private string GetCurrentUserAgent()
        => string.Join(" ", _http.DefaultRequestHeaders.UserAgent.Select(ua => ua.ToString()));


    public async Task<string?> DownloadAsync(string url, ExtensionOption extension)
    {
        try
        {
            if (extension == ExtensionOption.Audio)
            {
                var result = await DownloadFileAsync(url, "mp3");
                return result == "not found" ? null : Path.Combine(_downloadPath, result);
            }

            if (extension == ExtensionOption.Video)
            {
                var (videoInfo, audioInfo) = await GetVideoAndAudioInfoAsync(url);
                var sanitizedTitle = SanitizeFileName(videoInfo.Title);
                var outputPath = Path.Combine(_downloadPath, $"{sanitizedTitle}.mp4");
                var headers = MediaValidator.ConvertHttpClientToFfmpegHeaderArg(_http);

                if (await MediaValidator.DownloadAndMergeMediaAsync(videoInfo.Url, audioInfo.Url, outputPath, headers))
                    return outputPath;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Error] Bilibili download failed:{Environment.NewLine}{e}");
        }
        return null;
    }

    public async Task<MediaProcess.AudioInfo?> GetBilibiliStreamUrlAsync(string url)
    {
        UrlSourceInfo info = default;
        string header = "";
        TimeSpan? duration = null;

        for (int i = 0; i < _retryLimit; i++)
        {
            info = await GetFileUrlAsync(url, "mp3");
            if (info.Url == "-1") continue;

            header = MediaValidator.ConvertHttpClientToFfmpegHeaderArg(_http);
            duration = await MediaProcess.GetAudioDurationAsync(info.Url, header);
            break;
        }

        return new MediaProcess.AudioInfo
        {
            Title = info.Title,
            Creator = info.Author,
            Duration = (float)(duration?.TotalSeconds ?? 0f),
            Url = info.Url,
            FfmpegHeaderArgument = header,
        };
    }


    private async Task<string> DownloadFileAsync(string url, string extension)
    {
        for (int i = 0; i < _retryLimit; i++)
        {
            try
            {
                var info = await GetFileUrlAsync(url, extension);
                if (info.Url == "-1")
                {
                    Console.WriteLine("[Error] Unable to extract source url!");
                    return "not found";
                }

                var response = await _http.GetAsync(info.Url);
                if (!response.IsSuccessStatusCode) continue;

                var fileName = $"{SanitizeFileName(info.Title)}.{extension}";
                var filePath = Path.Combine(_downloadPath, fileName);
                await using var fs = new FileStream(filePath, FileMode.Create);
                await response.Content.CopyToAsync(fs);
                return fileName;
            }
            catch (Exception e)
            {
                Console.WriteLine($"[Error] Bilibili file download failed:{Environment.NewLine}{e}");
            }
        }
        return "not found";
    }

    private async Task<(UrlSourceInfo video, UrlSourceInfo audio)> GetVideoAndAudioInfoAsync(string url)
    {
        RefreshUserAgent();
        string ua = GetCurrentUserAgent();
        var html = await FetchHtmlAsync(url);
        var meta = ExtractTitleAuthorFromHtml(html);
        var dash = ExtractDashFromHtml(html);

        string videoUrl = await ResolveFirstValidUrlAsync(dash.video, ExtensionOption.Video, ua);
        string audioUrl = await ResolveFirstValidUrlAsync(dash.audio, ExtensionOption.Audio, ua);

        return (
            meta with { Url = videoUrl },
            meta with { Url = audioUrl }
        );
    }

    private async Task<UrlSourceInfo> GetFileUrlAsync(string url, string extension)
    {
        RefreshUserAgent();
        string ua = GetCurrentUserAgent();
        var html = await FetchHtmlAsync(url);
        var meta = ExtractTitleAuthorFromHtml(html);
        var dash = ExtractDashFromHtml(html);

        var targetStream = extension == "mp4" ? dash.video : dash.audio;
        var targetOption = extension == "mp4" ? ExtensionOption.Video : ExtensionOption.Audio;
        string resolvedUrl = await ResolveFirstValidUrlAsync(targetStream, targetOption, ua);

        return meta with { Url = resolvedUrl };
    }

    private async Task<string> FetchHtmlAsync(string url)
    {
        var response = await _http.SendAsync(new HttpRequestMessage(HttpMethod.Get, url));
        return await response.Content.ReadAsStringAsync();
    }

    private static (JArray video, JArray audio) ExtractDashFromHtml(string html)
    {
        var match = Regex.Match(html, @"__playinfo__=(.*?)</script><script>");
        var playInfo = JsonConvert.DeserializeObject<JObject>(match.Groups[1].Value);

        var video = playInfo?.GetValueOrDefault<JArray>("data", "dash", "video") ?? new JArray();
        var audio = playInfo?.GetValueOrDefault<JArray>("data", "dash", "audio") ?? new JArray();
        return (video, audio);
    }

    private static readonly List<Func<JToken, string>> _urlParsers = new()
    {
        t => t.GetValueOrDefault<string>("base_url"),
        t => t.GetValueOrDefault<string>("baseUrl"),
        t => t.GetValueOrDefault<JArray>("backup_url")[0].ToString(),
        t => t.GetValueOrDefault<JArray>("backupUrl")[0].ToString(),
    };

    private static async Task<string> ResolveFirstValidUrlAsync(JArray stream, ExtensionOption ext, string ua)
    {
        foreach (var item in stream)
        {
            foreach (var parser in _urlParsers)
            {
                try
                {
                    string candidate = parser(item);
                    if (await MediaValidator.IsValidMediaUrlAsync(candidate, ext, ua, DefaultReferer))
                        return candidate;
                }
                catch { /* empty , move on */ }
            }
        }
        return "-1";
    }


    private static UrlSourceInfo ExtractTitleAuthorFromHtml(string html)
    {
        var match = Regex.Match(html, @"__INITIAL_STATE__=(.*?);\(function\(\)");
        if (!match.Success) return default;

        using var doc = JsonDocument.Parse(match.Groups[1].Value);
        var root = doc.RootElement;

        if (!root.TryGetProperty("videoData", out var videoData)) return default;

        string title = videoData.TryGetProperty("title", out var t) ? t.GetString() ?? "未知" : "未知";
        string author = videoData.TryGetProperty("owner", out var owner) &&
                        owner.TryGetProperty("name", out var n)
                            ? n.GetString() ?? "未知"
                            : "未知";

        return new UrlSourceInfo(title, author);
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(name.Select(c => invalid.Contains(c) ? ' ' : c));
    }
}


public static class MediaValidator
{
    private static readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false });

    private static readonly Dictionary<string, string> _supportedVideoCodecs = new()
    {
        ["h264"] = "libx264",
        ["hevc"] = "libx265",
        ["av1"] = "libaom-av1",
        ["vp8"] = "libvpx",
        ["vp9"] = "libvpx-vp9",
        ["mpeg2"] = "mpeg2video",
        ["mpeg4"] = "mpeg4",
        ["theora"] = "libtheora",
        ["prores"] = "prores_ks",
        ["dnxhd"] = "dnxhd",
        ["jpeg"] = "mjpeg",
        ["h263"] = "h263",
        ["wmv3"] = "wmv3",
        ["flv1"] = "flv",
        ["mvc"] = "libx264",
    };

    private static readonly Dictionary<string, string> _supportedAudioCodecs = new()
    {
        ["aac"] = "aac",
        ["mp3"] = "libmp3lame",
        ["opus"] = "libopus",
        ["ac3"] = "ac3",
        ["flac"] = "flac",
        ["vorbis"] = "libvorbis",
        ["pcm"] = "pcm_s16le",
        ["alac"] = "alac",
        ["wma"] = "wmav2",
        ["speex"] = "libspeex",
        ["amr-nb"] = "libamr_nb",
        ["amr-wb"] = "libamr_wb",
    };

    public static async Task<bool> IsValidMediaUrlAsync(
        string url, ExtensionOption extension, string? ua = null, string referer = "https://www.bilibili.com")
    {
        try
        {
            if (await TryValidateAsync(HttpMethod.Head, url, extension, ua, referer, rangeHeader: null))
                return true;

            return await TryValidateAsync(HttpMethod.Get, url, extension, ua, referer,
                rangeHeader: new RangeHeaderValue(0, 1));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> TryValidateAsync(
        HttpMethod method, string url, ExtensionOption ext,
        string? ua, string referer, RangeHeaderValue? rangeHeader)
    {
        var request = new HttpRequestMessage(method, url);
        if (!string.IsNullOrEmpty(ua)) request.Headers.UserAgent.ParseAdd(ua);
        request.Headers.Referrer = new Uri(referer);
        if (rangeHeader is not null) request.Headers.Range = rangeHeader;

        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return false;

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (!IsMediaContentType(contentType)) return false;

        string headerArg = ConvertHttpRequestToFfmpegHeaderArg(request);
        return await IsMediaSupportedEncoding(url, ext, headerArg);
    }

    private static bool IsMediaContentType(string? contentType)
        => !string.IsNullOrEmpty(contentType) &&
           (contentType.StartsWith("video/") || contentType.StartsWith("audio/"));

    private static async Task<bool> IsMediaSupportedEncoding(string url, ExtensionOption ext, string headerArg)
    {
        var encoding = await GetMediaEncoding(url, ext, headerArg);
        var supported = ext == ExtensionOption.Video ? _supportedVideoCodecs : _supportedAudioCodecs;
        return supported.ContainsKey(encoding);
    }

    private static async Task<string> GetMediaEncoding(string url, ExtensionOption ext, string headerArg)
    {
        string stream = ext == ExtensionOption.Video ? "v:0" : "a:0";
        string args = $"-v error -select_streams {stream} -show_entries stream=codec_name " +
                        $"-of default=noprint_wrappers=1:nokey=1 {headerArg} \"{url}\"";

        var psi = new ProcessStartInfo
        {
            FileName = GlobalVariable.FfprobeExePath,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null) return "failed";

        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return string.IsNullOrWhiteSpace(error) ? output.Trim() : "unknown";
    }

    public static async Task<bool> DownloadAndMergeMediaAsync(
        string videoUrl, string audioUrl, string outputPath, string headerArg = "")
    {
        string args = $"-y {headerArg} -i \"{videoUrl}\" {headerArg} -i \"{audioUrl}\" " +
                      $"-c:v libx264 -crf 23 -preset fast -c:a aac -b:a 192k -f mp4 \"{outputPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = GlobalVariable.FfmpegExePath,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;
            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] ffmpeg merge failed: {ex.Message}");
            return false;
        }
    }

    public static string ConvertHttpRequestToFfmpegHeaderArg(HttpRequestMessage request)
    {
        var sb = new StringBuilder();
        foreach (var h in request.Headers)
            sb.Append($"{h.Key}: {string.Join(", ", h.Value)}\r\n");
        if (request.Content is not null)
            foreach (var h in request.Content.Headers)
                sb.Append($"{h.Key}: {string.Join(", ", h.Value)}\r\n");
        return $"-headers \"{sb}\"";
    }

    public static string ConvertHttpClientToFfmpegHeaderArg(HttpClient client)
    {
        var sb = new StringBuilder();
        foreach (var h in client.DefaultRequestHeaders)
            sb.Append($"{h.Key}: {string.Join(", ", h.Value)}\r\n");
        return $"-headers \"{sb}\"";
    }
}