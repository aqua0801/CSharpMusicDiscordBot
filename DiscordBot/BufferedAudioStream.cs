using System.Threading.Channels;

namespace DiscordBot;

/// <summary>
/// Continuously reads ffmpeg's PCM output into memory so ffmpeg keeps draining the network stream while paused (avoids source connection timeouts).
///
/// Two problems with the old version:
/// 1. It kept the whole song in one MemoryStream and never released played data — a 1-hour song ≈ 690MB of PCM stuck in memory.
///    Now it's a chunk queue; chunks that have been read are left to the GC.
/// 2. ReadAsync returned 0 while paused, and Playlist treats 0 as "end of song" → pressing pause could skip to the next song.
///    Pausing is now handled by Playlist itself (it just stops reading); this class no longer cares.
/// </summary>
public sealed class BufferedAudioStream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = true,
    });

    private byte[]? _current;
    private int _currentOffset;

    public async Task FeedFromStreamAsync(Stream source, CancellationToken ct = default)
    {
        Exception? error = null;
        try
        {
            var temp = new byte[16 * 1024];
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(temp, ct)) > 0)
                await _chunks.Writer.WriteAsync(temp.AsSpan(0, bytesRead).ToArray(), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // Don't throw here: the exception is passed to the reader via TryComplete(error) and logged by the playback loop.
            error = ex;
        }
        finally
        {
            _chunks.Writer.TryComplete(error);
        }
    }

    /// <summary>Reads data; returns 0 when the source has ended (or was cancelled).</summary>
    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
    {
        try
        {
            while (_current is null || _currentOffset >= _current.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(ct))
                    return 0;
                if (_chunks.Reader.TryRead(out var next))
                {
                    _current = next;
                    _currentOffset = 0;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 0;
        }

        int toCopy = Math.Min(buffer.Length, _current.Length - _currentOffset);
        _current.AsMemory(_currentOffset, toCopy).CopyTo(buffer);
        _currentOffset += toCopy;
        return toCopy;
    }
}
