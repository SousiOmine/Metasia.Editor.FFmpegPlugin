using System.Collections.Concurrent;
using FFmpegPlugin.Cache;
using FFmpegPlugin.Decode;

namespace FFmpegPlugin;

public class FrameProvider : IDisposable
{
    private readonly ConcurrentDictionary<string, FFmpegDecodeSession> _sessions = new();
    private readonly FrameCache _frameCache = new();
    private readonly object _lockObject = new();
    private bool _disposed = false;

    /// <summary>
    /// フレームを同期的に取得する
    /// </summary>
    public FrameItem GetFrame(string path, TimeSpan time)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FrameProvider));

        Console.WriteLine($"GetFrame: {path}, {time}");

        // キャッシュチェック
        FrameItem? cachedFrame = _frameCache.TryGetFrame(path, time, TimeSpan.FromMilliseconds(100));
        if (cachedFrame != null)
        {
            Console.WriteLine($"キャッシュヒット: {path}, {time}");
            return cachedFrame;
        }

        // キャッシュになければデコードして取得
        FFmpegDecodeSession session = GetOrCreateSession(path);

        try
        {
            // シングルフレームを非同期で取得し、同期的に待機
            var frameTask = session.GetSingleFrameAsync(time);
            FrameItem? frame = frameTask.GetAwaiter().GetResult();

            if (frame == null)
                throw new InvalidOperationException($"フレームの取得に失敗しました: {path}, {time}");

            // キャッシュに追加
            _frameCache.Add(frame);
            Console.WriteLine($"フレームデコード完了: {path}, {time}");

            // 周辺フレームをバックグラウンドでプリフェッチ
            _ = Task.Run(() => PrefetchSurroundingFrames(session, time));

            return frame;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"フレーム取得エラー: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 非同期でフレームを取得する（既存の互換性のため）
    /// </summary>
    public async Task<FrameItem> GetFrameAsync(string path, TimeSpan time)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(FrameProvider));

        Console.WriteLine($"GetFrameAsync: {path}, {time}");

        // キャッシュチェック
        FrameItem? cachedFrame = _frameCache.TryGetFrame(path, time, TimeSpan.FromMilliseconds(100));
        if (cachedFrame != null)
        {
            Console.WriteLine($"キャッシュヒット: {path}, {time}");
            return cachedFrame;
        }

        // キャッシュになければデコードして取得
        FFmpegDecodeSession session = GetOrCreateSession(path);

        try
        {
            FrameItem? frame = await session.GetSingleFrameAsync(time);
            if (frame == null)
                throw new InvalidOperationException($"フレームの取得に失敗しました: {path}, {time}");

            // キャッシュに追加
            _frameCache.Add(frame);
            Console.WriteLine($"フレームデコード完了: {path}, {time}");

            // 周辺フレームをバックグラウンドでプリフェッチ
            _ = Task.Run(() => PrefetchSurroundingFrames(session, time));

            return frame;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"フレーム取得エラー: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 指定されたパスのセッションを取得または作成する
    /// </summary>
    private FFmpegDecodeSession GetOrCreateSession(string path)
    {
        return _sessions.GetOrAdd(path, p => new FFmpegDecodeSession(p));
    }

    /// <summary>
    /// 周辺フレームをプリフェッチする
    /// </summary>
    private async Task PrefetchSurroundingFrames(FFmpegDecodeSession session, TimeSpan centerTime)
    {
        try
        {
            // 前後0.5秒のフレームをプリフェッチ
            var prefetchRange = TimeSpan.FromSeconds(0.5);
            var startTime = centerTime - prefetchRange;
            var endTime = centerTime + prefetchRange;

            if (startTime < TimeSpan.Zero)
                startTime = TimeSpan.Zero;

            var frameRate = 30.0; // 仮のフレームレート
            var frameInterval = TimeSpan.FromSeconds(1.0 / frameRate);

            await foreach (var frame in session.DecodeAsync(
                startTime,
                endTime - startTime,
                frameRate,
                CancellationToken.None))
            {
                // 既にキャッシュにあるフレームは追加しない
                if (_frameCache.TryGetFrame(frame.Path, frame.Time, TimeSpan.FromMilliseconds(50)) == null)
                {
                    _frameCache.Add(frame);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"プリフェッチエラー: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            foreach (var session in _sessions.Values)
            {
                session?.Dispose();
            }
            _sessions.Clear();
            _frameCache?.Dispose();
            _disposed = true;
        }
    }
}