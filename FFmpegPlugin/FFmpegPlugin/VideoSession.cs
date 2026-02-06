using FFmpegPlugin.Cache;
using FFmpegPlugin.Decode;

namespace FFmpegPlugin;

public sealed class VideoSession : IDisposable
{
    private static readonly TimeSpan MinForegroundWindow = TimeSpan.FromMilliseconds(80);
    private static readonly TimeSpan MinPrefetchWindow = TimeSpan.FromMilliseconds(900);

    public readonly string Path;

    private readonly FFmpegDecodeSession _decodeSession;
    private readonly FrameCache _frameCache;
    private readonly TimeSpan _seekTolerance;
    private readonly TimeSpan _frameDuration;
    private readonly TimeSpan _foregroundDecodeWindow;
    private readonly TimeSpan _prefetchWindow;
    private readonly TimeSpan _prefetchStartLead;
    private readonly TimeSpan _sequentialDeltaThreshold;

    private readonly Lock _stateLock = new();
    private readonly CancellationTokenSource _lifetimeCts = new();

    private Task? _prefetchTask;
    private TimeSpan _lastRequestTime = TimeSpan.MinValue;
    private TimeSpan _prefetchedUntil = TimeSpan.MinValue;
    private bool _disposed;

    public VideoSession(string videoPath, int maxCacheSize = 240)
    {
        Path = videoPath;
        _decodeSession = new FFmpegDecodeSession(videoPath);

        _frameDuration = _decodeSession.Framerate > 0
            ? TimeSpan.FromSeconds(1.0 / _decodeSession.Framerate)
            : TimeSpan.FromMilliseconds(16.666);
        _seekTolerance = ResolveSeekTolerance(_frameDuration);
        _foregroundDecodeWindow = Max(MinForegroundWindow, _frameDuration * 8);
        _prefetchWindow = Max(MinPrefetchWindow, _frameDuration * 90);
        _prefetchStartLead = Max(_frameDuration * 24, _foregroundDecodeWindow);
        _sequentialDeltaThreshold = _frameDuration * 6;

        _frameCache = new FrameCache(maxCacheSize, _frameDuration);
    }

    public async Task<FrameItem> GetFrameAsync(TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(VideoSession));
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        var isSequential = IsLikelySequentialRequest(time);

        if (TryGetCachedFrame(time, out var cached))
        {
            UpdateRequestState(time, isSequential);
            TriggerPrefetchIfNeeded(time, isSequential);
            return cached;
        }

        if (!isSequential)
        {
            UpdateRequestState(time, isSequential: false);
            var seekFrame = await DecodeSingleFrameAndCacheAsync(time, _lifetimeCts.Token).ConfigureAwait(false);
            TriggerPrefetchIfNeeded(time, isSequential: true);
            return seekFrame;
        }

        await DecodeRangeIntoCacheAsync(time, _foregroundDecodeWindow, _lifetimeCts.Token).ConfigureAwait(false);
        if (TryGetCachedFrame(time, out cached))
        {
            UpdateRequestState(time, isSequential: true);
            TriggerPrefetchIfNeeded(time, isSequential: true);
            return cached;
        }

        var singleFrame = await DecodeSingleFrameAndCacheAsync(time, _lifetimeCts.Token).ConfigureAwait(false);
        UpdateRequestState(time, isSequential: true);
        TriggerPrefetchIfNeeded(time, isSequential: true);
        return singleFrame;
    }

    public async Task<FrameItem> GetFrameAsync(int frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(VideoSession));

        if (frame < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frame), "フレーム番号は0以上である必要があります。");
        }

        if (_decodeSession.Framerate <= 0)
        {
            throw new InvalidOperationException("動画のフレームレートが不正です。");
        }

        var time = TimeSpan.FromSeconds(frame / _decodeSession.Framerate);
        return await GetFrameAsync(time).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCts.Cancel();

        try
        {
            _prefetchTask?.Wait(TimeSpan.FromMilliseconds(500));
        }
        catch (AggregateException)
        {
            // Dispose 時のキャンセル例外は無視
        }

        _lifetimeCts.Dispose();
        _decodeSession.Dispose();
        _frameCache.Dispose();
        GC.SuppressFinalize(this);
    }

    private bool TryGetCachedFrame(TimeSpan time, out FrameItem frame)
    {
        var cached = _frameCache.TryGetFrame(time, _seekTolerance);
        if (cached is null)
        {
            frame = null!;
            return false;
        }

        frame = cached;
        return true;
    }

    private async Task<FrameItem> DecodeSingleFrameAndCacheAsync(TimeSpan time, CancellationToken cancellationToken)
    {
        var singleFrame = await _decodeSession.GetSingleFrameAsync(time, cancellationToken).ConfigureAwait(false);
        if (singleFrame is null)
        {
            throw new InvalidOperationException($"フレームのデコードに失敗しました: {Path}, {time}");
        }

        const int maxAttempts = 3;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (_frameCache.Add(singleFrame))
            {
                UpdatePrefetchedUntil(singleFrame.Time);
                return singleFrame;
            }

            var cached = _frameCache.TryGetFrame(time, _seekTolerance);
            if (cached is not null)
            {
                singleFrame.Dispose();
                return cached;
            }

            if (attempt < maxAttempts - 1)
            {
                await Task.Yield();
            }
        }

        singleFrame.Dispose();
        throw new InvalidOperationException($"キャッシュ重複時のフレーム再取得に失敗しました: {Path}, {time}");
    }

    private async Task DecodeRangeIntoCacheAsync(TimeSpan start, TimeSpan window, CancellationToken cancellationToken)
    {
        await foreach (var frame in _decodeSession.DecodeAsync(start, window, cancellationToken))
        {
            if (!_frameCache.Add(frame))
            {
                frame.Dispose();
                continue;
            }

            UpdatePrefetchedUntil(frame.Time);
        }
    }

    private void TriggerPrefetchIfNeeded(TimeSpan currentTime, bool isSequential)
    {
        if (!isSequential || _lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        var prefetchStart = currentTime + _frameDuration;

        lock (_stateLock)
        {
            if (_prefetchTask is { IsCompleted: false })
            {
                return;
            }

            if (_prefetchedUntil != TimeSpan.MinValue && prefetchStart <= _prefetchedUntil - _prefetchStartLead)
            {
                return;
            }

            if (_prefetchedUntil > prefetchStart)
            {
                prefetchStart = _prefetchedUntil + _frameDuration;
            }

            _prefetchTask = Task.Run(
                () => PrefetchAsync(prefetchStart, _prefetchWindow, _lifetimeCts.Token),
                CancellationToken.None);
        }
    }

    private async Task PrefetchAsync(TimeSpan start, TimeSpan window, CancellationToken cancellationToken)
    {
        try
        {
            await DecodeRangeIntoCacheAsync(start, window, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // セッション破棄時の停止
        }
        catch
        {
            // プリフェッチ失敗はフォアグラウンド取得で補償する
        }
        finally
        {
            lock (_stateLock)
            {
                if (_prefetchTask is { IsCompleted: true })
                {
                    _prefetchTask = null;
                }
            }
        }
    }

    private bool IsLikelySequentialRequest(TimeSpan currentTime)
    {
        lock (_stateLock)
        {
            if (_lastRequestTime == TimeSpan.MinValue)
            {
                return false;
            }

            var delta = currentTime - _lastRequestTime;
            return delta >= TimeSpan.Zero && delta <= _sequentialDeltaThreshold;
        }
    }

    private void UpdateRequestState(TimeSpan time, bool isSequential)
    {
        lock (_stateLock)
        {
            _lastRequestTime = time;

            if (!isSequential)
            {
                _prefetchedUntil = time;
            }
            else if (time > _prefetchedUntil)
            {
                _prefetchedUntil = time;
            }
        }
    }

    private void UpdatePrefetchedUntil(TimeSpan time)
    {
        lock (_stateLock)
        {
            if (time > _prefetchedUntil)
            {
                _prefetchedUntil = time;
            }
        }
    }

    private static TimeSpan ResolveSeekTolerance(TimeSpan frameDuration)
    {
        if (frameDuration <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(16.666);
        }

        return TimeSpan.FromTicks(Math.Max(1, frameDuration.Ticks - 1));
    }

    private static TimeSpan Max(TimeSpan left, TimeSpan right)
    {
        return left >= right ? left : right;
    }
}
