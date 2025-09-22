using Discord.Commands;
using Discord;
using System.Threading.Tasks;
using Discord.Interactions;
using DiscordBot;
using Discord.Audio;

public class PrefixCommands : ModuleBase<SocketCommandContext>
{
    private readonly InteractionService _interactionService;

    public PrefixCommands(InteractionService interactionService)
    {
        _interactionService = interactionService;
    }

    [Command("react")]
    public async Task ReactAsync(ulong messageId, string emoji)
    {
        try
        {
            var message = await Context.Channel.GetMessageAsync(messageId);
            if (message is IUserMessage userMessage)
            {
                await userMessage.AddReactionAsync(new Emoji(emoji));
                await ReplyAsync($"小祥已使用{emoji}回覆！");
            }
            else
            {
                await ReplyAsync("小祥找不到該訊息！");
            }
        }
        catch
        {
            await ReplyAsync("小祥找不到該emoji或是其他未知錯誤！");
        }
    }

    [Command("join", RunMode = Discord.Commands.RunMode.Async)]
    public async Task JoinAsync(ulong? channelId = null)
    {
        try
        {
            if (channelId.HasValue)
            {
                try
                {
                    var channel = Context.Guild.GetVoiceChannel(channelId.Value);
                    await channel.ConnectAsync();
                }
                catch
                {
                    await ReplyAsync($"{GlobalVariable.botNickname}無法加入頻道 !");
                }
            }
            else
            {
                await Utils.FromContextJoin(Context);
            }
        }
        catch
        {
            await ReplyAsync($"{GlobalVariable.botNickname}遇到未知錯誤！");
            return;
        }

    }

    [Command("cache history")]
    public async Task CacheHistory()
    {
        LanguageModelCore._cacheChatHistory = !LanguageModelCore._cacheChatHistory;
        Console.WriteLine($"History cahcing is now {(LanguageModelCore._cacheChatHistory?"enabled":"disabled")} !");
    }

    [Command("sync")]
    public async Task SyncCommands()
    {
        await _interactionService.RegisterCommandsGloballyAsync(true);
        Console.WriteLine($"Slash commands registered , command count : {_interactionService.SlashCommands.Count} !");
    }


}
