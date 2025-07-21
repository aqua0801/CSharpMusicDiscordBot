using AngleSharp.Dom;
using Discord;
using Discord.Audio;
using Discord.Interactions;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.VisualBasic;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using System.Threading.Tasks;
using static DiscordBot.MediaProcess;

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
    public enum AddRemoveOption
    {
        [ChoiceDisplay("新增")]
        Add,
        [ChoiceDisplay("刪除")]
        Remove
    }

    public enum ExtensionOption
    {
        [ChoiceDisplay("Audio")]
        Audio,
        [ChoiceDisplay("Video")]
        Video
    }


    public class PlaylistAutocompleteHandler : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
        {
            ulong serverId = context.Guild.Id;

            string current = autocompleteInteraction.Data.Current.Value.ToString();

            if (!GlobalVariable.concurrentPlaylist.Exist(serverId))
                return AutocompletionResult.FromSuccess();

            var options =  GlobalVariable.concurrentPlaylist.GetPlaylists(serverId)
                            .Where(kvp => kvp.Key.Contains(current,StringComparison.OrdinalIgnoreCase))
                            .Take(25)
                            .Select(kvp => new AutocompleteResult(kvp.Key,kvp.Value));
            return AutocompletionResult.FromSuccess(options);
        }
    }

    public class SoundEffectsAutocompleteHandler : AutocompleteHandler
    {
        public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
        {
            string[] files = new DirectoryInfo(GlobalVariable.soundEffectsFolderPath)
                .GetFiles()
                .Where(file => file.Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                .Select(file => file.Name)
                .ToArray();
    
            var current = autocompleteInteraction.Data.Current.Value.ToString();
         
            var options = files
                        .Where(filename => filename.Contains(current, StringComparison.OrdinalIgnoreCase))
                        .Take(25)
                        .Select(filename=>Path.GetFileNameWithoutExtension(filename))
                        .Select(choice => new AutocompleteResult(choice, choice));
           
            return AutocompletionResult.FromSuccess(options);
        }
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

                if(!GlobalVariable.serverAudioClientMap.TryGetValue(Context.Guild.Id , out audioClient))
                {
                    try
                    {
                        audioClient = await vc.ConnectAsync();
                        GlobalVariable.serverAudioClientMap[Context.Guild.Id] = audioClient;
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


                var playlist = PlaylistSystem.GetorCreatePlaylist(Context.Guild, audioClient);
                bool firstTrack = playlist._urls.Count == 0;

                var urlTupleList = new List<Tuple<WebOption, string>>()
                {
                    Tuple.Create(WebOption.Youtube,url)
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


                //await MediaProcess.PlayAudioByUrlAndRespond(Context.Interaction , url , audioClient , display , MediaProcess.DetermineAudioUrlAlgorithm(WebOption.Youtube));
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

                if (!GlobalVariable.serverAudioClientMap.TryGetValue(Context.Guild.Id, out audioClient))
                {
                    try
                    {
                        audioClient = await vc.ConnectAsync();
                        GlobalVariable.serverAudioClientMap[Context.Guild.Id] = audioClient;
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
            [Autocomplete(typeof(PlaylistAutocompleteHandler))] string url,
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

                if (!GlobalVariable.serverAudioClientMap.TryGetValue(Context.Guild.Id, out audioClient))
                {
                    try
                    {
                        audioClient = await vc.ConnectAsync();
                        GlobalVariable.serverAudioClientMap[Context.Guild.Id] = audioClient;
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

                var urls = await MediaProcess.GetPlaylistUrlsAsync(url);

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

        [SlashCommand("原神體力", $"原神啟動")]
        public async Task CheckGenshinResin()
        {
            await DeferAsync();
            var result = await GlobalVariable.genshinService.CheckGenshinResin(Context.Guild.Id,Context.User.Id);
            
            if(result.Item1 == CheckStatus.ServerNotFound)
            {
                await FollowupAsync($"{GlobalVariable.botNickname}在世界樹找不到此伺服器的蹤跡！！");
            }
            else if (result.Item1 == CheckStatus.DiscordUserNotFound)
            {
                await FollowupAsync("世界樹沒有你的資料 莫非是降臨者！");
            }
            else if (result.Item1 == CheckStatus.UnknownError || result.Item3==null)
            {
                await FollowupAsync("小祥遇到提瓦特的力量無法參透的錯誤 原又輸！");
            }
            else
            {
                var note = result.Item3;
                
                await FollowupAsync($"目前體力 {note.CurrentResin}/{note.MaxResin} 預計滿體力時間{DateTime.Now + note.ResinRecoveryTime : yyyy-MM-dd HH:mm:ss}");
            }

        }

        [SlashCommand("音效板","上班摸魚寫的")]
        public async Task PlaySoundEffect(
            [Autocomplete(typeof(SoundEffectsAutocompleteHandler))] string filename,
            DisplayOption display = DisplayOption.Display)
        {
            await DeferAsync();

            bool eph = SlashCommands.ToEphemeral(display);
            int joinStatus = await Utils.FromInteractionJoin(Context);

            if (joinStatus == -1)
            {
                await FollowupAsync("你不在頻道裡喔！", ephemeral: eph);
            }
            else
            {
                var vc = Context.Guild.CurrentUser.VoiceChannel;
                IAudioClient? audioClient;

                if (!GlobalVariable.serverAudioClientMap.TryGetValue(Context.Guild.Id, out audioClient))
                {
                    try
                    {
                        audioClient = await vc.ConnectAsync();
                        GlobalVariable.serverAudioClientMap[Context.Guild.Id] = audioClient;
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
                
                string fullFilename = new DirectoryInfo(GlobalVariable.soundEffectsFolderPath).GetFiles()
                 .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Name).Equals(filename, StringComparison.OrdinalIgnoreCase))
                 ?.Name ?? $"{filename}.mp3";

                string absoluteFilePath = Path.GetFullPath(GlobalVariable.soundEffectsFolderPath + fullFilename);

                if (!File.Exists(absoluteFilePath))
                {
                    await FollowupAsync($"{GlobalVariable.botNickname}無法找到該檔案！");
                    return;
                }

                await Task.Run(async () =>
                {
                    var ffmpeg = await CreateLocalAsync(absoluteFilePath);

                    if (ffmpeg == null)
                    {
                        await FollowupAsync($"{GlobalVariable.botNickname}無法處理音訊 !", ephemeral: eph);
                        return;
                    }

                    await FollowupAsync($"{GlobalVariable.botNickname}播放音效 : {filename} !", ephemeral: eph);

                    await PlayAudioAsync(audioClient, ffmpeg);

                });

            }

        }

        [SlashCommand("新增刪除歌單","選擇新增或是刪除歌單")]
        public async Task AddRemovePlaylist(
            [Autocomplete(typeof(PlaylistAutocompleteHandler))] string text ,
            [Summary("對應網址", "新增歌單對應網址，刪除歌單不用填")] string url = "",
            [Summary("新增刪除", "新增或刪除歌單")] AddRemoveOption addRemove = AddRemoveOption.Add ,
            [Summary("指令顯示", "是否顯示指令")] DisplayOption display = DisplayOption.Display
            )
        {
            bool eph = SlashCommands.ToEphemeral(display);
            await DeferAsync(ephemeral:eph);

            if(addRemove==AddRemoveOption.Add && String.IsNullOrEmpty(url))
            {
                await FollowupAsync($"新增歌單功能需填入對應網址！",ephemeral:eph);
                return;
            }

            ulong serverID = Context.Guild.Id;

            var serverPlaylists = GlobalVariable.concurrentPlaylist.GetPlaylists(serverID);

            if(addRemove == AddRemoveOption.Add)
            {
                if (serverPlaylists.ContainsKey(text))
                {
                    await FollowupAsync("新增歌單不可重名！",ephemeral:eph);
                    return;
                }
                GlobalVariable.concurrentPlaylist.AddOrCreate(serverID,text,url);
                await FollowupAsync($"{GlobalVariable.botNickname}成功新增對應歌單 : {text} => {url}",ephemeral:eph);
            }
            else
            {
                if (!serverPlaylists.ContainsKey(text))
                {
                    await FollowupAsync("找不到此歌單，請確認輸入名稱是否一致！", ephemeral: eph);
                    return;
                }
                GlobalVariable.concurrentPlaylist.Remove(serverID,text);
                await FollowupAsync($"{GlobalVariable.botNickname}成功刪除歌單 : {text}",ephemeral:eph);
            }

        }

        [SlashCommand("下載","我是爬蟲，我才是爬蟲")]
        public async Task DownloadFileFrom(
            [Summary("網站")] WebOption web,
            [Summary("網址")] string url,
            [Summary("檔案類型")] ExtensionOption extension = ExtensionOption.Video ,
            [Summary("顯示指令")] DisplayOption display = DisplayOption.Display
            )
        {
            bool eph = SlashCommands.ToEphemeral(display);
            await DeferAsync(ephemeral:eph);

            try
            {
                await Task.Run(async () =>
                {
                    var downloadAlgorithm =  MediaProcess.DetermineDownloadVideoAlgorithm(web);
                    string? fullFilePath = await downloadAlgorithm(url, extension);

                    //Console.WriteLine($"[Log] Download completes , output path : {fullFilePath}");

                    if (fullFilePath == null)
                    {
                        Console.WriteLine(fullFilePath);
                        await FollowupAsync($"嗚嗚嗚{GlobalVariable.botNickname}下載失敗！");
                    }
                    else
                    {
                        string filename = Path.GetFileName(fullFilePath);

                        if (File.Exists(fullFilePath))
                        {
                            try
                            {
                                await FollowupWithFileAsync(new FileAttachment(path: fullFilePath, fileName: filename), text: $"{GlobalVariable.botNickname}下載成功！");
                            }
                            catch (HttpException ex) when (ex.Message.Contains("Request entity too large", StringComparison.OrdinalIgnoreCase))
                            {
                                await FollowupAsync("檔案過大，ffmpeg + ffprobe 壓縮中...", ephemeral: eph);
                   
                                try
                                {
                                    string Compressed = $"compressed{filename}";
                                    string outputPath = $"{GlobalVariable.downloadFolderPath}{Compressed}";
                                    FFmpegCompressor.CompressVideo(fullFilePath, outputPath);
                                    await FollowupWithFileAsync(new FileAttachment(outputPath, Compressed), text: "壓縮成功！", ephemeral: eph);
                                }
                                catch (Exception e)
                                {
                                    Console.WriteLine(e);
                                    await FollowupAsync($"嗚嗚嗚{GlobalVariable.botNickname}壓縮失敗！");
                                }

                            }
                            catch (Exception e)
                            {
                                Console.WriteLine(e);
                                await FollowupAsync("傳送時發生未知錯誤！", ephemeral: eph);
                            }
                        }
                        else
                        {
                            await FollowupAsync($"疑 {GlobalVariable.botNickname}下載完找不到檔案？！");
                        }
                    }

                });
            }
            catch
            {
                await FollowupAsync($"{GlobalVariable.botNickname}遇到未知錯誤 ！");
            }

        }

        [SlashCommand("search","這算法我寫了三天，希望真的很強")]
        public async Task SearchImages(
              [Autocomplete(typeof(ImageAlgorithm.SearchImageAutocomplete))] string query,
              DisplayOption display = DisplayOption.Display
              
            )
        {
            bool eph = SlashCommands.ToEphemeral(display);
            await DeferAsync(ephemeral:eph);

            if (File.Exists(query))
            {
                await FollowupWithFileAsync(query);
            }
            else
            {
                string path = ImageAlgorithm.SearchImage(query , maxResultCount : 1 , acceptsCachedResult : true)
                    .First()
                    .Path;
                await FollowupWithFileAsync(path);
            }

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
