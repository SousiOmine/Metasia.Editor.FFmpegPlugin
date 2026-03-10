using SkiaSharp;

namespace FFmpegPlugin.Decode;

public sealed class SingleFrameStream : Stream
{
    private readonly int _frameSize;
    private readonly BitmapPool _bitmapPool;
    private readonly SKBitmap _bitmap;
    private readonly IntPtr _pixels;
    private readonly Lock _writeLock = new();
    private int _filledBytes;
    private bool _taken;
    private bool _disposed;

    public SingleFrameStream(int width, int height, BitmapPool bitmapPool)
    {
        ArgumentNullException.ThrowIfNull(bitmapPool);

        _frameSize = RawFrameBuffer.ResolveFrameSizeOrThrow(width, height);
        _bitmapPool = bitmapPool;
        _bitmap = _bitmapPool.Rent();
        _pixels = _bitmap.GetPixels();
    }

    public bool HasFrame => System.Threading.Volatile.Read(ref _filledBytes) >= _frameSize;

    public SKBitmap TakeBitmap()
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(SingleFrameStream));
            }

            if (_taken)
            {
                throw new InvalidOperationException("Bitmap has already been taken.");
            }

            if (_filledBytes < _frameSize)
            {
                throw new InvalidOperationException("Frame is not complete.");
            }

            _taken = true;
            return _bitmap;
        }
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);

        if ((uint)offset > (uint)buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        if ((uint)count > (uint)(buffer.Length - offset))
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0)
        {
            return;
        }

        lock (_writeLock)
        {
            if (_disposed || _filledBytes >= _frameSize)
            {
                return;
            }

            var writableBytes = Math.Min(count, _frameSize - _filledBytes);
            RawFrameBuffer.CopyToUnmanaged(buffer, offset, IntPtr.Add(_pixels, _filledBytes), writableBytes);
            _filledBytes += writableBytes;
        }
    }

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        lock (_writeLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (disposing && !_taken)
            {
                _bitmapPool.Return(_bitmap);
            }

            base.Dispose(disposing);
        }
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }
}
