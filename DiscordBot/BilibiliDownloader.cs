using AngleSharp.Dom;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DiscordBot
{
    public  class BilibiliDownloader
    {
        private readonly HttpClient _httpClient;
        private readonly string _downloadPath = GlobalVariable.downloadFolderPath;
        private readonly string[] _supportedExtensions = { "mp3", "mp4" };
        private readonly int _retryLimit = 2;
        private readonly HttpClientHandler _handler;

        public BilibiliDownloader()
        {
            this._handler = new HttpClientHandler()
            {
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            this._httpClient = new HttpClient(this._handler);
            this._httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(this.GetRandomUserAgent());
            this._httpClient.DefaultRequestHeaders.Referrer = new Uri("https://www.bilibili.com");
        }

        private string GetRandomUserAgent()
        {
            var agents = new[] {
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/112.0.0.0 Safari/537.36",
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 Safari/537.36",
            };
            return agents[new Random().Next(agents.Length)];
        }
        public async Task<string?> DownloadAsync(string url, ExtensionOption extension)
        {
            if (extension == ExtensionOption.Audio)
            {
                var result = await DownloadFileAsync(url, "mp3");
                return result == "not found" ? null : Path.Combine(_downloadPath, result);
            }
            else if (extension == ExtensionOption.Video)
            {
                var videoResult = await DownloadFileAsync(url, "mp4");
                var audioResult = await DownloadFileAsync(url, "mp3");

                string? DetectStatus()
                {
                    if (videoResult != "not found" && audioResult != "not found") return null;
                    if (videoResult == "not found") return audioResult;
                    if (audioResult == "not found") return videoResult;
                    return null;
                }

                var detect = DetectStatus();
                if (detect != null) return detect;

                var outputPath = Path.Combine(_downloadPath, "result" + videoResult);
                MergeVideoAndAudio(Path.Combine(_downloadPath, videoResult),
                                   Path.Combine(_downloadPath, audioResult),
                                   outputPath);
                return outputPath;
            }

            return null;
        }

        public async Task<MediaProcess.AudioInfo?> GetBilibililStreamUrlAsync(string url)
        {
            var (fileUrl, title) = await GetFileUrlAsync(url, "mp3");
            var durationTimespan = await MediaProcess.GetAudioDurationAsync(fileUrl);
            float duraion = 0f;
            if (durationTimespan != null)
                duraion = (float)durationTimespan.Value.TotalSeconds;

            return new MediaProcess.AudioInfo()
            {
                Title = title,
                Creator = "Bilibili創作者解析懶得寫",
                Duration = duraion,
                Url = fileUrl
            };
        }


        public async IAsyncEnumerable<byte[]> GetChunksAsync(string url)
        {
            var (fileUrl, _) = await GetFileUrlAsync(url, "mp3");
            var stream = await _httpClient.GetStreamAsync(fileUrl);
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer)) > 0)
            {
                yield return buffer.Take(read).ToArray();
            }
        }

        private async Task<string> DownloadFileAsync(string url, string extension)
        {
            for (int i = 0; i < _retryLimit; i++)
            {
                try
                {
                    var (fileUrl, title) = await GetFileUrlAsync(url, extension);
                    if (fileUrl == "-1") return "not found";

                    var response = await _httpClient.GetAsync(fileUrl);
                    if (!response.IsSuccessStatusCode) continue;

                    title = SanitizeFileName(title);
                    var fileName = $"{title}.{extension}";
                    var filePath = Path.Combine(_downloadPath, fileName);
                    await using var fs = new FileStream(filePath, FileMode.Create);
                    await response.Content.CopyToAsync(fs);
                    return fileName;
                }
                catch { continue; }
            }

            return "not found";
        }

        private async Task<(string fileUrl, string title)> GetFileUrlAsync(string url, string extension)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(GetRandomUserAgent());
            var response = await _httpClient.SendAsync(request);
 
            var html = await response.Content.ReadAsStringAsync();

            var playInfoMatch = Regex.Match(html, @"__playinfo__=(.*?)</script><script>");
            var playInfo = JsonConvert.DeserializeObject<JObject>(playInfoMatch.Groups[1].Value);

            if (playInfo == null)
                return ("-1", "-1");

            string videoUrl = "-1";
            string audioUrl = "-1";

            var videoDash = playInfo.GetValueOrDefault<JArray>("data", "dash", "video");
            var audioDash = playInfo.GetValueOrDefault<JArray>("data", "dash", "audio");
            var vKeys = videoDash.GetKeys().Select(k => int.Parse(k));
            var aKeys = audioDash.GetKeys().Select(k => int.Parse(k));

            foreach (int key in vKeys)
            {
                var jArr = videoDash[key];
                if (jArr == null)
                    continue;
                var jKeys = jArr.GetKeys();
                if (jKeys.Contains("baseUrl"))
                {
                    if (await MediaValidator.IsValidMediaUrlAsync(jArr.GetValueOrDefault<string>("baseUrl")))
                    {
                        videoUrl = jArr.GetValueOrDefault<string>("baseUrl");
                        break;
                    }
                }
                if (jKeys.Contains("base_url"))
                {
                    if (await MediaValidator.IsValidMediaUrlAsync(jArr.GetValueOrDefault<string>("base_url")))
                    {
                        videoUrl = jArr.GetValueOrDefault<string>("base_url");
                        break;
                    }
                }
                if (jKeys.Contains("backup_url"))
                {
                    if (await MediaValidator.IsValidMediaUrlAsync(jArr.GetValueOrDefault<JArray>("backup_url")[0].ToString()))
                    {
                        videoUrl = jArr.GetValueOrDefault<JArray>("backup_url")[0].ToString();
                        break;
                    }
                }
                if (jKeys.Contains("backupUrl"))
                {
                    if (await MediaValidator.IsValidMediaUrlAsync(jArr.GetValueOrDefault<JArray>("backupUrl")[0].ToString()))
                    {
                        videoUrl = jArr.GetValueOrDefault<JArray>("backupUrl")[0].ToString();
                        break;
                    }
                }
            }

            foreach (int key in aKeys)
            {
                var jArr = audioDash[key];
                if (jArr == null)
                    continue;
                var jKeys = jArr.GetKeys();
                if (jKeys.Contains("baseUrl"))
                {
                    if (await MediaValidator.IsValidMediaUrlAsync(jArr.GetValueOrDefault<string>("baseUrl")))
                    {
                        audioUrl = jArr.GetValueOrDefault<string>("baseUrl");
                        break;
                    }
                }
                if (jKeys.Contains("base_url"))
                {
                    if (await MediaValidator.IsValidMediaUrlAsync(jArr.GetValueOrDefault<string>("base_url")))
                    {
                        audioUrl = jArr.GetValueOrDefault<string>("base_url");
                        break;
                    }
                }
                if (jKeys.Contains("backup_url"))
                {
                    if (await MediaValidator.IsValidMediaUrlAsync(jArr.GetValueOrDefault<JArray>("backup_url")[0].ToString()))
                    {
                        audioUrl = jArr.GetValueOrDefault<JArray>("backup_url")[0].ToString();
                        break;
                    }
                }
                if (jKeys.Contains("backupUrl"))
                {
                    if (await MediaValidator.IsValidMediaUrlAsync(jArr.GetValueOrDefault<JArray>("backupUrl")[0].ToString()))
                    {
                        audioUrl = jArr.GetValueOrDefault<JArray>("backupUrl")[0].ToString();
                        break;
                    }
                }
            }

            string title = ExtractTitleFromHtml(html);

            if (extension == "mp4") return (videoUrl, title);
            return (audioUrl, title);
        }

        public static string ExtractTitleFromHtml(string html)
        {
            string pattern = @"__INITIAL_STATE__=(.*?);\(function\(\)";
            var match = Regex.Match(html, pattern);
            if (!match.Success)
            {
                return "未知";
            }

            string jsonString = match.Groups[1].Value;

            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;

            if (root.TryGetProperty("videoData", out var videoData) &&
                videoData.TryGetProperty("title", out var titleProp))
            {
                return titleProp.GetString();
            }

            return "未知";
        }

        public async Task<string> GetStringWithEncodingAsync(string url, Encoding defaultEncoding)
        {
            var response = await _httpClient.GetAsync(url);
            var contentBytes = await response.Content.ReadAsByteArrayAsync();

            Encoding encoding = defaultEncoding;
            var charset = response.Content.Headers.ContentType?.CharSet;
            if (!string.IsNullOrEmpty(charset))
            {
                try
                {
                    encoding = Encoding.GetEncoding(charset);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[Error][Bilibili]{e}");
                }
            }

            return encoding.GetString(contentBytes);
        }

        private string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            return string.Concat(name.Select(c => invalid.Contains(c) ? ' ' : c));
        }

        private void MergeVideoAndAudio(string videoPath, string audioPath, string outputPath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = GlobalVariable.ffmpegExePath,
                Arguments = $"-i \"{videoPath}\" -i \"{audioPath}\" -c:v copy -c:a aac -strict experimental \"{outputPath}\" -y",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            process.WaitForExit();

            File.Delete(videoPath);
            File.Delete(audioPath);
        }

    }

    public static class MediaValidator
    {
        private static readonly HttpClient httpClient = new HttpClient();

        public static async Task<bool> IsValidMediaUrlAsync(string url)
        {
            try
            {
                var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
                var headResponse = await httpClient.SendAsync(headRequest);

                if (headResponse.IsSuccessStatusCode)
                {
                    var contentType = headResponse.Content.Headers.ContentType?.MediaType;
                    if (IsMediaContentType(contentType))
                        return true;
                }

                var getRequest = new HttpRequestMessage(HttpMethod.Get, url);
                getRequest.Headers.Range = new RangeHeaderValue(0, 1);
                var getResponse = await httpClient.SendAsync(getRequest);

                if (getResponse.IsSuccessStatusCode)
                {
                    var contentType = getResponse.Content.Headers.ContentType?.MediaType;
                    if (IsMediaContentType(contentType))
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }
        private static bool IsMediaContentType(string? contentType)
        {
            return !string.IsNullOrEmpty(contentType) &&
                   (contentType.StartsWith("video/") || contentType.StartsWith("audio/"));
        }
    }
}
