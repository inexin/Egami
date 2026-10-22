using BenchmarkDotNet.Running;

using Egami.Video.Benchmarks;

// Time VP9 decoding (V3 speed gate): -- --time-vp9 <file.ivf>...
if (args is ["--time-vp9", .. string[] files])
{
    Vp9Timing.Run(files);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
