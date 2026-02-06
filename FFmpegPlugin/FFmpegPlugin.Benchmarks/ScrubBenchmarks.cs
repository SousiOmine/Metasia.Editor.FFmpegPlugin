using System.Diagnostics;
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
    private static readonly TimeSpan SeekLatencyThreshold = TimeSpan.FromMilliseconds(50);
    private const double TargetFps = 60.0;
    private const int SeekLatencySampleCount = 30;
    private const int RealtimeJankFrameCount = 120;
    private const int ScenarioCycles = 3;
    private const int ScenarioPlaybackFrames = 30;

    private FFmpegDecodeSession? _decodeSession;
    private VideoSession? _videoSession;
    private TimeSpan[] _randomSeekTimes = [];
    private TimeSpan[] _nearSeekTimes = [];
    private TimeSpan[] _sequentialStartTimes = [];
    private TimeSpan[] _realTimeStartTimes = [];
    private TimeSpan[] _scenarioStartTimes = [];
    private int _randomSeekIndex = -1;
    private int _nearSeekIndex = -1;
    private int _sequentialIndex = -1;
    private int _realTimeIndex = -1;
    private int _scenarioIndex = -1;
    private TimeSpan _realtimeBenchmarkStart;
    private TimeSpan _benchmarkFrameDuration;
    private TimeSpan _mediaDuration;

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
        _benchmarkFrameDuration = ResolveFrameDuration(_decodeSession.Framerate);
        _mediaDuration = duration;

        _randomSeekTimes = BuildRandomSeekTimes(duration, RandomSeekCount);
        _nearSeekTimes = BuildNearSeekTimes(duration, NearSeekCount, _decodeSession.Framerate);
        _sequentialStartTimes = BuildSequentialStartTimes(duration, SequentialDecodeWindow, 8);
        _realTimeStartTimes = BuildSequentialStartTimes(duration, RealTimePlaybackWindow, 8);
        _scenarioStartTimes = BuildSequentialStartTimes(duration, RealTimePlaybackWindow, 8);
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

        var time = ClampRequestTime(Next(_randomSeekTimes, ref _randomSeekIndex));
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
            var time = ClampRequestTime(Next(_nearSeekTimes, ref _nearSeekIndex));
            var frame = await _videoSession!.GetFrameAsync(time);
            widthSum += frame.Bitmap.Width;
        }

        return widthSum;
    }

    [Benchmark(Description = "SeekLatency_Profiled")]
    public async Task<int> SeekLatency_Profiled()
    {
        EnsureSessions();

        var widthSum = 0;
        var samples = new List<long>(SeekLatencySampleCount);

        for (var i = 0; i < SeekLatencySampleCount; i++)
        {
            var time = ClampRequestTime(Next(_randomSeekTimes, ref _randomSeekIndex));
            var stopwatch = Stopwatch.StartNew();
            var frame = await _videoSession!.GetFrameAsync(time);
            stopwatch.Stop();
            samples.Add(stopwatch.ElapsedTicks);
            widthSum += frame.Bitmap.Width;
        }

        var stats = BuildLatencyStats(samples, SeekLatencyThreshold.Ticks);
        LogStats("SeekLatency_Profiled", stats);
        return widthSum;
    }

    [Benchmark(Description = "Realtime60fps_JankStats")]
    [MinIterationTime(100)]
    public async Task<int> Realtime60fps_JankStats()
    {
        EnsureSessions();

        var widthSum = 0;
        var samples = new List<long>(RealtimeJankFrameCount);
        var frameDurationTicks = _benchmarkFrameDuration.Ticks;

        for (var i = 0; i < RealtimeJankFrameCount; i++)
        {
            var requestTime = ClampRequestTime(_realtimeBenchmarkStart + TimeSpan.FromTicks(frameDurationTicks * i));
            var stopwatch = Stopwatch.StartNew();
            var frame = await _videoSession!.GetFrameAsync(requestTime);
            stopwatch.Stop();
            samples.Add(stopwatch.ElapsedTicks);
            widthSum += frame.Bitmap.Width;
        }

        var stats = BuildLatencyStats(samples, frameDurationTicks * 2);
        LogStats("Realtime60fps_JankStats", stats);
        return widthSum;
    }

    [Benchmark(Description = "PlaybackAndSeekScenario")]
    [MinIterationTime(100)]
    public async Task<int> PlaybackAndSeekScenario()
    {
        EnsureSessions();

        var widthSum = 0;
        var samples = new List<long>(ScenarioCycles * (ScenarioPlaybackFrames + 1));
        var frameDurationTicks = _benchmarkFrameDuration.Ticks;
        var currentTime = Next(_scenarioStartTimes, ref _scenarioIndex);

        for (var cycle = 0; cycle < ScenarioCycles; cycle++)
        {
            for (var i = 0; i < ScenarioPlaybackFrames; i++)
            {
                var requestTime = ClampRequestTime(currentTime + TimeSpan.FromTicks(frameDurationTicks * i));
                var stopwatch = Stopwatch.StartNew();
                var frame = await _videoSession!.GetFrameAsync(requestTime);
                stopwatch.Stop();
                samples.Add(stopwatch.ElapsedTicks);
                widthSum += frame.Bitmap.Width;
            }

            currentTime = ClampRequestTime(Next(_randomSeekTimes, ref _randomSeekIndex));
            var seekStopwatch = Stopwatch.StartNew();
            var seekFrame = await _videoSession!.GetFrameAsync(currentTime);
            seekStopwatch.Stop();
            samples.Add(seekStopwatch.ElapsedTicks);
            widthSum += seekFrame.Bitmap.Width;
            currentTime += _benchmarkFrameDuration;
        }

        var stats = BuildLatencyStats(samples, frameDurationTicks * 2);
        LogStats("PlaybackAndSeekScenario", stats);
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
        var frameDurationTicks = _benchmarkFrameDuration.Ticks;

        for (var i = 0; i < RealtimeOperationsPerInvoke; i++)
        {
            var requestTime = ClampRequestTime(_realtimeBenchmarkStart + TimeSpan.FromTicks(frameDurationTicks * i));
            var frame = await _videoSession!.GetFrameAsync(requestTime);
            widthSum += frame.Bitmap.Width;
        }

        return widthSum;
    }

    [IterationSetup(Target = nameof(Realtime60fps_GetFrameAsync))]
    public void SetupRealtimeBenchmark()
    {
        EnsureSessions();

        SetupRealtimeWarmup();
    }

    [IterationSetup(Target = nameof(Realtime60fps_JankStats))]
    public void SetupRealtimeJankBenchmark()
    {
        EnsureSessions();

        SetupRealtimeWarmup();
    }

    [IterationSetup(Target = nameof(PlaybackAndSeekScenario))]
    public void SetupPlaybackScenarioBenchmark()
    {
        EnsureSessions();

        SetupRealtimeWarmup();
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

    private TimeSpan ClampRequestTime(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (_mediaDuration <= TimeSpan.Zero)
        {
            return time;
        }

        var maxTime = _mediaDuration - _benchmarkFrameDuration;
        if (maxTime <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return time > maxTime ? maxTime : time;
    }

    private void SetupRealtimeWarmup()
    {
        _realtimeBenchmarkStart = Next(_realTimeStartTimes, ref _realTimeIndex);
        var frameDurationTicks = _benchmarkFrameDuration.Ticks;

        // 再生開始直後のバッファ立ち上がりを除いた、定常時のプレビュー性能を計測する。
        for (var i = 0; i < 4; i++)
        {
            var requestTime = ClampRequestTime(_realtimeBenchmarkStart + TimeSpan.FromTicks(frameDurationTicks * i));
            _videoSession!.GetFrameAsync(requestTime).GetAwaiter().GetResult();
        }
    }

    private static TimeSpan ResolveFrameDuration(double framerate)
    {
        return framerate > 0
            ? TimeSpan.FromSeconds(1.0 / framerate)
            : TimeSpan.FromSeconds(1.0 / TargetFps);
    }

    private static LatencyStats BuildLatencyStats(List<long> samples, long thresholdTicks)
    {
        if (samples.Count == 0)
        {
            return new LatencyStats(0, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0);
        }

        samples.Sort();
        long sum = 0;
        var overThreshold = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var value = samples[i];
            sum += value;
            if (value > thresholdTicks)
            {
                overThreshold++;
            }
        }

        var count = samples.Count;
        var average = TimeSpan.FromTicks(sum / count);
        var p95 = TimeSpan.FromTicks(samples[PercentileIndex(count, 0.95)]);
        var p99 = TimeSpan.FromTicks(samples[PercentileIndex(count, 0.99)]);
        var max = TimeSpan.FromTicks(samples[^1]);
        return new LatencyStats(count, average, p95, p99, max, overThreshold);
    }

    private static int PercentileIndex(int count, double percentile)
    {
        if (count <= 1)
        {
            return 0;
        }

        var rank = (int)Math.Ceiling(percentile * count) - 1;
        return Math.Clamp(rank, 0, count - 1);
    }

    private static void LogStats(string name, LatencyStats stats)
    {
        Console.WriteLine(
            $"[{name}] count={stats.Count}, avg={stats.Average.TotalMilliseconds:F2}ms, p95={stats.P95.TotalMilliseconds:F2}ms, p99={stats.P99.TotalMilliseconds:F2}ms, max={stats.Max.TotalMilliseconds:F2}ms, over={stats.OverThreshold}");
    }

    private sealed record LatencyStats(
        int Count,
        TimeSpan Average,
        TimeSpan P95,
        TimeSpan P99,
        TimeSpan Max,
        int OverThreshold);

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
