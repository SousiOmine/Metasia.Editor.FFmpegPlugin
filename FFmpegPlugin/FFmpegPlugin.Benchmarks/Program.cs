using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Running;
using Perfolizer.Horology;

var _ = BenchmarkEnvironment.Resolve();

var job = Job.Default
    .WithMinIterationTime(TimeInterval.FromMilliseconds(100))
    .WithUnrollFactor(1)
    .WithWarmupCount(1)
    .WithIterationCount(1);

var artifactsPath = Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Artifacts");
Environment.SetEnvironmentVariable("METASIA_BENCH_ARTIFACTS", artifactsPath);

var scrubConfig = ManualConfig
    .Create(DefaultConfig.Instance)
    .WithArtifactsPath(artifactsPath)
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddJob(job);

var previewConfig = ManualConfig
    .Create(DefaultConfig.Instance)
    .WithArtifactsPath(artifactsPath)
    .AddDiagnoser(MemoryDiagnoser.Default);

RunBenchmarksWithFilterFallback<ScrubBenchmarks>(scrubConfig, args);
RunBenchmarksWithFilterFallback<PreviewBenchmarks>(previewConfig, args);

static void RunBenchmarksWithFilterFallback<TBenchmark>(IConfig config, string[] benchmarkArgs)
{
    try
    {
        BenchmarkRunner.Run<TBenchmark>(config, benchmarkArgs);
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Sequence contains no elements", StringComparison.Ordinal))
    {
        // Filter does not match this benchmark class.
    }
}
