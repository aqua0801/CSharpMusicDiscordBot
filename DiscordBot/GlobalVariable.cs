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
        public static string ffmpegExePath = ".\\Data\\ffmpeg.exe";
        public static string PlaylistJsonFilePath = ".\\Data\\YoutubePlaylist.json";
        public static string EnvJsonFilePath = ".\\Data\\env.json";
        public static JObject? EnvJsonObject;

        public static ulong creatorID;

        public static ConcurrentDictionary<ulong, IAudioClient> ServerAudioClientMap = new ConcurrentDictionary<ulong, IAudioClient>();

        public static void Init()
        {
            GlobalVariable.EnvJsonObject = Json.Read(GlobalVariable.EnvJsonFilePath);
            GlobalVariable.botToken = GlobalVariable.EnvJsonObject.GetValueOrDefault<string>("bot_token");
            GlobalVariable.botNickname = GlobalVariable.EnvJsonObject.GetValueOrDefault<string>("bot_nickname");
            GlobalVariable.version = GlobalVariable.EnvJsonObject.GetValueOrDefault<string>("version");
            GlobalVariable.gitUrl = GlobalVariable.EnvJsonObject.GetValueOrDefault<string>("git_repo_link");
        }
    }
}
