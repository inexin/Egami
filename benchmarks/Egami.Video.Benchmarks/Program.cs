using BenchmarkDotNet.Running;

using Egami.Video.Benchmarks;

// Time VP9 decoding (V3 speed gate): -- --time-vp9 <file.ivf>...
if (args is ["--time-vp9", .. string[] files])
{
    Vp9Timing.Run(files);
    return;
}

// Time FFV1 decoding (V6 speed gate): -- --time-ffv1 <file.mkv>...
if (args is ["--time-ffv1", .. string[] ffv1])
{
    Ffv1Timing.Run(ffv1);
    return;
}

// Time and size an FFV1 encode (V6 gates): -- --encode-ffv1 <file.mkv>... [--slices=N --context=small --coder=tab --threads=N]
if (args is ["--encode-ffv1", .. string[] ffv1Encode])
{
    Ffv1EncodeTiming.Run(ffv1Encode);
    return;
}

// Time H.264 decoding (V7 speed gate): -- --time-h264 <file.h264>...
if (args is ["--time-h264", .. string[] h264])
{
    H264Timing.Run(h264);
    return;
}

// Time HEVC decoding (V8 speed gate): -- --time-hevc <file.h265>...
if (args is ["--time-hevc", .. string[] hevc])
{
    HevcTiming.Run(hevc);
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

// Time reading subtitle files and writing them as SRT (V1.2): -- --time-subtitles <file>..., or --time-subtitles --write <dir>.
if (args is ["--time-subtitles", .. string[] subtitles])
{
    SubtitleTiming.Run(subtitles);
    return;
}

// Time and write a VP9 encode (V5.3 gates): -- --encode-vp9 <clip.y4m> [--out=file.ivf --quality=Q ...]
if (args is ["--encode-vp9", .. string[] rest9])
{
    Vp9EncodeTiming.Run(rest9);
    return;
}

// Time and write an AV1 encode (V5.4 gates): -- --encode-av1 <clip.y4m> [--out=file.ivf --quality=Q ...]
if (args is ["--encode-av1", .. string[] rest1])
{
    Vp9EncodeTiming.Run(rest1, Egami.Video.Codecs.VideoCodecs.Av1);
    return;
}

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
