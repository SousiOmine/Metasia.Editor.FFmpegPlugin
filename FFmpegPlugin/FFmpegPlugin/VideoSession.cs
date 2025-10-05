using System.Diagnostics;
using FFmpegPlugin.Cache;
using FFmpegPlugin.Decode;

namespace FFmpegPlugin;

public class VideoSession : IDisposable
{
    public readonly string Path;
    private readonly FFmpegDecodeSession _decodeSession;
    private readonly FrameCache _frameCache;

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
            return frame;
        }

        try
        {
            var frameTask = _decodeSession.DecodeAsync(time, TimeSpan.FromMilliseconds(50), 30);
            await using var enumerator = frameTask.GetAsyncEnumerator();
            if(!await enumerator.MoveNextAsync())
            {
                throw new InvalidOperationException("フレームが見つかりませんでした");
            }
            var first = enumerator.Current;
            _frameCache.Add(first);

            _ = Task.Run(async () =>
            {
                await foreach (var frame in frameTask)
                {
                    Debug.WriteLine($"キャッシュに追加: {Path}, {frame.Time}");
                    _frameCache.Add(frame);
                }
            });
            Debug.WriteLine($"非同期で取得: {Path}, {time}");
            return first;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"フレーム取得エラー: {Path}, {time}, {ex.Message}");
            throw;
        }
    }

    public void Dispose()
    {
        _decodeSession.Dispose();
        _frameCache.Dispose(); // FrameCache.Dispose()が各FrameItem.Dispose()を呼ぶ
        GC.SuppressFinalize(this);
    }
}
