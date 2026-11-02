using NetCord.Rest;
using NetCord.Services.Commands;
using System.Net;

namespace DiscordBot;

public class PrefixCommands : CommandModule<CommandContext>
{
    [Command("join")]
    public async Task Join(ulong? channelId = null)
    {
        if (Context.Guild is not { } guild)
            return;

        if (channelId is not ulong id)
        {
            await ReplyAsync($"用法：{GlobalVariable.Config.CommandPrefix}join <頻道ID>");
            return;
        }

        // Previously join here didn't register in ServerVoiceClientMap, so /yt點歌 later saw "already in the same channel" but had no VoiceClient
        await VoiceManager.JoinAsync(guild.Id, id, reuseIfSameChannel: false);
    }

    [Command("come")]
    public async Task JoinToUser()
    {
        var result = await VoiceManager.JoinToUserVCAsync(Context);

        string? reply = result switch
        {
            { ToUserState: JoinToUserState.NotInGuild } => null,
            { ToUserState: JoinToUserState.UserNotInVC } => "你不在頻道裡喔！",
            { JoinState: not JoinState.Success } => $"{GlobalVariable.BotNickname}無法加入聊天室！{(result.Error is null ? "" : $"（{UserMessages.FromException(result.Error)}）")}",
            _ => null,
        };

        if (reply is not null)
            await ReplyAsync(reply);
    }

    [Command("leave")]
    public async Task Leave()
    {
        if (Context.Guild is { } guild)
            await VoiceManager.LeaveAsync(guild.Id);
    }

    [Command("react")]
    public async Task ReactAsync(ulong messageId, string emojiName)
    {
        var emoji = Utils.TryCreateEmoji(emojiName);
        if (emoji is null)
        {
            await ReplyAsync($"{GlobalVariable.BotNickname}用不了這個emoji！");
            return;
        }

        try
        {
            // Context.Channel is null when the channel isn't cached; going through REST directly is more reliable
            await Context.Client.Rest.AddMessageReactionAsync(Context.Message.ChannelId, messageId, emoji);
            await ReplyAsync($"{GlobalVariable.BotNickname}已使用{Utils.ReactionEmojiToString(emoji)}回覆！");
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            await ReplyAsync($"{GlobalVariable.BotNickname}找不到該訊息或emoji！");
        }
        catch (RestException ex)
        {
            Log.Warn($"!react failed message={messageId} emoji={emojiName}", ex);
            await ReplyAsync($"{GlobalVariable.BotNickname}加反應失敗：{UserMessages.FromException(ex)}");
        }
    }

    [Command("silent")]
    public async Task SilentAsync()
    {
        HoyoLabService.IsLoopCheckingResin = !HoyoLabService.IsLoopCheckingResin;

        await ReplyAsync(HoyoLabService.IsLoopCheckingResin
            ? $"{GlobalVariable.BotNickname}已開啟自動檢測樹脂功能！"
            : $"{GlobalVariable.BotNickname}已關閉自動檢測樹脂功能！");
    }
}
