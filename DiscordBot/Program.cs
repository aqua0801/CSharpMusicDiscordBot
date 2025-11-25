using DiscordBot;
using NetCord;
using NetCord.Gateway;
using NetCord.Logging;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.Commands;
using NetCord.Services.ComponentInteractions;

bool firstTimeReady = true;

GlobalVariable.Init();

GatewayClient client = new(new BotToken(GlobalVariable.botToken), new GatewayClientConfiguration
{
    Logger = new ConsoleLogger(),
    Intents = GatewayIntents.All 
});

var user = await client.Rest.GetCurrentUserAsync();
var services = new CommandService<CommandContext>();
var applicationService = new ApplicationCommandService<ApplicationCommandContext>();
var autocompleteService = new ApplicationCommandService<ApplicationCommandContext,AutocompleteInteractionContext>();
var interactionService = new ComponentInteractionService<ButtonInteractionContext>();

services.AddModules(typeof(Program).Assembly);
applicationService.AddModules(typeof(Program).Assembly);
autocompleteService.AddModules(typeof(Program).Assembly);
interactionService.AddModules(typeof(Program).Assembly);

async Task GetBotInfo()
{
    GlobalVariable.botName = user.Username;
    GlobalVariable.botID = user.Id;
    GlobalVariable.client = client;
    var application = await client.Rest.GetCurrentApplicationAsync();
    GlobalVariable.creatorName = application.Owner.GlobalName;
    GlobalVariable.creatorID = application.Owner.Id;
}
void LoopSetGameAsync()
{
    Timer t = new Timer(async _ =>
    {
        await client.UpdatePresenceAsync(new PresenceProperties(UserStatusType.Online)
        {
            Activities = new[] {
                new UserActivityProperties
                (
                    $"Status", UserActivityType.Custom
                )
                {
                    State = $"{GlobalVariable.botNickname}在{DateTime.Now:HH:mm}負債了{Utils.RandInt(0, 9999)}億！"
                }}
        });   

    }, null, TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(1));
    GlobalVariable.PermanentTimers.Add(t);
}

client.Ready += async (_) =>
{
    if (firstTimeReady)
    {
        Console.WriteLine($"正在初始化參數與自檢測方法...");
        await GetBotInfo();
        await PlaylistSystem.LoopCheckVoiceChannelAndUsers();
        ImageAlgorithm.LoopCheckExpiredCache();
        var a = MediaProcess.DetermineAudioUrlAlgorithm(WebOption.Bilibili);
        if (GlobalVariable.resinLoopCheck)
        {
            await Task.Run(async () =>
            {
                var loopcheck = new HoyoLabService();
                loopcheck.LoopCheckResin(client, 190, TimeSpan.FromMinutes(30), GameType.Genshin);
                await Task.Delay(200);
                loopcheck.LoopCheckResin(client, 280, TimeSpan.FromMinutes(30), GameType.HonkaiStarRail);
                await Task.Delay(200);
                loopcheck.LoopCheckResin(client, 225, TimeSpan.FromMinutes(30), GameType.ZenlessZoneZero);
            });
        }

        LoopSetGameAsync();
        firstTimeReady = false;
        Console.WriteLine($"目前登入 : {user.Username}#{user.Discriminator}");
    }
    else
    {
        Console.WriteLine($"重新登入 : {user.Username}#{user.Discriminator}");
    }
};

client.MessageCreate += async message => 
{
    if (!message.Content.StartsWith(GlobalVariable.commandPrefix) || message.Author.IsBot)
        return;

    if(message.Content == $"{GlobalVariable.commandPrefix}sync")
    {
        await applicationService.RegisterCommandsAsync(client.Rest, client.Id);
        await autocompleteService.RegisterCommandsAsync(client.Rest, client.Id);
        return;
    }

    var context = new CommandContext(message, client);

    var result = await services.ExecuteAsync(prefixLength: 1, context);

    if (result is not IFailResult failResult)
        return;

    Console.WriteLine($"[Error] Command failed : {failResult.Message}");
};

client.InteractionCreate += async interaction =>
{
    if (interaction is ButtonInteraction btnInteraction)
        await ButtonHelper.OnComponentExecuted(btnInteraction);
    else if (interaction is ApplicationCommandInteraction applicationInteraction)
        await applicationService.ExecuteAsync(new ApplicationCommandContext(applicationInteraction, client));
    else if (interaction is AutocompleteInteraction autoInteraction)
        await autocompleteService.ExecuteAutocompleteAsync(new AutocompleteInteractionContext(autoInteraction,client));
};

AppDomain.CurrentDomain.ProcessExit += async (s, e) =>
{
    foreach (var vc in GlobalVariable.serverVoiceClientMap.Values)
    {
        await client.LeaveVoiceChannel(vc.GuildId);
    }
    await client.CloseAsync();
    client.Dispose();
};

await client.StartAsync();
await Task.Delay(-1);