using NetCord.Gateway;
using NetCord.Gateway.Voice;
using System.Collections.Concurrent;

namespace DiscordBot;

public static class GlobalVariable
{
    public static readonly string DataFolderPath = "Data";
    public static readonly string FfmpegExePath = Path.Combine(DataFolderPath, "ffmpeg.exe");
    public static readonly string FfprobeExePath = Path.Combine(DataFolderPath, "ffprobe.exe");
    public static readonly string YtDlpExePath = "yt-dlp.exe";
    public static readonly string PlaylistJsonFilePath = Path.Combine(DataFolderPath, "playlist.json");
    public static readonly string EnvJsonFilePath = Path.Combine(DataFolderPath, "env.json");
    public static readonly string HoyolabJsonFilePath = Path.Combine(DataFolderPath, "HoyolabServiceJson.json");
    public static readonly string SoundEffectsFolderPath = Path.Combine(DataFolderPath, "SoundEffects");
    public static readonly string ImagesFolderPath = Path.Combine(DataFolderPath, "Images", "images");
    public static readonly string LabelsFolderPath = Path.Combine(DataFolderPath, "Images", "labels");
    public static readonly string DownloadFolderPath = "Download";

    // ── Settings ─────────────────────────────────────────────────
    public static BotConfig Config { get; private set; } = new();
    public static string BotNickname => Config.BotNickname;

    // ── Runtime data ────────────────────────────
    public static string BotName { get; set; } = "Unknown";
    public static ulong BotId { get; set; }
    public static string CreatorName { get; set; } = "Unknown";
    public static ulong CreatorId { get; set; }
    public static GatewayClient Client { get; set; } = null!;

    // ── Shared services ─────────────────────────────────────────────
    public static ConcurrentDictionary<ulong, VoiceClient> ServerVoiceClientMap { get; } = new();
    public static SavedPlaylistStore SavedPlaylists { get; private set; } = null!;
    public static HoyoLabService HoyoLab { get; private set; } = null!;

    public static void Init()
    {
        Config = Json.Read<BotConfig>(EnvJsonFilePath);
        Config.Validate();

        Directory.CreateDirectory(DownloadFolderPath);

        SavedPlaylists = new SavedPlaylistStore(PlaylistJsonFilePath);
        HoyoLab = HoyoLabService.LoadOrEmpty(HoyolabJsonFilePath);
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Download"));
    }
}
