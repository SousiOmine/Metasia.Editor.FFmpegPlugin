using System.Collections.Concurrent;
using System.Threading;
using SkiaSharp;

namespace FFmpegPlugin.Decode;

public sealed class BitmapPool : IDisposable
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _maxPoolSize;
    private readonly ConcurrentBag<SKBitmap> _pool = new();
    private int _pooledCount;
    private bool _disposed;

    public BitmapPool(int width, int height, int maxPoolSize = 64)
    {
        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Width must be greater than 0.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "Height must be greater than 0.");
        }

        _width = width;
        _height = height;
        _maxPoolSize = Math.Max(1, maxPoolSize);
    }

    public SKBitmap Rent()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(BitmapPool));

        if (_pool.TryTake(out var bitmap))
        {
            Interlocked.Decrement(ref _pooledCount);
            return bitmap;
        }

        return new SKBitmap(_width, _height, SKColorType.Bgra8888, SKAlphaType.Premul);
    }

    public void Return(SKBitmap bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (_disposed ||
            bitmap.Width != _width ||
            bitmap.Height != _height ||
            bitmap.ColorType != SKColorType.Bgra8888)
        {
            bitmap.Dispose();
            return;
        }

        var pooled = Interlocked.Increment(ref _pooledCount);
        if (pooled <= _maxPoolSize)
        {
            _pool.Add(bitmap);
            return;
        }

        Interlocked.Decrement(ref _pooledCount);
        bitmap.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_pool.TryTake(out var bitmap))
        {
            bitmap.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
