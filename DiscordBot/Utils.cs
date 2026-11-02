using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Logging;
using NetCord.Rest;
using NetCord.Services;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace DiscordBot;

public enum JoinToUserState
{
    NotInGuild, UserNotInVC, BotMoveToVC, BotJoinToVC, AlreadyInSameVC, Unknown
}

public enum JoinState
{
    Success, Fail, Unknown
}

public sealed record JoinResult
{
    public JoinToUserState ToUserState { get; set; } = JoinToUserState.Unknown;
    public JoinState JoinState { get; set; } = JoinState.Unknown;
    public VoiceClient? VC { get; set; }
    public ulong ChannelId { get; set; }
    public Exception? Error { get; set; }

    public override string ToString()
        => $"ToUser: {ToUserState}, Join: {JoinState}, VC: {VC?.GuildId.ToString() ?? "null"}";
}

public static class VoiceManager
{
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Only one join/leave may run per server at a time, so two concurrent commands don't open duplicate connections.</summary>
    private static readonly ConcurrentDictionary<ulong, SemaphoreSlim> _guildLocks = new();

    private static SemaphoreSlim GetLock(ulong guildId) => _guildLocks.GetOrAdd(guildId, _ => new SemaphoreSlim(1, 1));

    public static async Task<JoinResult> JoinToUserVCAsync<T>(T context) where T : IGuildContext, IUserContext
    {
        var guild = context.Guild;
        var result = new JoinResult { JoinState = JoinState.Fail };

        if (guild is null)
        {
            result.ToUserState = JoinToUserState.NotInGuild;
            return result;
        }

        if (!guild.VoiceStates.TryGetValue(context.User.Id, out var userState) || userState.ChannelId is not ulong userChannelId)
        {
            result.ToUserState = JoinToUserState.UserNotInVC;
            return result;
        }

        result.ChannelId = userChannelId;

        guild.VoiceStates.TryGetValue(GlobalVariable.Client.Id, out var botState);
        result.ToUserState = botState?.ChannelId switch
        {
            null => JoinToUserState.BotJoinToVC,
            var id when id == userChannelId => JoinToUserState.AlreadyInSameVC,
            _ => JoinToUserState.BotMoveToVC,
        };

        try
        {
            result.VC = await JoinAsync(guild.Id, userChannelId, reuseIfSameChannel: result.ToUserState == JoinToUserState.AlreadyInSameVC);
            result.JoinState = JoinState.Success;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to join voice channel guild={guild.Id} channel={userChannelId}", ex);
            result.JoinState = JoinState.Fail;
            result.VC = null;
            result.Error = ex;
        }

        return result;
    }

    /// <summary>Joins (or moves to) the given channel and waits for the connection to be Ready.</summary>
    public static async Task<VoiceClient> JoinAsync(ulong guildId, ulong channelId, bool reuseIfSameChannel = true)
    {
        var gate = GetLock(guildId);
        await gate.WaitAsync();
        try
        {
            // Previously, if the bot was already in the channel but had no VoiceClient in the map (e.g. joined via !join), it replied "cannot join".
            if (reuseIfSameChannel
                && GlobalVariable.ServerVoiceClientMap.TryGetValue(guildId, out var existing)
                && existing.Status == WebSocketStatus.Ready)
            {
                return existing;
            }

            var vc = await GlobalVariable.Client.JoinVoiceChannelAsync(guildId, channelId, new VoiceClientConfiguration
            {
                Logger = new ConsoleLogger(),
            });
            await vc.StartAsync();
            await WaitUntilReadyAsync(vc);

            // Previously the old VoiceClient was overwritten without Dispose when switching channels, leaking the websocket/UDP.
            if (GlobalVariable.ServerVoiceClientMap.TryRemove(guildId, out var old) && !ReferenceEquals(old, vc))
                DisposeQuietly(old);

            GlobalVariable.ServerVoiceClientMap[guildId] = vc;
            return vc;
        }
        finally
        {
            gate.Release();
        }
    }

    public static async Task LeaveAsync(ulong guildId)
    {
        if (PlaylistRegistry.Get(guildId) is { } playlist)
            await playlist.StopAsync();

        var gate = GetLock(guildId);
        await gate.WaitAsync();
        try
        {
            await GlobalVariable.Client.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null));

            if (GlobalVariable.ServerVoiceClientMap.TryRemove(guildId, out var vc))
                DisposeQuietly(vc);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task WaitUntilReadyAsync(VoiceClient vc)
    {
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (vc.Status != WebSocketStatus.Ready)
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Voice connection not ready within {ReadyTimeout.TotalSeconds} seconds (current status: {vc.Status})");
            await Task.Delay(100);
        }
    }

    private static void DisposeQuietly(VoiceClient vc)
    {
        try
        {
            vc.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to dispose VoiceClient guild={vc.GuildId}", ex);
        }
    }
}

public static partial class Utils
{
    public static Task DeferResponseAsync<T>(T context, DisplayOption display) where T : IInteractionContext
        => DeferResponseAsync(context.Interaction, display);

    public static async Task DeferResponseAsync(Interaction interaction, DisplayOption display)
        => await interaction.SendResponseAsync(InteractionCallback.DeferredMessage(display.ToEphemeralFlag()));

    [GeneratedRegex(@"^<a?:(?<name>[^:]+):(?<id>\d+)>$")]
    private static partial Regex CustomEmojiRegex();

    /// <summary>Parses a unicode emoji or &lt;:name:id&gt; format; returns null on failure.</summary>
    public static ReactionEmojiProperties? TryCreateEmoji(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        if (!name.Contains(':'))
            return new ReactionEmojiProperties(name);

        var match = CustomEmojiRegex().Match(name);
        if (!match.Success || !ulong.TryParse(match.Groups["id"].Value, out ulong id))
            return null;

        return new ReactionEmojiProperties(match.Groups["name"].Value, id);
    }

    public static string ReactionEmojiToString(ReactionEmojiProperties emoji)
        => emoji.Id is ulong id ? $"<:{emoji.Name}:{id}>" : emoji.Name;

    public static string GenerateHashCode(int length)
    {
        const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        return Random.Shared.GetString(chars, length);
    }

    public static double HybridScoreSimilarity(string input, string target)
    {
        input = input.ToLowerInvariant();
        target = target.ToLowerInvariant();

        double confidence = 0d;

        if (target.Length > 0 && target.StartsWith(input, StringComparison.Ordinal))
            confidence += 1.0d + (double)input.Length / target.Length;

        int index = target.IndexOf(input, StringComparison.Ordinal);
        if (index != -1)
            confidence += Math.Max(0d, 0.8d - (index * 0.01d));

        return 0.5d * confidence + LevenshteinSimilarity(input, target);
    }

    public static double LevenshteinSimilarity(string s1, string s2)
    {
        int len1 = s1.Length;
        int len2 = s2.Length;
        int maxLen = Math.Max(len1, len2);
        if (maxLen == 0) return 1.0;

        // Only two rows are needed; no need to allocate the full (n+1)*(m+1) matrix
        var prev = new int[len2 + 1];
        var curr = new int[len2 + 1];
        for (int j = 0; j <= len2; j++) prev[j] = j;

        for (int i = 1; i <= len1; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= len2; j++)
            {
                int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(prev[j] + 1, curr[j - 1] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }

        return 1.0 - (double)prev[len2] / maxLen;
    }

    /// <summary>[min, max), thread-safe (previously shared a single new Random(), which broke under concurrent calls).</summary>
    public static int RandInt(int min, int max) => Random.Shared.Next(min, max);

    public static string MentionWithID(ulong id) => $"<@{id}>";
}
