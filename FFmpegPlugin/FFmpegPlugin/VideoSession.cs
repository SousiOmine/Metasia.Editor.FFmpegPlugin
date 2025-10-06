using System.Diagnostics;
using FFmpegPlugin.Cache;
using FFmpegPlugin.Decode;

namespace FFmpegPlugin;

public class VideoSession : IDisposable
{
    public readonly string Path;
    private readonly FFmpegDecodeSession _decodeSession;
    private readonly FrameCache _frameCache;
    private Task? _prefetchTask = null;
    private TimeSpan _lastPrefetchTime = TimeSpan.MinValue;

    public VideoSession(string videoPath, int maxCacheSize = 1000)
    {
        Path = videoPath;
        _decodeSession = new FFmpegDecodeSession(videoPath);
        _frameCache = new FrameCache(maxCacheSize);
    }

    public async Task<FrameItem> GetFrameAsync(TimeSpan time)
    {
        Debug.WriteLine($"GetFrameAsync呼び出し: {Path}, {time}");
        var frame = _frameCache.TryGetFrame(Path, time, TimeSpan.FromMilliseconds(100));
        if (frame is not null)
        {
            Debug.WriteLine($"キャッシュから取得: {Path}, {time}");
            
            // キャッシュヒット時も先読みプリフェッチを実行
            TriggerPrefetchIfNeeded(time);
            return frame;
        }

        // 2. キャッシュにない場合、単一フレームだけを高速にデコードして即座に返す
        Debug.WriteLine($"キャッシュミス。単一フレームをデコード: {Path}, {time}");
        var singleFrame = await _decodeSession.GetSingleFrameAsync(time);
        
        if (singleFrame is null)
        {
            throw new InvalidOperationException($"フレームのデコードに失敗しました: {Path}, {time}");
        }
        
        _frameCache.Add(singleFrame); // 取得したフレームをキャッシュに追加

        // 3. バックグラウンドで先方向フレームをプリフェッチ（重複デコードを防止）
        TriggerPrefetchIfNeeded(time);

        return singleFrame;
    }

    private void TriggerPrefetchIfNeeded(TimeSpan currentTime)
    {
        // 前回のプリフェッチから十分離れていない場合はスキップ
        if (_lastPrefetchTime != TimeSpan.MinValue)
        {
            var deltaSeconds = Math.Abs((currentTime - _lastPrefetchTime).TotalSeconds);
            if (deltaSeconds < 1.0)
                return;
        }

        // 既にプリフェッチが実行中の場合はスキップ
        if (_prefetchTask != null && !_prefetchTask.IsCompleted)
            return;

        _lastPrefetchTime = currentTime;

        // プリフェッチを開始（既にキャッシュにあるフレームはスキップ）
        _prefetchTask = Task.Run(async () =>
        {
            Debug.WriteLine($"バックグラウンドプリフェッチ開始: {Path}, 開始時間={currentTime}");
            
            // 現在位置より少し先から3秒分をプリフェッチ
            var prefetchStart = currentTime + TimeSpan.FromMilliseconds(500);
            var prefetchDuration = TimeSpan.FromSeconds(3);
            
            // 既にキャッシュにある範囲はスキップ
            if (_frameCache.Contains(Path, prefetchStart, TimeSpan.FromMilliseconds(100)))
            {
                Debug.WriteLine($"プリフェッチ範囲は既にキャッシュ済み: {Path}, {prefetchStart}");
                return;
            }

            await foreach (var prefetchFrame in _decodeSession.DecodeAsync(prefetchStart, prefetchDuration))
            {
                _frameCache.Add(prefetchFrame);
            }
            Debug.WriteLine($"バックグラウンドプリフェッチ完了: {Path}");
        });
    }

    public void Dispose()
    {
        _decodeSession.Dispose();
        _frameCache.Dispose();
        GC.SuppressFinalize(this);
    }
}
