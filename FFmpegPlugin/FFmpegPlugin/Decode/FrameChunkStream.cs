using System.Buffers;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SkiaSharp;

namespace FFmpegPlugin.Decode;

public sealed class FrameChunkStream : Stream
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _frameSize;
    private readonly string _videoPath;
    private readonly TimeSpan _startTime;
    private readonly double _framerate;
    private readonly ChannelWriter<FrameItem> _writer;
    private readonly CancellationToken _writeCancellationToken;
    private readonly Lock _writeLock = new();
    private readonly byte[] _frameBuffer;

    private int _filledBytes;
    private int _frameIndex;
    private bool _completed;

    public FrameChunkStream(
        int width,
        int height,
        string videoPath,
        TimeSpan startTime,
        double framerate,
        ChannelWriter<FrameItem> writer,
        CancellationToken writeCancellationToken = default)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be greater than 0.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be greater than 0.");
        }

        ArgumentNullException.ThrowIfNull(videoPath);
        ArgumentNullException.ThrowIfNull(writer);

        var frameSize = (long)width * height * 4;
        if (frameSize > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame size is too large.");
        }

        _width = width;
        _height = height;
        _frameSize = (int)frameSize;
        _videoPath = videoPath;
        _startTime = startTime;
        _framerate = framerate > 0 ? framerate : 60.0;
        _writer = writer;
        _writeCancellationToken = writeCancellationToken;
        _frameBuffer = ArrayPool<byte>.Shared.Rent(_frameSize);
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
            if (_completed)
            {
                return;
            }

            while (count > 0)
            {
                var writableBytes = Math.Min(count, _frameSize - _filledBytes);
                Buffer.BlockCopy(buffer, offset, _frameBuffer, _filledBytes, writableBytes);

                _filledBytes += writableBytes;
                offset += writableBytes;
                count -= writableBytes;

                if (_filledBytes == _frameSize)
                {
                    PublishCurrentFrame();
                }
            }
        }
    }

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        lock (_writeLock)
        {
            if (!_completed)
            {
                _completed = true;
                ArrayPool<byte>.Shared.Return(_frameBuffer);
                _writer.TryComplete();
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

    private void PublishCurrentFrame()
    {
        var bitmap = new SKBitmap(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
        try
        {
            Marshal.Copy(_frameBuffer, 0, bitmap.GetPixels(), _frameSize);

            var frameItem = new FrameItem
            {
                Path = _videoPath,
                Time = CalculateFrameTime(_frameIndex),
                Bitmap = bitmap
            };

            _frameIndex++;
            _filledBytes = 0;

            // Producer thread can briefly outrun the consumer. If channel is full,
            // wait here instead of allocating and queueing additional large frames.
            if (_writer.TryWrite(frameItem))
            {
                return;
            }

            try
            {
                _writer.WriteAsync(frameItem, _writeCancellationToken).AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (_writeCancellationToken.IsCancellationRequested)
            {
                frameItem.Dispose();
            }
            catch (ChannelClosedException)
            {
                frameItem.Dispose();
            }
        }
        catch
        {
            bitmap.Dispose();
            throw;
        }
    }

    private TimeSpan CalculateFrameTime(int frameIndex)
    {
        var seconds = _startTime.TotalSeconds + (frameIndex / _framerate);
        if (seconds >= TimeSpan.MaxValue.TotalSeconds)
        {
            return TimeSpan.MaxValue;
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
