using DiscordBot;
using NetCord;
using NetCord.Gateway;
using NetCord.Logging;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using NetCord.Services.Commands;

try
{
    GlobalVariable.Init();
}
catch (Exception ex)
{
    Log.Error($"Initialization failed, please check the config files under {Path.GetFullPath(GlobalVariable.DataFolderPath)}", ex);
    return 1;
}

var config = GlobalVariable.Config;

GatewayClient client = new(new BotToken(config.BotToken), new GatewayClientConfiguration
{
    Logger = new ConsoleLogger(),
    Intents = GatewayIntents.All,
});
GlobalVariable.Client = client;

var commandService = new CommandService<CommandContext>();
var applicationService = new ApplicationCommandService<ApplicationCommandContext>();
var autocompleteService = new ApplicationCommandService<ApplicationCommandContext, AutocompleteInteractionContext>();

commandService.AddModules(typeof(Program).Assembly);
applicationService.AddModules(typeof(Program).Assembly);
autocompleteService.AddModules(typeof(Program).Assembly);

int readyCount = 0;

client.Ready += async _ =>
{
    if (Interlocked.Increment(ref readyCount) > 1)
    {
        Log.Info($"Reconnected: {GlobalVariable.BotName}");
        return;
    }

    Log.Info("Initializing settings and self-check routines...");

    var user = await client.Rest.GetCurrentUserAsync();
    var application = await client.Rest.GetCurrentApplicationAsync();
    GlobalVariable.BotName = user.Username;
    GlobalVariable.BotId = user.Id;
    GlobalVariable.CreatorName = application.Owner?.GlobalName ?? application.Owner?.Username ?? "Unknown";
    GlobalVariable.CreatorId = application.Owner?.Id ?? 0;

    ImageAlgorithm.StartCacheCleanup();

    if (config.ResinLoopCheck)
    {
        var hoyo = GlobalVariable.HoyoLab;
        hoyo.StartResinLoop(client, 180, TimeSpan.FromMinutes(60), GameType.Genshin, TimeSpan.Zero);
        hoyo.StartResinLoop(client, 270, TimeSpan.FromMinutes(60), GameType.HonkaiStarRail, TimeSpan.FromMilliseconds(200));
        hoyo.StartResinLoop(client, 220, TimeSpan.FromMinutes(60), GameType.ZenlessZoneZero, TimeSpan.FromMilliseconds(400));
        hoyo.StartDailyLoop(client, new TimeOnly(0, 0), [GameType.Genshin, GameType.HonkaiStarRail, GameType.ZenlessZoneZero]);
    }

    BackgroundLoop.RunPeriodic("Status update", TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1), async ct =>
    {
        await client.UpdatePresenceAsync(new PresenceProperties(UserStatusType.Online)
        {
            Activities =
            [
                new UserActivityProperties("Status", UserActivityType.Custom)
                {
                    State = $"{GlobalVariable.BotNickname}在{DateTime.Now:HH:mm}負債了{Utils.RandInt(0, 9999)}億！",
                },
            ],
        }, cancellationToken: ct);
    });

    await EnsureYtDlpAsync();

    Log.Info($"Logged in as: {user.Username} (owner: {GlobalVariable.CreatorName})");
};

client.MessageCreate += async message =>
{
    if (message.Author.IsBot || !message.Content.StartsWith(config.CommandPrefix))
        return;

    if (message.Content == $"{config.CommandPrefix}sync")
    {
        if (message.Author.Id != GlobalVariable.CreatorId)
            return;

        await applicationService.RegisterCommandsAsync(client.Rest, client.Id);
        await autocompleteService.RegisterCommandsAsync(client.Rest, client.Id);
        Log.Info("Slash commands re-registered");
        await message.ReplyAsync("已同步 slash commands！");
        return;
    }

    var result = await commandService.ExecuteAsync(prefixLength: 1, new CommandContext(message, client));

    switch (result)
    {
        case IExceptionResult ex:
            Log.Error($"Prefix command failed: {message.Content}", ex.Exception);
            await message.ReplyAsync($"嗚嗚嗚{GlobalVariable.BotNickname}遇到錯誤了：{UserMessages.FromException(ex.Exception)}");
            break;
        case NotFoundResult:
            break;
        case IFailResult fail:
            Log.Info($"Prefix command failed: {message.Content} → {fail.Message}");
            await message.ReplyAsync(UserMessages.FromFailResult(fail));
            break;
    }
};

client.InteractionCreate += async interaction =>
{
    switch (interaction)
    {
        case ButtonInteraction button:
            try
            {
                await ButtonHelper.HandleAsync(button);
            }
            catch (Exception ex)
            {
                Log.Error($"Button handling failed: {button.Data.CustomId}", ex);
            }
            break;

        case ApplicationCommandInteraction command:

            var commandResult = await applicationService.ExecuteAsync(new ApplicationCommandContext(command, client));
            if (commandResult is IExceptionResult commandEx)
            {
                Log.Error($"/{command.Data.Name} failed (user={command.User.Id} guild={command.GuildId})", commandEx.Exception);
                await command.TryReportErrorAsync($"嗚嗚嗚{GlobalVariable.BotNickname}遇到錯誤了：{UserMessages.FromException(commandEx.Exception)}");
            }
            else if (commandResult is IFailResult commandFail)
            {
                Log.Warn($"/{command.Data.Name} failed: {commandFail.Message}");
                await command.TryReportErrorAsync(UserMessages.FromFailResult(commandFail));
            }
            break;

        case AutocompleteInteraction autocomplete:
            var autoResult = await autocompleteService.ExecuteAutocompleteAsync(new AutocompleteInteractionContext(autocomplete, client));
            if (autoResult is IExceptionResult autoEx)
                Log.Error($"/{autocomplete.Data.Name} autocomplete failed", autoEx.Exception);
            else if (autoResult is IFailResult autoFail)
                Log.Warn($"/{autocomplete.Data.Name} autocomplete failed: {autoFail.Message}");
            break;
    }
};


var shutdownRequested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var cleanupDone = new ManualResetEventSlim(); 

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdownRequested.TrySetResult();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) =>
{
    shutdownRequested.TrySetResult();
    cleanupDone.Wait(TimeSpan.FromSeconds(5));
};

await client.StartAsync();
await shutdownRequested.Task;

Log.Info("Shutting down...");
await ShutdownAsync();
cleanupDone.Set();
return 0;

async Task ShutdownAsync()
{
    BackgroundLoop.StopAll();

    await RunQuietly("Save playlists", () =>
    {
        GlobalVariable.SavedPlaylists.SaveIfDirty();
        return Task.CompletedTask;
    });
    await RunQuietly("Stop playback", PlaylistRegistry.StopAllAsync);

    foreach (var guildId in GlobalVariable.ServerVoiceClientMap.Keys.ToList())
        await RunQuietly($"Leave voice channel {guildId}", () => VoiceManager.LeaveAsync(guildId));

    await RunQuietly("Close gateway", async () => await client.CloseAsync());
    client.Dispose();
}

static async Task RunQuietly(string name, Func<Task> action)
{
    try
    {
        await action();
    }
    catch (Exception ex)
    {
        Log.Warn($"Shutdown step '{name}' failed", ex);
    }
}

static async Task EnsureYtDlpAsync()
{
    try
    {
        var ytdl = new YoutubeDLSharp.YoutubeDL { YoutubeDLPath = GlobalVariable.YtDlpExePath };
        Log.Info($"yt-dlp version: {ytdl.Version}");
    }
    catch (Exception ex)
    {
        Log.Warn($"yt-dlp invalid or missing ({ex.Message}), downloading automatically...");
        try
        {
            await YoutubeDLSharp.Utils.DownloadYtDlp();
            Log.Info("yt-dlp downloaded");
        }
        catch (Exception downloadEx)
        {
            Log.Error("yt-dlp auto-download failed, YouTube features will be unavailable", downloadEx);
        }
    }
}
