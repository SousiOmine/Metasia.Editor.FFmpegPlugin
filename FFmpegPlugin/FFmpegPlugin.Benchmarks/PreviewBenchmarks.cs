using System.Diagnostics;
using System.Text;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using FFmpegPlugin;
using FFmpegPlugin.Decode;
using FFMpegCore;

[SimpleJob(RunStrategy.ColdStart, iterationCount: 1, warmupCount: 0, invocationCount: 1)]
public class PreviewBenchmarks
{
    private const string ArtifactsPathEnvVar = "METASIA_BENCH_ARTIFACTS";
    private static readonly TimeSpan LongPreviewWindow = TimeSpan.FromSeconds(20);
    private const double TargetFps = 60.0;
    private const int WarmupFrames = 12;

    private FFmpegDecodeSession? _decodeSession;
    private VideoSession? _videoSession;
    private TimeSpan _benchmarkFrameDuration;
    private TimeSpan _mediaDuration;
    private TimeSpan _previewStartTime;

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
        _previewStartTime = ResolvePreviewStartTime(duration);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _videoSession?.Dispose();
        _decodeSession?.Dispose();
    }

    [Benchmark(Description = "Preview20s_Once")]
    public async Task<int> Preview20s_Once()
    {
        EnsureSessions();

        await WarmupAsync().ConfigureAwait(false);

        var widthSum = 0;
        var samples = new List<PreviewSample>();
        var frameDurationTicks = _benchmarkFrameDuration.Ticks;
        var previewWindow = _mediaDuration > TimeSpan.Zero && _mediaDuration < LongPreviewWindow
            ? _mediaDuration
            : LongPreviewWindow;
        var frameCount = (int)Math.Max(1, Math.Round(previewWindow.Ticks / (double)frameDurationTicks));

        var scheduleStart = Stopwatch.GetTimestamp();
        for (var i = 0; i < frameCount; i++)
        {
            var scheduledWallTime = scheduleStart + ToStopwatchTicks(TimeSpan.FromTicks(frameDurationTicks * i));
            await WaitUntilAsync(scheduledWallTime).ConfigureAwait(false);

            var requestTime = ClampRequestTime(_previewStartTime + TimeSpan.FromTicks(frameDurationTicks * i));
            var decodeStopwatch = Stopwatch.StartNew();
            var frame = await _videoSession!.GetFrameAsync(requestTime).ConfigureAwait(false);
            decodeStopwatch.Stop();

            var completion = Stopwatch.GetTimestamp();
            var latenessTicks = Math.Max(0, completion - scheduledWallTime);
            var decodeTicks = ToTimeSpanTicks(decodeStopwatch.ElapsedTicks);
            var latenessTimeSpanTicks = StopwatchTicksToTimeSpanTicks(latenessTicks);
            samples.Add(new PreviewSample(i, decodeTicks, latenessTimeSpanTicks));
            widthSum += frame.Bitmap.Width;
        }

        WriteTimelineCsv("Preview20s_Once_Timeline", samples);
        Console.WriteLine(
            $"[Preview20s_Once] fallbackSingleDecodeCount={_videoSession!.FallbackSingleDecodeCount}, workerRestartCount={_videoSession.WorkerRestartCount}");
        return widthSum;
    }

    private async Task WarmupAsync()
    {
        var warmupStart = ClampRequestTime(_previewStartTime - (_benchmarkFrameDuration * WarmupFrames));
        var frameDurationTicks = _benchmarkFrameDuration.Ticks;

        for (var i = 0; i < WarmupFrames; i++)
        {
            var requestTime = ClampRequestTime(warmupStart + TimeSpan.FromTicks(frameDurationTicks * i));
            await _videoSession!.GetFrameAsync(requestTime).ConfigureAwait(false);
        }
    }

    private static async Task WaitUntilAsync(long targetStopwatchTimestamp)
    {
        while (true)
        {
            var now = Stopwatch.GetTimestamp();
            var remainingStopwatchTicks = targetStopwatchTimestamp - now;
            if (remainingStopwatchTicks <= 0)
            {
                return;
            }

            var remaining = TimeSpan.FromTicks(StopwatchTicksToTimeSpanTicks(remainingStopwatchTicks));
            if (remaining > TimeSpan.FromMilliseconds(2))
            {
                await Task.Delay(remaining - TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
                continue;
            }

            Thread.SpinWait(128);
        }
    }

    private void EnsureSessions()
    {
        if (_decodeSession is null || _videoSession is null)
        {
            throw new InvalidOperationException("ベンチマーク初期化が完了していません。");
        }
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

    private static TimeSpan ResolveFrameDuration(double framerate)
    {
        return framerate > 0
            ? TimeSpan.FromSeconds(1.0 / framerate)
            : TimeSpan.FromSeconds(1.0 / TargetFps);
    }

    private static TimeSpan ResolvePreviewStartTime(TimeSpan duration)
    {
        var maxStart = duration - LongPreviewWindow - TimeSpan.FromMilliseconds(100);
        if (maxStart < TimeSpan.Zero)
        {
            maxStart = TimeSpan.Zero;
        }

        var random = new Random(20260206);
        return TimeSpan.FromSeconds(random.NextDouble() * Math.Max(0.001, maxStart.TotalSeconds));
    }

    private static void WriteTimelineCsv(string name, IReadOnlyList<PreviewSample> samples)
    {
        if (samples.Count == 0)
        {
            return;
        }

        var builder = new StringBuilder(samples.Count * 32);
        builder.AppendLine("frame,decode_ms,lateness_ms");
        foreach (var sample in samples)
        {
            builder.Append(sample.Frame);
            builder.Append(',');
            builder.Append(TimeSpan.FromTicks(sample.DecodeTicks).TotalMilliseconds.ToString("F3"));
            builder.Append(',');
            builder.Append(TimeSpan.FromTicks(sample.LatenessTicks).TotalMilliseconds.ToString("F3"));
            builder.AppendLine();
        }

        var artifactsDir = ResolveArtifactsDirectory();
        Directory.CreateDirectory(artifactsDir);
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var fileName = $"{name}-{timestamp}.csv";
        var path = Path.Combine(artifactsDir, fileName);
        File.WriteAllText(path, builder.ToString(), Encoding.ASCII);
        Console.WriteLine($"[{name}] timeline saved: {path}");
    }

    private static string ResolveArtifactsDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(ArtifactsPathEnvVar);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.Combine(configured, "results");
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Artifacts", "results");
    }

    private static long ToTimeSpanTicks(long stopwatchTicks)
    {
        return (long)(stopwatchTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
    }

    private static long ToStopwatchTicks(TimeSpan timeSpan)
    {
        return (long)(timeSpan.Ticks * (double)Stopwatch.Frequency / TimeSpan.TicksPerSecond);
    }

    private static long StopwatchTicksToTimeSpanTicks(long stopwatchTicks)
    {
        return (long)(stopwatchTicks * (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency);
    }

    private readonly record struct PreviewSample(int Frame, long DecodeTicks, long LatenessTicks);
}
