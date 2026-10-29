using BenchmarkDotNet.Running;

// The step 2.13 speed gate: each Egami.SystemDrawing call beside the native Egami calls that do the same work (the baseline).
// Run all:  dotnet run -c Release --project benchmarks/Egami.SystemDrawing.Benchmarks -- --filter "*"
BenchmarkSwitcher.FromAssembly(typeof(Egami.SystemDrawing.Benchmarks.DropInBenchmarks).Assembly).Run(args);
