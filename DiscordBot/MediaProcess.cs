using Discord.Audio;
using Discord.Rest;
using Discord.WebSocket;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;
using YoutubeExplode.Videos.Streams;

namespace DiscordBot
{
    public class MediaProcess
    {
        private static readonly YoutubeDL ytdl = new YoutubeDL()
        {
            FFmpegPath = GlobalVariable.ffmpegExePath,
            YoutubeDLPath = "yt-dlp.exe"
        };

        private static readonly OptionSet options = new OptionSet()
        {
            Format = "bestaudio",
            DumpSingleJson = true,
            NoPlaylist = true
        };


        public class AudioInfo
        {
            public string Title { get; set; }
            public string Creator { get; set; }
            public string Url { get; set; }
            public float Duration { get; set; }
        }


        private static readonly BilibiliDownloader bilibiliDownloader = new BilibiliDownloader();

        public static Func<string,Task<AudioInfo?>> DetermineAudioUrlAlgorithm(WebOption web)
        {
            switch (web)
            {
                case (WebOption.Youtube):
                    return GetYoutubeStreamUrlAsync;
                case (WebOption.Bilibili):
                    return bilibiliDownloader.GetBilibililStreamUrlAsync;
                default:
                    throw new InvalidOperationException("Unsupported website for audio extraction !");
            }
        }

 
        public static async Task<Func<string,ExtensionOption, Task<string?>>> DetermineDownloadVideoAlgorithm(WebOption web )
        {
            if (web == WebOption.Youtube) 
            {
                return DownloadVideoFromYoutube;
            }
            else if(web == WebOption.Bilibili)
            {
                return bilibiliDownloader.DownloadAsync;
            }
            throw new InvalidOperationException("Unable to determine algorithm");
        }

        public static async Task<string?> DownloadVideoFromYoutube(string url , ExtensionOption extension)
        {
            string ext = (extension == ExtensionOption.Video) ? ".mp4" : ".mp3";
            string filename = $"{Utils.GenerateHashCode(8)}{ext}";
            string fullfilename = Path.Combine(GlobalVariable.downloadFolderPath, filename);
            var ytlp = new YoutubeDL()
            {
                FFmpegPath = GlobalVariable.ffmpegExePath,
                YoutubeDLPath ="yt-dlp.exe"
            };
            ytdl.RestrictFilenames = true;
            ytdl.OverwriteFiles = true;
            ytdl.OutputFileTemplate = filename;
            ytdl.OutputFolder = GlobalVariable.downloadFolderPath;
 
            string TryFindFilename()
            {
                if (File.Exists(fullfilename)) return fullfilename;
                var files = new DirectoryInfo(GlobalVariable.downloadFolderPath).GetFiles();

                foreach(var file in files)
                {
                    if (file.Name.StartsWith(filename))
                    {
                        if (!file.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase))
                        {
                            string newFullPath = Path.Combine($"{file.DirectoryName}" , $"{filename}");
                            File.Move(file.FullName,newFullPath);
                            return newFullPath;
                        }
                        return file.FullName;
                    }
                }

                return "failed to find";
            }
            
            if (extension == ExtensionOption.Audio)  await ytdl.RunAudioDownload(url);
            else await ytdl.RunVideoDownload(url);
            
            return TryFindFilename();
        }


        public static async Task<List<string>> GetPlaylistUrlsAsync(string url)
        {
            var youtube = new YoutubeClient();
            var playlist = await youtube.Playlists.GetVideosAsync(url);
            return playlist
                .Select(video => $"https://www.youtube.com/watch?v={video.Id}")
                .ToList();
        }

        public static async Task<AudioInfo?> GetYoutubeStreamUrlAsync(string url)
        {
            var result = await ytdl.RunVideoDataFetch(url,overrideOptions:options);
            if (!result.Success || result.Data == null)
                return null;
            var data = result.Data;
           
            return new AudioInfo
            {
                Title = data.Title,
                Creator = data.Creator ?? data.Uploader,
                Url = data.Url,
                Duration = data.Duration?? 0f
            };
        }

        public static async Task<TimeSpan?> GetAudioDurationAsync(string url)
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v quiet -print_format json -show_format -i \"{url}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            using var reader = process.StandardOutput;
            string output = await reader.ReadToEndAsync();
            await process.WaitForExitAsync();

            var json = JsonDocument.Parse(output);
            if (json.RootElement.TryGetProperty("format", out var format)
                && format.TryGetProperty("duration", out var durationElement)
                && double.TryParse(durationElement.GetString(), out var durationSec))
            {
                return TimeSpan.FromSeconds(durationSec);
            }
            return null;
        }


        public static async Task<Process?> CreateStreamAsync(AudioInfo audioInfo)
        {
            var ffmpeg = new ProcessStartInfo
            {
                FileName = GlobalVariable.ffmpegExePath,
                Arguments = $"-reconnect 1 -reconnect_streamed 1 -reconnect_delay_max 5 -i \"{audioInfo.Url}\" -filter:a \"volume=0.25\" -vn -f s16le -ar 48000 -ac 2 pipe:1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            var process = Process.Start(ffmpeg);

            if (process != null)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (s, e) =>
                {
                    Console.WriteLine($"[FFmpeg] Process exited with code: {process.ExitCode}");
                };
            }

            return process;
        }

        public static async Task<Process?> CreateLocalAsync(string absoluteFilePath)
        {
            var ffmpeg = new ProcessStartInfo
            {
                FileName = GlobalVariable.ffmpegExePath,
                Arguments = $"-hide_banner -loglevel error -i \"{absoluteFilePath}\" -f s16le -ar 48000 -ac 2 pipe:1",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = false,
                CreateNoWindow = true
            };

            var process = Process.Start(ffmpeg);

            if (process != null)
            {
                process.EnableRaisingEvents = true;
                process.Exited += (s, e) =>
                {
                    Console.WriteLine($"[FFmpeg] Process exited with code: {process.ExitCode}");
                };
            }

            return process;
        }

        public static TimeSpan GetLocalMediaDuration(string filePath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "ffprobe",
                Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{filePath}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (double.TryParse(output, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.FromSeconds(0);
        }


        public static async Task PlayAudioAsync(IAudioClient client, Process ffmpeg)
        { 
            using var output = ffmpeg.StandardOutput.BaseStream;
            var discordStream = client.CreatePCMStream(AudioApplication.Music,bufferMillis:1500);
            try
            {
                await output.CopyToAsync(discordStream);
            }
            catch(Exception e)
            {
                Console.WriteLine($"[ERROR]{e}");
            }
            finally
            {
                await discordStream.FlushAsync();
                await discordStream.DisposeAsync();
            }
        }

        public static async Task PlayAudioByUrlAndRespond(SocketInteraction interaction , string url , IAudioClient client , DisplayOption display , Func<string,Task<AudioInfo?>>urlAlgorithm)
        {
            await Task.Run(async () => {

                bool eph = SlashCommands.ToEphemeral(display);
                AudioInfo? audioInfo =  await urlAlgorithm(url);

                if (audioInfo == null)
                {
                    await interaction.FollowupAsync($"{GlobalVariable.botNickname}無法解析音訊網址 !",ephemeral: eph);
                    return;
                }

                var ffmpeg = await CreateStreamAsync(audioInfo);

                if (ffmpeg == null)
                {
                    await interaction.FollowupAsync($"{GlobalVariable.botNickname}無法處理音訊 !", ephemeral: eph);
                    return;
                }

                await interaction.FollowupAsync($"{GlobalVariable.botNickname}激情開唱 : {audioInfo.Title} !", ephemeral: eph);

                await PlayAudioAsync(client, ffmpeg);

            });
        }


    }

    public static class FFmpegCompressor
    {
        private const int MinAudioBitrate = 32000;
        private const int MaxAudioBitrate = 256000;

        public static void CompressVideo(string inputPath, string outputPath, int targetSizeMB = 10 , string ffmpegPath = GlobalVariable.ffmpegExePath , string ffprobePath = GlobalVariable.ffprobeExePath)
        {
            // 1. Get media info using ffprobe
            var probeJson = RunProcess(ffprobePath, $"-v quiet -print_format json -show_format -show_streams \"{inputPath}\"");

            using var doc = JsonDocument.Parse(probeJson);
            var format = doc.RootElement.GetProperty("format");
            double duration = double.Parse(format.GetProperty("duration").GetString());

            // 2. Calculate bitrates
            double targetBitrate = (targetSizeMB *8192f) / (1.073741824 * duration);
            double audioBitrate = Math.Round(targetBitrate * 0.2);
            double videoBitrate = Math.Round(targetBitrate * 0.8);

            RunProcessVoid(ffmpegPath, $"-y -i \"{inputPath}\" -c:v h264_nvenc -preset fast -b:v {videoBitrate}k -c:a aac -b:a {audioBitrate}k \"{outputPath}\"");
        }

        private static string RunProcess(string exePath, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            return output + error;
        }
        private static void RunProcessVoid(string exePath, string args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = args,
                RedirectStandardOutput = false,
                RedirectStandardError = false,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using (Process process = new Process())
            {
                process.StartInfo = psi;
                process.Start();
                process.WaitForExit();
            }

        }
    }
}
