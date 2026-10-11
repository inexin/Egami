using BenchmarkDotNet.Running;

// Run all:      dotnet run -c Release --project benchmarks/Egami.Drawing.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egami.Drawing.Benchmarks -- --filter "*Fill*"
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
