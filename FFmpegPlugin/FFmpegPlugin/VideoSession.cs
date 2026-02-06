using System.Diagnostics;
using FFmpegPlugin.Cache;
using FFmpegPlugin.Decode;

namespace FFmpegPlugin;

public sealed class VideoSession : IDisposable
{
    private static readonly TimeSpan WorkerRestartGap = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SequentialWaitTimeout = TimeSpan.FromMilliseconds(45);

    public readonly string Path;

    private readonly FFmpegDecodeSession _decodeSession;
    private readonly FrameCache _frameCache;
    private readonly SequentialDecodeWorker _decodeWorker;
    private readonly TimeSpan _seekTolerance;
    private readonly TimeSpan _frameDuration;
    private readonly TimeSpan _sequentialDeltaThreshold;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _frameArrivedSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private TimeSpan _lastRequestTime = TimeSpan.MinValue;
    private TimeSpan _workerTargetTime = TimeSpan.MinValue;
    private TimeSpan _workerDecodedUntil = TimeSpan.MinValue;
    private bool _workerNeedsRestart = true;
    private long _fallbackSingleDecodeCount;
    private long _workerRestartCount;
    private bool _disposed;

    internal long FallbackSingleDecodeCount => Interlocked.Read(ref _fallbackSingleDecodeCount);
    internal long WorkerRestartCount => Interlocked.Read(ref _workerRestartCount);

    public VideoSession(string videoPath, int maxCacheSize = 240)
    {
        Path = videoPath;
        _decodeSession = new FFmpegDecodeSession(videoPath);

        _frameDuration = _decodeSession.Framerate > 0
            ? TimeSpan.FromSeconds(1.0 / _decodeSession.Framerate)
            : TimeSpan.FromMilliseconds(16.666);
        _seekTolerance = ResolveSeekTolerance(_frameDuration);
        _sequentialDeltaThreshold = _frameDuration * 10;

        _frameCache = new FrameCache(maxCacheSize, _frameDuration);
        _decodeWorker = new SequentialDecodeWorker(
            _decodeSession,
            _frameDuration,
            OnWorkerFrameDecoded,
            ex => Debug.WriteLine($"シーケンシャルデコードワーカーエラー: {ex.Message}"));
    }

    public async Task<FrameItem> GetFrameAsync(TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(VideoSession));
        var requestTime = ClampRequestTime(time);
        var isSequential = IsLikelySequentialRequest(requestTime);

        if (TryGetCachedFrame(requestTime, out var cached))
        {
            if (!isSequential)
            {
                MarkWorkerRestartNeeded(requestTime);
            }
            else
            {
                await EnsureWorkerReadyAsync(requestTime).ConfigureAwait(false);
            }

            UpdateRequestState(requestTime);
            return cached;
        }

        if (!isSequential)
        {
            var seekFrame = await DecodeSingleFrameAndCacheAsync(requestTime, _lifetimeCts.Token).ConfigureAwait(false);
            MarkWorkerRestartNeeded(requestTime);
            UpdateRequestState(requestTime);
            return seekFrame;
        }

        await EnsureWorkerReadyAsync(requestTime).ConfigureAwait(false);

        var cachedFrame = await WaitForCachedFrameAsync(requestTime, SequentialWaitTimeout, _lifetimeCts.Token).ConfigureAwait(false);
        if (cachedFrame is not null)
        {
            UpdateRequestState(requestTime);
            return cachedFrame;
        }

        Interlocked.Increment(ref _fallbackSingleDecodeCount);
        var singleFrame = await DecodeSingleFrameAndCacheAsync(requestTime, _lifetimeCts.Token).ConfigureAwait(false);
        UpdateWorkerDemand(requestTime);
        UpdateRequestState(requestTime);
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
            _decodeWorker.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            // session cancellation
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"デコードワーカー停止エラー: {ex.Message}");
        }

        _frameArrivedSignal.Dispose();
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

    private async Task<FrameItem?> WaitForCachedFrameAsync(TimeSpan time, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (TryGetCachedFrame(time, out var cached))
        {
            return cached;
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        while (true)
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp);
            var remaining = timeout - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            var signaled = await _frameArrivedSignal.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            if (!signaled)
            {
                return null;
            }

            if (TryGetCachedFrame(time, out cached))
            {
                return cached;
            }
        }
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
                OnFrameAdded(singleFrame.Time);
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

    private void OnWorkerFrameDecoded(FrameItem frame)
    {
        if (!_frameCache.Add(frame))
        {
            frame.Dispose();
            return;
        }

        OnFrameAdded(frame.Time);
    }

    private void OnFrameAdded(TimeSpan frameTime)
    {
        lock (_stateLock)
        {
            if (frameTime > _workerDecodedUntil)
            {
                _workerDecodedUntil = frameTime;
            }
        }

        _frameArrivedSignal.Release();
    }

    private async Task RestartWorkerAsync(TimeSpan requestTime)
    {
        if (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        lock (_stateLock)
        {
            _workerTargetTime = requestTime;
            _workerDecodedUntil = TimeSpan.MinValue;
            _workerNeedsRestart = false;
        }

        Interlocked.Increment(ref _workerRestartCount);
        await _decodeWorker.EnsureStartedAt(requestTime, _lifetimeCts.Token).ConfigureAwait(false);
    }

    private async Task EnsureWorkerReadyAsync(TimeSpan requestTime)
    {
        var shouldRestart = false;
        lock (_stateLock)
        {
            shouldRestart = _workerNeedsRestart;
        }

        if (shouldRestart || ShouldRestartWorker(requestTime))
        {
            await RestartWorkerAsync(requestTime).ConfigureAwait(false);
            return;
        }

        UpdateWorkerDemand(requestTime);
    }

    private void MarkWorkerRestartNeeded(TimeSpan requestTime)
    {
        lock (_stateLock)
        {
            _workerNeedsRestart = true;
            _workerTargetTime = requestTime;
        }
    }

    private void UpdateWorkerDemand(TimeSpan requestTime)
    {
        lock (_stateLock)
        {
            if (_workerTargetTime == TimeSpan.MinValue || requestTime > _workerTargetTime)
            {
                _workerTargetTime = requestTime;
            }

            var decodedUntil = _decodeWorker.DecodedUntil;
            if (decodedUntil > _workerDecodedUntil)
            {
                _workerDecodedUntil = decodedUntil;
            }
        }

        _decodeWorker.UpdateDemand(requestTime);
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

    private bool ShouldRestartWorker(TimeSpan requestTime)
    {
        var isRunning = _decodeWorker.IsRunning;
        var decodedUntil = _decodeWorker.DecodedUntil;

        lock (_stateLock)
        {
            if (!isRunning)
            {
                return true;
            }

            if (_workerTargetTime == TimeSpan.MinValue)
            {
                return true;
            }

            if (decodedUntil > _workerDecodedUntil)
            {
                _workerDecodedUntil = decodedUntil;
            }

            if (_workerDecodedUntil == TimeSpan.MinValue)
            {
                return false;
            }

            var gap = requestTime - _workerDecodedUntil;
            return gap >= WorkerRestartGap;
        }
    }

    private void UpdateRequestState(TimeSpan time)
    {
        lock (_stateLock)
        {
            _lastRequestTime = time;
        }
    }

    private TimeSpan ClampRequestTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        var duration = _decodeSession.Duration;
        if (duration <= TimeSpan.Zero)
        {
            return time;
        }

        var maxTime = duration - _frameDuration;
        if (maxTime <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return time > maxTime ? maxTime : time;
    }

    private static TimeSpan ResolveSeekTolerance(TimeSpan frameDuration)
    {
        if (frameDuration <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(16.666);
        }

        return TimeSpan.FromTicks(Math.Max(1, frameDuration.Ticks - 1));
    }
}
