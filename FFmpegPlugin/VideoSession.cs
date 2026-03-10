using System.Diagnostics;
using FFmpegPlugin.Cache;
using FFmpegPlugin.Decode;

namespace FFmpegPlugin;

public sealed class VideoSession : IDisposable
{
    private static readonly TimeSpan SequentialWaitTimeout = TimeSpan.FromMilliseconds(45);
    private static readonly TimeSpan RecoverySequentialWaitTimeout = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan CatchupWaitTimeout = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan MaxSpeedEstimationDelta = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MinSequentialDeltaThreshold = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MaxCatchupDelta = TimeSpan.FromSeconds(2.5);
    private const long DefaultCacheBudgetBytes = 768L * 1024 * 1024;
    private const int MinAutoCacheFrames = 12;
    private const int MaxAutoCacheFrames = 240;
    private const int SequentialFallbackRestartThreshold = 2;
    private const double SpeedSmoothingAlpha = 0.25;
    private const double MinAdaptiveSpeed = 0.35;
    private const double MaxAdaptiveSpeed = 4.0;
    private const double HeadroomIncreaseFactor = 1.45;
    private const double HeadroomReduceFactor = 0.85;
    private const double StrategyUpdateThresholdMilliseconds = 18;

    public readonly string Path;

    private readonly FFmpegDecodeSession _decodeSession;
    private readonly FrameCache _frameCache;
    private readonly SequentialDecodeWorker _decodeWorker;
    private readonly TimeSpan _seekTolerance;
    private readonly TimeSpan _frameDuration;
    private readonly TimeSpan _sequentialDeltaThreshold;
    private readonly TimeSpan _baseDecodeChunkLength;
    private readonly TimeSpan _baseTargetLookAhead;
    private readonly TimeSpan _minDecodeChunkLength;
    private readonly TimeSpan _maxDecodeChunkLength;
    private readonly TimeSpan _minTargetLookAhead;
    private readonly TimeSpan _maxTargetLookAhead;
    private readonly Lock _stateLock = new();
    private readonly SemaphoreSlim _frameArrivedSignal = new(0, int.MaxValue);
    private readonly CancellationTokenSource _lifetimeCts = new();

    private TimeSpan _lastRequestTime = TimeSpan.MinValue;
    private long _lastRequestTimestamp;
    private TimeSpan _workerTargetTime = TimeSpan.MinValue;
    private TimeSpan _workerDecodedUntil = TimeSpan.MinValue;
    private TimeSpan _currentDecodeChunkLength;
    private TimeSpan _currentTargetLookAhead;
    private double _smoothedRequestSpeed = 1.0;
    private bool _workerNeedsRestart = true;
    private long _fallbackSingleDecodeCount;
    private long _workerRestartCount;
    private int _sequentialFallbackStreak;
    private int _workerPrimeInFlight;
    private bool _disposed;

    internal long FallbackSingleDecodeCount => Interlocked.Read(ref _fallbackSingleDecodeCount);
    internal long WorkerRestartCount => Interlocked.Read(ref _workerRestartCount);

    public VideoSession(string videoPath, int maxCacheSize = 0)
    {
        Path = videoPath;
        _decodeSession = new FFmpegDecodeSession(videoPath);

        _frameDuration = DecodeTime.ResolveFrameDuration(_decodeSession.Framerate);
        _seekTolerance = DecodeTime.ResolveSeekTolerance(_frameDuration);
        var sequentialThresholdTicks = Math.Max((_frameDuration * 10).Ticks, MinSequentialDeltaThreshold.Ticks);
        _sequentialDeltaThreshold = TimeSpan.FromTicks(sequentialThresholdTicks);

        var useLegacyTuning = maxCacheSize <= 0;
        var resolvedCacheSize = useLegacyTuning
            ? 240
            : ResolveCacheSize(maxCacheSize, _decodeSession.Width, _decodeSession.Height);
        var lookAhead = useLegacyTuning
            ? TimeSpan.FromSeconds(3.5)
            : ResolveTargetLookAhead(_frameDuration, resolvedCacheSize);
        var chunkLength = useLegacyTuning
            ? TimeSpan.FromSeconds(2)
            : ResolveDecodeChunkLength(_frameDuration, lookAhead);

        var lookAheadFrames = _frameDuration > TimeSpan.Zero
            ? Math.Max(1, (int)Math.Round(lookAhead.Ticks / (double)_frameDuration.Ticks))
            : 60;
        var chunkFrames = _frameDuration > TimeSpan.Zero
            ? Math.Max(1, (int)Math.Round(chunkLength.Ticks / (double)_frameDuration.Ticks))
            : 24;

        var minLookAheadFrames = Math.Clamp((int)Math.Round(resolvedCacheSize * 0.18), 8, 72);
        var maxLookAheadFrames = Math.Clamp((int)Math.Round(resolvedCacheSize * 0.9), 30, 220);
        var minChunkFrames = Math.Clamp(minLookAheadFrames / 2, 6, 54);
        var maxChunkFrames = Math.Clamp(maxLookAheadFrames / 2, 14, 120);

        _minTargetLookAhead = _frameDuration * minLookAheadFrames;
        _maxTargetLookAhead = _frameDuration * maxLookAheadFrames;
        _minDecodeChunkLength = _frameDuration * minChunkFrames;
        _maxDecodeChunkLength = _frameDuration * maxChunkFrames;
        _baseTargetLookAhead = ClampTimeSpan(_frameDuration * lookAheadFrames, _minTargetLookAhead, _maxTargetLookAhead);
        _baseDecodeChunkLength = ClampTimeSpan(_frameDuration * chunkFrames, _minDecodeChunkLength, _maxDecodeChunkLength);
        _currentTargetLookAhead = _baseTargetLookAhead;
        _currentDecodeChunkLength = _baseDecodeChunkLength;

        _frameCache = new FrameCache(resolvedCacheSize, _frameDuration);
        _decodeWorker = new SequentialDecodeWorker(
            _decodeSession,
            _frameDuration,
            OnWorkerFrameDecoded,
            ex => Debug.WriteLine($"シーケンシャルデコードワーカーエラー: {ex.Message}"),
            decodeChunkLength: _baseDecodeChunkLength,
            targetLookAhead: _baseTargetLookAhead);
    }

    public async Task<FrameItem> GetFrameAsync(TimeSpan time)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(VideoSession));
        var requestTime = ClampRequestTime(time);
        var motion = CaptureRequestMotion(requestTime);
        var isSequential = IsLikelySequentialRequest(requestTime);

        if (TryGetCachedFrame(requestTime, out var cached))
        {
            ResetSequentialFallbackStreak();
            if (!isSequential)
            {
                ResetMotionEstimate();
                MarkWorkerRestartNeeded(requestTime);
            }
            else
            {
                await EnsureWorkerReadyAsync(requestTime, motion).ConfigureAwait(false);
            }

            UpdateRequestState(requestTime);
            return cached;
        }

        if (!isSequential)
        {
            if (ShouldAttemptCatchup(motion))
            {
                MarkWorkerRestartNeeded(requestTime);
                await EnsureWorkerReadyAsync(requestTime, motion).ConfigureAwait(false);

                var catchupFrame = await WaitForCachedFrameAsync(requestTime, CatchupWaitTimeout, _lifetimeCts.Token).ConfigureAwait(false);
                if (catchupFrame is not null)
                {
                    ResetSequentialFallbackStreak();
                    UpdateRequestState(requestTime);
                    return catchupFrame;
                }
            }

            var seekFrame = await DecodeSingleFrameAndCacheAsync(requestTime, _lifetimeCts.Token).ConfigureAwait(false);
            ResetSequentialFallbackStreak();
            ResetMotionEstimate();
            MarkWorkerRestartNeeded(requestTime);
            PrimeWorkerForPlayback(requestTime);
            UpdateRequestState(requestTime);
            return seekFrame;
        }

        await EnsureWorkerReadyAsync(requestTime, motion).ConfigureAwait(false);

        var cachedFrame = await WaitForCachedFrameAsync(requestTime, ResolveSequentialWaitTimeout(), _lifetimeCts.Token).ConfigureAwait(false);
        if (cachedFrame is not null)
        {
            ResetSequentialFallbackStreak();
            UpdateRequestState(requestTime);
            return cachedFrame;
        }

        Interlocked.Increment(ref _fallbackSingleDecodeCount);
        var singleFrame = await DecodeSingleFrameAndCacheAsync(requestTime, _lifetimeCts.Token).ConfigureAwait(false);
        var fallbackStreak = IncrementSequentialFallbackStreak();
        if (fallbackStreak >= SequentialFallbackRestartThreshold)
        {
            MarkWorkerRestartNeeded(requestTime);
            await EnsureWorkerReadyAsync(requestTime, motion).ConfigureAwait(false);
            ResetSequentialFallbackStreak();
        }
        else
        {
            UpdateWorkerDemand(requestTime);
        }
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
                OnFrameAdded(singleFrame.Time, producedByWorker: false);
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

        OnFrameAdded(frame.Time, producedByWorker: true);
    }

    private void OnFrameAdded(TimeSpan frameTime, bool producedByWorker)
    {
        if (producedByWorker)
        {
            lock (_stateLock)
            {
                if (frameTime > _workerDecodedUntil)
                {
                    _workerDecodedUntil = frameTime;
                }
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

    private async Task EnsureWorkerReadyAsync(TimeSpan requestTime, RequestMotion motion = default)
    {
        ApplyAdaptiveWorkerStrategy(requestTime, motion);

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

    private void PrimeWorkerForPlayback(TimeSpan requestTime)
    {
        if (_lifetimeCts.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _workerPrimeInFlight, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureWorkerReadyAsync(requestTime).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // cancellation race while restarting worker
            }
            catch (ObjectDisposedException)
            {
                // session disposed
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"バックグラウンドワーカープライム失敗: {ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _workerPrimeInFlight, 0);
            }
        });
    }

    private void UpdateWorkerDemand(TimeSpan requestTime)
    {
        lock (_stateLock)
        {
            if (_workerTargetTime == TimeSpan.MinValue || requestTime > _workerTargetTime)
            {
                _workerTargetTime = requestTime;
            }
        }

        _decodeWorker.UpdateDemand(requestTime);
    }

    private RequestMotion CaptureRequestMotion(TimeSpan requestTime)
    {
        var now = Stopwatch.GetTimestamp();
        lock (_stateLock)
        {
            if (_lastRequestTime == TimeSpan.MinValue || _lastRequestTimestamp == 0)
            {
                return RequestMotion.None;
            }

            var delta = requestTime - _lastRequestTime;
            if (delta.Duration() > MaxSpeedEstimationDelta)
            {
                _smoothedRequestSpeed = 1.0;
                return new RequestMotion(delta, _smoothedRequestSpeed, HasSignal: false);
            }

            var elapsed = Stopwatch.GetElapsedTime(_lastRequestTimestamp, now);
            if (elapsed <= TimeSpan.Zero || elapsed > TimeSpan.FromSeconds(1.2))
            {
                return new RequestMotion(delta, _smoothedRequestSpeed, HasSignal: false);
            }

            var instantSpeed = delta.TotalSeconds / elapsed.TotalSeconds;
            if (!double.IsFinite(instantSpeed))
            {
                return new RequestMotion(delta, _smoothedRequestSpeed, HasSignal: false);
            }

            instantSpeed = Math.Clamp(instantSpeed, -MaxAdaptiveSpeed * 2, MaxAdaptiveSpeed * 2);
            _smoothedRequestSpeed = (_smoothedRequestSpeed * (1 - SpeedSmoothingAlpha)) + (instantSpeed * SpeedSmoothingAlpha);
            return new RequestMotion(delta, _smoothedRequestSpeed, HasSignal: true);
        }
    }

    private void ApplyAdaptiveWorkerStrategy(TimeSpan requestTime, RequestMotion motion)
    {
        if (_frameDuration <= TimeSpan.Zero)
        {
            return;
        }

        var speed = motion.HasSignal
            ? motion.SmoothedSpeed
            : ReadSmoothedSpeed();

        if (motion.HasSignal && motion.Delta < TimeSpan.Zero)
        {
            speed = MinAdaptiveSpeed;
        }

        var normalizedSpeed = Math.Clamp(Math.Abs(speed), MinAdaptiveSpeed, MaxAdaptiveSpeed);
        var headroom = GetWorkerHeadroom(requestTime);

        var headroomFactor = 1.0;
        if (headroom <= _frameDuration * 8)
        {
            headroomFactor = HeadroomIncreaseFactor;
        }
        else if (headroom >= _baseTargetLookAhead * 1.4)
        {
            headroomFactor = HeadroomReduceFactor;
        }

        var lookAheadScale = normalizedSpeed * headroomFactor;
        var chunkScale = normalizedSpeed * (headroomFactor > 1 ? 1.18 : 1.0);

        var nextLookAhead = ClampTimeSpan(ScaleTimeSpan(_baseTargetLookAhead, lookAheadScale), _minTargetLookAhead, _maxTargetLookAhead);
        var nextChunk = ClampTimeSpan(ScaleTimeSpan(_baseDecodeChunkLength, chunkScale), _minDecodeChunkLength, _maxDecodeChunkLength);
        if (nextChunk > nextLookAhead)
        {
            nextChunk = nextLookAhead;
        }

        var shouldUpdate = false;
        lock (_stateLock)
        {
            var chunkDelta = Math.Abs((_currentDecodeChunkLength - nextChunk).TotalMilliseconds);
            var lookAheadDelta = Math.Abs((_currentTargetLookAhead - nextLookAhead).TotalMilliseconds);
            if (chunkDelta >= StrategyUpdateThresholdMilliseconds || lookAheadDelta >= StrategyUpdateThresholdMilliseconds)
            {
                _currentDecodeChunkLength = nextChunk;
                _currentTargetLookAhead = nextLookAhead;
                shouldUpdate = true;
            }
        }

        if (shouldUpdate)
        {
            _decodeWorker.UpdateStrategy(nextChunk, nextLookAhead);
        }
    }

    private TimeSpan GetWorkerHeadroom(TimeSpan requestTime)
    {
        lock (_stateLock)
        {
            if (_workerDecodedUntil == TimeSpan.MinValue)
            {
                return TimeSpan.Zero;
            }

            var headroom = _workerDecodedUntil - requestTime;
            return headroom > TimeSpan.Zero ? headroom : TimeSpan.Zero;
        }
    }

    private double ReadSmoothedSpeed()
    {
        lock (_stateLock)
        {
            return _smoothedRequestSpeed;
        }
    }

    private void ResetMotionEstimate()
    {
        lock (_stateLock)
        {
            _smoothedRequestSpeed = 1.0;
        }
    }

    private TimeSpan ResolveSequentialWaitTimeout()
    {
        lock (_stateLock)
        {
            return _sequentialFallbackStreak > 0
                ? RecoverySequentialWaitTimeout
                : SequentialWaitTimeout;
        }
    }

    private bool ShouldAttemptCatchup(RequestMotion motion)
    {
        if (!motion.HasSignal || motion.Delta <= _sequentialDeltaThreshold)
        {
            return false;
        }

        return motion.Delta <= MaxCatchupDelta;
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

            if (_workerDecodedUntil == TimeSpan.MinValue)
            {
                return false;
            }

            // Keep a single persistent FFmpeg process during continuous playback.
            // Explicit seek/non-sequential requests are handled by _workerNeedsRestart.
            return false;
        }
    }

    private void UpdateRequestState(TimeSpan time)
    {
        lock (_stateLock)
        {
            _lastRequestTime = time;
            _lastRequestTimestamp = Stopwatch.GetTimestamp();
        }
    }

    private int IncrementSequentialFallbackStreak()
    {
        lock (_stateLock)
        {
            _sequentialFallbackStreak++;
            return _sequentialFallbackStreak;
        }
    }

    private void ResetSequentialFallbackStreak()
    {
        lock (_stateLock)
        {
            _sequentialFallbackStreak = 0;
        }
    }

    private TimeSpan ClampRequestTime(TimeSpan time)
    {
        return DecodeTime.ClampToMedia(time, _decodeSession.Duration, _frameDuration);
    }

    private static int ResolveCacheSize(int configuredSize, int width, int height)
    {
        if (configuredSize > 0)
        {
            return configuredSize;
        }

        var frameBytes = (long)width * height * 4;
        if (frameBytes <= 0)
        {
            return 96;
        }

        var fhdFrameBytes = 1920L * 1080 * 4;
        if (frameBytes <= fhdFrameBytes)
        {
            return MaxAutoCacheFrames;
        }

        var byBudget = (int)(DefaultCacheBudgetBytes / frameBytes);
        return Math.Clamp(byBudget, MinAutoCacheFrames, 120);
    }

    private static TimeSpan ResolveTargetLookAhead(TimeSpan frameDuration, int cacheFrames)
    {
        if (frameDuration <= TimeSpan.Zero)
        {
            return TimeSpan.FromSeconds(1);
        }

        if (cacheFrames >= 210)
        {
            return TimeSpan.FromSeconds(3.5);
        }

        var lookAheadFrames = Math.Clamp((int)Math.Round(cacheFrames * 0.8), 18, 180);
        return frameDuration * lookAheadFrames;
    }

    private static TimeSpan ResolveDecodeChunkLength(TimeSpan frameDuration, TimeSpan lookAhead)
    {
        if (frameDuration <= TimeSpan.Zero)
        {
            return TimeSpan.FromMilliseconds(250);
        }

        if (lookAhead >= TimeSpan.FromSeconds(3.5))
        {
            return TimeSpan.FromSeconds(2);
        }

        var lookAheadFrames = Math.Max(1, (int)Math.Round(lookAhead.Ticks / (double)frameDuration.Ticks));
        var chunkFrames = Math.Clamp(lookAheadFrames / 2, 8, 90);
        return frameDuration * chunkFrames;
    }

    private static TimeSpan ClampTimeSpan(TimeSpan value, TimeSpan min, TimeSpan max)
    {
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return value;
    }

    private static TimeSpan ScaleTimeSpan(TimeSpan value, double factor)
    {
        if (value <= TimeSpan.Zero || !double.IsFinite(factor) || factor <= 0)
        {
            return value;
        }

        var scaledTicks = (long)Math.Round(value.Ticks * factor);
        return scaledTicks <= 0 ? TimeSpan.FromTicks(1) : TimeSpan.FromTicks(scaledTicks);
    }

    private readonly record struct RequestMotion(TimeSpan Delta, double SmoothedSpeed, bool HasSignal)
    {
        internal static RequestMotion None => new(TimeSpan.Zero, 1.0, false);
    }
}
