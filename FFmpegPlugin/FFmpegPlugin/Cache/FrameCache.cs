using System.Collections.Concurrent;
using FFmpegPlugin.Decode;

namespace FFmpegPlugin.Cache;

public class FrameCache : IDisposable
{
    private readonly int _maxCacheSize;
    private readonly ConcurrentDictionary<string, LinkedList<FrameCacheItem>> _cacheByPath;
    private readonly Lock _lockObject = new();

    public FrameCache(int maxCacheSize = 1000)
    {
        _maxCacheSize = maxCacheSize;
        _cacheByPath = new ConcurrentDictionary<string, LinkedList<FrameCacheItem>>();
    }

    public FrameItem? TryGetFrame(string path, TimeSpan time, TimeSpan seekTolerance)
    {
        if (!_cacheByPath.TryGetValue(path, out var frames))
            return null;

        lock (_lockObject)
        {
            var node = frames.First;
            FrameCacheItem? bestMatch = null;
            LinkedListNode<FrameCacheItem>? bestMatchNode = null;
            double bestDistance = double.MaxValue;

            while (node != null)
            {
                var (isNear, distance) = node.Value.Frame.IsNearTime(time, seekTolerance);
                if (isNear && distance.TotalSeconds < bestDistance)
                {
                    bestMatch = node.Value;
                    bestMatchNode = node;
                    bestDistance = distance.TotalSeconds;
                }

                if (distance.TotalSeconds > seekTolerance.TotalSeconds * 2)
                    break;

                node = node.Next;
            }

            if (bestMatch != null && bestMatchNode != null)
            {
                bestMatch.LastUsedTime = DateTime.Now;
                frames.Remove(bestMatchNode);
                frames.AddFirst(bestMatchNode);
                return bestMatch.Frame;
            }
        }

        return null;
    }

    public void Add(FrameItem frame)
    {
        ArgumentNullException.ThrowIfNull(frame, nameof(frame));

        var frames = _cacheByPath.GetOrAdd(frame.Path, _ => new LinkedList<FrameCacheItem>());
        var cacheItem = new FrameCacheItem(frame, DateTime.Now);

        lock (_lockObject)
        {
            frames.AddFirst(cacheItem);

            int totalFrames = _cacheByPath.Values.Sum(list => list.Count);
            if (totalFrames > _maxCacheSize)
            {
                CleanupOldFrames();
            }
        }
    }

    private void CleanupOldFrames()
    {
        var allItems = _cacheByPath.Values
            .SelectMany(list => list)
            .OrderBy(item => item.LastUsedTime)
            .ToList();

        int itemsToRemove = allItems.Count - _maxCacheSize;
        if (itemsToRemove <= 0) return;

        foreach (var item in allItems.Take(itemsToRemove))
        {
            if (_cacheByPath.TryGetValue(item.Frame.Path, out var frames))
            {
                lock (_lockObject)
                {
                    var node = frames.Find(item);
                    if (node != null)
                    {
                        frames.Remove(node);
                        if (frames.Count == 0)
                        {
                            _cacheByPath.TryRemove(item.Frame.Path, out _);
                        }
                        // エビクション時にSKBitmapを適切に破棄
                        item.Frame.Dispose();
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        // すべてのキャッシュされたFrameItemを破棄
        foreach (var frames in _cacheByPath.Values)
        {
            foreach (var item in frames)
            {
                item.Frame.Dispose();
            }
        }
        _cacheByPath.Clear();
        GC.SuppressFinalize(this);
    }
}