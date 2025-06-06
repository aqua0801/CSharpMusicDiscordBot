﻿using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using DiscordBot;
using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;

Console.InputEncoding = System.Text.Encoding.UTF8;
Console.OutputEncoding = System.Text.Encoding.UTF8;

var config = new DiscordSocketConfig
{
    GatewayIntents = GatewayIntents.AllUnprivileged | GatewayIntents.MessageContent,
    LogLevel = LogSeverity.Info
};

var client = new DiscordSocketClient(config);
var commands = new CommandService();
var interactions = new InteractionService(client.Rest);
client.ButtonExecuted += ButtonHelper.OnComponentExecuted;

var services = new ServiceCollection()
    .AddSingleton(client)
    .AddSingleton(commands)
    .AddSingleton(interactions)
    .BuildServiceProvider();

GlobalVariable.Init();


Task LogAsync(LogMessage msg)
{
    Console.WriteLine($"[LOG]{msg}");
    return Task.CompletedTask;
}

async Task GetBotInfo()
{
    GlobalVariable.botName = client.CurrentUser.Username;
    GlobalVariable.botID = client.CurrentUser.Id;
    var application = await client.GetApplicationInfoAsync();
    GlobalVariable.creatorName = application.Owner.Username;
    GlobalVariable.creatorID = application.Owner.Id;
}

client.Log += LogAsync;
commands.Log += LogAsync;
interactions.Log += LogAsync;

client.Ready += async () =>
{
    Console.WriteLine($"目前登入 : {client.CurrentUser.Username}#{client.CurrentUser.Discriminator}");
    await GetBotInfo();
    await GlobalVariable.genshinService.LoopCheckGenshinResin(client,190,TimeSpan.FromMinutes(30));
    await PlaylistSystem.LoopCheckVoiceChannelAndUsers();
};

client.MessageReceived += async (messageParam) =>
{
    if (messageParam is not SocketUserMessage message) return;
    if (message.Author.IsBot) return;

    int argPos = 0;
    if (!message.HasCharPrefix('!', ref argPos)) return;

    var context = new SocketCommandContext(client, message);
    var result = await commands.ExecuteAsync(context, argPos, services);

    if (!result.IsSuccess && result.Error != CommandError.UnknownCommand)
    {
        Console.WriteLine($"錯誤指令： {result.ErrorReason}");
        await context.Channel.SendMessageAsync($"❌ {result.ErrorReason}");
    }
};

client.InviteCreated += async (invite) =>
{
    Console.WriteLine($"New invite created: {invite.Code} by {invite.Inviter}");
};

client.GuildScheduledEventUpdated += async (before, after) =>
{
    Console.WriteLine($"Event updated: {before.Value.Name} → {after.Name}");
};


client.InteractionCreated += async (interaction) =>
{
    var intctx = new SocketInteractionContext(client, interaction);
    await interactions.ExecuteCommandAsync(intctx, null);
};

await commands.AddModulesAsync(Assembly.GetEntryAssembly(),services);
await interactions.AddModulesAsync(Assembly.GetEntryAssembly(),null);
await client.LoginAsync(TokenType.Bot, GlobalVariable.botToken);
await client.StartAsync();


await Task.Delay(-1);
