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
    .WithIterationCount(5);

var config = ManualConfig
    .Create(DefaultConfig.Instance)
    .AddDiagnoser(MemoryDiagnoser.Default)
    .AddJob(job);

BenchmarkRunner.Run<ScrubBenchmarks>(config);
