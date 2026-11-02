using NetCord;
using NetCord.Gateway.Voice;
using NetCord.Rest;
using NetCord.Services;
using NetCord.Services.ApplicationCommands;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace DiscordBot;

/// <summary>
/// Unhandled exceptions in commands become IExceptionResult, which Program.cs logs and reports to the user,
/// so there's no more try { } catch { } everywhere swallowing errors.
/// </summary>
public class SlashCommands : ApplicationCommandModule<ApplicationCommandContext>
{
    // ── Shared voice helpers ───────────────────────────────────────

    public static async Task<(bool Success, JoinResult Result)> HandleResponseJoinToVCAsync<T>(T context, DisplayOption display)
        where T : IUserContext, IGuildContext, IInteractionContext
    {
        var joinResult = await VoiceManager.JoinToUserVCAsync(context);

        string? failMessage = joinResult switch
        {
            { ToUserState: JoinToUserState.NotInGuild } => "這個指令只能在伺服器裡使用！",
            { ToUserState: JoinToUserState.UserNotInVC } => "你不在頻道裡喔！",
            { JoinState: not JoinState.Success } or { VC: null } => $"無法加入聊天室！{(joinResult.Error is null ? "" : $"（{UserMessages.FromException(joinResult.Error)}）")}",
            _ => null,
        };

        if (failMessage is not null)
        {
            await context.Interaction.SendFollowupMessageAsync(failMessage, display);
            return (false, joinResult);
        }

        await joinResult.VC!.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
        return (true, joinResult);
    }

    private async Task EnqueueAndPlayAsync(DisplayOption display, Func<Task<IReadOnlyList<QueueEntry>?>> resolveEntries)
    {
        await Utils.DeferResponseAsync(Context, display);

        var (success, result) = await HandleResponseJoinToVCAsync(Context, display);
        if (!success) return;

        var entries = await resolveEntries();
        if (entries is null || entries.Count == 0)
        {
            await Context.Interaction.SendFollowupMessageAsync("無法解析或歌單無效！", display);
            return;
        }

        var playlist = PlaylistRegistry.GetOrCreate(Context.Guild!.Id, result.VC!, result.ChannelId);
        playlist.Enqueue(entries);

        if (playlist.TryStart(Context.Interaction))
            await Context.Interaction.SendFollowupMessageAsync($"{GlobalVariable.BotNickname}激情開唱{(entries.Count > 1 ? $"{entries.Count}首音樂" : "")}！", display);
        else
            await Context.Interaction.SendFollowupMessageAsync($"成功插入{entries.Count}首歌！", display);
    }

    [SlashCommand("yt點歌", "客服小祥激情開唱", Contexts = new[] { InteractionContextType.Guild })]
    public Task PlayMusicFromYoutubeUrl(
        [SlashCommandParameter(Name = "網址")] string url,
        DisplayOption display = DisplayOption.Display)
        => EnqueueAndPlayAsync(display, () => Task.FromResult<IReadOnlyList<QueueEntry>?>([new QueueEntry(WebOption.Youtube, url)]));

    [SlashCommand("bilibili點歌", "客服小祥激情開唱", Contexts = new[] { InteractionContextType.Guild })]
    public Task PlayMusicFromBilibiliUrl(
        [SlashCommandParameter(Name = "網址")] string url,
        DisplayOption display = DisplayOption.Display)
        => EnqueueAndPlayAsync(display, () => Task.FromResult<IReadOnlyList<QueueEntry>?>([new QueueEntry(WebOption.Bilibili, url)]));

    [SlashCommand("yt歌單", "客服小祥激情開唱", Contexts = new[] { InteractionContextType.Guild })]
    public Task PlayMusicFromYoutubePlaylist(
        [SlashCommandParameter(AutocompleteProviderType = typeof(PlaylistAutocompleteHandler))] string url,
        RandomOption random,
        DisplayOption display = DisplayOption.Display)
        => EnqueueAndPlayAsync(display, async () =>
        {
            List<string> urls;
            try
            {
                urls = await MediaProcess.GetPlaylistUrlsAsync(url);
            }
            catch (Exception ex)
            {
                Log.Warn($"Failed to parse YouTube playlist {url}", ex);
                return null;
            }

            if (random == RandomOption.Random)
                Random.Shared.Shuffle(CollectionsMarshal.AsSpan(urls));

            return urls.Select(u => new QueueEntry(WebOption.Youtube, u)).ToList();
        });

    // ── Misc ─────────────────────────────────────────────────────

    [SlashCommand("開源", "我超 盒")]
    public async Task Credits()
    {
        await Utils.DeferResponseAsync(Context, DisplayOption.Display);

        var config = GlobalVariable.Config;
        await FollowupAsync(new InteractionMessageProperties
        {
            Content = $"目前執行中的{GlobalVariable.BotName}由C# dotnet10.0建構(NetCord)，版本 : {config.Version}\n" +
                      $"Github url (Discord.Net): {config.GitUrl}\n" +
                      $"Github url (NetCord): {config.GitUrl2}\n" +
                      $"All Credits to {Utils.MentionWithID(GlobalVariable.CreatorId)}",
            Flags = MessageFlags.SuppressEmbeds,
        });
    }

    // ── HoYoLAB ──────────────────────────────────────────────────

    [SlashCommand("原神體力", "原神啟動")]
    public Task CheckGenshinResin() => ReplyResinAsync(GameType.Genshin);

    [SlashCommand("崩鐵體力", "沒人覺得DoT隊很詭異嗎")]
    public Task CheckHsrResin() => ReplyResinAsync(GameType.HonkaiStarRail);

    [SlashCommand("絕區零體力", "那反舌鳥怎麼辦?")]
    public Task CheckZzzBattery() => ReplyResinAsync(GameType.ZenlessZoneZero);

    private async Task ReplyResinAsync(GameType type)
    {
        await Utils.DeferResponseAsync(Context, DisplayOption.Display);

        var infos = await GlobalVariable.HoyoLab.GetInfoByDiscordIdAsync(Context.User.Id, type);
        foreach (var info in infos)
            await FollowupAsync(FormatHoyolabInfo(info.UserInfo, HoyoLabService.GetMeta(type).PlayerCallName));
    }

    private static string FormatHoyolabInfo(HoyolabUserInfo info, string callName) => info.Status switch
    {
        CheckStatus.Success =>
            $"{callName} : {info.Name}\n" +
            $"每日進度 : {info.GetDailyProgress()} [{(info.IsDailyDone() ? "已完成" : "未完成")}]\n" +
            $"目前體力 : {info.CurrentResin}/{info.MaxResin}\n" +
            $"預計滿體力時間 : {info.MaxAt:yyyy-MM-dd HH:mm:ss}\n" +
            HoyolabNotifier.FormatRemaining(info.RecoveryTime),
        CheckStatus.UserNotFound => "你不在名單中 !",
        CheckStatus.UserNotRegister => "你又沒玩 !",
        _ => $"嗚嗚嗚{GlobalVariable.BotNickname}查詢 {info.Name} 失敗 !{(info.ErrorMessage is null ? "" : $"\n原因：{info.ErrorMessage}")}",
    };

    [SlashCommand("體力總結", "查成分!")]
    public async Task CheckAllGamesResin()
    {
        await Utils.DeferResponseAsync(Context, DisplayOption.Display);

        (GameType Type, string Label)[] games =
        [
            (GameType.Genshin, "Genshin"), (GameType.HonkaiStarRail, "Hsr"), (GameType.ZenlessZoneZero, "Zzz"),
        ];

        var sf = new StringFormatting.StringFormatter(StringFormatting.StringFormatter.PadAlign.Right, 100) { PaddingSpace = 2 };
        var errors = new StringBuilder();

        foreach (var (type, label) in games)
        {
            foreach (var info in await GlobalVariable.HoyoLab.GetInfoByDiscordIdAsync(Context.User.Id, type))
            {
                var u = info.UserInfo;
                if (u.Status == CheckStatus.UnknownError)
                {
                    errors.AppendLine($"[{label}] {u.Name} 查詢失敗：{u.ErrorMessage}");
                    continue;
                }
                if (u.Status != CheckStatus.Success)
                    continue;

                var dailyProgress = u.GetDailyProgress().ToColored(u.IsDailyDone() ? AnsiHelper.AnsiColor.Gray : AnsiHelper.AnsiColor.Red);
                sf.AddStringTemps($"[{label}]", 0);
                sf.AddStringTemps($"[{u.CurrentResin}/{u.MaxResin}]", 1);
                sf.AddStringTemps($"[{dailyProgress}]", 2);
                sf.AddStringTemps($"[{u.Name}]");
                sf.NewLine();
            }
        }

        string infoText = sf.ToFormattedString().TrimEnd('\r', '\n');
        string errorText = errors.ToString().TrimEnd();

        if (infoText.Length == 0 && errorText.Length == 0)
        {
            await FollowupAsync($"{GlobalVariable.BotNickname}並未收集任何資訊 !");
            return;
        }

        var reply = new StringBuilder();
        if (infoText.Length > 0) reply.AppendLine(AnsiHelper.WrapAnsiBlock(infoText));
        if (errorText.Length > 0) reply.Append(errorText);
        await FollowupAsync(reply.ToString());
    }

    // ── Soundboard ───────────────────────────────────────────────

    [SlashCommand("音效板", "上班摸魚寫的", Contexts = new[] { InteractionContextType.Guild })]
    public async Task PlaySoundEffect(
        [SlashCommandParameter(AutocompleteProviderType = typeof(SoundEffectsAutocompleteHandler))] string filename,
        DisplayOption display = DisplayOption.Display)
    {
        await Utils.DeferResponseAsync(Context, display);

        // Only accept files that actually exist in the folder; previously a miss fell back to "{filename}.mp3", allowing ..\ to escape the folder
        string? path = SoundEffectsAutocompleteHandler.FindSoundEffect(filename);
        if (path is null)
        {
            await Context.Interaction.SendFollowupMessageAsync($"{GlobalVariable.BotNickname}無法找到該檔案！", display);
            return;
        }

        var (success, result) = await HandleResponseJoinToVCAsync(Context, display);
        if (!success) return;

        var ffmpeg = MediaProcess.StartLocalProcess(path);
        await Context.Interaction.SendFollowupMessageAsync($"{GlobalVariable.BotNickname}播放音效 : {filename} !", display);
        await MediaProcess.PlayAudioAsync(result.VC!, ffmpeg);
    }

    // ── Playlist management ──────────────────────────────────────

    [SlashCommand("新增刪除歌單", "選擇新增或是刪除歌單", Contexts = new[] { InteractionContextType.Guild })]
    public async Task AddRemovePlaylist(
        [SlashCommandParameter(Name = "歌單名稱", AutocompleteProviderType = typeof(PlaylistAutocompleteHandler))] string text,
        [SlashCommandParameter(Name = "對應網址", Description = "新增歌單對應網址，刪除歌單不用填")] string url = "",
        [SlashCommandParameter(Name = "新增刪除", Description = "新增或刪除歌單")] AddRemoveOption addRemove = AddRemoveOption.Add,
        DisplayOption display = DisplayOption.Display)
    {
        await Utils.DeferResponseAsync(Context, display);

        if (Context.Guild is not { } guild)
        {
            await Context.Interaction.SendFollowupMessageAsync("這個指令只能在伺服器裡使用！", display);
            return;
        }

        var store = GlobalVariable.SavedPlaylists;
        string nick = GlobalVariable.BotNickname;

        string reply = addRemove switch
        {
            AddRemoveOption.Add when string.IsNullOrWhiteSpace(url) => "新增歌單功能需填入對應網址！",
            AddRemoveOption.Add when store.TryAdd(guild.Id, text, url) => $"{nick}成功新增對應歌單 : {text} => {url}",
            AddRemoveOption.Add => "新增歌單不可重名！",

            AddRemoveOption.Modify when string.IsNullOrWhiteSpace(url) => "修改歌單功能未填入對應網址！",
            AddRemoveOption.Modify when store.TryUpdate(guild.Id, text, url, out var oldUrl) => $"修改完成 : {text} : {oldUrl} => {url}！",
            AddRemoveOption.Modify => "修改歌單功能該歌單不存在！",

            AddRemoveOption.Remove when store.TryRemove(guild.Id, text) => $"{nick}成功刪除歌單 : {text}",
            AddRemoveOption.Remove => "找不到此歌單，請確認輸入名稱是否一致！",

            _ => throw new ArgumentOutOfRangeException(nameof(addRemove), addRemove, null),
        };

        await Context.Interaction.SendFollowupMessageAsync(reply, display);
    }

    // ── Download ─────────────────────────────────────────────────

    [SlashCommand("下載", "我是爬蟲，我才是爬蟲")]
    public async Task DownloadFileFrom(
        [SlashCommandParameter(Name = "網站")] WebOption web,
        [SlashCommandParameter(Name = "網址")] string url,
        [SlashCommandParameter(Name = "檔案類型")] ExtensionOption extension = ExtensionOption.Video,
        DisplayOption display = DisplayOption.Display)
    {
        await Utils.DeferResponseAsync(Context, display);

        string? fullFilePath = await MediaProcess.ResolveDownloadVideoAlgorithm(web)(url, extension);
        if (fullFilePath is null || !File.Exists(fullFilePath))
        {
            await Context.Interaction.SendFollowupMessageAsync($"嗚嗚嗚{GlobalVariable.BotNickname}下載失敗！", display);
            return;
        }

        string filename = Path.GetFileName(fullFilePath);
        int limitMB = GlobalVariable.Config.GetUploadLimitMB(Context.Guild?.PremiumTier ?? 0);

        // Previously compared with integer division Length / 1MB, so 10.9MB counted as 10MB and wasn't compressed
        if (new FileInfo(fullFilePath).Length > limitMB * 1024L * 1024L)
        {
            await Context.Interaction.SendFollowupMessageAsync($"偵測到檔案過大（上限 {limitMB}MB），ffmpeg 壓縮中...", display);
            await SendCompressedAsync(fullFilePath, filename, limitMB, display, "壓縮成功！");
            return;
        }

        try
        {
            await SendFileAsync(fullFilePath, filename, $"{GlobalVariable.BotNickname}下載成功！", display);
        }
        catch (RestException ex) when (ex.StatusCode == HttpStatusCode.RequestEntityTooLarge)
        {
            Log.Warn($"Upload of {filename} rejected by Discord (413), compressing to 10MB instead");
            await Context.Interaction.SendFollowupMessageAsync("拋出檔案過大例外，ffmpeg 壓縮中...", display);
            await SendCompressedAsync(fullFilePath, filename, 10, display, "壓縮成功！");
        }
    }

    private async Task SendCompressedAsync(string inputPath, string filename, int targetMB, DisplayOption display, string successMessage)
    {
        string outputPath = Path.Combine(GlobalVariable.DownloadFolderPath, $"compressed_{filename}");
        try
        {
            await FFmpegCompressor.CompressAsync(inputPath, outputPath, targetMB);
            await SendFileAsync(outputPath, filename, successMessage, display);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to compress/upload {inputPath}", ex);
            await Context.Interaction.SendFollowupMessageAsync($"嗚嗚嗚{GlobalVariable.BotNickname}壓縮失敗！（{UserMessages.FromException(ex)}）", display);
        }
        finally
        {
            TryDelete(outputPath);
        }
    }

    /// <summary>Streams from File.OpenRead used to never be disposed, keeping the file locked until GC.</summary>
    private async Task SendFileAsync(string path, string attachmentName, string content, DisplayOption display)
    {
        await using var stream = File.OpenRead(path);
        await Context.Interaction.SendFollowupMessageAsync(new InteractionMessageProperties
        {
            Content = content,
            Attachments = [new AttachmentProperties(attachmentName, stream)],
            Flags = display.ToEphemeralFlag(),
        });
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to delete temp file {path}", ex);
        }
    }

    // ── Image search ─────────────────────────────────────────────

    [SlashCommand("search", "這算法我寫了三天，希望真的很強")]
    public async Task SearchImages(
        [SlashCommandParameter(AutocompleteProviderType = typeof(ImageAlgorithm.SearchImageAutocomplete))] string query,
        DisplayOption display = DisplayOption.Display)
    {
        await Utils.DeferResponseAsync(Context, display);

        string? path = ImageAlgorithm.ResolveImagePath(query);
        if (path is null)
        {
            await Context.Interaction.SendFollowupMessageAsync($"{GlobalVariable.BotNickname}找不到符合的圖片！", display);
            return;
        }

        await SendFileAsync(path, Path.GetFileName(path), "", display);
    }
}

// ── Autocomplete ─────────────────────────────────────────────────

public class PlaylistAutocompleteHandler : IAutocompleteProvider<AutocompleteInteractionContext>
{
    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        if (context.Interaction.Guild is not { } guild)
            return ValueTask.FromResult<IEnumerable<ApplicationCommandOptionChoiceProperties>?>(Array.Empty<ApplicationCommandOptionChoiceProperties>());

        string current = option.Value ?? "";

        var choices = GlobalVariable.SavedPlaylists.GetAll(guild.Id)
            .Where(kv => current.Length == 0 || kv.Key.Contains(current, StringComparison.OrdinalIgnoreCase))
            .Where(kv => kv.Key.Length <= 100 && kv.Value.Length <= 100) // Discord limits choice name/value to 100 chars
            .Take(25)
            .Select(kv => new ApplicationCommandOptionChoiceProperties(kv.Key, kv.Value))
            .ToList();

        return ValueTask.FromResult<IEnumerable<ApplicationCommandOptionChoiceProperties>?>(choices);
    }
}

public class SoundEffectsAutocompleteHandler : IAutocompleteProvider<AutocompleteInteractionContext>
{
    private static readonly string[] Extensions = [".mp3", ".mp4"];

    private static IEnumerable<FileInfo> EnumerateSoundEffects()
    {
        var dir = new DirectoryInfo(GlobalVariable.SoundEffectsFolderPath);
        if (!dir.Exists)
        {
            Log.Warn($"Sound effects folder not found: {dir.FullName}");
            return [];
        }
        return dir.EnumerateFiles().Where(f => Extensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase));
    }

    public static string? FindSoundEffect(string name)
        => EnumerateSoundEffects()
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f.Name).Equals(name, StringComparison.OrdinalIgnoreCase))
            ?.FullName;

    public ValueTask<IEnumerable<ApplicationCommandOptionChoiceProperties>?> GetChoicesAsync(ApplicationCommandInteractionDataOption option, AutocompleteInteractionContext context)
    {
        string current = option.Value ?? "";

        var choices = EnumerateSoundEffects()
            .Select(f => Path.GetFileNameWithoutExtension(f.Name))
            .Where(name => name.Contains(current, StringComparison.OrdinalIgnoreCase) && name.Length <= 100)
            .Take(25)
            .Select(name => new ApplicationCommandOptionChoiceProperties(name, name))
            .ToList();

        return ValueTask.FromResult<IEnumerable<ApplicationCommandOptionChoiceProperties>?>(choices);
    }
}

// ── Option enums ─────────────────────────────────────────────────

public enum DisplayOption
{
    [SlashCommandChoice(Name = "顯示指令")]
    Display,
    [SlashCommandChoice(Name = "不顯示指令")]
    Hide,
}

public enum WebOption
{
    [SlashCommandChoice(Name = "Youtube")]
    Youtube,
    [SlashCommandChoice(Name = "Bilibili")]
    Bilibili,
}

public enum RandomOption
{
    [SlashCommandChoice(Name = "隨機播放")]
    Random,
    [SlashCommandChoice(Name = "正常播放")]
    Normal,
}

public enum AddRemoveOption
{
    [SlashCommandChoice(Name = "新增")]
    Add,
    [SlashCommandChoice(Name = "刪除")]
    Remove,
    [SlashCommandChoice(Name = "修改")]
    Modify,
}

public enum ExtensionOption
{
    [SlashCommandChoice(Name = "Audio")]
    Audio,
    [SlashCommandChoice(Name = "Video")]
    Video,
}
