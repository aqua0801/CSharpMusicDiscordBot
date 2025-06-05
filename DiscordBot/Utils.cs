using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DiscordBot
{

    public static class Utils
    {
        public static Random randSeed = new Random();

        /// <summary>
        /// Handle join/move for prefix commands.
        /// Returns:
        /// -2: user not in voice, but bot is
        /// -1: user & bot not in voice
        ///  0: already in same channel
        ///  1: moved to user's channel
        ///  2: joined to user's channel
        /// </summary>
        public static async Task<int> FromContextJoin(SocketCommandContext ctx)
        {
            var user = ctx.User as IGuildUser;
            var userVoice = user?.VoiceChannel;
            var botVoice = (ctx.Guild.CurrentUser as IGuildUser)?.VoiceChannel;

            if (userVoice == null && botVoice == null)
                return -1;

            if (botVoice != null)
            {
                if (botVoice.Id == userVoice.Id)
                    return 0;
                if (userVoice == null)
                    return -2;
                await botVoice.DisconnectAsync();
                await userVoice.ConnectAsync();
                return 1;
            }

            if (userVoice != null)
            {
                await userVoice.ConnectAsync();
                return 2;
            }
            return -1;
        }

        /// <summary>
        /// Handle join/move for prefix commands.
        /// Returns:
        /// -2: user not in voice, but bot is
        /// -1: user & bot not in voice
        ///  0: already in same channel
        ///  1: moved to user's channel
        ///  2: joined to user's channel
        /// </summary>
        public static async Task<int> FromInteractionJoin(SocketInteractionContext intctx)
        {
            var user = intctx.User as IGuildUser;
            var userVoice = user?.VoiceChannel;
            var botVoice = (intctx.Guild.CurrentUser as IGuildUser)?.VoiceChannel;

            if (userVoice == null && botVoice == null)
                return -1;

            if (botVoice != null)
            {
                if (userVoice == null)
                    return -2;

                if (botVoice.Id == userVoice.Id)
                    return 0;

                await botVoice.DisconnectAsync(); 
                var audioClient = await userVoice.ConnectAsync();
                GlobalVariable.serverAudioClientMap[intctx.Guild.Id] = audioClient;
                return 1;
            }

            if (userVoice != null)
            {
                var audioClient = await userVoice.ConnectAsync();
                GlobalVariable.serverAudioClientMap[intctx.Guild.Id] = audioClient;
                return 2;
            }

            return -1;
        }

        public static async Task DisconnectFromSVC(SocketVoiceChannel svc)
        {
            if (svc == null)
                return;
            await svc.DisconnectAsync();
        }

        public static string GenerateHashCode(int length)
        {
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var random = new Random();
            return new string(Enumerable.Repeat(chars, length)
                .Select(s => s[random.Next(s.Length)]).ToArray());
        }

        public static double LevenshteinSimilarity(string s1, string s2)
        {
            int len1 = s1.Length;
            int len2 = s2.Length;
            int[,] dp = new int[len1 + 1, len2 + 1];

            for (int i = 0; i <= len1; i++) dp[i, 0] = i;
            for (int j = 0; j <= len2; j++) dp[0, j] = j;

            for (int i = 1; i <= len1; i++)
            {
                for (int j = 1; j <= len2; j++)
                {
                    int cost = (s1[i - 1] == s2[j - 1]) ? 0 : 1;
                    dp[i, j] = Math.Min(
                        Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                        dp[i - 1, j - 1] + cost
                    );
                }
            }

            int distance = dp[len1, len2];
            int maxLen = Math.Max(len1, len2);
            return maxLen == 0 ? 1.0 : 1.0 - (double)distance / maxLen;
        }

        public static void Swap<T>(ref T t1 , ref T t2)
        {
            T tmp = t1;
            t1 = t2;
            t2 = tmp;
        }

        public static int RandInt(int Min, int Max)
        {
            return Utils.randSeed.Next(Min, Max);
        }

        public static float RandFloat(float Min, float Max)
        {
            float range = Max - Min;
            return Min + (range * (float)Utils.randSeed.NextDouble());
        }

        public static string MentionWithID(ulong id)
        {
            return $"<@{id}>";
        }


        public static bool IsNullableType<T>(bool StrictNullable = false)
        {
            if (Nullable.GetUnderlyingType(typeof(T)) != null) return true;
            if (typeof(T).IsValueType) return false;
            return !StrictNullable;
        }

        public static List<int> Range(int Max)
        {
            return Utils.Range(0, Max, 1);
        }

        public static List<int> Range(int Min, int Max, int Step = 1)
        {
            List<int> Nums = new List<int>();
            for (int i = Min; i < Max; i += Step)
            {
                Nums.Add(i);
            }
            return Nums;
        }

    }

    public static class Extensions
    {
        public static void Shuffle<T>(this IList<T> lst)
        {
            int count = lst.Count;
            for (int i = count -1 ; i > 0; i--)
            {
                int swapIndex = Utils.RandInt(0,i+1);
                (lst[i], lst[swapIndex]) = (lst[swapIndex], lst[i]);
            }
        }
    }

    public class ThreadAffinityHelper
    {
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern UIntPtr SetThreadAffinityMask(IntPtr hThread, UIntPtr dwThreadAffinityMask);

        public static void SetAffinity(int coreIndex)
        {
            if (coreIndex < 0 || coreIndex >= Environment.ProcessorCount)
                throw new ArgumentOutOfRangeException(nameof(coreIndex));

            IntPtr handle = GetCurrentThread();
            UIntPtr mask = (UIntPtr)(1 << coreIndex); // 1 << n gives bitmask for CPU n
            SetThreadAffinityMask(handle, mask);
        }
    }
}
