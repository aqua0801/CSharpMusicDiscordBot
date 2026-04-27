public sealed class BufferedAudioStream : Stream
{
    private readonly MemoryStream _buffer = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _dataReady = new(0);

    private long _readPosition;
    private bool _isPaused;
    private bool _feedComplete;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _buffer.Length;
    public override long Position
    {
        get => _readPosition;
        set => throw new NotSupportedException();
    }


    public void Pause()
    {
        lock (_lock) _isPaused = true;
    }

    public void Resume()
    {
        lock (_lock)
        {
            _isPaused = false;
            _dataReady.Release();
        }
    }


    public async Task FeedFromStreamAsync(Stream source, CancellationToken ct = default)
    {
        var temp = new byte[4096];
        try
        {
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(temp, ct)) > 0)
            {
                lock (_lock)
                {
                    long savedPos = _buffer.Position;
                    _buffer.Position = _buffer.Length;
                    _buffer.Write(temp, 0, bytesRead);
                    _buffer.Position = savedPos;
                }
                _dataReady.Release();
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _feedComplete = true;
            _dataReady.Release();
        }
    }


    public override async Task<int> ReadAsync(
        byte[] buffer, int offset, int count, CancellationToken ct)
    {
        while (true)
        {
            if (ct.IsCancellationRequested) return 0;

            lock (_lock)
            {
                if (_isPaused) return 0;

                if (_readPosition < _buffer.Length)
                {
                    _buffer.Position = _readPosition;
                    int toRead = (int)Math.Min(count, _buffer.Length - _readPosition);
                    int read = _buffer.Read(buffer, offset, toRead);
                    _readPosition += read;
                    return read;
                }

                if (_feedComplete) return 0;
            }

            await _dataReady.WaitAsync(ct);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _buffer.Dispose(); _dataReady.Dispose(); }
        base.Dispose(disposing);
    }


    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}