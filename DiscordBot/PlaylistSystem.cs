using NetCord;
using NetCord.Gateway.Voice;
using NetCord.Rest;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace DiscordBot;

public readonly record struct QueueEntry(WebOption Source, string Url);

public static class PlaylistRegistry
{
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromSeconds(6);

    private static readonly ConcurrentDictionary<ulong, Playlist> _entries = new();

    public static Playlist GetOrCreate(ulong guildId, VoiceClient vc, ulong channelId)
        => _entries.GetOrAdd(guildId, _ => new Playlist(guildId, vc, channelId));

    public static Playlist? Get(ulong guildId)
        => _entries.TryGetValue(guildId, out var p) ? p : null;

    public static void Remove(Playlist playlist)
        => _entries.TryRemove(new KeyValuePair<ulong, Playlist>(playlist.GuildId, playlist));

    public static async Task StopAllAsync()
    {
        foreach (var playlist in _entries.Values)
            await playlist.StopAsync();
    }
}

public sealed class Playlist : IAsyncDisposable
{
    private const int FrameBytes = 3840; // 20ms @ 48kHz, stereo, s16

    public ulong GuildId { get; }
    public ulong ChannelId { get; }
    public bool IsPaused => _isPaused;
    public bool IsRepeat => _repeat;
    public int PlayingIndex => _playingIndex;
    public AudioInfo? CurrentTrack => _currentTrack;

    private readonly VoiceClient _vc;

    private readonly List<QueueEntry> _queue = new();
    private readonly Lock _queueLock = new();

    private volatile bool _isPaused;
    private volatile bool _repeat;
    private volatile int _playingIndex;
    private int _started;
    private int _disposeGuard;

    private DateTime _startTime;
    private DateTime _pauseTime;

    private volatile AudioInfo? _currentTrack;
    private Stream? _discordStream;

    private CancellationTokenSource _skipCts = new();
    private readonly CancellationTokenSource _stopCts = new();

    private Interaction? _interaction;
    private RestMessage? _message;

    public Playlist(ulong guildId, VoiceClient vc, ulong channelId)
    {
        GuildId = guildId;
        ChannelId = channelId;
        _vc = vc;
    }

    public int Count
    {
        get { lock (_queueLock) return _queue.Count; }
    }

    public void Enqueue(IEnumerable<QueueEntry> entries)
    {
        lock (_queueLock) _queue.AddRange(entries);
    }

    private bool TryGetEntry(int index, out QueueEntry entry)
    {
        lock (_queueLock)
        {
            if (index >= 0 && index < _queue.Count)
            {
                entry = _queue[index];
                return true;
            }
        }
        entry = default;
        return false;
    }

    public bool TryStart(Interaction interaction)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0 || _stopCts.IsCancellationRequested)
            return false;

        _interaction = interaction;
        _playingIndex = 0;

        _ = Task.Run(RunAsync);
        return true;
    }

    private async Task RunAsync()
    {
        try
        {
            _discordStream = _vc.CreateVoiceStream();
            BackgroundLoop.RunPeriodic($"Playlist message update guild={GuildId}", PlaylistRegistry.UpdateInterval, PlaylistRegistry.UpdateInterval,
                async _ => await RefreshMessageAsync(),
                _stopCts.Token);

            while (!_stopCts.IsCancellationRequested && TryGetEntry(_playingIndex, out var entry))
            {
                var oldSkip = Interlocked.Exchange(ref _skipCts, new CancellationTokenSource());
                oldSkip.Dispose();

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(_skipCts.Token, _stopCts.Token);

                try
                {
                    await PlayTrackAsync(entry, linked.Token);
                }
                catch (Exception) when (linked.IsCancellationRequested)
                {
                    // Skip / stop: exceptions from the stream being closed after cancellation are expected
                }
                catch (Exception ex)
                {
                    // Move on to the next song if one fails; previously one parse failure ended the whole playlist
                    Log.Error($"[Playlist] Playback failed, skipping: {entry.Source} {entry.Url}", ex);
                    if (_repeat) _repeat = false; // Avoid endlessly replaying the same broken song
                }

                if (_stopCts.IsCancellationRequested) break;
                if (!_repeat) _playingIndex++;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[Playlist] Exception in playback loop guild={GuildId}", ex);
        }
        finally
        {
            await DisposeAsync();
        }
    }

    private async Task PlayTrackAsync(QueueEntry entry, CancellationToken ct)
    {
        var info = await MediaProcess.ResolveAudioUrlAlgorithm(entry.Source)(entry.Url)
            ?? throw new InvalidOperationException("Unable to resolve audio URL");

        _currentTrack = info;
        _isPaused = false;
        _startTime = DateTime.Now;

        Process? ffmpeg = null;
        try
        {
            ffmpeg = MediaProcess.StartStreamProcess(info);

            var buffer = new BufferedAudioStream();
            var feedTask = buffer.FeedFromStreamAsync(ffmpeg.StandardOutput.BaseStream, ct);

            if (_message is null && _interaction is not null)
                _message = await _interaction.SendFollowupMessageAsync(BuildMessageProperties());
            else
                await RefreshMessageAsync();

            await StreamAudioAsync(buffer, ct);
            await feedTask;
        }
        finally
        {
            ProcessRunner.KillQuietly(ffmpeg);
        }
    }

    private async Task StreamAudioAsync(BufferedAudioStream source, CancellationToken ct)
    {
        var buffer = new byte[FrameBytes];

        // leaveOpen: the underlying voice stream is shared by the whole playlist; previously each song created an OpusEncodeStream without disposing it, leaking one native encoder per song
        await using var opus = new OpusEncodeStream(_discordStream!, PcmFormat.Short, VoiceChannels.Stereo, OpusApplication.Audio, leaveOpen: true);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (_isPaused)
                {
                    await Task.Delay(20, ct);
                    continue;
                }

                int bytesRead = await source.ReadAsync(buffer, ct);
                if (bytesRead <= 0) break;

                if (bytesRead < buffer.Length)
                    Array.Clear(buffer, bytesRead, buffer.Length - bytesRead);

                await opus.WriteAsync(buffer, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            try
            {
                await opus.FlushAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Log.Warn("[Playlist] Failed to flush opus stream", ex);
            }
        }
    }

    // ── Messages ─────────────────────────────────────────────────

    public async Task RefreshMessageAsync()
    {
        var message = _message;
        if (message is null || _disposeGuard != 0) return;

        try
        {
            await message.ModifyAsync(msg =>
            {
                msg.Embeds = [BuildTrackEmbed()];
                msg.Components = new IMessageComponentProperties[] { ButtonHelper.BuildActionRow(this) };
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"[Playlist] Failed to update now-playing message guild={GuildId}", ex);
        }
    }

    private InteractionMessageProperties BuildMessageProperties()
        => new InteractionMessageProperties()
            .AddEmbeds(BuildTrackEmbed())
            .AddComponents(ButtonHelper.BuildActionRow(this));

    private EmbedProperties BuildTrackEmbed()
    {
        var track = _currentTrack;
        int index = _playingIndex;
        int count = Count;

        var elapsed = _isPaused ? _pauseTime - _startTime : DateTime.Now - _startTime;
        var total = TimeSpan.FromSeconds(track?.Duration ?? 0);
        string source = TryGetEntry(index, out var entry) ? entry.Source.ToString() : "?";
        string repeatTag = _repeat ? "[循環播放]" : "[正常播放]";

        return new EmbedProperties()
            .WithTitle($"[{source}] {track?.Title ?? "未知"} - {track?.Creator ?? "未知"} {repeatTag}")
            .WithDescription(
                $"{GlobalVariable.BotNickname} 正在唱第 {index + 1} 首歌，" +
                $"後面還有 {Math.Max(0, count - index - 1)} 首！\n" +
                BuildProgressBar(elapsed, total))
            .WithColor(new Color(0, 0, 139));
    }

    private string BuildProgressBar(TimeSpan current, TimeSpan total, int length = 40)
    {
        if (total.TotalSeconds <= 0) return "[?]";
        if (current > total) current = total;
        if (current < TimeSpan.Zero) current = TimeSpan.Zero;

        double ratio = current.TotalSeconds / total.TotalSeconds;
        int filled = (int)Math.Min(length - 1, length * ratio);
        string bar = $"{new string('=', filled)}>{new string('-', length - filled - 1)}";
        string status = _isPaused ? "暫停中" : "播放中";

        // Songs over an hour used to display as 05:00 / 10:00 (mm:ss drops the hours)
        string fmt = total.TotalHours >= 1 ? @"h\:mm\:ss" : @"mm\:ss";
        return $"[{bar}] {current.ToString(fmt)} / {total.ToString(fmt)} [{status}]";
    }

    // ── Controls ─────────────────────────────────────────────────

    public void TogglePause()
    {
        if (_isPaused)
            _startTime += DateTime.Now - _pauseTime;
        else
            _pauseTime = DateTime.Now;

        _isPaused = !_isPaused;
    }

    public void Skip()
    {
        try
        {
            _skipCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Switching songs right now, ignore
        }
    }

    public void ToggleRepeat() => _repeat = !_repeat;

    public Task StopAsync() => DisposeAsync().AsTask();

    public string GetCurrentUrl()
        => TryGetEntry(_playingIndex, out var entry) ? entry.Url : "https://www.youtube.com/watch?v=dQw4w9WgXcQ";

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeGuard, 1) != 0) return;

        _stopCts.Cancel();
        PlaylistRegistry.Remove(this);

        if (_discordStream is not null)
        {
            try
            {
                await _discordStream.DisposeAsync();
            }
            catch (Exception ex)
            {
                Log.Warn($"[Playlist] Failed to dispose voice stream guild={GuildId}", ex);
            }
        }

        if (_message is not null)
        {
            try
            {
                await _message.ModifyAsync(msg =>
                {
                    msg.Content = "已結束/終止";
                    msg.Embeds = [];
                    msg.Components = [];
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"[Playlist] Failed to update end message guild={GuildId}", ex);
            }
        }
    }
}

public static class ButtonHelper
{
    private const string Prefix = "pl:";
    private const string IdPause = "pr";
    private const string IdSkip = "sk";
    private const string IdStop = "st";
    private const string IdLoop = "lp";

    public static ActionRowProperties BuildActionRow(Playlist playlist)
    {
        ulong id = playlist.GuildId;
        return new ActionRowProperties().AddComponents(
            new ButtonProperties($"{Prefix}{IdPause}_{id}", "▶️ 暫停/繼續", ButtonStyle.Success),
            new ButtonProperties($"{Prefix}{IdSkip}_{id}", "⏭️ 下一首", ButtonStyle.Primary),
            new ButtonProperties($"{Prefix}{IdStop}_{id}", "⏹️ 終止播放", ButtonStyle.Danger),
            new ButtonProperties($"{Prefix}{IdLoop}_{id}", "🔁 循環播放", ButtonStyle.Secondary),
            new LinkButtonProperties(playlist.GetCurrentUrl(), "🔗 連結")
        );
    }

    public static async Task HandleAsync(ButtonInteraction interaction)
    {
        // Respond to Discord first (without a reply within 3 seconds the button shows "This interaction failed"); previously an exception in StopAsync meant no response was sent at all
        await interaction.SendResponseAsync(InteractionCallback.DeferredModifyMessage);

        if (!TryParseId(interaction.Data.CustomId, out var action, out var guildId))
        {
            Log.Warn($"Received unrecognized button id: {interaction.Data.CustomId}");
            return;
        }

        var playlist = PlaylistRegistry.Get(guildId);
        if (playlist is null) return;

        switch (action)
        {
            case IdPause: playlist.TogglePause(); break;
            case IdSkip: playlist.Skip(); return;
            case IdStop: await playlist.StopAsync(); return;
            case IdLoop: playlist.ToggleRepeat(); break;
            default:
                Log.Warn($"Unknown playlist button action: {action}");
                return;
        }

        await playlist.RefreshMessageAsync();
    }

    /// <summary>"pl:pr_123456789" → ("pr", 123456789)</summary>
    private static bool TryParseId(string customId, out string action, out ulong guildId)
    {
        action = "";
        guildId = 0;

        // Compatibility with old unprefixed buttons (messages sent before the bot restarted)
        var body = customId.StartsWith(Prefix, StringComparison.Ordinal) ? customId[Prefix.Length..] : customId;

        int sep = body.IndexOf('_');
        if (sep < 0 || !ulong.TryParse(body[(sep + 1)..], out guildId))
            return false;

        action = body[..sep];
        return true;
    }
}

/// <summary>Per-server "playlist name → URL" map, stored in Data/playlist.json.</summary>
public sealed class SavedPlaylistStore
{
    private readonly ConcurrentDictionary<ulong, ConcurrentDictionary<string, string>> _store = new();
    private readonly Lock _fileLock = new();
    private readonly string _path;
    private volatile bool _isDirty;

    public SavedPlaylistStore(string jsonFilePath, bool enableAutosave = true)
    {
        _path = jsonFilePath;
        Load();
        if (enableAutosave)
            BackgroundLoop.RunPeriodic("Playlist autosave", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10), ct =>
            {
                SaveIfDirty();
                return Task.CompletedTask;
            });
    }

    // ── CRUD ─────────────────────────────────────────────────────

    public bool TryAdd(ulong guildId, string name, string url)
    {
        var dict = _store.GetOrAdd(guildId, _ => new ConcurrentDictionary<string, string>());
        if (!dict.TryAdd(name, url)) return false;
        _isDirty = true;
        return true;
    }

    /// <summary>Updates an existing playlist URL and returns the old one. ("Modify" used to edit the copy returned by GetAll, so it was never saved)</summary>
    public bool TryUpdate(ulong guildId, string name, string newUrl, out string oldUrl)
    {
        oldUrl = "";
        if (!_store.TryGetValue(guildId, out var dict) || !dict.TryGetValue(name, out var current))
            return false;
        if (!dict.TryUpdate(name, newUrl, current))
            return false;

        oldUrl = current;
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

    public IReadOnlyDictionary<string, string> GetAll(ulong guildId)
        => _store.TryGetValue(guildId, out var dict)
            ? dict.ToDictionary()
            : new Dictionary<string, string>();

    // ── Save / load ──────────────────────────────────────────────

    private void Load()
    {
        lock (_fileLock)
        {
            _store.Clear();

            if (!File.Exists(_path))
            {
                Log.Warn($"{Path.GetFullPath(_path)} not found, starting with empty playlists");
                return;
            }

            // Throw on malformed data so startup fails — otherwise the next autosave would overwrite users' playlists with empty data
            var root = Json.Read<Dictionary<ulong, Dictionary<string, string>>>(_path);
            foreach (var (guildId, playlists) in root)
                _store[guildId] = new ConcurrentDictionary<string, string>(playlists);

            Log.Info($"Loaded playlists for {root.Count} server(s)");
        }
    }

    public void SaveIfDirty()
    {
        if (!_isDirty) return;
        _isDirty = false;

        try
        {
            lock (_fileLock)
            {
                var snapshot = _store.ToDictionary(kv => kv.Key, kv => kv.Value.ToDictionary());
                Json.WriteAtomic(_path, snapshot);
            }
        }
        catch (Exception ex)
        {
            _isDirty = true; // Retry on the next tick
            Log.Error($"Failed to save playlists to {_path}", ex);
        }
    }
}
