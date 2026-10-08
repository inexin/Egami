using BenchmarkDotNet.Running;

// Run all:      dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --filter "*PngDecode*"
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
