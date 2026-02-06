using System.Collections.Concurrent;
using FFmpegPlugin.Decode;

namespace FFmpegPlugin;

public class FrameProvider : IDisposable
{
    private readonly ConcurrentDictionary<string, VideoSession> _sessions = new();
    private bool _disposed = false;

    /// <summary>
    /// 非同期でフレームを取得する
    /// </summary>
    public async Task<FrameItem> GetFrameAsync(string path, TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(FrameProvider));

        var session = _sessions.GetOrAdd(path, p => new VideoSession(p));
        return await session.GetFrameAsync(time);
    }

    public async Task<FrameItem> GetFrameAsync(string path, int frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(FrameProvider));

        var session = _sessions.GetOrAdd(path, p => new VideoSession(p));
        return await session.GetFrameAsync(frame);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            
            // すべてのVideoSessionを破棄
            foreach (var session in _sessions.Values)
            {
                session.Dispose();
            }
            _sessions.Clear();
        }
        GC.SuppressFinalize(this);
    }
}
