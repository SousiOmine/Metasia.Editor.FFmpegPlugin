namespace FFmpegPlugin.Decode;

internal sealed class SequentialDecodeWorker : IAsyncDisposable
{
    private static readonly TimeSpan DefaultDecodeChunkLength = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DefaultTargetLookAhead = TimeSpan.FromSeconds(2);

    private readonly FFmpegDecodeSession _decodeSession;
    private readonly TimeSpan _frameDuration;
    private TimeSpan _decodeChunkLength;
    private TimeSpan _targetLookAhead;
    private readonly Action<FrameItem> _onDecodedFrame;
    private readonly Action<Exception>? _onWorkerError;
    private readonly SemaphoreSlim _demandSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Lock _stateLock = new();

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private TimeSpan _demandTime = TimeSpan.MinValue;
    private TimeSpan _decodedUntil = TimeSpan.MinValue;
    private bool _disposed;

    internal SequentialDecodeWorker(
        FFmpegDecodeSession decodeSession,
        TimeSpan frameDuration,
        Action<FrameItem> onDecodedFrame,
        Action<Exception>? onWorkerError = null,
        TimeSpan? decodeChunkLength = null,
        TimeSpan? targetLookAhead = null)
    {
        ArgumentNullException.ThrowIfNull(decodeSession);
        ArgumentNullException.ThrowIfNull(onDecodedFrame);

        _decodeSession = decodeSession;
        _frameDuration = frameDuration > TimeSpan.Zero
            ? frameDuration
            : DecodeTime.ResolveFrameDuration(0);
        var initialChunkLength = decodeChunkLength ?? DefaultDecodeChunkLength;
        _decodeChunkLength = NormalizeDecodeChunkLength(initialChunkLength);

        var initialLookAhead = targetLookAhead ?? DefaultTargetLookAhead;
        _targetLookAhead = NormalizeTargetLookAhead(initialLookAhead, _decodeChunkLength, _frameDuration);
        _onDecodedFrame = onDecodedFrame;
        _onWorkerError = onWorkerError;
    }

    internal TimeSpan DecodedUntil
    {
        get
        {
            lock (_stateLock)
            {
                return _decodedUntil;
            }
        }
    }

    internal bool IsRunning
    {
        get
        {
            lock (_stateLock)
            {
                return _workerTask is { IsCompleted: false };
            }
        }
    }

    internal async Task EnsureStartedAt(TimeSpan requestTime, CancellationToken sessionToken)
    {
        ThrowIfDisposed();
        var startTime = ClampTime(requestTime);

        CancellationTokenSource? previousCts = null;
        Task? previousTask = null;

        await _lifecycleLock.WaitAsync(sessionToken).ConfigureAwait(false);
        try
        {
            CancellationTokenSource nextCts;
            Task nextTask;

            lock (_stateLock)
            {
                previousCts = _workerCts;
                previousTask = _workerTask;

                nextCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                _workerCts = nextCts;
                _demandTime = startTime;
                _decodedUntil = TimeSpan.MinValue;
                nextTask = Task.Run(() => RunLoopAsync(startTime, nextCts.Token), CancellationToken.None);
                _workerTask = nextTask;
            }

            SignalDemand();
        }
        finally
        {
            _lifecycleLock.Release();
        }

        if (previousCts is not null)
        {
            CancelSafely(previousCts);
        }

        if (previousTask is not null)
        {
            _ = ObserveWorkerShutdownAsync(previousTask, previousCts);
            return;
        }

        previousCts?.Dispose();
    }

    internal void UpdateDemand(TimeSpan requestTime)
    {
        var demand = ClampTime(requestTime);
        lock (_stateLock)
        {
            if (_workerTask is not { IsCompleted: false })
            {
                return;
            }

            if (_demandTime == TimeSpan.MinValue || demand > _demandTime)
            {
                _demandTime = demand;
            }
        }

        SignalDemand();
    }

    internal void UpdateStrategy(TimeSpan decodeChunkLength, TimeSpan targetLookAhead)
    {
        var normalizedChunkLength = NormalizeDecodeChunkLength(decodeChunkLength);
        var normalizedLookAhead = NormalizeTargetLookAhead(targetLookAhead, normalizedChunkLength, _frameDuration);

        lock (_stateLock)
        {
            _decodeChunkLength = normalizedChunkLength;
            _targetLookAhead = normalizedLookAhead;
        }

        SignalDemand();
    }

    internal async Task StopAsync()
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            CancellationTokenSource? cts;
            Task? task;
            lock (_stateLock)
            {
                cts = _workerCts;
                task = _workerTask;
                _workerCts = null;
                _workerTask = null;
                _decodedUntil = TimeSpan.MinValue;
                _demandTime = TimeSpan.MinValue;
            }

            if (cts is not null)
            {
                CancelSafely(cts);
            }
            SignalDemand();

            if (task is not null)
            {
                try
                {
                    await task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // normal cancellation path
                }
                catch (Exception) when (cts?.IsCancellationRequested == true)
                {
                    // cancellation race while terminating FFmpeg process
                }
            }

            cts?.Dispose();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _demandSignal.Dispose();
        _lifecycleLock.Dispose();
    }

    private async Task RunLoopAsync(TimeSpan startTime, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var frame in _decodeSession.DecodeContinuousAsync(startTime, cancellationToken))
            {
                _onDecodedFrame(frame);

                lock (_stateLock)
                {
                    if (frame.Time > _decodedUntil)
                    {
                        _decodedUntil = frame.Time;
                    }
                }

                while (true)
                {
                    var shouldWait = false;
                    lock (_stateLock)
                    {
                        // Stop consuming the pipe when enough frames are buffered.
                        // FFmpeg blocks on pipe write and the same process stays alive.
                        shouldWait = ShouldWaitForDemandLocked();
                    }

                    if (!shouldWait)
                    {
                        break;
                    }

                    await _demandSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // cancellation is expected on stop/restart
        }
        catch (Exception ex)
        {
            _onWorkerError?.Invoke(ex);
        }
    }

    private bool ShouldWaitForDemandLocked()
    {
        if (_demandTime == TimeSpan.MinValue)
        {
            return true;
        }

        if (_decodedUntil == TimeSpan.MinValue)
        {
            return false;
        }

        var effectiveLookAhead = _targetLookAhead < _decodeChunkLength
            ? _decodeChunkLength
            : _targetLookAhead;
        var target = ClampTime(_demandTime + effectiveLookAhead);
        return _decodedUntil >= target;
    }

    private void SignalDemand()
    {
        if (_demandSignal.CurrentCount == 0)
        {
            _demandSignal.Release();
        }
    }

    private static void CancelSafely(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (AggregateException)
        {
            // cancellation callbacks from FFmpeg process can throw if process already exited
        }
        catch (ObjectDisposedException)
        {
            // token already disposed
        }
    }

    private async Task ObserveWorkerShutdownAsync(Task task, CancellationTokenSource? cts)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts?.IsCancellationRequested == true)
        {
            // previous worker cancellation
        }
        catch (Exception) when (cts?.IsCancellationRequested == true)
        {
            // FFMpegCore cancellation race while restarting worker
        }
        catch (Exception ex)
        {
            _onWorkerError?.Invoke(ex);
        }
        finally
        {
            cts?.Dispose();
        }
    }

    private static TimeSpan NormalizeDecodeChunkLength(TimeSpan decodeChunkLength)
    {
        if (decodeChunkLength <= TimeSpan.Zero)
        {
            return DefaultDecodeChunkLength;
        }

        return decodeChunkLength;
    }

    private static TimeSpan NormalizeTargetLookAhead(TimeSpan targetLookAhead, TimeSpan decodeChunkLength, TimeSpan frameDuration)
    {
        if (targetLookAhead <= TimeSpan.Zero)
        {
            return DefaultTargetLookAhead;
        }

        var minLookAhead = decodeChunkLength < frameDuration * 2
            ? frameDuration * 2
            : decodeChunkLength;
        return targetLookAhead < minLookAhead ? minLookAhead : targetLookAhead;
    }

    private TimeSpan ClampTime(TimeSpan time)
    {
        return DecodeTime.ClampToMedia(time, _decodeSession.Duration, _frameDuration);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(SequentialDecodeWorker));
    }
}
