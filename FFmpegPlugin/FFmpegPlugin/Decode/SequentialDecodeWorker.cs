namespace FFmpegPlugin.Decode;

internal sealed class SequentialDecodeWorker : IAsyncDisposable
{
    private static readonly TimeSpan DecodeChunkLength = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TargetLookAhead = TimeSpan.FromSeconds(3.5);

    private readonly FFmpegDecodeSession _decodeSession;
    private readonly TimeSpan _frameDuration;
    private readonly Action<FrameItem> _onDecodedFrame;
    private readonly Action<Exception>? _onWorkerError;
    private readonly SemaphoreSlim _demandSignal = new(0, int.MaxValue);
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly Lock _stateLock = new();

    private CancellationTokenSource? _workerCts;
    private Task? _workerTask;
    private TimeSpan _demandTime = TimeSpan.MinValue;
    private TimeSpan _decodedUntil = TimeSpan.MinValue;
    private TimeSpan _decodeCursor = TimeSpan.MinValue;
    private bool _disposed;

    internal SequentialDecodeWorker(
        FFmpegDecodeSession decodeSession,
        TimeSpan frameDuration,
        Action<FrameItem> onDecodedFrame,
        Action<Exception>? onWorkerError = null)
    {
        ArgumentNullException.ThrowIfNull(decodeSession);
        ArgumentNullException.ThrowIfNull(onDecodedFrame);

        _decodeSession = decodeSession;
        _frameDuration = frameDuration > TimeSpan.Zero
            ? frameDuration
            : TimeSpan.FromMilliseconds(16.666);
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

        await _lifecycleLock.WaitAsync(sessionToken).ConfigureAwait(false);
        try
        {
            CancellationTokenSource? previousCts;
            Task? previousTask;
            CancellationTokenSource nextCts;
            Task nextTask;

            lock (_stateLock)
            {
                previousCts = _workerCts;
                previousTask = _workerTask;

                nextCts = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
                _workerCts = nextCts;
                _demandTime = startTime;
                _decodeCursor = startTime;
                _decodedUntil = TimeSpan.MinValue;
                nextTask = Task.Run(() => RunLoopAsync(nextCts.Token), CancellationToken.None);
                _workerTask = nextTask;
            }

            SignalDemand();

            if (previousCts is not null)
            {
                CancelSafely(previousCts);
            }

            if (previousTask is not null)
            {
                try
                {
                    await previousTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // previous worker cancellation
                }
                catch (Exception) when (previousCts?.IsCancellationRequested == true)
                {
                    // FFMpegCore cancellation race while restarting worker
                }
            }

            previousCts?.Dispose();
        }
        finally
        {
            _lifecycleLock.Release();
        }
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
                _decodeCursor = TimeSpan.MinValue;
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

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var workItem = BuildWorkItem();
                if (workItem.ShouldWaitForDemand)
                {
                    await _demandSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var lastDecodedFrame = TimeSpan.MinValue;
                await foreach (var frame in _decodeSession.DecodeAsync(workItem.Start, workItem.Length, cancellationToken))
                {
                    _onDecodedFrame(frame);
                    lastDecodedFrame = frame.Time;
                }

                lock (_stateLock)
                {
                    if (lastDecodedFrame != TimeSpan.MinValue)
                    {
                        if (lastDecodedFrame > _decodedUntil)
                        {
                            _decodedUntil = lastDecodedFrame;
                        }

                        _decodeCursor = ClampTime(lastDecodedFrame + _frameDuration);
                    }
                    else
                    {
                        var advanced = ClampTime(workItem.Start + workItem.Length);
                        if (advanced > _decodeCursor)
                        {
                            _decodeCursor = advanced;
                        }

                        if (workItem.Start > _decodedUntil)
                        {
                            _decodedUntil = workItem.Start;
                        }
                    }
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

    private WorkItem BuildWorkItem()
    {
        lock (_stateLock)
        {
            if (_demandTime == TimeSpan.MinValue)
            {
                return WorkItem.WaitForDemand;
            }

            if (_decodeCursor == TimeSpan.MinValue)
            {
                _decodeCursor = _demandTime;
            }

            var target = ClampTime(_demandTime + TargetLookAhead);
            if (_decodedUntil != TimeSpan.MinValue && _decodedUntil >= target)
            {
                return WorkItem.WaitForDemand;
            }

            var start = ClampTime(_decodeCursor);
            if (start >= target)
            {
                _decodedUntil = target;
                return WorkItem.WaitForDemand;
            }

            var remaining = target - start;
            var length = remaining < DecodeChunkLength ? remaining : DecodeChunkLength;
            if (length <= TimeSpan.Zero)
            {
                return WorkItem.WaitForDemand;
            }

            var duration = _decodeSession.Duration;
            if (duration > TimeSpan.Zero)
            {
                var maxLength = duration - start;
                if (maxLength <= TimeSpan.Zero)
                {
                    return WorkItem.WaitForDemand;
                }

                if (length > maxLength)
                {
                    length = maxLength;
                }
            }

            return new WorkItem(start, length, ShouldWaitForDemand: false);
        }
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

    private TimeSpan ClampTime(TimeSpan time)
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

        var max = duration - _frameDuration;
        if (max <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return time > max ? max : time;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(SequentialDecodeWorker));
    }

    private readonly record struct WorkItem(TimeSpan Start, TimeSpan Length, bool ShouldWaitForDemand)
    {
        internal static WorkItem WaitForDemand => new(TimeSpan.Zero, TimeSpan.Zero, true);
    }
}
