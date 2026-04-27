using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Rest;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DiscordBot;

public readonly record struct QueueEntry(WebOption Source, string Url);

public static class PlaylistRegistry
{
    public const int UpdateIntervalSeconds = 6;

    private static readonly ConcurrentDictionary<ulong, Playlist> _entries = new();

    public static Playlist GetOrCreate(Guild guild, VoiceClient vc, ulong channelId)
        => _entries.GetOrAdd(guild.Id, _ => new Playlist(guild, vc, channelId));

    public static Playlist? Get(ulong guildId)
        => _entries.TryGetValue(guildId, out var p) ? p : null;

    public static void Remove(ulong guildId)
        => _entries.TryRemove(guildId, out _);
}

public sealed class Playlist : IAsyncDisposable
{
    public Guild Guild { get; }
    public ulong ChannelId { get; }
    public bool IsPaused => _isPaused;
    public bool IsRepeat => _repeat;
    public int PlayingIndex => _playingIndex;
    public IReadOnlyList<QueueEntry> Queue => _queue;
    public MediaProcess.AudioInfo? CurrentTrack => _currentTrack;


    private readonly VoiceClient _vc;
    private readonly List<QueueEntry> _queue = new();

    private volatile bool _isPaused;
    private volatile bool _repeat;

    private int _playingIndex;
    private DateTime _startTime;
    private DateTime _pauseTime;

    private MediaProcess.AudioInfo? _currentTrack;
    private BufferedAudioStream? _currentAudio;
    private Stream? _discordStream;
    private Process? _ffmpeg;

    private CancellationTokenSource _skipCts = new();
    private readonly CancellationTokenSource _stopCts = new();

    private int _disposeGuard;

    private Interaction? _interaction;
    private RestMessage? _message;
    private Timer? _updateTimer;

    public Playlist(Guild guild, VoiceClient vc, ulong channelId)
    {
        Guild = guild;
        ChannelId = channelId;
        _vc = vc;
    }

    public void Enqueue(IEnumerable<QueueEntry> entries) => _queue.AddRange(entries);

    public async Task StartAsync(Interaction interaction)
    {
        if (_queue.Count == 0 || _stopCts.IsCancellationRequested) return;

        _interaction = interaction;
        _playingIndex = 0;
        _discordStream = _vc.CreateVoiceStream();

        _updateTimer = new Timer(
            async _ => await TryUpdateMessageAsync(),
            null,
            TimeSpan.FromSeconds(PlaylistRegistry.UpdateIntervalSeconds),
            TimeSpan.FromSeconds(PlaylistRegistry.UpdateIntervalSeconds));

        await RunPlaybackLoopAsync();
    }


    private async Task RunPlaybackLoopAsync()
    {
        try
        {
            while (!_stopCts.IsCancellationRequested && _playingIndex < _queue.Count)
            {
                _skipCts = new CancellationTokenSource();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    _skipCts.Token, _stopCts.Token);

                await PlayCurrentTrackAsync(linked.Token);

                if (_stopCts.IsCancellationRequested) break;
                if (!_repeat) _playingIndex++;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Playlist] 播放迴圈發生例外：{ex}");
        }
        finally
        {
            await DisposeAsync();
        }
    }

    private async Task PlayCurrentTrackAsync(CancellationToken ct)
    {
        var entry = _queue[_playingIndex];
        var resolve = MediaProcess.ResolveAudioUrlAlgorithm(entry.Source);
        var info = await resolve(entry.Url);

        if (info is null)
        {
            Console.WriteLine($"[Playlist] 無法解析 URL，跳過：{entry.Url}");
            return;
        }

        _currentTrack = info;
        _ffmpeg = await MediaProcess.CreateStreamAsync(info);

        if (_ffmpeg is null)
        {
            Console.WriteLine($"[Playlist] FFmpeg 建立失敗，跳過：{info.Title}");
            return;
        }

        _currentAudio = new BufferedAudioStream();

        _ = _currentAudio
            .FeedFromStreamAsync(_ffmpeg.StandardOutput.BaseStream, ct)
            .ContinueWith(
                t => Console.WriteLine($"[Feed Error] {t.Exception?.Flatten()}"),
                TaskContinuationOptions.OnlyOnFaulted);

        _startTime = DateTime.Now;

        if (_message is null)
            _message = await _interaction!.SendFollowupMessageAsync(BuildMessageProperties());

        await StreamAudioAsync(ct);

        _currentAudio.Dispose();
        _currentAudio = null;

        _ffmpeg.Kill(entireProcessTree: true);
        _ffmpeg.Dispose();
        _ffmpeg = null;
    }

    private async Task StreamAudioAsync(CancellationToken ct)
    {
        var buffer = new byte[3840];
        var opusStream = new OpusEncodeStream(
            _discordStream!, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    await Task.Delay(20, ct);
                    continue;
                }

                int bytesRead = await _currentAudio!.ReadAsync(buffer, 0, buffer.Length, ct);

                if (bytesRead <= 0) break; 

                if (bytesRead < buffer.Length)
                    Array.Clear(buffer, bytesRead, buffer.Length - bytesRead);

                await opusStream.WriteAsync(buffer, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            await opusStream.FlushAsync();
        }
    }


    private async Task TryUpdateMessageAsync()
    {
        if (_message is null || _stopCts.IsCancellationRequested) return;
        try
        {
            await _message.ModifyAsync(msg => msg.Embeds = new[] { BuildTrackEmbed() });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Error] 更新訊息失敗：{ex.Message}");
        }
    }

    private InteractionMessageProperties BuildMessageProperties()
        => new InteractionMessageProperties()
            .AddEmbeds(BuildTrackEmbed())
            .AddComponents(ButtonHelper.BuildActionRow(this));

    private EmbedProperties BuildTrackEmbed()
    {
        var elapsed = _isPaused ? _pauseTime - _startTime : DateTime.Now - _startTime;
        var total = TimeSpan.FromSeconds(_currentTrack?.Duration ?? 0);
        var entry = _queue[_playingIndex];
        var repeatTag = _repeat ? "[循環播放]" : "[正常播放]";

        return new EmbedProperties()
            .WithTitle(
                $"[{entry.Source}] {_currentTrack?.Title ?? "未知"} - " +
                $"{_currentTrack?.Creator ?? "未知"} {repeatTag}")
            .WithDescription(
                $"{GlobalVariable.BotNickname} 正在唱第 {_playingIndex + 1} 首歌，" +
                $"後面還有 {_queue.Count - _playingIndex - 1} 首！\n" +
                BuildProgressBar(elapsed, total))
            .WithColor(new Color(0, 0, 139));
    }

    private string BuildProgressBar(TimeSpan current, TimeSpan total, int length = 40)
    {
        if (total.TotalSeconds <= 0) return "[?]";
        if (current > total) current = total;

        double ratio = Math.Clamp(current.TotalSeconds / total.TotalSeconds, 0, 1);
        int filled = (int)Math.Min(length - 1, length * ratio);
        string bar = $"{new string('=', filled)}>{new string('-', length - filled - 1)}";
        string status = _isPaused ? "暫停中" : "播放中";

        return $"[{bar}] {current:mm\\:ss} / {total:mm\\:ss} [{status}]";
    }


    public void TogglePause()
    {
        if (_isPaused)
        {
            _startTime += DateTime.Now - _pauseTime; 
            _currentAudio?.Resume();
        }
        else
        {
            _pauseTime = DateTime.Now;
            _currentAudio?.Pause();
        }
        _isPaused = !_isPaused;
    }

    public void Skip() => _skipCts.Cancel();
    public void ToggleRepeat() => _repeat = !_repeat;
    public Task StopAsync() => DisposeAsync().AsTask();

    public string GetCurrentUrl()
        => _playingIndex < _queue.Count
            ? _queue[_playingIndex].Url
            : "https://www.youtube.com/watch?v=dQw4w9WgXcQ";


    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _disposeGuard, 1, 0) != 0) return;

        _stopCts.Cancel();
        _updateTimer?.Dispose();
        _currentAudio?.Dispose();

        if (_discordStream is not null)
            await _discordStream.DisposeAsync();

        if (_message is not null)
        {
            await _message.ModifyAsync(msg =>
            {
                msg.Content = "已結束/終止";
                msg.Embeds = null;
                msg.Components = null;
            });
        }

        PlaylistRegistry.Remove(Guild.Id);
    }
}


public static class ButtonHelper
{
    private const string IdPause = "pr";
    private const string IdSkip = "sk";
    private const string IdStop = "st";
    private const string IdLoop = "lp";

    public static ActionRowProperties BuildActionRow(Playlist playlist)
    {
        ulong id = playlist.Guild.Id;
        return new ActionRowProperties().AddComponents(
            new ButtonProperties($"{IdPause}_{id}", "▶️ 暫停/繼續", ButtonStyle.Success),
            new ButtonProperties($"{IdSkip}_{id}", "⏭️ 下一首", ButtonStyle.Primary),
            new ButtonProperties($"{IdStop}_{id}", "⏹️ 終止播放", ButtonStyle.Danger),
            new ButtonProperties($"{IdLoop}_{id}", "🔁 循環播放", ButtonStyle.Secondary),
            new LinkButtonProperties(playlist.GetCurrentUrl(), "🔗 連結")
        );
    }

    public static async Task HandleAsync(ButtonInteraction interaction)
    {
        var (action, guildId) = ParseId(interaction.Data.CustomId);
        var playlist = PlaylistRegistry.Get(guildId);

        if (playlist is not null)
        {
            switch (action)
            {
                case IdPause: playlist.TogglePause(); break;
                case IdSkip: playlist.Skip(); break;
                case IdStop: await playlist.StopAsync(); break;
                case IdLoop: playlist.ToggleRepeat(); break;
            }
        }

        await interaction.SendResponseAsync(InteractionCallback.DeferredModifyMessage);
    }

    /// <summary>"pr_123456789" → ("pr", 123456789)</summary>
    private static (string action, ulong guildId) ParseId(string customId)
    {
        int sep = customId.IndexOf('_');
        if (sep < 0) return (customId, 0);

        ulong.TryParse(customId[(sep + 1)..], out var guildId);
        return (customId[..sep], guildId);
    }
}


public sealed class SavedPlaylistStore
{
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<string, string>> _store = new();
    private readonly object _fileLock = new();
    private readonly string _path;
    private volatile bool _isDirty;

    public SavedPlaylistStore(string jsonFilePath, bool enableAutosave = true)
    {
        _path = jsonFilePath;
        Load();
        if (enableAutosave) StartAutosave();
    }

    // ── CRUD ──────────────────────────────────────────────

    public bool TryAdd(ulong guildId, string name, string url)
    {
        var dict = _store.GetOrAdd(guildId, _ => new ConcurrentDictionary<string, string>());
        if (!dict.TryAdd(name, url)) return false;
        _isDirty = true;
        return true;
    }

    public bool TryRemove(ulong guildId, string name)
    {
        if (!_store.TryGetValue(guildId, out var dict)) return false;
        if (!dict.TryRemove(name, out _)) return false;
        _isDirty = true;
        return true;
    }

    public Dictionary<string, string> GetAll(ulong guildId)
        => _store.TryGetValue(guildId, out var dict)
            ? dict.ToDictionary()
            : new Dictionary<string, string>();

    public bool HasGuild(ulong guildId) => _store.ContainsKey(guildId);

    // ── Serialization ────────────────────────────────────────────

    public void Load()
    {
        lock (_fileLock)
        {
            var root = Json.Read(_path);
            _store.Clear();

            foreach (var guildProp in root.Properties())
            {
                if (!ulong.TryParse(guildProp.Name, out var guildId)) continue;
                if (guildProp.Value is not JObject playlistObj) continue;

                var playlists = new ConcurrentDictionary<string, string>();
                foreach (var entry in playlistObj.Properties())
                    playlists[entry.Name] = entry.Value?.ToString() ?? string.Empty;

                _store[guildId] = playlists;
            }
        }
    }

    public void Save()
    {
        lock (_fileLock)
        {
            var root = new JObject();
            foreach (var (guildId, playlists) in _store)
            {
                var guildObj = new JObject();
                foreach (var (name, url) in playlists)
                    guildObj[name] = url;
                root[guildId.ToString()] = guildObj;
            }
            Json.Write(root, _path);
        }
    }


    private void StartAutosave()
    {
        var timer = new Timer(_ =>
        {
            if (!_isDirty) return;
            _isDirty = false;
            Save();
        }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        GlobalVariable.PermanentTimers.Add(timer);
    }
}