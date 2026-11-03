using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Egami.Formats.Extended.Textures.Blocks;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// The ETC, EAC, ASTC and UASTC block encoders the KTX writer will use, one thread, at the default effort 4: a 2048 × 2048 photo-like
/// picture encoded block by block, so a change shows up in time and allocations. The encoders are slow next to the decoders, so
/// a run measures three iterations after one warmup.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(launchCount: 1, warmupCount: 1, iterationCount: 3)]
public class KtxEncodeBenchmarks
{
    private const int Size = 2048;
    private const int Effort = 4;
    private byte[] _rgba = [];
    private byte[] _blocks = [];

    [Params("ETC1", "ETC2-RGB", "ETC2-RGBA", "EAC-R11", "ASTC-4x4", "ASTC-6x6", "ASTC-8x8", "UASTC")]
    public string Format { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        using Raster<Rgba8> photo = BenchImages.Photo(Size, Size);
        _rgba = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            MemoryMarshal.AsBytes(photo.Row(y)).CopyTo(_rgba.AsSpan(y * Size * 4));
        }

        _blocks = new byte[Size * Size];
    }

    [Benchmark]
    public int Encode()
    {
        if (Format.StartsWith("ASTC", StringComparison.Ordinal))
        {
            int footprint = Format[5] - '0';
            return EncodeAstc(Astc.For(footprint, footprint)!);
        }

        Span<byte> texels = stackalloc byte[64];
        Span<ushort> red = stackalloc ushort[16];
        int unit = Format is "ETC2-RGBA" or "UASTC" ? 16 : 8;
        int at = 0;
        for (int by = 0; by < Size; by += 4)
        {
            for (int bx = 0; bx < Size; bx += 4)
            {
                for (int y = 0; y < 4; y++)
                {
                    _rgba.AsSpan((((by + y) * Size) + bx) * 4, 16).CopyTo(texels[(y * 16)..]);
                }

                Span<byte> block = _blocks.AsSpan(at, unit);
                switch (Format)
                {
                    case "ETC1":
                        EtcEncoder.EncodeRgb(texels, block, Effort, etc2: false, punchThrough: false);
                        break;
                    case "ETC2-RGB":
                        EtcEncoder.EncodeRgb(texels, block, Effort, etc2: true, punchThrough: false);
                        break;
                    case "ETC2-RGBA":
                        EtcEncoder.EncodeRgba(texels, block, Effort);
                        break;
                    case "UASTC":
                        UastcEncoder.Encode(texels, block, Effort);
                        break;
                    default:
                        for (int i = 0; i < 16; i++)
                        {
                            red[i] = (ushort)(texels[i * 4] * 257);
                        }

                        EtcEncoder.EncodeEac11(red, 1, block, Effort, signed: false);
                        break;
                }

                at += unit;
            }
        }

        return at;
    }

    private int EncodeAstc(Astc astc)
    {
        int w = astc.Width;
        byte[] texels = new byte[astc.Texels * 4];
        int at = 0;
        for (int by = 0; by < Size; by += w)
        {
            for (int bx = 0; bx < Size; bx += w)
            {
                for (int y = 0; y < w; y++)
                {
                    int row = Math.Min(by + y, Size - 1);
                    for (int x = 0; x < w; x++)
                    {
                        _rgba.AsSpan(((row * Size) + Math.Min(bx + x, Size - 1)) * 4, 4).CopyTo(texels.AsSpan(((y * w) + x) * 4));
                    }
                }

                AstcEncoder.Encode(astc, texels, _blocks.AsSpan(at, 16), Effort, srgb: false);
                at += 16;
            }
        }

        return at;
    }
}
