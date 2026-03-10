using FFmpegPlugin.Decode;

namespace FFmpegPlugin.Cache;

public sealed class FrameCache : IDisposable
{
    private readonly int _maxCacheSize;
    private readonly long _quantizationTicks;
    private readonly Dictionary<long, FrameCacheItem> _frameCache = new();
    // Fixed-size FIFO ring buffer for sequential preview playback.
    private readonly FrameCacheItem?[] _ringBuffer;
    private readonly Lock _lockObject = new();
    private int _head;
    private int _count;

    public FrameCache(int maxCacheSize = 1000, TimeSpan? quantization = null)
    {
        _maxCacheSize = Math.Max(1, maxCacheSize);
        var quantizationTicks = quantization?.Ticks ?? TimeSpan.FromMilliseconds(10).Ticks;
        _quantizationTicks = Math.Max(1, quantizationTicks);
        _ringBuffer = new FrameCacheItem[_maxCacheSize];
    }

    public FrameItem? TryGetFrame(TimeSpan time, TimeSpan seekTolerance)
    {
        var targetTicks = time.Ticks;
        var toleranceTicks = seekTolerance.Ticks;
        var minTicks = Math.Max(0, targetTicks - toleranceTicks);
        var maxTicks = targetTicks + toleranceTicks;
        var minQuantized = QuantizeTime(minTicks);
        var maxQuantized = QuantizeTime(maxTicks);

        lock (_lockObject)
        {
            FrameCacheItem? bestMatch = null;
            var bestDistanceTicks = long.MaxValue;

            for (var quantizedTicks = minQuantized; quantizedTicks <= maxQuantized; quantizedTicks += _quantizationTicks)
            {
                if (!_frameCache.TryGetValue(quantizedTicks, out var cacheItem))
                {
                    continue;
                }

                var (isNear, distanceTicks) = cacheItem.Frame.IsNearTimeTicks(targetTicks, toleranceTicks);
                if (!isNear || distanceTicks >= bestDistanceTicks)
                {
                    continue;
                }

                bestMatch = cacheItem;
                bestDistanceTicks = distanceTicks;
            }

            if (bestMatch is null)
            {
                return null;
            }

            return bestMatch.Frame;
        }
    }

    public bool Contains(TimeSpan time, TimeSpan tolerance)
    {
        var targetTicks = time.Ticks;
        var toleranceTicks = tolerance.Ticks;
        var minTicks = Math.Max(0, targetTicks - toleranceTicks);
        var maxTicks = targetTicks + toleranceTicks;
        var minQuantized = QuantizeTime(minTicks);
        var maxQuantized = QuantizeTime(maxTicks);

        lock (_lockObject)
        {
            for (var quantizedTicks = minQuantized; quantizedTicks <= maxQuantized; quantizedTicks += _quantizationTicks)
            {
                if (!_frameCache.TryGetValue(quantizedTicks, out var cacheItem))
                {
                    continue;
                }

                var (isNear, _) = cacheItem.Frame.IsNearTimeTicks(targetTicks, toleranceTicks);
                if (isNear)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public bool Add(FrameItem frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var quantizedTicks = QuantizeTime(frame.Time.Ticks);

        lock (_lockObject)
        {
            if (_frameCache.ContainsKey(quantizedTicks))
            {
                return false;
            }

            var cacheItem = new FrameCacheItem(frame, quantizedTicks);
            _frameCache[quantizedTicks] = cacheItem;

            if (_count < _ringBuffer.Length)
            {
                var insertIndex = (_head + _count) % _ringBuffer.Length;
                _ringBuffer[insertIndex] = cacheItem;
                _count++;
            }
            else
            {
                var evicted = _ringBuffer[_head];
                if (evicted is not null)
                {
                    _frameCache.Remove(evicted.QuantizedTicks);
                    evicted.Frame.Dispose();
                }

                _ringBuffer[_head] = cacheItem;
                _head = (_head + 1) % _ringBuffer.Length;
            }

            return true;
        }
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            foreach (var item in _frameCache.Values)
            {
                item.Frame.Dispose();
            }

            _frameCache.Clear();
            Array.Clear(_ringBuffer, 0, _ringBuffer.Length);
            _head = 0;
            _count = 0;
        }

        GC.SuppressFinalize(this);
    }

    private long QuantizeTime(long ticks)
    {
        return (ticks / _quantizationTicks) * _quantizationTicks;
    }
}
