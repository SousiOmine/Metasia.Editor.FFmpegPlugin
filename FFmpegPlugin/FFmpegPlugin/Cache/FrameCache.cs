using System.Collections.Concurrent;
using FFmpegPlugin.Decode;

namespace FFmpegPlugin.Cache;

public sealed class FrameCache : IDisposable
{
    private readonly int _maxCacheSize;
    private readonly long _quantizationTicks;
    private readonly ConcurrentDictionary<long, FrameCacheItem> _frameCache = new();
    private readonly LinkedList<long> _lruList = new();
    private readonly Lock _lockObject = new();

    public FrameCache(int maxCacheSize = 1000, TimeSpan? quantization = null)
    {
        _maxCacheSize = Math.Max(1, maxCacheSize);
        var quantizationTicks = quantization?.Ticks ?? TimeSpan.FromMilliseconds(10).Ticks;
        _quantizationTicks = Math.Max(1, quantizationTicks);
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
            var bestDistance = double.MaxValue;

            for (var quantizedTicks = minQuantized; quantizedTicks <= maxQuantized; quantizedTicks += _quantizationTicks)
            {
                if (!_frameCache.TryGetValue(quantizedTicks, out var cacheItem))
                {
                    continue;
                }

                var (isNear, distance) = cacheItem.Frame.IsNearTime(time, seekTolerance);
                if (!isNear || distance.TotalSeconds >= bestDistance)
                {
                    continue;
                }

                bestMatch = cacheItem;
                bestDistance = distance.TotalSeconds;
            }

            if (bestMatch is null)
            {
                return null;
            }

            _lruList.Remove(bestMatch.LruNode);
            _lruList.AddFirst(bestMatch.LruNode);
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

                var (isNear, _) = cacheItem.Frame.IsNearTime(time, tolerance);
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

            var node = _lruList.AddFirst(quantizedTicks);
            var cacheItem = new FrameCacheItem(frame, node);
            _frameCache[quantizedTicks] = cacheItem;

            while (_frameCache.Count > _maxCacheSize && _lruList.Last is not null)
            {
                var evictionKey = _lruList.Last.Value;
                _lruList.RemoveLast();
                if (_frameCache.TryRemove(evictionKey, out var removedItem))
                {
                    removedItem.Frame.Dispose();
                }
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
            _lruList.Clear();
        }

        GC.SuppressFinalize(this);
    }

    private long QuantizeTime(long ticks)
    {
        return (ticks / _quantizationTicks) * _quantizationTicks;
    }
}
