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

// Time and write an Opus encode (V5.7 speed gate): -- --encode-opus <audio file> [--out=file.opus --bitrate=BITS ...]
if (args is ["--encode-opus", .. string[] opus])
{
    OpusEncodeTiming.Run(opus);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
