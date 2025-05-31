using Discord.Commands;
using Discord;
using System.Threading.Tasks;
using Discord.Interactions;

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

    [Command("sync")]
    public async Task SyncCommands()
    {
        await _interactionService.RegisterCommandsGloballyAsync(true);
        Console.WriteLine($"Slash commands registered , command count : {_interactionService.SlashCommands.Count} !");
    }

}
