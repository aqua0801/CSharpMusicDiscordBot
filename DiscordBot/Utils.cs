using Discord;
using Discord.Commands;
using Discord.Interactions;
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
                GlobalVariable.ServerAudioClientMap[intctx.Guild.Id] = audioClient;
                return 1;
            }

            if (userVoice != null)
            {
                var audioClient = await userVoice.ConnectAsync();
                GlobalVariable.ServerAudioClientMap[intctx.Guild.Id] = audioClient;
                return 2;
            }

            return -1;
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
}
