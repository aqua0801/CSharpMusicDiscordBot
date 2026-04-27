using DiscordBot;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Gateway.Voice.Encryption;
using NetCord.Logging;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.Commands;

public class PrefixCommands : CommandModule<CommandContext>
{
    [Command("join")]
    public async Task Join(ulong? channelID = null )
    {
        if (channelID.HasValue)
        {
            var vc = await GlobalVariable.client.JoinVoiceChannelAsync
                (
                    Context.Guild.Id,
                    channelID.Value,
                    new VoiceClientConfiguration()
                    {
                        Logger = new ConsoleLogger(),
                    }
                );
            await vc.StartAsync();
        }
    }

    [Command("come")]
    public async Task JoinToUser()
    {
        var result = await Utils.JoinToUserVC(Context);
    }

    [Command("leave")]
    public async Task Leave()
    {
        await GlobalVariable.client.LeaveVoiceChannel(Context.Guild.Id);
    }

    [Command("react")]
    public async Task ReactAsync(ulong messageId, string emojiName)
    {
        try
        {
            var message = await Context.Channel.GetMessageAsync(messageId);
            
            if (message !=null)
            {
                var emoji = Utils.TryCreateEmoji(emojiName);

                if (emoji != null)
                {
                    await message.AddReactionAsync(emoji);
                    await ReplyAsync($"{GlobalVariable.BotNickname}已使用{Utils.ReactionEmojiToString(emoji)}回覆！");
                }
                else
                {
                    await ReplyAsync($"{GlobalVariable.BotNickname}用不了這個emoji！");
                }

            }
            else
            {
                await ReplyAsync($"{GlobalVariable.BotNickname}找不到該訊息！");
            }
        }
        catch
        {
            await ReplyAsync($"{GlobalVariable.BotNickname}找不到該emoji或是其他未知錯誤！");
        }
    }

    [Command("silent")]
    public async Task SilentAsync()
    {
        HoyoLabService.IsLoopCheckingResin = !HoyoLabService.IsLoopCheckingResin;

        if (HoyoLabService.IsLoopCheckingResin)
        {
            await ReplyAsync($"{GlobalVariable.BotNickname}已開啟自動檢測樹脂功能！");
        }
        else
        {
            await ReplyAsync($"{GlobalVariable.BotNickname}已關閉自動檢測樹脂功能！");
        }
    }
}

