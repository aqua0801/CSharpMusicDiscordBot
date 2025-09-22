using Discord;
using Discord.WebSocket;
using MarchSeven;
using MarchSeven.Models.Abstractions;
using MarchSeven.Models.Core;
using MarchSeven.Models.Core.Cookie;
using MarchSeven.Models.Core.EndPoints;
using MarchSeven.Models.GenshinImpact;
using MarchSeven.Models.HoYoLab;
using MarchSeven.Models.ZenlessZoneZero;
using MarchSeven.Util.Errors;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiscordBot
{
    public record HoyolabUserInfo
    {
        public CheckStatus Status { get; set; } = CheckStatus.Unknown;
        public string Name { get; set; } = "Unknown";
        public int CurrentResin { get;set; }
        public int MaxResin { get; set; }
        public DateTime MaxAt { get;set; }
        public HoyolabUserInfo() { }
        public HoyolabUserInfo(HoyolabUserInfo other)
        {
            Status = other.Status;
            Name = other.Name;
            CurrentResin = other.CurrentResin;
            MaxResin = other.MaxResin;
            MaxAt = other.MaxAt;
        }
    }

    public record HoyolabBroadcastInfo : HoyolabUserInfo
    {
        public string DiscordId { get; set; } = "";
        public string BroadcastChannelId { get; set; } = "";
        public string BroadcastServerId { get; set; } = "";

        public HoyolabBroadcastInfo(HoyolabUserInfo info): base(info)
        {

        }
    }

    public enum GameType
    {
        Genshin , ZenlessZoneZero , HonkaiStarRail
    }

    public enum CheckStatus
    {
        Success , UserNotFound , UserNotRegister , Unknown , UnknownError
    }

    public class HoyoLabService
    {
        private struct GameInfo
        {
            public string GameKey { get; set; } 
            public string GameName { get; set; } 
            public string PlayerCallName { get; set; } 
        }
        private static Dictionary<GameType, GameInfo> gameInfoMap = new Dictionary<GameType, GameInfo>()
        {
            {GameType.Genshin,new GameInfo()
            {
                GameKey = "gs_id",
                GameName = "原神",
                PlayerCallName = "旅行者"
            } },
            {GameType.HonkaiStarRail,new GameInfo()
            {
                GameKey = "hsr_id",
                GameName = "崩鐵",
                PlayerCallName = "開拓者"
            } },
            {GameType.ZenlessZoneZero,new GameInfo()
            {
                GameKey = "zzz_id",
                GameName = "絕區零",
                PlayerCallName = "繩匠"
            } }
        };
        private static string cookiesJsonFilePath = ".\\Data\\genshin_cookies_and_id.json";
        private JObject cookiesJsonObject;

        public HoyoLabService( )
        {
            cookiesJsonObject = Json.Read(cookiesJsonFilePath);
        }

        private async Task<HoyolabUserInfo?> GetGenshinInfoAsync(string cookie, string huid, string guid)
        {
            var (name, region) = await this.GetPlayerNameRegionAsync(huid, guid, cookie);

            string url = $"https://bbs-api-os.hoyolab.com/game_record/genshin/api/dailyNote?server={region}&role_id={guid}";

            var response = await SendRequestAsync(url, cookie);
            var json = JsonDocument.Parse(response);

            if (!json.RootElement.TryGetProperty("data", out var root))
            {
                throw new Exception("Failed to get genshin info !");
            }

            int cur = root.GetProperty("current_resin").GetInt32();
            int max = root.GetProperty("max_resin").GetInt32();
            int restore = int.Parse(root.GetProperty("resin_recovery_time").ToString());

            return new HoyolabUserInfo()
            {
                Name = name,
                CurrentResin = cur,
                MaxResin = max,
                MaxAt = DateTime.Now + TimeSpan.FromSeconds(restore)
            };

        }

        private async Task<HoyolabUserInfo?> GetHonkaiInfoAsync(string cookie, string huid, string guid)
        {
            var (name, region) = await this.GetPlayerNameRegionAsync(huid, guid, cookie);

            string url = $"https://bbs-api-os.hoyolab.com/game_record/hkrpg/api/note?server={region}&role_id={guid}";

            var response = await SendRequestAsync(url, cookie , true);
            var json = JsonDocument.Parse(response);

            if (!json.RootElement.TryGetProperty("data", out var root))
            {
                throw new Exception("Failed to get hsr info !");
            }

            int cur = root.GetProperty("current_stamina").GetInt32();
            int max = root.GetProperty("max_stamina").GetInt32();
            int restore = int.Parse(root.GetProperty("stamina_recover_time").ToString());

            return new HoyolabUserInfo()
            {
                Name = name,
                CurrentResin = cur,
                MaxResin = max,
                MaxAt = DateTime.Now + TimeSpan.FromSeconds(restore)
            };
        }

        private async Task<HoyolabUserInfo?> GetZzzInfoAsync(string cookie, string huid, string guid)
        {
            var (name, region) = await this.GetPlayerNameRegionAsync(huid, guid, cookie);

            string url = $"https://sg-public-api.hoyolab.com/event/game_record_zzz/api/zzz/note?server={region}&role_id={guid}";

            var response = await SendRequestAsync(url, cookie);
            var json = JsonDocument.Parse(response);

            if (!json.RootElement.TryGetProperty("data", out var root))
            {
                throw new Exception("Failed to get zzz info !");
            }
   
            var energy = root.GetProperty("energy");
            var progress = energy.GetProperty("progress");
            int cur = progress.GetProperty("current").GetInt32();
            int max = progress.GetProperty("max").GetInt32();
            int restore = energy.GetProperty("restore").GetInt32();

            return new HoyolabUserInfo()
            {
                Name = name,
                CurrentResin = cur,
                MaxResin = max,
                MaxAt = DateTime.Now + TimeSpan.FromSeconds(restore)
            };

        }


        public void LoopCheckResin(DiscordSocketClient client, int batteryThreshold, TimeSpan interval , GameType type)
        {
            Timer t = new Timer(async _ =>
            {
                var notifyInfos = await this.CheckExceedResin(batteryThreshold,type);

                if (notifyInfos == null)
                    return;

                foreach (var notifyInfo in notifyInfos)
                {
                    var channel = await client.GetChannelAsync(ulong.Parse(notifyInfo.BroadcastChannelId));
                    if (channel != null)
                    {
                        var info = gameInfoMap[type];
                        string notifyString = $"[{info.GameName}]{GlobalVariable.botNickname}偵測到{info.PlayerCallName} {Utils.MentionWithID(ulong.Parse(notifyInfo.DiscordId))}";
                        if (notifyInfo.CurrentResin >= notifyInfo.MaxResin)
                        {
                            notifyString += "爆體力辣！";
                        }
                        else
                        {
                            notifyString += "的體力快滿了！";
                        }
                        notifyString += Environment.NewLine;
                        notifyString += $"{notifyInfo.CurrentResin}/{notifyInfo.MaxResin}";
                        notifyString += Environment.NewLine;
                        notifyString += $"預計滿體力時間 {notifyInfo.MaxAt:yyyy-MM-dd HH:mm:ss}";
                        if (channel is ITextChannel itc)
                            await itc.SendMessageAsync(notifyString);
                    }
                }

            }, null, TimeSpan.FromSeconds(0), interval);

            GlobalVariable.PermanentTimers.Add(t);
        }

        public async Task<List<HoyolabBroadcastInfo>> CheckExceedResin(int threshold , GameType type)
        {
            var servers = this.cookiesJsonObject.GetKeys();

            List<HoyolabBroadcastInfo> notifyInfos = new List<HoyolabBroadcastInfo>();

            if (servers == null)
                return notifyInfos;

            if (servers.Count < 1)
                return notifyInfos;

            foreach (string server in servers)
            {
                string channel = this.cookiesJsonObject.GetValueOrDefault<string>(server, "service_channel_id");
                var users = this.cookiesJsonObject.GetValueOrDefault<JArray>(server, "users");

                foreach (var user in users)
                {
                    string gameKey = gameInfoMap[type].GameKey;
                    string guid = user.GetValueOrDefault<string>(gameKey);
                    if (guid == "-1")
                        continue;
                    string dcID = user.GetValueOrDefault<string>("dc_id");
                    var c = user.GetValueOrDefault<JObject>("cookies");
                    string cookie = ToCookieString(c);
                    string huid = GetHoyoUid(c);
                    var algorithm = this.ToGetInfoAlgorithm(type);

                    var info = await algorithm(cookie, huid, guid);
                    if (info != null && info.CurrentResin >= threshold)
                    {
                        var broadcast = new HoyolabBroadcastInfo(info)
                        {
                            Status = CheckStatus.Success,
                            DiscordId = dcID,
                            BroadcastServerId = server,
                            BroadcastChannelId = channel
                        };
                        notifyInfos.Add(broadcast);
                    }

                    //int maxTry = 3;
                    //for(int i = 0; i < maxTry; i++)
                    //{
                    //    try
                    //    {
                    //        var info = await algorithm(cookie, huid, guid);
                    //        if (info != null && info.CurrentResin >= threshold)
                    //        {
                    //            var broadcast = new HoyolabBroadcastInfo(info)
                    //            {
                    //                Status = CheckStatus.Success,
                    //                DiscordId = dcID,
                    //                BroadcastServerId = server,
                    //                BroadcastChannelId = channel
                    //            };
                    //            notifyInfos.Add(broadcast);
                    //        }
                    //        if(info!=null)
                    //            break; 
                    //    }
                    //    catch
                    //    {

                    //    }
                    //}

                }
            }

            return notifyInfos;
        }




        public Func<string,string,string,Task<HoyolabUserInfo?>> ToGetInfoAlgorithm(GameType type)
        {
            switch (type)
            {
                case GameType.Genshin:
                    return this.GetGenshinInfoAsync;
                case GameType.ZenlessZoneZero:
                    return this.GetZzzInfoAsync;
                case GameType.HonkaiStarRail:
                    return this.GetHonkaiInfoAsync;
            }
            throw new Exception("Unsupported algorithm !");
        }

        public async Task<HoyolabUserInfo> GetInfoAsyncByDiscordId(string dcid ,GameType type)
        {
            bool seen = false , tried = false;
            var keys = cookiesJsonObject.GetKeys();
            foreach(var key in keys)
            {
                var sid = cookiesJsonObject.GetValueOrDefault<string>(key, "service_channel_id");
                var arr = cookiesJsonObject.GetValueOrDefault<JArray>(key,"users");
                for(int i =0;i<arr.Count;i++)
                {
                    var user = arr[i];
                    string id = user.GetValueOrDefault<string>("dc_id");
                    if (id == dcid)
                    {
                        string gameKey = gameInfoMap[type].GameKey;
                        string guid = user.GetValueOrDefault<string>(gameKey);
                        //user not playing this game
                        if (guid == "-1")
                        {
                            seen = true;
                            continue;
                        }
                        var c = user.GetValueOrDefault<JObject>("cookies");
                        string cookie = this.ToCookieString(c);
                        string huid = this.GetHoyoUid(c);
                        var algorithm = this.ToGetInfoAlgorithm(type);
                        var info = await algorithm(cookie,huid,guid);

                        if (info != null)
                        {
                            var broadcast = new HoyolabBroadcastInfo(info)
                            {
                                Status = CheckStatus.Success,
                                DiscordId = dcid,
                                BroadcastChannelId = sid,
                                BroadcastServerId = key
                            };
                            return broadcast;
                        }
                        else
                            tried = true;
                    }
                }

            }

            if (seen)
                return new HoyolabUserInfo() {Status = CheckStatus.UserNotRegister };
            if(tried)
                return new HoyolabUserInfo() { Status = CheckStatus.UnknownError };

            return  new HoyolabUserInfo() { Status = CheckStatus.UserNotFound };
        }

        private string ToCookieString(JObject obj)
        {
             return String.Join("; ",
                    obj.Properties().Select(p => $"{p.Name}={p.Value}")
                );
        }

        private string GetHoyoUid(JObject obj)
        {
            var keys = obj.GetKeys();
            foreach(var key in keys)
            {
                if(key.StartsWith("ltuid"))
                    return obj.GetValueOrDefault<string>(key);
            }
            return "-1";
        }


        //DS
        private static readonly string SALT_OVERSEA = "6s25p5ox5y14umn1p61aqyyvbvvl3lrt";
        private static readonly string TEXT =
            "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

        private static string GenerateDynamicSecret()
        {
            var t = (int)(DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalSeconds;
            var r = GenerateRandomString(6);
            var h = ComputeMD5Hash($"salt={SALT_OVERSEA}&t={t}&r={r}");
            return $"{t},{r},{h}";
        }

        private static string GenerateRandomString(int n)
        {
            var result = "";
            var random = new Random();

            for (var i = 0; i < n; i++)
            {
                result += TEXT[random.Next(TEXT.Length)];
            }

            return result;
        }

        private static string ComputeMD5Hash(string str)
        {
            var md5Byte = MD5.HashData(Encoding.UTF8.GetBytes(str));
            return Convert.ToHexStringLower(md5Byte);
        }


        private async Task<string> SendRequestAsync(string url, string cookie , bool ds = false)
        {
            var http = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
            request.Headers.Add("Cookie",cookie);

            request.Headers.Add("x-rpc-app_version", "1.5.0");
            request.Headers.Add("x-rpc-client_type", "5");
            request.Headers.Add("x-rpc-language", "zh-tw");

            if (ds)
                request.Headers.Add("ds",GenerateDynamicSecret());

            var response = await http.SendAsync(request);
            //response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }

        private async Task<(string,string)> GetPlayerNameRegionAsync(string huid , string guid , string cookie)
        {
            string url = $"https://bbs-api-os.hoyoverse.com/game_record/card/wapi/getGameRecordCard?uid={huid}";
            var response = await SendRequestAsync(url, cookie);
            var json = JsonDocument.Parse(response);

            if (!json.RootElement.TryGetProperty("data", out var root))
            {
                throw new Exception("Failed to get name !");
            }
            var lst = root.GetProperty("list");
            var length = lst.GetArrayLength();

            if (length < 1)
            {
                throw new Exception("No game info !");
            }

            for(int i = 0; i < length; i++)
            {
                var r = lst[i];

                if(r.TryGetProperty("game_role_id",out var pid))
                {
                    string sid = pid.ToString();
                    if(sid == guid)
                    {
                        return (r.GetProperty("nickname").ToString(),r.GetProperty("region").ToString());
                    }
                }
            }

            return ("Unknown","Unknown");
        }

    }
}
