using Humanizer;
using NetCord.Gateway;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace DiscordBot;

// ── Public data models ───────────────────────────────────────────

public record HoyolabUserInfo
{
    public CheckStatus Status { get; init; } = CheckStatus.Unknown;
    public string Name { get; init; } = "未知";
    public int CurrentResin { get; init; }
    public int MaxResin { get; init; }
    public TimeSpan RecoveryTime { get; init; }
    public DateTime MaxAt { get; init; }
    public GameType Type { get; init; }

    /// <summary>Reason when Status is UnknownError (e.g. expired cookie); can be shown directly to the user.</summary>
    public string? ErrorMessage { get; init; }

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
    public ulong DiscordId { get; init; }
    public ulong BroadcastChannelId { get; init; }
    public ulong BroadcastServerId { get; init; }
}

public enum GameType { Genshin, ZenlessZoneZero, HonkaiStarRail }
public enum CheckStatus { Success, UserNotFound, UserNotRegister, Unknown, UnknownError }

/// <summary>HoYoLAB API returned retcode != 0 (expired cookie, risk control, ...).</summary>
public sealed class HoyolabApiException(int retcode, string? apiMessage, string url)
    : Exception($"HoYoLAB API error retcode={retcode} message={apiMessage} @ {url}")
{
    public int RetCode { get; } = retcode;
    public string? ApiMessage { get; } = apiMessage;
}

// ── Config file models (Data/HoyolabServiceJson.json) ────────────

/// <summary>File format: { "&lt;discord id&gt;": HoyolabAccount, ... }</summary>
public sealed record HoyolabAccount
{
    [JsonPropertyName("server_id")] public string? ServerId { get; init; }
    [JsonPropertyName("channel_id")] public string? ChannelId { get; init; }
    [JsonPropertyName("cookies")] public Dictionary<string, string> Cookies { get; init; } = new();
    [JsonPropertyName("gs_id")] public List<string>? GenshinIds { get; init; }
    [JsonPropertyName("hsr_id")] public List<string>? HsrIds { get; init; }
    [JsonPropertyName("zzz_id")] public List<string>? ZzzIds { get; init; }

    public IReadOnlyList<string> GetGameIds(GameType type) => (type switch
    {
        GameType.Genshin => GenshinIds,
        GameType.HonkaiStarRail => HsrIds,
        GameType.ZenlessZoneZero => ZzzIds,
        _ => null,
    }) ?? new List<string>();

    public string CookieHeader => string.Join("; ", Cookies.Select(kv => $"{kv.Key}={kv.Value}"));

    /// <summary>HoYoLAB account uid (ltuid / ltuid_v2).</summary>
    public string? HoyoUid => Cookies.FirstOrDefault(kv => kv.Key.StartsWith("ltuid", StringComparison.Ordinal)).Value;
}

// ── Message formatting ───────────────────────────────────────────

public static class HoyolabNotifier
{
    public static string BuildResinMessage(HoyolabBroadcastInfo broadcast, HoyoLabService.GameMeta meta)
    {
        var u = broadcast.UserInfo;
        var status = u.CurrentResin >= u.MaxResin ? "爆體力辣！" : "的體力快滿了！";

        return new StringBuilder()
            .AppendLine($"[{meta.GameName}]{GlobalVariable.BotNickname}偵測到{meta.PlayerCallName} {u.Name} {Utils.MentionWithID(broadcast.DiscordId)}{status}")
            .AppendLine($"{u.CurrentResin}/{u.MaxResin}")
            .AppendLine($"預計滿體力時間 {u.MaxAt:yyyy-MM-dd HH:mm:ss}")
            .Append(FormatRemaining(u.RecoveryTime))
            .ToString();
    }

    public static string BuildDailyMessage(IEnumerable<HoyolabBroadcastInfo> group)
    {
        var sb = new StringBuilder();
        foreach (var b in group)
        {
            var u = b.UserInfo;
            var meta = HoyoLabService.GetMeta(u.Type);
            sb.AppendLine(
                $"[{meta.GameName}]{GlobalVariable.BotNickname}偵測到{meta.PlayerCallName} {u.Name} " +
                $"{Utils.MentionWithID(b.DiscordId)} 仍未完成每日任務！" +
                $"\n目前進度：[{u.GetDailyProgress()}]");
        }
        return sb.ToString();
    }

    public static string FormatRemaining(TimeSpan recovery)
    {
        var humanized = recovery.Duration().Humanize(2, collectionSeparator: " ");
        return recovery >= TimeSpan.Zero ? $"剩餘 : {humanized}" : $"超出 : {humanized}";
    }
}

// ── Service ──────────────────────────────────────────────────────

public sealed class HoyoLabService
{
    /// <summary>Toggle switched by !silent.</summary>
    public static volatile bool IsLoopCheckingResin = true;

    public readonly record struct GameMeta(string GameName, string PlayerCallName);

    private static readonly Dictionary<GameType, GameMeta> _gameMeta = new()
    {
        [GameType.Genshin] = new("原神", "旅行者"),
        [GameType.HonkaiStarRail] = new("崩鐵", "開拓者"),
        [GameType.ZenlessZoneZero] = new("絕區零", "繩匠"),
    };

    public static GameMeta GetMeta(GameType type) => _gameMeta[type];

    private static readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(20),
    };

    private static readonly ConcurrentDictionary<(string huid, string guid), (string name, string region)> _nameRegionCache = new();

    private readonly IReadOnlyDictionary<string, HoyolabAccount> _accounts;

    private HoyoLabService(IReadOnlyDictionary<string, HoyolabAccount> accounts) => _accounts = accounts;

    /// <summary>
    /// Loads the account file; a missing file means no accounts (stamina features disabled), and format errors log the JSON location.
    /// This ctor used to run in GlobalVariable's static initializer, so a missing file killed the whole bot with TypeInitializationException.
    /// </summary>
    public static HoyoLabService LoadOrEmpty(string path)
    {
        try
        {
            var accounts = Json.Read<Dictionary<string, HoyolabAccount>>(path);
            Log.Info($"Loaded {accounts.Count} HoYoLAB account(s)");
            return new HoyoLabService(accounts);
        }
        catch (FileNotFoundException)
        {
            Log.Warn($"{Path.GetFullPath(path)} not found, HoYoLAB features disabled");
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to read {Path.GetFullPath(path)}, HoYoLAB features disabled", ex);
        }
        return new HoyoLabService(new Dictionary<string, HoyolabAccount>());
    }

    // ── Background checks ────────────────────────────────────────

    public void StartResinLoop(GatewayClient client, int threshold, TimeSpan interval, GameType type, TimeSpan initialDelay)
    {
        var meta = _gameMeta[type];
        BackgroundLoop.RunPeriodic($"Stamina check - {type}", interval, initialDelay, async ct =>
        {
            if (!IsLoopCheckingResin) return;

            foreach (var broadcast in await CollectAsync(type, info => info.CurrentResin >= threshold))
                await TrySendAsync(client, broadcast.BroadcastChannelId, HoyolabNotifier.BuildResinMessage(broadcast, meta));
        });
    }

    public void StartDailyLoop(GatewayClient client, TimeOnly at, IReadOnlyList<GameType> types)
    {
        BackgroundLoop.RunDaily("Daily commission check", at, async ct =>
        {
            var hits = new List<HoyolabBroadcastInfo>();
            foreach (var type in types)
                hits.AddRange(await CollectAsync(type, info => !info.IsDailyDone()));

            foreach (var group in hits.GroupBy(b => (b.DiscordId, b.BroadcastServerId)))
                await TrySendAsync(client, group.First().BroadcastChannelId, HoyolabNotifier.BuildDailyMessage(group));
        });
    }

    private static async Task TrySendAsync(GatewayClient client, ulong channelId, string message)
    {
        if (channelId == 0)
        {
            Log.Warn("HoYoLAB account has no channel_id set, skipping notification");
            return;
        }

        try
        {
            await client.Rest.SendMessageAsync(channelId, message);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to send HoYoLAB notification to channel {channelId}", ex);
        }
    }

    /// <summary>
    /// Queries and filters all accounts. A single account failure is logged without aborting
    /// (previously one expired cookie blew up the whole round, including everyone else's notifications).
    /// </summary>
    private async Task<List<HoyolabBroadcastInfo>> CollectAsync(GameType type, Func<HoyolabUserInfo, bool> filter)
    {
        var results = new List<HoyolabBroadcastInfo>();

        foreach (var (dcId, account) in _accounts)
        {
            foreach (var guid in account.GetGameIds(type))
            {
                try
                {
                    var info = await FetchAsync(type, account, guid);
                    if (filter(info))
                        results.Add(MakeBroadcast(info, dcId, account));
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to query {type} discord={dcId} uid={guid}", ex);
                }
            }
        }

        return results;
    }

    // ── Queries for commands ─────────────────────────────────────

    public async Task<IReadOnlyList<HoyolabBroadcastInfo>> GetInfoByDiscordIdAsync(ulong discordId, GameType type)
    {
        string dcId = discordId.ToString();
        if (!_accounts.TryGetValue(dcId, out var account))
            return [new HoyolabBroadcastInfo(new HoyolabUserInfo { Status = CheckStatus.UserNotFound, Type = type })];

        var guids = account.GetGameIds(type);
        if (guids.Count == 0)
            return [new HoyolabBroadcastInfo(new HoyolabUserInfo { Status = CheckStatus.UserNotRegister, Type = type })];

        var results = new List<HoyolabBroadcastInfo>();
        foreach (var guid in guids)
        {
            try
            {
                results.Add(MakeBroadcast(await FetchAsync(type, account, guid), dcId, account));
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to query {type} discord={dcId} uid={guid}", ex);
                string reason = UserMessages.FromException(ex);
                results.Add(new HoyolabBroadcastInfo(new HoyolabUserInfo
                {
                    Status = CheckStatus.UnknownError,
                    Type = type,
                    Name = guid,
                    ErrorMessage = reason,
                }));
            }
        }
        return results;
    }

    private static HoyolabBroadcastInfo MakeBroadcast(HoyolabUserInfo info, string dcId, HoyolabAccount account)
        => new(info with { Status = CheckStatus.Success })
        {
            DiscordId = ulong.TryParse(dcId, out var d) ? d : 0,
            BroadcastServerId = ulong.TryParse(account.ServerId, out var s) ? s : 0,
            BroadcastChannelId = ulong.TryParse(account.ChannelId, out var c) ? c : 0,
        };

    // ── Per-game APIs ────────────────────────────────────────────

    private Task<HoyolabUserInfo> FetchAsync(GameType type, HoyolabAccount account, string guid)
    {
        string cookie = account.CookieHeader;
        string huid = account.HoyoUid ?? throw new UserFacingException("ltuid / ltuid_v2 not found in cookies", "帳號設定的 cookie 缺少 ltuid，請重新設定");

        return type switch
        {
            GameType.Genshin => GetGenshinInfoAsync(cookie, huid, guid),
            GameType.HonkaiStarRail => GetHonkaiInfoAsync(cookie, huid, guid),
            GameType.ZenlessZoneZero => GetZzzInfoAsync(cookie, huid, guid),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };
    }

    private async Task<HoyolabUserInfo> GetGenshinInfoAsync(string cookie, string huid, string guid)
    {
        var (name, region) = await GetPlayerNameRegionAsync(huid, guid, cookie);
        var note = await FetchDataAsync<GenshinNote>(
            $"https://bbs-api-os.hoyolab.com/game_record/genshin/api/dailyNote?server={region}&role_id={guid}",
            cookie, ds: false);

        var recovery = TimeSpan.FromSeconds(note.ResinRecoveryTime);
        return new GenshinUserInfo
        {
            Name = name,
            CurrentResin = note.CurrentResin,
            MaxResin = note.MaxResin,
            RecoveryTime = recovery,
            MaxAt = DateTime.Now + recovery,
            MaxTasksCount = note.TotalTaskNum,
            SolvedTasksCount = note.FinishedTaskNum,
            Type = GameType.Genshin,
        };
    }

    private async Task<HoyolabUserInfo> GetHonkaiInfoAsync(string cookie, string huid, string guid)
    {
        var (name, region) = await GetPlayerNameRegionAsync(huid, guid, cookie);
        var note = await FetchDataAsync<HsrNote>(
            $"https://bbs-api-os.hoyolab.com/game_record/hkrpg/api/note?server={region}&role_id={guid}",
            cookie, ds: true);

        var recovery = TimeSpan.FromSeconds(note.StaminaRecoverTime);
        return new HsrUserInfo
        {
            Name = name,
            CurrentResin = note.CurrentStamina,
            MaxResin = note.MaxStamina,
            RecoveryTime = recovery,
            MaxAt = DateTime.Now + recovery,
            MaxTrainScore = note.MaxTrainScore,
            CurrentTrainScore = note.CurrentTrainScore,
            Type = GameType.HonkaiStarRail,
        };
    }

    private async Task<HoyolabUserInfo> GetZzzInfoAsync(string cookie, string huid, string guid)
    {
        var (name, region) = await GetPlayerNameRegionAsync(huid, guid, cookie);
        var note = await FetchDataAsync<ZzzNote>(
            $"https://sg-public-api.hoyolab.com/event/game_record_zzz/api/zzz/note?server={region}&role_id={guid}",
            cookie, ds: false);

        var energy = note.Energy ?? throw new InvalidDataException("ZZZ note is missing energy");
        var vitality = note.Vitality ?? throw new InvalidDataException("ZZZ note is missing vitality");
        var recovery = TimeSpan.FromSeconds(energy.Restore);

        return new ZzzUserInfo
        {
            Name = name,
            CurrentResin = energy.Progress?.Current ?? 0,
            MaxResin = energy.Progress?.Max ?? 0,
            RecoveryTime = recovery,
            MaxAt = DateTime.Now + recovery,
            MaxVitality = vitality.Max,
            CurrentVitality = vitality.Current,
            Type = GameType.ZenlessZoneZero,
        };
    }

    private async Task<(string name, string region)> GetPlayerNameRegionAsync(string huid, string guid, string cookie)
    {
        if (_nameRegionCache.TryGetValue((huid, guid), out var cached))
            return cached;

        var card = await FetchDataAsync<GameRecordCard>(
            $"https://bbs-api-os.hoyoverse.com/game_record/card/wapi/getGameRecordCard?uid={huid}",
            cookie, ds: false);

        var entry = card.List.FirstOrDefault(e => e.GameRoleId == guid)
            ?? throw new UserFacingException(
                $"Game uid {guid} not found in the record card of HoYoLAB account {huid} (record may be private or the uid is wrong)",
                $"找不到遊戲 uid {guid}，可能是戰績未公開或 uid 填錯");

        var result = (entry.Nickname ?? "未知", entry.Region ?? throw new InvalidDataException($"uid {guid} has no region"));
        _nameRegionCache[(huid, guid)] = result;
        return result;
    }

    // ── HTTP ─────────────────────────────────────────────────────

    private static async Task<T> FetchDataAsync<T>(string url, string cookie, bool ds) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36");
        request.Headers.TryAddWithoutValidation("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("x-rpc-app_version", "1.5.0");
        request.Headers.TryAddWithoutValidation("x-rpc-client_type", "5");
        request.Headers.TryAddWithoutValidation("x-rpc-language", "zh-tw");
        if (ds)
            request.Headers.TryAddWithoutValidation("ds", GenerateDynamicSecret());

        using var response = await _http.SendAsync(request);
        string body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)response.StatusCode} @ {url}{Environment.NewLine}{Json.Truncate(body, 300)}", null, response.StatusCode);

        var envelope = Json.Deserialize<HoyoResponse<T>>(body, url);
        if (envelope.RetCode != 0)
            throw new HoyolabApiException(envelope.RetCode, envelope.Message, url);

        return envelope.Data ?? throw new InvalidDataException($"HoYoLAB response has no data @ {url}");
    }

    private const string SaltOversea = "6s25p5ox5y14umn1p61aqyyvbvvl3lrt";
    private const string DsChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    private static string GenerateDynamicSecret()
    {
        long t = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string r = Random.Shared.GetString(DsChars, 6);
        string h = Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes($"salt={SaltOversea}&t={t}&r={r}")));
        return $"{t},{r},{h}";
    }

    // ── DTO ──────────────────────────────────────────────────────

    private sealed record HoyoResponse<T>
    {
        [JsonPropertyName("retcode")] public int RetCode { get; init; }
        [JsonPropertyName("message")] public string? Message { get; init; }
        [JsonPropertyName("data")] public T? Data { get; init; }
    }

    private sealed record GameRecordCard
    {
        [JsonPropertyName("list")] public List<GameRecordEntry> List { get; init; } = [];
    }

    private sealed record GameRecordEntry
    {
        [JsonPropertyName("game_role_id")] public string? GameRoleId { get; init; }
        [JsonPropertyName("nickname")] public string? Nickname { get; init; }
        [JsonPropertyName("region")] public string? Region { get; init; }
    }

    // resin_recovery_time is a string "12345" in the Genshin API; handled by NumberHandling.AllowReadingFromString
    private sealed record GenshinNote
    {
        [JsonPropertyName("current_resin")] public int CurrentResin { get; init; }
        [JsonPropertyName("max_resin")] public int MaxResin { get; init; }
        [JsonPropertyName("resin_recovery_time")] public long ResinRecoveryTime { get; init; }
        [JsonPropertyName("total_task_num")] public int TotalTaskNum { get; init; }
        [JsonPropertyName("finished_task_num")] public int FinishedTaskNum { get; init; }
    }

    private sealed record HsrNote
    {
        [JsonPropertyName("current_stamina")] public int CurrentStamina { get; init; }
        [JsonPropertyName("max_stamina")] public int MaxStamina { get; init; }
        [JsonPropertyName("stamina_recover_time")] public long StaminaRecoverTime { get; init; }
        [JsonPropertyName("current_train_score")] public int CurrentTrainScore { get; init; }
        [JsonPropertyName("max_train_score")] public int MaxTrainScore { get; init; }
    }

    private sealed record ZzzNote
    {
        [JsonPropertyName("energy")] public ZzzEnergy? Energy { get; init; }
        [JsonPropertyName("vitality")] public ZzzProgress? Vitality { get; init; }
    }

    private sealed record ZzzEnergy
    {
        [JsonPropertyName("progress")] public ZzzProgress? Progress { get; init; }
        [JsonPropertyName("restore")] public long Restore { get; init; }
    }

    private sealed record ZzzProgress
    {
        [JsonPropertyName("current")] public int Current { get; init; }
        [JsonPropertyName("max")] public int Max { get; init; }
    }
}
