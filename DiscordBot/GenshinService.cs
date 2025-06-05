using System;
using System.Linq;
using System.Threading.Tasks;
using Discord;
using Newtonsoft.Json.Linq;
using GenshinInfo.Managers;
using GenshinInfo.Models;
using AngleSharp.Common;
using System.Runtime.CompilerServices;
using Discord.WebSocket;


namespace DiscordBot
{
    public enum CheckStatus
    {
        ServerNotFound , DiscordUserNotFound , UnknownError , Success
    }

    public struct NotifyInfo
    {
        public ulong DiscordID { get; set; }

        public ulong ChannelID { get; set; }

        public RTNoteData Note { get; set; }
    }

    public class GenshinService
    {
        private static string cookiesJsonFilePath = ".\\Data\\genshin_cookies_and_id.json";
        private JObject cookiesJsonObject;
        public GenshinService()
        {
            this.cookiesJsonObject = Json.Read(GenshinService.cookiesJsonFilePath);
        }

        public async Task<Tuple<CheckStatus, ulong, RTNoteData?>> CheckGenshinResin(ulong serverID, ulong dcID)
        {
            var servers = this.cookiesJsonObject.GetKeys();

            if (!servers.Contains(serverID.ToString()))
            {
                return new Tuple<CheckStatus, ulong, RTNoteData?>(CheckStatus.ServerNotFound, 0ul, null);
            }

            var channel = this.cookiesJsonObject.GetValueOrDefault<ulong>(serverID.ToString(), "service_channel_id");
            var users = this.cookiesJsonObject.GetValueOrDefault<JArray>(serverID.ToString(), "users");

            foreach (var user in users)
            {
                string _dcID = user.GetValueOrDefault<string>("dc_id");
                if (_dcID == dcID.ToString())
                {
                    long gsID = user.GetValueOrDefault<long>("gs_id");
                    var cookies = user.GetValueOrDefault<JObject>("cookies");
                    return Tuple.Create(CheckStatus.Success, channel, await this.GetGenshinInfo(gsID, cookies));
                }
            }
            return new Tuple<CheckStatus, ulong, RTNoteData?>(CheckStatus.DiscordUserNotFound, 0ul, null);
        }

        private async Task<RTNoteData?> GetGenshinInfo(long gsID, JObject cookies)
        {
            string ltuid = "", ltoken = "";
            bool useV2 = false;

            var cookiesKeys = cookies.GetKeys();

            if (cookiesKeys.Contains("ltuid"))
            {
                ltuid = cookies.GetValueOrDefault<string>("ltuid");
            }
            else if (cookiesKeys.Contains("ltuid_v2"))
            {
                useV2 = true;
                ltuid = cookies.GetValueOrDefault<string>("ltuid_v2");
            }

            if (useV2 && cookiesKeys.Contains("ltoken_v2"))
            {
                ltoken = cookies.GetValueOrDefault<string>("ltoken_v2");
            }
            else if (cookiesKeys.Contains("ltoken"))
            {
                ltoken = cookies.GetValueOrDefault<string>("ltoken");
            }

            var infoManager = new GenshinInfoManager(gsID.ToString(), ltuid, ltoken) { UseV2Cookie = useV2 };
            
            return await infoManager.GetRealTimeNotes();
        }

        public async Task<List<NotifyInfo>> CheckGenshinResin(int resinThreshold)
        {
            var servers = this.cookiesJsonObject.GetKeys();

            List<NotifyInfo> notifyInfos = new List<NotifyInfo>();

            if (servers == null)
                return notifyInfos;

            if (servers.Count < 1)
                return notifyInfos;

            foreach(string server in servers)
            {
                var channel = this.cookiesJsonObject.GetValueOrDefault<ulong>(server, "service_channel_id");
                var users = this.cookiesJsonObject.GetValueOrDefault<JArray>(server, "users");

                foreach (var user in users)
                {
                    long gsID = user.GetValueOrDefault<long>("gs_id");
                    ulong dcID = user.GetValueOrDefault<ulong>("dc_id");
                    var cookies = user.GetValueOrDefault<JObject>("cookies");
                    var note = await this.GetGenshinInfo(gsID,cookies);

                    if (note!=null && note.CurrentResin >= resinThreshold)
                    {
                        notifyInfos.Add(new NotifyInfo { 
                            DiscordID = dcID,
                            ChannelID = channel,
                            Note = note
                        });
                    }
                }
            }

            return notifyInfos;
        }


        public async Task LoopCheckGenshinResin(DiscordSocketClient client , int resinThreshold, TimeSpan interval)
        {
            Timer t = new Timer(async _ =>
            {
                var notifyInfos = await this.CheckGenshinResin(resinThreshold);

                foreach(var notifyInfo in notifyInfos)
                {
                    var channel = await client.GetChannelAsync(notifyInfo.ChannelID);
                    if (channel != null)
                    {
                        string notifyString = $"{GlobalVariable.botNickname}偵測到旅行者 {Utils.MentionWithID(notifyInfo.DiscordID)}";
                        if (notifyInfo.Note.CurrentResin >= notifyInfo.Note.MaxResin)
                        {
                            notifyString += "爆體力辣！";
                        }
                        else
                        {
                            notifyString += "的體力快滿了！";
                        }
                        notifyString += Environment.NewLine;
                        notifyString += $"{notifyInfo.Note.CurrentResin}/{notifyInfo.Note.MaxResin}";
                        notifyString += Environment.NewLine;
                        notifyString += $"預計滿體力時間 {DateTime.Now + notifyInfo.Note.ResinRecoveryTime:yyyy-MM-dd HH:mm:ss}";
                        if (channel is ITextChannel itc)  
                            await itc.SendMessageAsync(notifyString);
                    }
 
                }
                
            } , null ,TimeSpan.FromSeconds(0),interval);

            GlobalVariable.PermanentTimers.Add(t);
        }

    }


}
