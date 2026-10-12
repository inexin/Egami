using BenchmarkDotNet.Running;

// Run all:      dotnet run -c Release --project benchmarks/Egami.Fonts.Benchmarks -- --filter "*"
// Run a class:  dotnet run -c Release --project benchmarks/Egami.Fonts.Benchmarks -- --filter "*Load*"
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
