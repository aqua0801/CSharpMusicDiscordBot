using NetCord.Gateway.Voice;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Serialization;
using YoutubeDLSharp;
using YoutubeDLSharp.Options;
using YoutubeExplode;
using YoutubeExplode.Common;

namespace DiscordBot;

public sealed record AudioInfo
{
    public string Title { get; init; } = "未知";
    public string Creator { get; init; } = "未知";
    public required string Url { get; init; }
    public double Duration { get; init; }

    public string FfmpegHeaders { get; init; } = "";
}

public static class MediaProcess
{
    private static readonly YoutubeDL _ytdl = CreateYoutubeDL();

    private static readonly OptionSet _streamOptions = new()
    {
        Format = "bestaudio",
        DumpSingleJson = true,
        NoPlaylist = true,
    };

    private static readonly BilibiliDownloader _bilibili = new();

    private static YoutubeDL CreateYoutubeDL() => new()
    {
        FFmpegPath = GlobalVariable.FfmpegExePath,
        YoutubeDLPath = GlobalVariable.YtDlpExePath,
    };

    public static Func<string, Task<AudioInfo?>> ResolveAudioUrlAlgorithm(WebOption web) => web switch
    {
        WebOption.Youtube => GetYoutubeStreamUrlAsync,
        WebOption.Bilibili => _bilibili.GetBilibiliStreamUrlAsync,
        _ => throw new ArgumentOutOfRangeException(nameof(web), web, "Unsupported audio source type"),
    };

    public static Func<string, ExtensionOption, Task<string?>> ResolveDownloadVideoAlgorithm(WebOption web) => web switch
    {
        WebOption.Youtube => DownloadVideoFromYoutubeAsync,
        WebOption.Bilibili => _bilibili.DownloadAsync,
        _ => throw new ArgumentOutOfRangeException(nameof(web), web, "Unsupported download extension"),
    };

    // ── YouTube ──────────────────────────────────────────────

    public static async Task<string?> DownloadVideoFromYoutubeAsync(string url, ExtensionOption extension)
    {
        string hash = Utils.GenerateHashCode(8);

        var ytdl = CreateYoutubeDL();
        ytdl.RestrictFilenames = true;
        ytdl.OverwriteFiles = true;
        ytdl.OutputFolder = GlobalVariable.DownloadFolderPath;
        ytdl.OutputFileTemplate = $"{hash}.%(ext)s";

        RunResult<string> result = extension == ExtensionOption.Audio
            ? await ytdl.RunAudioDownload(url, AudioConversionFormat.Mp3)
            : await ytdl.RunVideoDownload(url, mergeFormat: DownloadMergeFormat.Mp4);

        if (!result.Success)
        {
            Log.Error($"yt-dlp failed to download {url}{Environment.NewLine}{string.Join(Environment.NewLine, result.ErrorOutput)}");
            return null;
        }

        if (!string.IsNullOrEmpty(result.Data) && File.Exists(result.Data))
            return Path.GetFullPath(result.Data);

        var found = new DirectoryInfo(GlobalVariable.DownloadFolderPath)
            .EnumerateFiles($"{hash}.*")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (found is null)
            Log.Error($"yt-dlp reported success but cannot find output file hash={hash} data={result.Data}");

        return found?.FullName;
    }

    public static async Task<List<string>> GetPlaylistUrlsAsync(string url)
    {
        var youtube = new YoutubeClient();
        var videos = await youtube.Playlists.GetVideosAsync(url);
        return videos.Select(video => $"https://www.youtube.com/watch?v={video.Id}").ToList();
    }

    public static async Task<AudioInfo?> GetYoutubeStreamUrlAsync(string url)
    {
        var result = await _ytdl.RunVideoDataFetch(url, overrideOptions: _streamOptions);
        if (!result.Success || result.Data is null)
        {
            Log.Warn($"yt-dlp failed to parse {url}{Environment.NewLine}{string.Join(Environment.NewLine, result.ErrorOutput)}");
            return null;
        }

        var data = result.Data;
        return new AudioInfo
        {
            Title = data.Title ?? "未知",
            Creator = data.Creator ?? data.Uploader ?? "未知",
            Url = data.Url,
            Duration = data.Duration ?? 0,
        };
    }

    // ── ffprobe ──────────────────────────────────────────────

    internal sealed record FfprobeOutput
    {
        [JsonPropertyName("format")] public FfprobeFormat? Format { get; init; }
        [JsonPropertyName("streams")] public List<FfprobeStream> Streams { get; init; } = [];
    }

    internal sealed record FfprobeFormat
    {
        [JsonPropertyName("duration")] public string? Duration { get; init; }
    }

    internal sealed record FfprobeStream
    {
        [JsonPropertyName("codec_type")] public string? CodecType { get; init; }
    }

    internal static async Task<FfprobeOutput> ProbeAsync(string input, string headers = "", bool includeStreams = false)
    {
        var args = new List<string> { "-v", "error", "-print_format", "json", "-show_format" };
        if (includeStreams) args.Add("-show_streams");
        if (!string.IsNullOrEmpty(headers)) args.AddRange(["-headers", headers]);
        args.AddRange(["-i", input]);

        var result = await ProcessRunner.RunAsync(GlobalVariable.FfprobeExePath, args);
        result.EnsureSuccess("ffprobe");
        return Json.Deserialize<FfprobeOutput>(result.StdOut, "ffprobe");
    }

    internal static double? ParseDuration(FfprobeOutput probe)
        => double.TryParse(probe.Format?.Duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : null;

    public static async Task<TimeSpan?> GetAudioDurationAsync(string url, string headers)
    {
        try
        {
            var probe = await ProbeAsync(url, headers);
            return ParseDuration(probe) is double s ? TimeSpan.FromSeconds(s) : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to get the duration of {url}", ex);
            return null;
        }
    }

    // ── ffmpeg → PCM ─────────────────────────────────────────

    public static Process StartStreamProcess(AudioInfo audioInfo)
    {
        var args = new List<string>
        {
            "-nostdin", "-hide_banner", "-loglevel", "error",
            "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_delay_max", "5",
        };
        if (!string.IsNullOrEmpty(audioInfo.FfmpegHeaders))
            args.AddRange(["-headers", audioInfo.FfmpegHeaders]);
        args.AddRange(["-i", audioInfo.Url, "-filter:a", "volume=0.25", "-vn", "-f", "s16le", "-ar", "48000", "-ac", "2", "pipe:1"]);

        return ProcessRunner.StartPcmProcess(args, audioInfo.Title);
    }

    public static Process StartLocalProcess(string absoluteFilePath)
    {
        string[] args = ["-nostdin", "-hide_banner", "-loglevel", "error", "-i", absoluteFilePath, "-f", "s16le", "-ar", "48000", "-ac", "2", "pipe:1"];
        return ProcessRunner.StartPcmProcess(args, Path.GetFileName(absoluteFilePath));
    }

    public static async Task PlayAudioAsync(VoiceClient vc, Process ffmpeg)
    {
        try
        {
            await vc.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
            await using var outStream = vc.CreateVoiceStream();
            await using var opus = new OpusEncodeStream(outStream, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio);
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(opus);
            await opus.FlushAsync();
        }
        finally
        {
            ProcessRunner.KillQuietly(ffmpeg);
        }
    }
}

public static class FFmpegCompressor
{
    /// <summary>Tries NVENC first, falls back to libx264 (for machines without an NVIDIA card).</summary>
    public static async Task CompressAsync(string inputPath, string outputPath, int targetSizeMB = 10)
    {
        var probe = await MediaProcess.ProbeAsync(inputPath, includeStreams: true);
        if (MediaProcess.ParseDuration(probe) is not double duration || duration <= 0)
            throw new InvalidDataException($"ffprobe failed to get the duration of {inputPath}");

        // 5% overhead
        double totalKbps = targetSizeMB * 8192d * 0.95 / duration;
        bool hasVideo = probe.Streams.Any(s => s.CodecType == "video");

        if (!hasVideo)
        {
            int audioKbps = (int)Math.Clamp(totalKbps, 32, 320);
            var audioRun = await ProcessRunner.RunAsync(GlobalVariable.FfmpegExePath,
                ["-nostdin", "-y", "-i", inputPath, "-vn", "-c:a", "libmp3lame", "-b:a", $"{audioKbps}k", outputPath]);
            audioRun.EnsureSuccess("ffmpeg(audio)");
            return;
        }

        int audioBitrate = (int)Math.Clamp(totalKbps * 0.15, 32, 128);
        int videoBitrate = (int)Math.Max(totalKbps - audioBitrate, 50);

        foreach (var (codec, preset) in new[] { ("h264_nvenc", "fast"), ("libx264", "veryfast") })
        {
            var run = await ProcessRunner.RunAsync(GlobalVariable.FfmpegExePath,
                ["-nostdin", "-y", "-i", inputPath, "-c:v", codec, "-preset", preset, "-b:v", $"{videoBitrate}k", "-c:a", "aac", "-b:a", $"{audioBitrate}k", outputPath]);

            if (run.ExitCode == 0)
                return;

            Log.Warn($"ffmpeg failed with {codec} (exit {run.ExitCode}), {Json.Truncate(run.StdErr, 800)}");
        }

        throw new InvalidOperationException($"ffmpeg failed to compress {inputPath} (both NVENC and libx264 failed, see log for details)");
    }
}

public readonly record struct ProcessResult(string FileName, int ExitCode, string StdOut, string StdErr)
{
    public void EnsureSuccess(string label)
    {
        if (ExitCode != 0)
            throw new InvalidOperationException($"{label} exit code {ExitCode}: {Json.Truncate(StdErr.Trim(), 800)}");
    }
}

public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> args, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Unable to start {fileName}");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }

        return new ProcessResult(fileName, process.ExitCode, await stdout, await stderr);
    }

    public static Process StartPcmProcess(IEnumerable<string> args, string label)
    {
        var psi = new ProcessStartInfo(GlobalVariable.FfmpegExePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Unable to start ffmpeg ({GlobalVariable.FfmpegExePath})");

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Log.Warn($"[ffmpeg:{label}] {e.Data}");
        };
        process.BeginErrorReadLine();

        return process;
    }

    public static void KillQuietly(Process? process)
    {
        if (process is null) return;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {

        }
        finally
        {
            process.Dispose();
        }
    }
}
