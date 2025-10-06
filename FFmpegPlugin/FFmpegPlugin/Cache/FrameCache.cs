using System.Collections.Concurrent;
using System.Diagnostics;
using FFmpegPlugin.Decode;

namespace FFmpegPlugin.Cache;

public class FrameCache : IDisposable
{
    private readonly int _maxCacheSize;
    // 高速検索のための辞書: (path, timeTicks) -> FrameItem
    // 時間は量子化して格納（10ms = 100,000 Ticks単位）
    private readonly ConcurrentDictionary<(string, long), FrameCacheItem> _frameCache;
    // LRU管理用のリンクリスト
    private readonly LinkedList<(string, long)> _lruList;
    private readonly Lock _lockObject = new();
    
    // 時間の量子化単位（10ms = 100,000 Ticks）
    private const long QuantizationTicks = 100_000;

    public FrameCache(int maxCacheSize = 1000)
    {
        _maxCacheSize = maxCacheSize;
        _frameCache = new ConcurrentDictionary<(string, long), FrameCacheItem>();
        _lruList = new LinkedList<(string, long)>();
    }
    
    /// <summary>
    /// 時間を量子化してキーを作成
    /// </summary>
    private static long QuantizeTime(TimeSpan time)
    {
        return (time.Ticks / QuantizationTicks) * QuantizationTicks;
    }

    public FrameItem? TryGetFrame(string path, TimeSpan time, TimeSpan seekTolerance)
    {
        // 要求時間を中心に、許容範囲内のキャッシュエントリを探索
        long targetTicks = time.Ticks;
        long toleranceTicks = seekTolerance.Ticks;
        
        FrameCacheItem? bestMatch = null;
        long bestKey = -1;
        double bestDistance = double.MaxValue;

        // 量子化された時間範囲を計算
        long minTicks = Math.Max(0, targetTicks - toleranceTicks);
        long maxTicks = targetTicks + toleranceTicks;
        long minQuantized = QuantizeTime(TimeSpan.FromTicks(minTicks));
        long maxQuantized = QuantizeTime(TimeSpan.FromTicks(maxTicks));
        
        // 近傍の量子化時間を検索
        for (long quantizedTicks = minQuantized; quantizedTicks <= maxQuantized; quantizedTicks += QuantizationTicks)
        {
            var key = (path, quantizedTicks);
            if (_frameCache.TryGetValue(key, out var cacheItem))
            {
                var (isNear, distance) = cacheItem.Frame.IsNearTime(time, seekTolerance);
                if (isNear && distance.TotalSeconds < bestDistance)
                {
                    bestMatch = cacheItem;
                    bestKey = quantizedTicks;
                    bestDistance = distance.TotalSeconds;
                }
            }
        }

        if (bestMatch != null && bestKey >= 0)
        {
            lock (_lockObject)
            {
                // LRUリストを更新
                _lruList.Remove((path, bestKey));
                _lruList.AddFirst((path, bestKey));
                bestMatch.LastUsedTime = DateTime.Now;
            }
            
            Debug.WriteLine($"キャッシュマッチ: 要求={time}, フレーム={bestMatch.Frame.Time}, 差分={bestDistance * 1000:F3}ms");
            return bestMatch.Frame;
        }

        return null;
    }

    public bool Contains(string path, TimeSpan time, TimeSpan tolerance)
    {
        long targetTicks = time.Ticks;
        long toleranceTicks = tolerance.Ticks;
        
        long minTicks = Math.Max(0, targetTicks - toleranceTicks);
        long maxTicks = targetTicks + toleranceTicks;
        long minQuantized = QuantizeTime(TimeSpan.FromTicks(minTicks));
        long maxQuantized = QuantizeTime(TimeSpan.FromTicks(maxTicks));
        
        for (long quantizedTicks = minQuantized; quantizedTicks <= maxQuantized; quantizedTicks += QuantizationTicks)
        {
            if (_frameCache.ContainsKey((path, quantizedTicks)))
                return true;
        }
        return false;
    }

    public void Add(FrameItem frame)
    {
        ArgumentNullException.ThrowIfNull(frame, nameof(frame));

        // 時間を量子化してキーを作成
        long quantizedTicks = QuantizeTime(frame.Time);
        var key = (frame.Path, quantizedTicks);

        // 既に存在する場合はスキップ（重複防止）
        if (_frameCache.ContainsKey(key))
            return;

        var cacheItem = new FrameCacheItem(frame, DateTime.Now);

        lock (_lockObject)
        {
            _frameCache[key] = cacheItem;
            _lruList.AddFirst(key);

            // キャッシュサイズ超過時に最も古いものを削除
            while (_frameCache.Count > _maxCacheSize && _lruList.Last != null)
            {
                var oldestKey = _lruList.Last.Value;
                _lruList.RemoveLast();
                
                if (_frameCache.TryRemove(oldestKey, out var removedItem))
                {
                    removedItem.Frame.Dispose();
                }
            }
        }
    }

    public void Dispose()
    {
        lock (_lockObject)
        {
            // すべてのキャッシュされたFrameItemを破棄
            foreach (var item in _frameCache.Values)
            {
                item.Frame.Dispose();
            }
            _frameCache.Clear();
            _lruList.Clear();
        }
        GC.SuppressFinalize(this);
    }
}