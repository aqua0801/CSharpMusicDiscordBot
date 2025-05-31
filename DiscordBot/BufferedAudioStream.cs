public class BufferedAudioStream : Stream
{
    private readonly MemoryStream _buffer = new();
    private long _readPosition = 0;
    private bool _isPaused = false;
    private bool _isFeedingComplete = false;
    private readonly object _lock = new();
    private readonly SemaphoreSlim _dataAvailable = new(0);

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _buffer.Length;
    public override long Position { get => _readPosition; set => throw new NotSupportedException(); }

    public void Pause()
    {
        lock (_lock) _isPaused = true;
    }

    public void Resume()
    {
        lock (_lock)
        {
            _isPaused = false;
            _dataAvailable.Release();
        }
    }

    public void MarkFeedingComplete()
    {
        _isFeedingComplete = true;
        _dataAvailable.Release();
    }

    public async Task FeedFromStreamAsync(Stream source)
    {
        byte[] temp = new byte[4096];
        int bytesRead;

        while ((bytesRead = await source.ReadAsync(temp, 0, temp.Length)) > 0)
        {
            lock (_lock)
            {
                long pos = _buffer.Position;
                _buffer.Position = _buffer.Length;
                _buffer.Write(temp, 0, bytesRead);
                _buffer.Position = pos;
            }
            _dataAvailable.Release();
        }
        MarkFeedingComplete();
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        while (true)
        {
            if (cancellationToken.IsCancellationRequested)
                return 0;
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

                if (_isFeedingComplete)
                    return 0;
            }
            await _dataAvailable.WaitAsync(cancellationToken);
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
    }

    #region NotSupported
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    #endregion
}
