using Humanizer;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using NetCord;
using NetCord.Gateway;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace DiscordBot
{
    public record HoyolabUserInfo
    {
        public CheckStatus Status { get; set; } = CheckStatus.Unknown;
        public string Name { get; set; } = "Unknown";
        public int CurrentResin { get; set; }
        public int MaxResin { get; set; }
        public TimeSpan RecoveryTime { get; set; }
        public DateTime MaxAt { get; set; }
        public GameType Type { get; set; }
        public HoyolabUserInfo() { }
        public HoyolabUserInfo(HoyolabUserInfo other)
        {
            Status = other.Status;
            Name = other.Name;
            CurrentResin = other.CurrentResin;
            MaxResin = other.MaxResin;
            RecoveryTime = other.RecoveryTime;
            MaxAt = other.MaxAt;
            Type = other.Type;
        }

        public virtual bool IsDailyDone() => false;
        public virtual string GetDailyProgress() => "-1/-1";
    }

    public record HoyolabBroadcastInfo
    {
        public HoyolabUserInfo UserInfo { get; set; }
        public string DiscordId { get; set; } = "";
        public string BroadcastChannelId { get; set; } = "";
        public string BroadcastServerId { get; set; } = "";

        public HoyolabBroadcastInfo(HoyolabUserInfo info) 
        {
            this.UserInfo = info;
        }
    }

    public record GenshinUserInfo : HoyolabUserInfo
    {
        public int MaxTasksCount { get; set; } = 4;
        public int SolvedTasksCount { get; set; } = 0;
        public override bool IsDailyDone() => this.SolvedTasksCount >= this.MaxTasksCount;
        public override string GetDailyProgress() => $"{this.SolvedTasksCount}/{this.MaxTasksCount}";
    }

    public record HsrUserInfo : HoyolabUserInfo
    {
        public int MaxTrainScore { get; set; } = 500;
        public int CurrentTrainScore { get; set; } = 0;

        public override bool IsDailyDone() => this.CurrentTrainScore >= this.MaxTrainScore;
        public override string GetDailyProgress() => $"{this.CurrentTrainScore}/{this.MaxTrainScore}";
    }

    public record ZzzUserInfo : HoyolabUserInfo
    {
        public int MaxVitality { get; set; } = 400;
        public int CurrentVitality { get; set; } = 0;
        public override bool IsDailyDone() => this.CurrentVitality >= this.MaxVitality;
        public override string GetDailyProgress() => $"{this.CurrentVitality}/{this.MaxVitality}";
    }


    public enum GameType
    {
        Genshin, ZenlessZoneZero, HonkaiStarRail
    }

    public enum CheckStatus
    {
        Success, UserNotFound, UserNotRegister, Unknown, UnknownError
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
        private static string cookiesJsonFilePath = ".\\Data\\HoyolabServiceJson.json";
        private JObject cookiesJsonObject;
        private static ConcurrentDictionary<(string huid, string guid), (string name , string region)> userNameRegionCacheMap = new();

        public HoyoLabService()
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
            TimeSpan sec = TimeSpan.FromSeconds(restore);

            int totalTask = root.GetProperty("total_task_num").GetInt32();
            int currentTask = root.GetProperty("finished_task_num").GetInt32();

            return new GenshinUserInfo()
            {
                Name = name,
                CurrentResin = cur,
                MaxResin = max,
                RecoveryTime = sec,
                MaxAt = DateTime.Now + sec,
                MaxTasksCount = totalTask,
                SolvedTasksCount = currentTask,
                Type = GameType.Genshin
            };

        }

        private async Task<HoyolabUserInfo?> GetHonkaiInfoAsync(string cookie, string huid, string guid)
        {
            var (name, region) = await this.GetPlayerNameRegionAsync(huid, guid, cookie);

            string url = $"https://bbs-api-os.hoyolab.com/game_record/hkrpg/api/note?server={region}&role_id={guid}";

            var response = await SendRequestAsync(url, cookie, true);
            var json = JsonDocument.Parse(response);

            if (!json.RootElement.TryGetProperty("data", out var root))
            {
                throw new Exception("Failed to get hsr info !");
            }

            string inf = root.ToString();

            int cur = root.GetProperty("current_stamina").GetInt32();
            int max = root.GetProperty("max_stamina").GetInt32();
            int restore = int.Parse(root.GetProperty("stamina_recover_time").ToString());
            TimeSpan sec = TimeSpan.FromSeconds(restore);

            int maxTrainScore = root.GetProperty("max_train_score").GetInt32();
            int currentTrainScore = root.GetProperty("current_train_score").GetInt32();

            return new HsrUserInfo()
            {
                Name = name,
                CurrentResin = cur,
                MaxResin = max,
                RecoveryTime = sec,
                MaxAt = DateTime.Now + sec,
                MaxTrainScore = maxTrainScore,
                CurrentTrainScore = currentTrainScore , 
                Type = GameType.HonkaiStarRail
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
            TimeSpan sec = TimeSpan.FromSeconds(restore);

            var vitality = root.GetProperty("vitality");
            var maxVitality = vitality.GetProperty("max").GetInt32();
            var currentVitality = vitality.GetProperty("current").GetInt32();

            return new ZzzUserInfo()
            {
                Name = name,
                CurrentResin = cur,
                MaxResin = max,
                RecoveryTime = sec,
                MaxAt = DateTime.Now + sec,
                MaxVitality = maxVitality,
                CurrentVitality = currentVitality,
                Type = GameType.ZenlessZoneZero
            };

        }


        public void LoopCheckResin(GatewayClient client, int batteryThreshold, TimeSpan interval, GameType type)
        {
            Timer t = new Timer(async _ =>
            {
                try
                {
                    var notifyInfos = await this.CheckExceedResin(batteryThreshold, type);

                    if (notifyInfos == null)
                        return;

                    foreach (var notifyInfo in notifyInfos)
                    {
                        var channel = await client.Rest.GetChannelAsync(ulong.Parse(notifyInfo.BroadcastChannelId));
                        if (channel != null)
                        {
                            var info = gameInfoMap[type];
                            var uinfo = notifyInfo.UserInfo;
                            string nickname = uinfo.Name;
                            string notifyString = $"[{info.GameName}]{GlobalVariable.botNickname}偵測到{info.PlayerCallName} {nickname} {Utils.MentionWithID(ulong.Parse(notifyInfo.DiscordId))}";
                            if (uinfo.CurrentResin >= uinfo.MaxResin)
                            {
                                notifyString += "爆體力辣！";
                            }
                            else
                            {
                                notifyString += "的體力快滿了！";
                            }

                            string humanized = uinfo.RecoveryTime.Humanize(2, collectionSeparator: " ");
                            string remainingTime = (uinfo.RecoveryTime >= TimeSpan.Zero) ?
                                $"剩餘 : {humanized}" :
                                $"超出 : {humanized}";

                            notifyString += $"{Environment.NewLine}{uinfo.CurrentResin}/{uinfo.MaxResin}";
                            notifyString += $"{Environment.NewLine}預計滿體力時間 {uinfo.MaxAt:yyyy-MM-dd HH:mm:ss}";
                            notifyString += $"{Environment.NewLine}{remainingTime}";

                            if (channel is TextChannel itc)
                                await itc.SendMessageAsync(notifyString);
                        }
                    }
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[LOOP] Failed to check resin , {e}");
                }

            }, null, TimeSpan.FromSeconds(0), interval);

            GlobalVariable.PermanentTimers.Add(t);
        }

        public void LoopCheckDailyDone(GatewayClient client ,  TimeOnly targetTime , GameType[] types , TimeSpan? interval = null)
        {
            if(interval==null)
                interval = TimeSpan.FromDays(1);

            var now = DateTime.Now;
            var todayTarget = now.Date + targetTime.ToTimeSpan(); 
            var firstDelay = todayTarget > now ? todayTarget - now : todayTarget.AddDays(1) - now;

            var t = new Timer(async _ =>
            {
                try
                {
                    var notifyInfos = new List<HoyolabBroadcastInfo>();

                    foreach (var type in types)
                        notifyInfos.AddRange(await this.CheckUnfinishedDaily(type));

                    if (notifyInfos == null)
                        return;

                    var notifyGroups = notifyInfos.GroupBy(info => (info.DiscordId, info.BroadcastServerId)).ToList();

                    if (notifyGroups == null)
                        return;

                    foreach (var group in notifyGroups)
                    {
                        StringBuilder builder = new StringBuilder();

                        object channel = null ;

                        foreach(var binfo in group)
                        {
                            var uinfo = binfo.UserInfo;
                            var ginfo = gameInfoMap[uinfo.Type];

                            if(channel==null)
                                channel = await client.Rest.GetChannelAsync(ulong.Parse(binfo.BroadcastChannelId));

                            builder.AppendLine($"[{ginfo.GameName}]{GlobalVariable.botNickname}偵測到{ginfo.PlayerCallName} {uinfo.Name} {Utils.MentionWithID(ulong.Parse(binfo.DiscordId))} 仍未完成每日任務 ! " +
                                $"{Environment.NewLine}目前進度 : [{uinfo.GetDailyProgress()}]");
                        }

                        if (channel is TextChannel itc)
                            await itc.SendMessageAsync(builder.ToString());

                    }

                }
                catch (Exception e)
                {
                    Console.WriteLine($"[LOOP] Failed to check resin , {e}");
                }

            }, null, firstDelay, interval.Value);

            GlobalVariable.PermanentTimers.Add(t);
        }


        public async Task<List<HoyolabBroadcastInfo>> CheckExceedResin(int threshold, GameType type)
        {
            return await this.CheckUserInfoCondition(
                type,
                (info) => info.CurrentResin >= threshold
                );
        }

        public async Task<List<HoyolabBroadcastInfo>> CheckUnfinishedDaily(GameType type)
        {
            return await this.CheckUserInfoCondition(
                type, 
                (info) => !info.IsDailyDone()
                );
        }

        public async Task<List<HoyolabBroadcastInfo>> CheckUserInfoCondition(GameType type , Func<HoyolabUserInfo,bool> filter)
        {
            var servers = this.cookiesJsonObject.GetKeys();

            List<HoyolabBroadcastInfo> notifyInfos = new List<HoyolabBroadcastInfo>();

            if (servers == null)
                return notifyInfos;

            if (servers.Count < 1)
                return notifyInfos;

            var algorithm = this.ToGetInfoAlgorithm(type);
            string gameKey = gameInfoMap[type].GameKey;

            foreach (string server in servers)
            {
                string channel = this.cookiesJsonObject.GetValueOrDefault<string>(server, "service_channel_id");
                var users = this.cookiesJsonObject.GetValueOrDefault<JObject>(server, "users");
                var dcIds = users.GetKeys();

                foreach (var dcId in dcIds)
                {
                    var accounts = users.GetValueOrDefault<JArray>(dcId);
                    if (accounts == null)
                        continue;
                    
                    foreach (var account in accounts)
                    {
                        var c = account.GetValueOrDefault<JObject>("cookies");
                        string cookie = ToCookieString(c);
                        string huid = GetHoyoUid(c);
                        var guids = account.GetValueOrDefault<string[]>(gameKey);

                        foreach(var guid in guids)
                        {
                            var info = await algorithm(cookie, huid, guid);
                            if (info != null && filter(info))
                            {
                                var broadcast = new HoyolabBroadcastInfo(info)
                                {
                                    DiscordId = dcId,
                                    BroadcastServerId = server,
                                    BroadcastChannelId = channel
                                };
                                broadcast.UserInfo.Status = CheckStatus.Success;
                                notifyInfos.Add(broadcast);
                            }
                        }
         
                    }
                }
            }

            return notifyInfos;
        }



        public Func<string, string, string, Task<HoyolabUserInfo?>> ToGetInfoAlgorithm(GameType type)
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

        public async Task<IReadOnlyList<HoyolabBroadcastInfo>> GetInfoAsyncByDiscordId(string dcid, GameType type)
        {
            bool seen = false, tried = false;
            var servers = cookiesJsonObject.GetKeys();
            var results = new List<HoyolabBroadcastInfo>();
            var algorithm = this.ToGetInfoAlgorithm(type);
            string gameKey = gameInfoMap[type].GameKey;

            foreach (string server in servers)
            {
                string channel = this.cookiesJsonObject.GetValueOrDefault<string>(server, "service_channel_id");
                var users = this.cookiesJsonObject.GetValueOrDefault<JObject>(server, "users");
                var dcIds = users.GetKeys();

                if(dcIds.Contains(dcid))
                {
                    seen = true;

                    var accounts = users.GetValueOrDefault<JArray>(dcid);

                    foreach (var account in accounts)
                    {
                        var c = account.GetValueOrDefault<JObject>("cookies");
                        string cookie = ToCookieString(c);
                        string huid = GetHoyoUid(c);
                        var guids = account.GetValueOrDefault<string[]>(gameKey);

                        foreach(var guid in guids)
                        {

                            var info = await algorithm(cookie, huid, guid);
                            if (info != null)
                            {
                                var broadcast = new HoyolabBroadcastInfo(info)
                                {
                                    DiscordId = dcid,
                                    BroadcastServerId = server,
                                    BroadcastChannelId = channel
                                };
                                broadcast.UserInfo.Status = CheckStatus.Success;
                                results.Add(broadcast);
                            }
                            else
                                tried = true;
                        }

                    }

                    if (results.Count > 0)
                        return results;
                }

            }

            var invalidUser = new HoyolabUserInfo();

            if (seen)
                invalidUser.Status = CheckStatus.UserNotRegister;
            else if (tried)
                invalidUser.Status = CheckStatus.UnknownError;
            else
                invalidUser.Status = CheckStatus.UserNotFound;

            return new[] { new HoyolabBroadcastInfo(invalidUser) };
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
            foreach (var key in keys)
            {
                if (key.StartsWith("ltuid"))
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

        private async Task<string> SendRequestAsync(string url, string cookie, bool ds = false)
        {
            var http = new HttpClient();
            var request = new HttpRequestMessage(HttpMethod.Get, url);

            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
            request.Headers.Add("Cookie", cookie);

            request.Headers.Add("x-rpc-app_version", "1.5.0");
            request.Headers.Add("x-rpc-client_type", "5");
            request.Headers.Add("x-rpc-language", "zh-tw");

            if (ds)
                request.Headers.Add("ds", GenerateDynamicSecret());

            var response = await http.SendAsync(request);
            //response.EnsureSuccessStatusCode();

            return await response.Content.ReadAsStringAsync();
        }

        private async Task<(string, string)> GetPlayerNameRegionAsync(string huid, string guid, string cookie)
        {
            if(userNameRegionCacheMap.TryGetValue((huid,guid), out var cached))
                return (cached);
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

            for (int i = 0; i < length; i++)
            {
                var r = lst[i];

                if (r.TryGetProperty("game_role_id", out var pid))
                {
                    string sid = pid.ToString();
                    if (sid == guid)
                    {
                        string name = r.GetProperty("nickname").ToString(), region = r.GetProperty("region").ToString();
                        userNameRegionCacheMap[(huid,guid)] = (name, region);
                        return (name,region);
                    }
                }
            }

            return ("Unknown", "Unknown");
        }

    }
}
