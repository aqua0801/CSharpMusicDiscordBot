using Discord.Audio;
using Discord.Interactions;
using Discord.WebSocket;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;

namespace DiscordBot
{
    public enum DisplayOption
    {
        [ChoiceDisplay("顯示指令")]
        Display,
        [ChoiceDisplay("不顯示指令")]
        Hide
    }
    public enum WebOption
    {
        [ChoiceDisplay("Youtube")]
        Youtube,
        [ChoiceDisplay("Bilibili")]
        Bilibili
    }
    public enum RandomOption
    {
        [ChoiceDisplay("隨機播放")]
        Random,
        [ChoiceDisplay("正常播放")]
        Normal
    }

    public class SlashCommands : InteractionModuleBase<SocketInteractionContext>
    {
        public static bool ToEphemeral(DisplayOption display) => display == DisplayOption.Hide;


        [SlashCommand("yt點歌", $"客服小祥激情開唱")]
        public async Task PlayMusicFromYoutubeUrl(
            [Summary("網址")]string url , 
            DisplayOption display = DisplayOption.Display
            )
        {
            bool eph = SlashCommands.ToEphemeral(display);
            await DeferAsync(ephemeral:eph);

            int joinStatus = await Utils.FromInteractionJoin(Context);

            if (joinStatus == -1)
            {
                await FollowupAsync("你不在頻道裡喔！",ephemeral:eph);
            }
            else
            {
                var vc = Context.Guild.CurrentUser.VoiceChannel;
                IAudioClient? audioClient;

                if(!GlobalVariable.ServerAudioClientMap.TryGetValue(Context.Guild.Id , out audioClient))
                {
                    try
                    {
                        audioClient = await vc.ConnectAsync();
                        GlobalVariable.ServerAudioClientMap[Context.Guild.Id] = audioClient;
                    }
                    catch
                    {
                        await FollowupAsync($"{GlobalVariable.botNickname}迷路啦！", ephemeral: eph);
                        return;
                    }
                }

                if(audioClient == null)
                {
                    await FollowupAsync($"{GlobalVariable.botNickname}迷路啦！", ephemeral: eph);
                    return;
                }

                await AudioProcess.PlayAudioByUrlAndRespond(Context.Interaction , url , audioClient , display , AudioProcess.DetermineAudioUrlAlgorithm(WebOption.Youtube));
            }
        }

        [SlashCommand("bilibili點歌", $"客服小祥激情開唱")]
        public async Task PlayMusicFromBilibiliUrl(
            [Summary("網址")] string url,
            DisplayOption display = DisplayOption.Display
            )
        {
            bool eph = SlashCommands.ToEphemeral(display);
            await DeferAsync(ephemeral: eph);

            int joinStatus = await Utils.FromInteractionJoin(Context);

            if (joinStatus == -1)
            {
                await FollowupAsync("你不在頻道裡喔！", ephemeral: eph);
            }
            else
            {
                var vc = Context.Guild.CurrentUser.VoiceChannel;
                IAudioClient? audioClient;

                if (!GlobalVariable.ServerAudioClientMap.TryGetValue(Context.Guild.Id, out audioClient))
                {
                    try
                    {
                        audioClient = await vc.ConnectAsync();
                        GlobalVariable.ServerAudioClientMap[Context.Guild.Id] = audioClient;
                    }
                    catch
                    {
                        await FollowupAsync($"{GlobalVariable.botNickname}迷路啦！", ephemeral: eph);
                        return;
                    }
                }

                if (audioClient == null)
                {
                    await FollowupAsync($"{GlobalVariable.botNickname}迷路啦！", ephemeral: eph);
                    return;
                }

                var playlist = PlaylistSystem.GetorCreatePlaylist(Context.Guild, audioClient);
                bool firstTrack = playlist._urls.Count == 0;

                var urlTupleList = new List<Tuple<WebOption, string>>()
                {
                    Tuple.Create(WebOption.Bilibili,url)
                };

                playlist.AddUrls(urlTupleList);

                if (firstTrack)
                {
                    await FollowupAsync($"{GlobalVariable.botNickname}激情開唱！", ephemeral: eph);
                    await playlist.StartAsync(Context.Interaction);
                }
                else
                {
                    await FollowupAsync($"成功插入1首歌！", ephemeral: eph);
                }
            }
        }

        [SlashCommand("yt歌單", $"客服小祥激情開唱")]
        public async Task PlayMusicFromYoutubePlaylist(
            [Summary("網址")] string url,
            RandomOption random,
            DisplayOption display = DisplayOption.Display
            )
        {
            bool eph = SlashCommands.ToEphemeral(display);
            await DeferAsync(ephemeral: eph);

            int joinStatus = await Utils.FromInteractionJoin(Context);

            if (joinStatus == -1)
            {
                await FollowupAsync("你不在頻道裡喔！", ephemeral: eph);
            }
            else
            {
                var vc = Context.Guild.CurrentUser.VoiceChannel;
                IAudioClient? audioClient;

                if (!GlobalVariable.ServerAudioClientMap.TryGetValue(Context.Guild.Id, out audioClient))
                {
                    try
                    {
                        audioClient = await vc.ConnectAsync();
                        GlobalVariable.ServerAudioClientMap[Context.Guild.Id] = audioClient;
                    }
                    catch
                    {
                        await FollowupAsync($"{GlobalVariable.botNickname}迷路啦！", ephemeral: eph);
                        return;
                    }
                }

                if (audioClient == null)
                {
                    await FollowupAsync($"{GlobalVariable.botNickname}迷路啦！", ephemeral: eph);
                    return;
                }

                var playlist = PlaylistSystem.GetorCreatePlaylist(Context.Guild,audioClient);
                bool firstTrack = playlist._urls.Count == 0;

                var urls = await AudioProcess.GetPlaylistUrlsAsync(url);

                if(urls ==null || urls.Count < 1)
                {
                    await FollowupAsync($"無法解析或歌單無效！", ephemeral: eph);
                    return;
                }

                if (random == RandomOption.Random)
                {
                    urls.Shuffle();
                }

                var urlTupleList = urls
                    .Select(url=> Tuple.Create(WebOption.Youtube,url))
                    .ToList();

                playlist.AddUrls(urlTupleList);

                if(firstTrack)
                {
                    await FollowupAsync($"{GlobalVariable.botNickname}激情開唱{urls.Count}首音樂！", ephemeral: eph);
                    await playlist.StartAsync(Context.Interaction);
                }
                else
                {
                    await FollowupAsync($"成功插入{urls.Count}首歌！", ephemeral: eph);
                }
                
            }
        }

        [SlashCommand("開源", $"我超 盒")]
        public async Task Credicts()
        {
            await RespondAsync($"目前執行中的{GlobalVariable.botName}由C# dotnet9.0建構，版本 : {GlobalVariable.version}" + Environment.NewLine +
                $"Github url : {GlobalVariable.gitUrl}" + Environment.NewLine +
                $"All Credicts to {Utils.MentionWithID(GlobalVariable.creatorID)}");
        }

    }

    public class InteractionHandler
    {
        private readonly DiscordSocketClient _client;
        private readonly InteractionService _interactions;
        private readonly IServiceProvider _services;

        public InteractionHandler(DiscordSocketClient client, InteractionService interactions, IServiceProvider services)
        {
            _client = client;
            _interactions = interactions;
            _services = services;
        }

        public async Task InitializeAsync()
        {
            _client.InteractionCreated += HandleInteraction;
            _client.Ready += RegisterCommandsAsync;

            await _interactions.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
        }

        private async Task HandleInteraction(SocketInteraction interaction)
        {
            var ctx = new SocketInteractionContext(_client, interaction);
            var result = await _interactions.ExecuteCommandAsync(ctx, _services);

            if (!result.IsSuccess)
                Console.WriteLine($"Interaction Error: {result.ErrorReason}");
        }

        private async Task RegisterCommandsAsync()
        {
            await _interactions.RegisterCommandsGloballyAsync(); 
        }
    }
}
