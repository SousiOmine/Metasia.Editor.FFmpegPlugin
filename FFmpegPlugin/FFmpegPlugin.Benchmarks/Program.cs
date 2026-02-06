using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Running;

var _ = BenchmarkEnvironment.Resolve();

var config = ManualConfig
    .Create(DefaultConfig.Instance)
    .AddDiagnoser(MemoryDiagnoser.Default);

BenchmarkRunner.Run<ScrubBenchmarks>(config);
