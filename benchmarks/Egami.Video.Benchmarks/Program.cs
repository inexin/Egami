using BenchmarkDotNet.Running;

using Egami.Video.Benchmarks;

// Time VP9 decoding (V3 speed gate): -- --time-vp9 <file.ivf>...
if (args is ["--time-vp9", .. string[] files])
{
    Vp9Timing.Run(files);
    return;
}

// Time and write a VP8 encode (V5.2 gates): -- --encode-vp8 <clip.y4m> [--out=file.ivf --quality=Q ...]
if (args is ["--encode-vp8", .. string[] rest])
{
    Vp8EncodeTiming.Run(rest);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
