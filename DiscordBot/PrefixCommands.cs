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
                    await ReplyAsync($"{GlobalVariable.botNickname}已使用{Utils.ReactionEmojiToString(emoji)}回覆！");
                }
                else
                {
                    await ReplyAsync($"{GlobalVariable.botNickname}用不了這個emoji！");
                }

            }
            else
            {
                await ReplyAsync($"{GlobalVariable.botNickname}找不到該訊息！");
            }
        }
        catch
        {
            await ReplyAsync($"{GlobalVariable.botNickname}找不到該emoji或是其他未知錯誤！");
        }
    }


}


//public class PrefixCommands : ModuleBase<SocketCommandContext>
//{
//    private readonly InteractionService _interactionService;

//    public PrefixCommands(InteractionService interactionService)
//    {
//        _interactionService = interactionService;
//    }

//    [Command("react")]
//    public async Task ReactAsync(ulong messageId, string emoji)
//    {
//        try
//        {
//            var message = await Context.Channel.GetMessageAsync(messageId);
//            if (message is IUserMessage userMessage)
//            {
//                await userMessage.AddReactionAsync(new Emoji(emoji));
//                await ReplyAsync($"{GlobalVariable.botNickname}已使用{emoji}回覆！");
//            }
//            else
//            {
//                await ReplyAsync($"{GlobalVariable.botNickname}找不到該訊息！");
//            }
//        }
//        catch
//        {
//            await ReplyAsync($"{GlobalVariable.botNickname}找不到該emoji或是其他未知錯誤！");
//        }
//    }

//    [Command("join", RunMode = Discord.Commands.RunMode.Async)]
//    public async Task JoinAsync(ulong? channelId = null)
//    {
//        try
//        {
//            if (channelId.HasValue)
//            {
//                try
//                {
//                    var channel = Context.Guild.GetVoiceChannel(channelId.Value);
//                    await channel.ConnectAsync();
//                }
//                catch
//                {
//                    await ReplyAsync($"{GlobalVariable.botNickname}無法加入頻道 !");
//                }
//            }
//            else
//            {
//                await Utils.FromContextJoin(Context);
//            }
//        }
//        catch
//        {
//            await ReplyAsync($"{GlobalVariable.botNickname}遇到未知錯誤！");
//            return;
//        }

//    }

//    [Command("cache history")]
//    public async Task CacheHistory()
//    {
//        LanguageModelCore._cacheChatHistory = !LanguageModelCore._cacheChatHistory;
//        Console.WriteLine($"History cahcing is now {(LanguageModelCore._cacheChatHistory?"enabled":"disabled")} !");
//    }

//    [Command("sync")]
//    public async Task SyncCommands()
//    {
//        await _interactionService.RegisterCommandsGloballyAsync(true);
//        Console.WriteLine($"Slash commands registered , command count : {_interactionService.SlashCommands.Count} !");
//    }


//}
