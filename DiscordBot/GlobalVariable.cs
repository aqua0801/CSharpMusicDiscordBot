using Discord.Audio;
using Newtonsoft.Json.Linq;

using System.Collections.Concurrent;

namespace DiscordBot
{
    public static class GlobalVariable
    {
        public static string botToken = "Unknown";
        public static string botName = "Unknown";
        public static string botNickname = "Unknown";
        public static string creatorName = "Unknown";

        public static string gitUrl = "";
        public static string version = "0.0.0.0";
        public const string ffmpegExePath = ".\\Data\\ffmpeg.exe";
        public const string ffprobeExePath = ".\\Data\\ffprobe.exe";
        public const string playlistJsonFilePath = ".\\Data\\playlist.json";
        public const string envJsonFilePath = ".\\Data\\env.json";
        public const string soundEffectsFolderPath = ".\\Data\\SoundEffects\\";
        public const string downloadFolderPath = ".\\Download\\";
        public const string imagesFolderPath = ".\\Data\\Images\\images\\";
        public const string labelsFolderPath = ".\\Data\\Images\\labels\\";
        public static JObject envJsonObject = new JObject();

        public static ulong botID;
        public static ulong creatorID;

        public static ConcurrentPlaylistSystem concurrentPlaylist = new ConcurrentPlaylistSystem(GlobalVariable.playlistJsonFilePath,true);
        public static ConcurrentDictionary<ulong, IAudioClient> serverAudioClientMap = new ConcurrentDictionary<ulong, IAudioClient>();
        public static HoyoLabService hoyoLab = new HoyoLabService();
     

        public static ConcurrentBag<Timer> PermanentTimers = new ConcurrentBag<Timer>();

        public static void Init()
        {
            GlobalVariable.envJsonObject = Json.Read(GlobalVariable.envJsonFilePath);
            GlobalVariable.botToken = GlobalVariable.envJsonObject.GetValueOrDefault<string>("bot_token");
            GlobalVariable.botNickname = GlobalVariable.envJsonObject.GetValueOrDefault<string>("bot_nickname");
            GlobalVariable.version = GlobalVariable.envJsonObject.GetValueOrDefault<string>("version");
            GlobalVariable.gitUrl = GlobalVariable.envJsonObject.GetValueOrDefault<string>("git_repo_link");
        }
    }
}
