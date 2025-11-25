using DiscordBot;
using Humanizer;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Hosting.Services.ApplicationCommands;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using System.IO;
using System.Security.Principal;

public class SlashCommands : ApplicationCommandModule<ApplicationCommandContext>
{
    public static bool ToEphemeral(DisplayOption display) => display == DisplayOption.Hide;

    public static async Task<(bool success,JoinResult result)> HandleResponseJoinToVC<T>(T context , DisplayOption display) where T : IUserContext , IGuildContext , IInteractionContext
    {
        JoinResult joinResult = null;
        try
        {
            joinResult = await Utils.JoinToUserVC(context);
            if (joinResult.ToUserState == JoinToUserState.UserNotInVC)
            {
                await context.Interaction.SendFollowupMessageAsync("你不在頻道裡喔！", display);
                return (false, joinResult);
            }
            else if (joinResult.JoinState == JoinState.Fail || joinResult.VC == null)
            {
                await context.Interaction.SendFollowupMessageAsync("無法加入聊天室！", display);
                return (false, joinResult);
            }
            else
            {
                await joinResult.VC.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
                return (true, joinResult);
            }
                
        }
        catch
        {
            return (false,joinResult);
        }
    }

    [SlashCommand("yt點歌", $"客服小祥激情開唱")]
    public async Task PlayMusicFromYoutubeUrl(
        [SlashCommandParameter(Name = "網址")] string url,
        DisplayOption display = DisplayOption.Display
        )
    {
        await Utils.DeferResponse(Context,display);

        var (success, result) = await HandleResponseJoinToVC(Context,display);

        if(success)
        {
            var vc = result.VC;

            var playlist = PlaylistSystem.GetorCreatePlaylist(Context.Guild, vc , result.ChannelID);
            bool firstTrack = playlist._urls.Count == 0;

            var urlTupleList = new List<Tuple<WebOption, string>>()
                    {
                        Tuple.Create(WebOption.Youtube,url)
                    };

            playlist.AddUrls(urlTupleList);

            if (firstTrack)
            {
                await Context.Interaction.SendFollowupMessageAsync($"{GlobalVariable.botNickname}激情開唱！", display);
                await playlist.StartAsync(Context.Interaction);
            }
            else
            {
                await Context.Interaction.SendFollowupMessageAsync($"成功插入1首歌！", display);
            }
        }
    }

    [SlashCommand("bilibili點歌", $"客服小祥激情開唱")]
    public async Task PlayMusicFromBilibiliUrl(
        [SlashCommandParameter(Name = "網址")] string url,
        DisplayOption display = DisplayOption.Display
        )
    {
        await Utils.DeferResponse(Context, display);

        var (success, result) = await HandleResponseJoinToVC(Context, display);

        if (success)
        {
            var vc = result.VC;

            var playlist = PlaylistSystem.GetorCreatePlaylist(Context.Guild, vc , result.ChannelID);
            bool firstTrack = playlist._urls.Count == 0;

            var urlTupleList = new List<Tuple<WebOption, string>>()
                    {
                        Tuple.Create(WebOption.Bilibili,url)
                    };

            playlist.AddUrls(urlTupleList);

            if (firstTrack)
            {
                await Context.Interaction.SendFollowupMessageAsync($"{GlobalVariable.botNickname}激情開唱！", display);
                await playlist.StartAsync(Context.Interaction);
            }
            else
            {
                await Context.Interaction.SendFollowupMessageAsync($"成功插入1首歌！", display);
            }
        }
    }

    [SlashCommand("yt歌單", $"客服小祥激情開唱")]
    public async Task PlayMusicFromYoutubePlaylist(
        [SlashCommandParameter( AutocompleteProviderType = typeof(PlaylistAutocompleteHandler))]  string url,
        RandomOption random,
        DisplayOption display = DisplayOption.Display
        )
    {
        await Utils.DeferResponse(Context, display);

        var (success, result) = await HandleResponseJoinToVC(Context, display);

        if (success)
        {
            var vc = result.VC;
            var playlist = PlaylistSystem.GetorCreatePlaylist(Context.Guild, vc, result.ChannelID);
            bool firstTrack = playlist._urls.Count == 0;

            var urls = await MediaProcess.GetPlaylistUrlsAsync(url);

            if (urls == null || urls.Count < 1)
            {
                await Context.Interaction.SendFollowupMessageAsync($"無法解析或歌單無效！", display);
                return;
            }

            if (random == RandomOption.Random)
            {
                urls.Shuffle();
            }

            var urlTupleList = urls
                .Select(url => Tuple.Create(WebOption.Youtube, url))
                .ToList();

            playlist.AddUrls(urlTupleList);

            if (firstTrack)
            {
                await Context.Interaction.SendFollowupMessageAsync($"{GlobalVariable.botNickname}激情開唱{urls.Count}首音樂！", display);
                await playlist.StartAsync(Context.Interaction);
            }
            else
            {
                await Context.Interaction.SendFollowupMessageAsync($"成功插入{urls.Count}首歌！", display);
            }

        }
    }

    [SlashCommand("開源", $"我超 盒")]
    public async Task Credicts()
    {
        await Utils.DeferResponse(Context,DisplayOption.Display);

        var message = new InteractionMessageProperties()
        {
            Content = $"目前執行中的{GlobalVariable.botName}由C# dotnet9.0建構(NetCord)，版本 : {GlobalVariable.version}" + Environment.NewLine +
                      $"Github url (Discord.Net): {GlobalVariable.gitUrl}" + Environment.NewLine +
                      $"Github url (NetCord): {GlobalVariable.gitUrl2}" + Environment.NewLine +
                        $"All Credicts to {Utils.MentionWithID(GlobalVariable.creatorID)}",
            Flags = MessageFlags.SuppressEmbeds 
        };

        await Context.Interaction.SendFollowupMessageAsync(message);
    }

    [SlashCommand("原神體力", $"原神啟動")]
    public async Task CheckGenshinResin()
    {
        await Utils.DeferResponse(Context,DisplayOption.Display);
        ulong dcid = Context.User.Id;
        try
        {
            var info = await GlobalVariable.hoyoLab.GetInfoAsyncByDiscordId(dcid.ToString(), GameType.Genshin);

            await this.FollowupHoyolabInfo(info, "旅行者");
        }
        catch
        {
            await FollowupAsync($"嗚嗚嗚{GlobalVariable.botNickname}查詢失敗 !");
        }

    }

    [SlashCommand("崩鐵體力", $"沒人覺得DoT隊很詭異嗎")]
    public async Task CheckHsrResin()
    {
        await Utils.DeferResponse(Context, DisplayOption.Display);
        ulong dcid = Context.User.Id;
        try
        {
            var info = await GlobalVariable.hoyoLab.GetInfoAsyncByDiscordId(dcid.ToString(), GameType.HonkaiStarRail);

            await this.FollowupHoyolabInfo(info, "開拓者");
        }
        catch
        {
            await FollowupAsync($"嗚嗚嗚{GlobalVariable.botNickname}查詢失敗 !");
        }

    }

    [SlashCommand("絕區零體力", "那反舌鳥怎麼辦?")]
    public async Task CheckZzzBattery()
    {
        await Utils.DeferResponse(Context, DisplayOption.Display);
        ulong dcid = Context.User.Id;
        try
        {
            var info = await GlobalVariable.hoyoLab.GetInfoAsyncByDiscordId(dcid.ToString(), GameType.ZenlessZoneZero);

            await this.FollowupHoyolabInfo(info, "繩匠");
        }
        catch
        {
            await FollowupAsync($"嗚嗚嗚{GlobalVariable.botNickname}查詢失敗 !");
        }
    }

    [SlashCommand("體力總結", "查成分!")]
    public async Task CheckAllGamesResin()
    {
        await Utils.DeferResponse(Context, DisplayOption.Display);
        ulong dcid = Context.User.Id;
        try
        {
            var map = new[]
            {
                (GameType.Genshin,"Genshin"),(GameType.HonkaiStarRail,"Hsr"),(GameType.ZenlessZoneZero,"Zzz")
            };

            StringFormatting.StringFormatter sf = new StringFormatting.StringFormatter(StringFormatting.StringFormatter.PadAlign.Right, 100);
            sf.PaddingSpace = 2;

            foreach (var set in map)
            {
                var info = await GlobalVariable.hoyoLab.GetInfoAsyncByDiscordId(dcid.ToString(), set.Item1);
                if (info != null && info.Status == CheckStatus.Success)
                {
                    sf.AddStringTemps($"[{set.Item2}]", 0);
                    sf.AddStringTemps($"[{info.CurrentResin}/{info.MaxResin}]", 1);
                    sf.AddStringTemps($"[{info.MaxAt:yyyy-MM-dd HH:mm:ss}]", 2);
                    sf.AddStringTemps($" [{info.Name}]");
                    sf.NewLine();
                }

            }

            string infoText = sf.ToFormattedString().TrimEnd('\r', '\n');

            if (infoText.Length < 1)
            {
                await FollowupAsync($"{GlobalVariable.botNickname}並未收集任何資訊 !");
            }
            else
            {
                infoText = $"```{infoText}```";
                await FollowupAsync($"{infoText}");
            }
        }
        catch
        {
            await FollowupAsync($"嗚嗚嗚{GlobalVariable.botNickname}查詢失敗 !");
        }
    }


    private async Task FollowupHoyolabInfo(HoyolabUserInfo info, string callname)
    {
        if (info != null)
        {
            if (info.Status == CheckStatus.Success)
            {
                string humanized = info.RecoveryTime.Humanize(2, collectionSeparator: " ");
                string remainingTime = (info.RecoveryTime >= TimeSpan.Zero) ?
                    $"剩餘 : {humanized}" :
                    $"超出 : {humanized}";

                await FollowupAsync(
                    $"{callname} : {info.Name}{Environment.NewLine}" +
                    $"目前體力 : {info.CurrentResin}/{info.MaxResin}{Environment.NewLine}" +
                    $"預計滿體力時間 : {info.MaxAt:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                    $"{remainingTime}");
            }
            else if (info.Status == CheckStatus.UserNotFound)
            {
                await FollowupAsync("你不在名單中 !");
            }
            else if (info.Status == CheckStatus.UserNotRegister)
            {
                await FollowupAsync("你又沒玩 !");
            }
            else
                await FollowupAsync($"嗚嗚嗚{GlobalVariable.botNickname}查詢失敗 !");
        }
        else
        {
            await FollowupAsync($"嗚嗚嗚{GlobalVariable.botNickname}查詢失敗 !");
        }
    }

    [SlashCommand("音效板", "上班摸魚寫的")]
    public async Task PlaySoundEffect(
        [SlashCommandParameter(AutocompleteProviderType = typeof(SoundEffectsAutocompleteHandler))] string filename,
        DisplayOption display = DisplayOption.Display)
    {
        await Utils.DeferResponse(Context , display);

        var (success, result) = await HandleResponseJoinToVC(Context,display);

        if(success)
        {
            var vc = result.VC;

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
                var ffmpeg = await MediaProcess.CreateLocalAsync(absoluteFilePath);

                if (ffmpeg == null)
                {
                    await Context.Interaction.SendFollowupMessageAsync($"{GlobalVariable.botNickname}無法處理音訊 !", display);
                    return;
                }

                await Context.Interaction.SendFollowupMessageAsync($"{GlobalVariable.botNickname}播放音效 : {filename} !", display);
                
                await MediaProcess.PlayAudioAsync(vc , ffmpeg);

            });
        }
    }

    [SlashCommand("新增刪除歌單", "選擇新增或是刪除歌單")]
    public async Task AddRemovePlaylist(
        [SlashCommandParameter(Name = "歌單名稱" , AutocompleteProviderType = typeof(PlaylistAutocompleteHandler))] string text,
        [SlashCommandParameter(Name = "對應網址", Description = "新增歌單對應網址，刪除歌單不用填")] string url = "",
        [SlashCommandParameter(Name = "新增刪除", Description = "新增或刪除歌單")] AddRemoveOption addRemove = AddRemoveOption.Add,
        DisplayOption display = DisplayOption.Display
        )
    {
        await Utils.DeferResponse(Context,display);

        if (addRemove == AddRemoveOption.Add && String.IsNullOrEmpty(url))
        {
            await Context.Interaction.SendFollowupMessageAsync($"新增歌單功能需填入對應網址！", display);
            return;
        }

        ulong serverID = Context.Guild.Id;

        var serverPlaylists = GlobalVariable.concurrentPlaylist.GetPlaylists(serverID);

        if (addRemove == AddRemoveOption.Modify)
        {
            if(String.IsNullOrEmpty(url) )
            {
                await Context.Interaction.SendFollowupMessageAsync($"修改歌單功能未填入對應網址！", display);
            }
            else if(!serverPlaylists.ContainsKey(text))
            {
                await Context.Interaction.SendFollowupMessageAsync($"修改歌單功能該歌單不存在！", display);
            }
            else
            {
                var cache = serverPlaylists[text];
                serverPlaylists[text] = url;
                await Context.Interaction.SendFollowupMessageAsync($"修改完成 : {text} : {cache} => {url}！", display);
            }
            return;
        }

        if (addRemove == AddRemoveOption.Add)
        {
            if (serverPlaylists.ContainsKey(text))
            {
                await Context.Interaction.SendFollowupMessageAsync("新增歌單不可重名！", display);
                return;
            }
            GlobalVariable.concurrentPlaylist.AddOrCreate(serverID, text, url);
            await Context.Interaction.SendFollowupMessageAsync($"{GlobalVariable.botNickname}成功新增對應歌單 : {text} => {url}", display);
        }
        else
        {
            if (!serverPlaylists.ContainsKey(text))
            {
                await Context.Interaction.SendFollowupMessageAsync("找不到此歌單，請確認輸入名稱是否一致！", display);
                return;
            }
            GlobalVariable.concurrentPlaylist.Remove(serverID, text);
            await Context.Interaction.SendFollowupMessageAsync($"{GlobalVariable.botNickname}成功刪除歌單 : {text}", display);
        }

    }

    [SlashCommand("下載", "我是爬蟲，我才是爬蟲")]
    public async Task DownloadFileFrom(
        [SlashCommandParameter(Name = "網站")] WebOption web,
        [SlashCommandParameter(Name = "網址")] string url,
        [SlashCommandParameter(Name = "檔案類型")] ExtensionOption extension = ExtensionOption.Video,
        DisplayOption display = DisplayOption.Display
        )
    {
        await Utils.DeferResponse(Context,display);

        try
        {
            await Task.Run(async () =>
            {
                var downloadAlgorithm = MediaProcess.DetermineDownloadVideoAlgorithm(web);
                string? fullFilePath = await downloadAlgorithm(url, extension);

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
                            var stream = File.OpenRead(fullFilePath);

                            int tier = Context.Guild.PremiumTier;
                            int size = 10 ;

                            if (tier > GlobalVariable.serverTierUploadFileSize.Count)
                                size = GlobalVariable.serverTierUploadFileSize.Last();
                            else
                                size = GlobalVariable.serverTierUploadFileSize[tier];

                            if(stream.Length / (1024L * 1024L) > size) 
                            {
                                await Context.Interaction.SendFollowupMessageAsync("偵測到檔案過大，ffmpeg + ffprobe 壓縮中...", display);

                                string Compressed = $"compressed{filename}";
                                string outputPath = $"{GlobalVariable.downloadFolderPath}{Compressed}";
                                FFmpegCompressor.CompressVideo(fullFilePath, outputPath , size);

                                stream = File.OpenRead(outputPath);
                            }

                            var message = new InteractionMessageProperties()
                            {
                                Content = $"{GlobalVariable.botNickname}下載成功！",
                                Attachments = new[]
                                {
                                    new AttachmentProperties(filename,stream)
                                },
                                Flags = display.ToEphemeralFlag()
                            };
                            await Context.Interaction.SendFollowupMessageAsync(message);
                        }
                        catch (Exception ex) when (ex.Message.Contains("Request entity too large", StringComparison.OrdinalIgnoreCase))
                        {
                            await Context.Interaction.SendFollowupMessageAsync("拋出檔案過大例外，ffmpeg + ffprobe 壓縮中...", display);

                            try
                            {
                                string Compressed = $"compressed{filename}";
                                string outputPath = $"{GlobalVariable.downloadFolderPath}{Compressed}";
                                FFmpegCompressor.CompressVideo(fullFilePath, outputPath);
                                var stream = File.OpenRead(outputPath);
                                var message = new InteractionMessageProperties()
                                {
                                    Content = $"壓縮成功！",
                                    Attachments = new[]
                                    {
                                        new AttachmentProperties(filename,stream)
                                    },
                                    Flags = display.ToEphemeralFlag()
                                };
                                await Context.Interaction.SendFollowupMessageAsync(message);
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
                            await Context.Interaction.SendFollowupMessageAsync("傳送時發生未知錯誤！", display);
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

    [SlashCommand("search", "這算法我寫了三天，希望真的很強")]
    public async Task SearchImages(
          [SlashCommandParameter(AutocompleteProviderType = typeof(ImageAlgorithm.SearchImageAutocomplete))] string query,
          DisplayOption display = DisplayOption.Display
        )
    {
        await Utils.DeferResponse(Context,display);

        var message = new InteractionMessageProperties()
        {
            Flags = display.ToEphemeralFlag()
        };

        string path ;

        if (File.Exists(query))
            path = query;
        else
        {
            path = ImageAlgorithm.SearchImage(query, maxResultCount: 1, acceptsCachedResult: true)
                .First()
                .Path;
        }

        var stream = File.OpenRead(path);
        message.Attachments = new[] { new AttachmentProperties(query, stream) };
        await Context.Interaction.SendFollowupMessageAsync(message);
    }
}

public class PlaylistAutocompleteHandler : IAutocompleteProvider<AutocompleteInteractionContext> 
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        ulong serverId = context.Interaction.Guild.Id;
        var result = new List<ApplicationCommandOptionChoiceProperties>();

        if (!GlobalVariable.concurrentPlaylist.Exist(serverId))
            return ValueTask.FromResult(result.AsEnumerable());

        var current = option.Value;

        foreach (var op in GlobalVariable.concurrentPlaylist.GetPlaylists(serverId)
                        .Where(kvp => (current.Length>0)?kvp.Key.Contains(current, StringComparison.OrdinalIgnoreCase):true)
                        .Take(25))
        {
            result.Add(new(op.Key, op.Value));
        }
        
        return ValueTask.FromResult(result.AsEnumerable());
    }
}

public class SoundEffectsAutocompleteHandler : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        string[] files = new DirectoryInfo(GlobalVariable.soundEffectsFolderPath)
            .GetFiles()
            .Where(file => file.Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || file.Name.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.Name)
            .ToArray();

        var current = option.Value;

        var options = files
                    .Where(filename => filename.Contains(current, StringComparison.OrdinalIgnoreCase))
                    .Take(25)
                    .Select(filename => Path.GetFileNameWithoutExtension(filename))
                    .Select(choice => new ApplicationCommandOptionChoiceProperties(choice, choice));

        return ValueTask.FromResult(options);
    }
}


public enum DisplayOption
{
    [SlashCommandChoice(Name = "顯示指令")]
    Display,
    [SlashCommandChoice(Name = "不顯示指令")]
    Hide
}
public enum WebOption
{
    [SlashCommandChoice(Name = "Youtube")]
    Youtube,
    [SlashCommandChoice(Name = "Bilibili")]
    Bilibili
}
public enum RandomOption
{
    [SlashCommandChoice(Name = "隨機播放")]
    Random,
    [SlashCommandChoice(Name = "正常播放")]
    Normal
}
public enum AddRemoveOption
{
    [SlashCommandChoice(Name = "新增")]
    Add,
    [SlashCommandChoice(Name = "刪除")]
    Remove,
    [SlashCommandChoice(Name = "修改")]
    Modify
}

public enum ExtensionOption
{
    [SlashCommandChoice(Name = "Audio")]
    Audio,
    [SlashCommandChoice(Name = "Video")]
    Video
}


//    [SlashCommand("聊天", "cuda加速真的好快")]
//    public async Task ChatWithBot(
//          [Summary("內容")] string prompt,
//          DisplayOption display = DisplayOption.Display
//        )
//    {
//        bool eph = SlashCommands.ToEphemeral(display);
//        await DeferAsync(ephemeral: eph);

//        //_ = Task.Run(async () =>
//        //{
//        //    string response = LanguageModelCore.GetModelResponse(prompt , Context.Channel.Id);
//        //    await FollowupAsync (response,ephemeral:eph);
//        //});
//        _ = Task.Run(async () =>
//        {
//            const int TIMEOUT_MS = 50_000;
//            Stopwatch sw = new Stopwatch();
//            IUserMessage? msg = null;
//            var res = LanguageModelCore.GetModelResponseToken(prompt, Context.Channel.Id);
//            sw.Start();
//            while (string.IsNullOrEmpty(res.Response) && sw.ElapsedMilliseconds < TIMEOUT_MS)
//            {
//                if (!String.IsNullOrEmpty(res.Tokens))
//                {
//                    if (msg == null)
//                        msg = await FollowupAsync(res.Tokens);
//                    else
//                        await msg.ModifyAsync(prop =>
//                        {
//                            prop.Content = res.Tokens;
//                        });
//                }

//                await Task.Delay(2000);
//            }

//            //await FollowupAsync(res.Response);

//            if (msg != null)
//            {
//                await Task.Delay(1000);
//                if (!String.IsNullOrEmpty(res.Response))
//                {
//                    await msg.ModifyAsync(prop =>
//                    {
//                        prop.Content = $"{res.Response}";
//                    });
//                }
//                else
//                {
//                    await msg.ModifyAsync(prop =>
//                    {
//                        prop.Content = $"Response timed out after {TIMEOUT_MS / 1000}s.";
//                    });
//                }

//            }



//        });
//    }

//    [SlashCommand("清空聊天紀錄", "當這逼機器人開始亂回話的時候")]
//    public async Task ClearChatHistory()
//    {
//        await DeferAsync();
//        LanguageModelCore.ClearChatHistory(Context.Channel.Id);
//        await FollowupAsync("對話紀錄已清除！");
//    }


//}


