using Humanizer;
using NetCord;
using NetCord.Gateway;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DiscordBot;

public record HoyolabUserInfo
{
    public CheckStatus Status { get; init; } = CheckStatus.Unknown;
    public string Name { get; init; } = "Unknown";
    public int CurrentResin { get; init; }
    public int MaxResin { get; init; }
    public TimeSpan RecoveryTime { get; init; }
    public DateTime MaxAt { get; init; }
    public GameType Type { get; init; }

    public virtual bool IsDailyDone() => false;
    public virtual string GetDailyProgress() => "-1/-1";
}

public record GenshinUserInfo : HoyolabUserInfo
{
    public int MaxTasksCount { get; init; } = 4;
    public int SolvedTasksCount { get; init; }

    public override bool IsDailyDone() => SolvedTasksCount >= MaxTasksCount;
    public override string GetDailyProgress() => $"{SolvedTasksCount}/{MaxTasksCount}";
}

public record HsrUserInfo : HoyolabUserInfo
{
    public int MaxTrainScore { get; init; } = 500;
    public int CurrentTrainScore { get; init; }

    public override bool IsDailyDone() => CurrentTrainScore >= MaxTrainScore;
    public override string GetDailyProgress() => $"{CurrentTrainScore}/{MaxTrainScore}";
}

public record ZzzUserInfo : HoyolabUserInfo
{
    public int MaxVitality { get; init; } = 400;
    public int CurrentVitality { get; init; }

    public override bool IsDailyDone() => CurrentVitality >= MaxVitality;
    public override string GetDailyProgress() => $"{CurrentVitality}/{MaxVitality}";
}

public record HoyolabBroadcastInfo(HoyolabUserInfo UserInfo)
{
    public string DiscordId { get; init; } = "";
    public string BroadcastChannelId { get; init; } = "";
    public string BroadcastServerId { get; init; } = "";
}

public enum GameType { Genshin, ZenlessZoneZero, HonkaiStarRail }
public enum CheckStatus { Success, UserNotFound, UserNotRegister, Unknown, UnknownError }

public delegate Task<HoyolabUserInfo?> AlgorithmHandler(string cookie, string huid, string guid);


public static class HoyolabNotifier
{
    public static string BuildResinMessage(HoyolabBroadcastInfo broadcast, HoyoLabService.GameMeta meta)
    {
        var u = broadcast.UserInfo;
        var status = u.CurrentResin >= u.MaxResin ? "爆體力辣！" : "的體力快滿了！";
        var humanized = u.RecoveryTime.Humanize(2, collectionSeparator: " ");
        var remainingTime = u.RecoveryTime >= TimeSpan.Zero
            ? $"剩餘 : {humanized}"
            : $"超出 : {humanized}";

        return new StringBuilder()
            .AppendLine($"[{meta.GameName}]{GlobalVariable.BotNickname}偵測到{meta.PlayerCallName} {u.Name} {Utils.MentionWithID(ulong.Parse(broadcast.DiscordId))}{status}")
            .AppendLine($"{u.CurrentResin}/{u.MaxResin}")
            .AppendLine($"預計滿體力時間 {u.MaxAt:yyyy-MM-dd HH:mm:ss}")
            .Append(remainingTime)
            .ToString();
    }

    public static string BuildDailyMessage(IEnumerable<HoyolabBroadcastInfo> group, Func<GameType, HoyoLabService.GameMeta> getMeta)
    {
        var sb = new StringBuilder();
        foreach (var b in group)
        {
            var u = b.UserInfo;
            var meta = getMeta(u.Type);
            sb.AppendLine(
                $"[{meta.GameName}]{GlobalVariable.BotNickname}偵測到{meta.PlayerCallName} {u.Name} " +
                $"{Utils.MentionWithID(ulong.Parse(b.DiscordId))} 仍未完成每日任務！" +
                $"\n目前進度：[{u.GetDailyProgress()}]");
        }
        return sb.ToString();
    }
}


public class HoyoLabService
{
    public static bool IsLoopCheckingResin { get; set; } = true;

    public readonly record struct GameMeta(string GameKey, string GameName, string PlayerCallName);

    private static readonly Dictionary<GameType, GameMeta> _gameMeta = new()
    {
        [GameType.Genshin] = new("gs_id", "原神", "旅行者"),
        [GameType.HonkaiStarRail] = new("hsr_id", "崩鐵", "開拓者"),
        [GameType.ZenlessZoneZero] = new("zzz_id", "絕區零", "繩匠"),
    };

    private static readonly HttpClient _http = new(new HttpClientHandler()
    {
        UseCookies = false
    });
    private static readonly ConcurrentDictionary<(string huid, string guid), (string name, string region)> _nameRegionCache = new();

    private readonly JObject _cookiesJson;

    public HoyoLabService()
    {
        _cookiesJson = Json.Read(".\\Data\\HoyolabServiceJson.json");
    }

    public GameMeta GetMeta(GameType type) => _gameMeta[type];


    public void LoopCheckResin(GatewayClient client, int threshold, TimeSpan interval, GameType type)
    {
        var t = new Timer(async _ =>
        {
            try
            {
                if (!IsLoopCheckingResin) return;

                var hits = await CheckExceedResin(threshold, type);
                var meta = _gameMeta[type];

                foreach (var broadcast in hits)
                {
                    var channel = await client.Rest.GetChannelAsync(ulong.Parse(broadcast.BroadcastChannelId)) as TextChannel;
                    if (channel is not null)
                        await channel.SendMessageAsync(HoyolabNotifier.BuildResinMessage(broadcast, meta));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[LOOP] LoopCheckResin failed: {e}");
            }
        }, null, TimeSpan.Zero, interval);

        GlobalVariable.PermanentTimers.Add(t);
    }

    public void LoopCheckDailyDone(GatewayClient client, TimeOnly targetTime, GameType[] types, TimeSpan? interval = null)
    {
        interval ??= TimeSpan.FromDays(1);

        var now = DateTime.Now;
        var todayTarget = now.Date + targetTime.ToTimeSpan();
        var firstDelay = todayTarget > now ? todayTarget - now : todayTarget.AddDays(1) - now;

        var t = new Timer(async _ =>
        {
            try
            {
                var hits = new List<HoyolabBroadcastInfo>();
                foreach (var type in types)
                    hits.AddRange(await CheckUnfinishedDaily(type));

                var groups = hits.GroupBy(b => (b.DiscordId, b.BroadcastServerId));

                foreach (var group in groups)
                {
                    var first = group.First();
                    var channel = await client.Rest.GetChannelAsync(ulong.Parse(first.BroadcastChannelId)) as TextChannel;
                    if (channel is not null)
                        await channel.SendMessageAsync(HoyolabNotifier.BuildDailyMessage(group, t => _gameMeta[t]));
                }
            }
            catch (Exception e)
            {
                Console.WriteLine($"[LOOP] LoopCheckDailyDone failed: {e}");
            }
        }, null, firstDelay, interval.Value);

        GlobalVariable.PermanentTimers.Add(t);
    }


    public Task<List<HoyolabBroadcastInfo>> CheckExceedResin(int threshold, GameType type)
        => CheckUserInfoCondition(type, info => info.CurrentResin >= threshold);

    public Task<List<HoyolabBroadcastInfo>> CheckUnfinishedDaily(GameType type)
        => CheckUserInfoCondition(type, info => !info.IsDailyDone());

    public async Task<List<HoyolabBroadcastInfo>> CheckUserInfoCondition(
        GameType type, Func<HoyolabUserInfo, bool> filter)
    {
        var results = new List<HoyolabBroadcastInfo>();
        var algorithm = ResolveGameAlgorithm(type);
        var gameKey = _gameMeta[type].GameKey;

        foreach (var (dcId, serverId, channelId, cookie, huid, guid) in EnumerateAccounts(gameKey))
        {
            var info = await algorithm(cookie, huid, guid);
            if (info is not null && filter(info))
                results.Add(MakeBroadcast(info, dcId, serverId, channelId));
        }

        return results;
    }

    public async Task<IReadOnlyList<HoyolabBroadcastInfo>> GetInfoAsyncByDiscordId(string dcid, GameType type)
    {
        if(!_cookiesJson.TryGetValue(dcid , out var userData))
            return new[] { new HoyolabBroadcastInfo(new HoyolabUserInfo { Status = CheckStatus.UserNotFound }) };

        var meta = _gameMeta[type];
        var gameIds = userData.GetValueOrDefault<JArray>(meta.GameKey);

        if(gameIds==null || gameIds.Type==JTokenType.Null || gameIds.Count < 1)
            return new[] { new HoyolabBroadcastInfo(new HoyolabUserInfo { Status = CheckStatus.UserNotRegister }) };

        var results = new List<HoyolabBroadcastInfo>();

        var algorithm = ResolveGameAlgorithm(type);

        foreach (var (serverId, channelId, cookie, huid, guid) in EnumerateAccount(meta.GameKey, dcid))
        {
            var info = await algorithm(cookie:cookie, huid:huid, guid:guid);

            if (info is not null)
            {
                results.Add(MakeBroadcast(info, dcid, serverId, channelId));
            }
        }

        if (results.Count < 1)
            return new[] { new HoyolabBroadcastInfo(new HoyolabUserInfo { Status = CheckStatus.UnknownError }) };

        return results;
    }


    private async Task<HoyolabUserInfo?> GetGenshinInfoAsync(string cookie, string huid, string guid)
    {
        var (name, region) = await GetPlayerNameRegionAsync(huid, guid, cookie);
        var root = await FetchDataAsync(
            $"https://bbs-api-os.hoyolab.com/game_record/genshin/api/dailyNote?server={region}&role_id={guid}",
            cookie, ds: false, errorLabel: "genshin");

        return new GenshinUserInfo
        {
            Name = name,
            CurrentResin = root.GetProperty("current_resin").GetInt32(),
            MaxResin = root.GetProperty("max_resin").GetInt32(),
            RecoveryTime = ParseRecoveryTime(root, "resin_recovery_time"),
            MaxAt = DateTime.Now + ParseRecoveryTime(root, "resin_recovery_time"),
            MaxTasksCount = root.GetProperty("total_task_num").GetInt32(),
            SolvedTasksCount = root.GetProperty("finished_task_num").GetInt32(),
            Type = GameType.Genshin,
        };
    }

    private async Task<HoyolabUserInfo?> GetHonkaiInfoAsync(string cookie, string huid, string guid)
    {
        var (name, region) = await GetPlayerNameRegionAsync(huid, guid, cookie);
        var root = await FetchDataAsync(
            $"https://bbs-api-os.hoyolab.com/game_record/hkrpg/api/note?server={region}&role_id={guid}",
            cookie, ds: true, errorLabel: "hsr");

        var recovery = ParseRecoveryTime(root, "stamina_recover_time");
        return new HsrUserInfo
        {
            Name = name,
            CurrentResin = root.GetProperty("current_stamina").GetInt32(),
            MaxResin = root.GetProperty("max_stamina").GetInt32(),
            RecoveryTime = recovery,
            MaxAt = DateTime.Now + recovery,
            MaxTrainScore = root.GetProperty("max_train_score").GetInt32(),
            CurrentTrainScore = root.GetProperty("current_train_score").GetInt32(),
            Type = GameType.HonkaiStarRail,
        };
    }

    private async Task<HoyolabUserInfo?> GetZzzInfoAsync(string cookie, string huid, string guid)
    {
        var (name, region) = await GetPlayerNameRegionAsync(huid, guid, cookie);
        var root = await FetchDataAsync(
            $"https://sg-public-api.hoyolab.com/event/game_record_zzz/api/zzz/note?server={region}&role_id={guid}",
            cookie, ds: false, errorLabel: "zzz");

        var energy = root.GetProperty("energy");
        var progress = energy.GetProperty("progress");
        var recovery = TimeSpan.FromSeconds(energy.GetProperty("restore").GetInt32());
        var vitality = root.GetProperty("vitality");

        return new ZzzUserInfo
        {
            Name = name,
            CurrentResin = progress.GetProperty("current").GetInt32(),
            MaxResin = progress.GetProperty("max").GetInt32(),
            RecoveryTime = recovery,
            MaxAt = DateTime.Now + recovery,
            MaxVitality = vitality.GetProperty("max").GetInt32(),
            CurrentVitality = vitality.GetProperty("current").GetInt32(),
            Type = GameType.ZenlessZoneZero,
        };
    }


    private async Task<JsonElement> FetchDataAsync(string url, string cookie, bool ds, string errorLabel)
    {
        var response = await SendRequestAsync(url, cookie, ds);
        using var json = JsonDocument.Parse(response);

        if (!json.RootElement.TryGetProperty("data", out var root))
            throw new Exception($"Failed to get {errorLabel} info!");

        return root.Clone();
    }

    private static TimeSpan ParseRecoveryTime(JsonElement root, string key)
        => TimeSpan.FromSeconds(int.Parse(root.GetProperty(key).ToString()));


    private IEnumerable<(string dcId, string serverId, string channelId, string cookie, string huid, string guid)>
        EnumerateAccounts(string gameKey)
    {
        var dcIds = _cookiesJson.GetKeys();

        foreach(var dcId in dcIds)
        foreach(var (serverId , channelId , cookie , huid , guid) in EnumerateAccount(gameKey,dcId))
            yield return (dcId, serverId, channelId, cookie, huid, guid);
    }

    private IEnumerable<(string serverId, string channelId, string cookie, string huid, string guid)> EnumerateAccount(string gameKey ,string dcId)
    {
        if (!_cookiesJson.TryGetValue(dcId, out var userData))
            yield break;

        var guids = userData.GetValueOrDefault<JArray>(gameKey);

        if (guids == null || guids.Type == JTokenType.Null || guids.Count < 1)
            yield break;

        var serverId = userData.GetValueOrDefault<string>("server_id");
        var channelId = userData.GetValueOrDefault<string>("channel_id");   
        var cookies = userData.GetValueOrDefault<JObject>("cookies");
        var cookie = ToCookieString(cookies);
        var huid = GetHoyoUid(cookies);

        foreach (var guid in guids) 
            yield return (serverId,channelId,cookie,huid,guid.ToString());

    }


    private static HoyolabBroadcastInfo MakeBroadcast(
        HoyolabUserInfo info, string dcId, string server, string channel)
        => new(info with { Status = CheckStatus.Success })
        {
            DiscordId = dcId,
            BroadcastServerId = server,
            BroadcastChannelId = channel,
        };

    public AlgorithmHandler ResolveGameAlgorithm(GameType type)
        => type switch
        {
            GameType.Genshin => GetGenshinInfoAsync,
            GameType.HonkaiStarRail => GetHonkaiInfoAsync,
            GameType.ZenlessZoneZero => GetZzzInfoAsync,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };


    private async Task<string> SendRequestAsync(string url, string cookie, bool ds = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
        request.Headers.Add("Cookie", cookie);
        request.Headers.Add("x-rpc-app_version", "1.5.0");
        request.Headers.Add("x-rpc-client_type", "5");
        request.Headers.Add("x-rpc-language", "zh-tw");

        if (ds)
            request.Headers.Add("ds", GenerateDynamicSecret());

        return await (await _http.SendAsync(request)).Content.ReadAsStringAsync();
    }

    private async Task<(string name, string region)> GetPlayerNameRegionAsync(string huid, string guid, string cookie)
    {
        if (_nameRegionCache.TryGetValue((huid, guid), out var cached))
            return cached;

        var root = await FetchDataAsync(
            $"https://bbs-api-os.hoyoverse.com/game_record/card/wapi/getGameRecordCard?uid={huid}",
            cookie, ds: false, errorLabel: "player name/region");

        var list = root.GetProperty("list");
        for (int i = 0; i < list.GetArrayLength(); i++)
        {
            var entry = list[i];
            if (entry.TryGetProperty("game_role_id", out var pid) && pid.ToString() == guid)
            {
                var result = (entry.GetProperty("nickname").ToString(), entry.GetProperty("region").ToString());
                _nameRegionCache[(huid, guid)] = result;
                return result;
            }
        }

        return ("Unknown", "Unknown");
    }


    private static readonly string _saltOversea = "6s25p5ox5y14umn1p61aqyyvbvvl3lrt";
    private static readonly string _chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static string GenerateDynamicSecret()
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string r = new(Enumerable.Range(0, 6).Select(_ => _chars[Random.Shared.Next(_chars.Length)]).ToArray());
        string h = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"salt={_saltOversea}&t={t}&r={r}")));
        return $"{t},{r},{h}";
    }


    private string ToCookieString(JObject obj)
        => string.Join("; ", obj.Properties().Select(p => $"{p.Name}={p.Value}"));

    private string GetHoyoUid(JObject obj)
    {
        foreach (var key in obj.GetKeys())
            if (key.StartsWith("ltuid"))
                return obj.GetValueOrDefault<string>(key);
        return "-1";
    }
}