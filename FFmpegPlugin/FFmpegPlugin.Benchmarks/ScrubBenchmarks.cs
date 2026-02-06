using BenchmarkDotNet.Attributes;
using FFmpegPlugin;
using FFmpegPlugin.Decode;
using FFMpegCore;

public class ScrubBenchmarks
{
    private const int RandomSeekCount = 60;
    private const int NearSeekCount = 60;
    private const int NearSeekOperationsPerInvoke = 256;
    private const int RealtimeOperationsPerInvoke = 60;
    private static readonly TimeSpan SequentialDecodeWindow = TimeSpan.FromSeconds(0.5);
    private static readonly TimeSpan RealTimePlaybackWindow = TimeSpan.FromSeconds(1);
    private const double TargetFps = 60.0;

    private FFmpegDecodeSession? _decodeSession;
    private VideoSession? _videoSession;
    private TimeSpan[] _randomSeekTimes = [];
    private TimeSpan[] _nearSeekTimes = [];
    private TimeSpan[] _sequentialStartTimes = [];
    private TimeSpan[] _realTimeStartTimes = [];
    private int _randomSeekIndex = -1;
    private int _nearSeekIndex = -1;
    private int _sequentialIndex = -1;
    private int _realTimeIndex = -1;
    private TimeSpan _realtimeBenchmarkStart;

    [GlobalSetup]
    public void Setup()
    {
        var env = BenchmarkEnvironment.Resolve();
        var media = FFProbe.Analyse(env.VideoPath);

        var duration = media.Duration;
        if (duration <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("動画の長さを取得できませんでした。");
        }

        _decodeSession = new FFmpegDecodeSession(env.VideoPath);
        _videoSession = new VideoSession(env.VideoPath);

        _randomSeekTimes = BuildRandomSeekTimes(duration, RandomSeekCount);
        _nearSeekTimes = BuildNearSeekTimes(duration, NearSeekCount, _decodeSession.Framerate);
        _sequentialStartTimes = BuildSequentialStartTimes(duration, SequentialDecodeWindow, 8);
        _realTimeStartTimes = BuildSequentialStartTimes(duration, RealTimePlaybackWindow, 8);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _videoSession?.Dispose();
        _decodeSession?.Dispose();
    }

    [Benchmark(Description = "RandomSeek_GetSingleFrameAsync")]
    public async Task<int> RandomSeek_GetSingleFrameAsync()
    {
        EnsureSessions();

        var time = Next(_randomSeekTimes, ref _randomSeekIndex);
        var frame = await _decodeSession!.GetSingleFrameAsync(time);
        if (frame is null)
        {
            throw new InvalidOperationException($"単一フレーム取得に失敗しました。time={time}");
        }

        var width = frame.Bitmap.Width;
        frame.Dispose();
        return width;
    }

    [Benchmark(Description = "NearSeek_GetFrameAsync", OperationsPerInvoke = NearSeekOperationsPerInvoke)]
    [MinIterationTime(100)]
    public async Task<int> NearSeek_GetFrameAsync()
    {
        EnsureSessions();

        var widthSum = 0;

        for (var i = 0; i < NearSeekOperationsPerInvoke; i++)
        {
            var time = Next(_nearSeekTimes, ref _nearSeekIndex);
            var frame = await _videoSession!.GetFrameAsync(time);
            widthSum += frame.Bitmap.Width;
        }

        return widthSum;
    }

    [Benchmark(Description = "SequentialDecode_DecodeAsync")]
    public async Task<int> SequentialDecode_DecodeAsync()
    {
        EnsureSessions();

        var start = Next(_sequentialStartTimes, ref _sequentialIndex);
        var decodedFrames = 0;

        await foreach (var frame in _decodeSession!.DecodeAsync(start, SequentialDecodeWindow))
        {
            decodedFrames++;
            frame.Dispose();
        }

        return decodedFrames;
    }

    [Benchmark(Description = "Realtime60fps_GetFrameAsync", OperationsPerInvoke = RealtimeOperationsPerInvoke)]
    [MinIterationTime(100)]
    public async Task<int> Realtime60fps_GetFrameAsync()
    {
        EnsureSessions();

        var widthSum = 0;
        var frameDurationTicks = TimeSpan.FromSeconds(1.0 / TargetFps).Ticks;

        for (var i = 0; i < RealtimeOperationsPerInvoke; i++)
        {
            var requestTime = _realtimeBenchmarkStart + TimeSpan.FromTicks(frameDurationTicks * i);
            var frame = await _videoSession!.GetFrameAsync(requestTime);
            widthSum += frame.Bitmap.Width;
        }

        return widthSum;
    }

    [IterationSetup(Target = nameof(Realtime60fps_GetFrameAsync))]
    public void SetupRealtimeBenchmark()
    {
        EnsureSessions();

        _realtimeBenchmarkStart = Next(_realTimeStartTimes, ref _realTimeIndex);
        var frameDurationTicks = TimeSpan.FromSeconds(1.0 / TargetFps).Ticks;

        // 再生開始直後のバッファ立ち上がりを除いた、定常時のプレビュー性能を計測する。
        for (var i = 0; i < 4; i++)
        {
            var requestTime = _realtimeBenchmarkStart + TimeSpan.FromTicks(frameDurationTicks * i);
            _videoSession!.GetFrameAsync(requestTime).GetAwaiter().GetResult();
        }
    }

    private void EnsureSessions()
    {
        if (_decodeSession is null || _videoSession is null)
        {
            throw new InvalidOperationException("ベンチマーク初期化が完了していません。");
        }
    }

    private static TimeSpan Next(TimeSpan[] values, ref int index)
    {
        var next = Interlocked.Increment(ref index);
        return values[next % values.Length];
    }

    private static TimeSpan[] BuildRandomSeekTimes(TimeSpan duration, int count)
    {
        var min = TimeSpan.FromMilliseconds(100);
        var max = duration - TimeSpan.FromMilliseconds(100);
        if (max <= min)
        {
            max = duration;
        }

        var random = new Random(20260206);
        var values = new TimeSpan[count];
        var rangeSeconds = Math.Max(0.001, (max - min).TotalSeconds);

        for (var i = 0; i < values.Length; i++)
        {
            values[i] = min + TimeSpan.FromSeconds(random.NextDouble() * rangeSeconds);
        }

        return values;
    }

    private static TimeSpan[] BuildNearSeekTimes(TimeSpan duration, int count, double framerate)
    {
        var frameDuration = framerate > 0
            ? TimeSpan.FromSeconds(1.0 / framerate)
            : TimeSpan.FromMilliseconds(16.666);

        var values = new TimeSpan[count];
        var random = new Random(20260206);
        var maxStart = duration - (frameDuration * 4);
        if (maxStart < TimeSpan.Zero)
        {
            maxStart = TimeSpan.Zero;
        }

        var current = TimeSpan.FromSeconds(random.NextDouble() * Math.Max(0.001, maxStart.TotalSeconds));
        for (var i = 0; i < values.Length; i++)
        {
            var stepFrames = random.Next(1, 4);
            current += frameDuration * stepFrames;

            if (current >= duration)
            {
                current = TimeSpan.FromMilliseconds(100);
            }

            values[i] = current;
        }

        return values;
    }

    private static TimeSpan[] BuildSequentialStartTimes(TimeSpan duration, TimeSpan window, int count)
    {
        var values = new TimeSpan[count];
        var maxStart = duration - window - TimeSpan.FromMilliseconds(100);
        if (maxStart < TimeSpan.Zero)
        {
            maxStart = TimeSpan.Zero;
        }

        var random = new Random(20260206);
        for (var i = 0; i < values.Length; i++)
        {
            values[i] = TimeSpan.FromSeconds(random.NextDouble() * Math.Max(0.001, maxStart.TotalSeconds));
        }

        return values;
    }
}
