using Discord.Audio;
using Discord.WebSocket;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;
using YoutubeExplode;
using YoutubeExplode.Common;
using YoutubeExplode.Playlists;

namespace DiscordBot
{
    public class AudioProcess
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
                    throw new Exception("Unexpected website parse required !");
            }
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
                Creator = data.Creator,
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
}
