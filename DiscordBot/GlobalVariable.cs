using NetCord.Gateway;
using NetCord.Gateway.Voice;
using Newtonsoft.Json.Linq;

using System.Collections.Concurrent;

namespace DiscordBot
{
    public static class GlobalVariable
    {
        public static string BotToken = "Unknown";
        public static string BotName = "Unknown";
        public static string BotNickname = "Unknown";
        public static string CreatorName = "Unknown";

        public static string GitUrl = "";
        public static string GitUrl2 = "";
        public static string Version = "0.0.0.0";
        public const string FfmpegExePath = ".\\Data\\ffmpeg.exe";
        public const string FfprobeExePath = ".\\Data\\ffprobe.exe";
        public const string PlaylistJsonFilePath = ".\\Data\\playlist.json";
        public const string EnvJsonFilePath = ".\\Data\\env.json";
        public const string SoundEffectsFolderPath = ".\\Data\\SoundEffects\\";
        public const string DownloadFolderPath = ".\\Download\\";
        public const string ImagesFolderPath = ".\\Data\\Images\\images\\";
        public const string LabelsFolderPath = ".\\Data\\Images\\labels\\";
        public static JObject EnvJsonObject = new JObject();
        public static char CommandPrefix = '!';
        public static bool ResinLoopCheck = false;

        public static ulong BotID;
        public static ulong CreatorID;

        public static GatewayClient client;

        public static ConcurrentBag<Timer> PermanentTimers = new ConcurrentBag<Timer>();
        public static SavedPlaylistStore ConcurrentPlaylist = new SavedPlaylistStore(GlobalVariable.PlaylistJsonFilePath, true);
        public static ConcurrentDictionary<ulong, VoiceClient> ServerVoiceClientMap = new ConcurrentDictionary<ulong, VoiceClient>();
        public static HoyoLabService HoyoLab = new HoyoLabService();
        public static List<int> ServerTierUploadFileSize;

        public static void Init()
        {
            GlobalVariable.EnvJsonObject = Json.Read(GlobalVariable.EnvJsonFilePath);
            GlobalVariable.BotToken = GlobalVariable.EnvJsonObject.GetValueOrDefault<string>("bot_token");
            GlobalVariable.BotNickname = GlobalVariable.EnvJsonObject.GetValueOrDefault<string>("bot_nickname");
            GlobalVariable.Version = GlobalVariable.EnvJsonObject.GetValueOrDefault<string>("version");
            GlobalVariable.GitUrl = GlobalVariable.EnvJsonObject.GetValueOrDefault<string>("git_repo_link");
            GlobalVariable.GitUrl2 = GlobalVariable.EnvJsonObject.GetValueOrDefault<string>("git_repo_link2");
            GlobalVariable.ServerTierUploadFileSize = GlobalVariable.EnvJsonObject.GetValueOrDefault<List<int>>("server_tier_file_size");
            GlobalVariable.CommandPrefix = GlobalVariable.EnvJsonObject.GetValueOrDefault<char>("prefix");
            GlobalVariable.ResinLoopCheck = GlobalVariable.EnvJsonObject.GetValueOrDefault<bool>("resin_loop_check");
        }
    }
}
