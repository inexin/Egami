using System.Buffers.Binary;

using BenchmarkDotNet.Attributes;

using Egami.Formats.Extended;
using Egami.Formats.Extended.Raw;

namespace Egami.Benchmarks;

/// <summary>
/// Camera RAW decoding and developing, one thread: a 3000 × 2000 14-bit DNG of 256 × 256 lossless JPEG tiles, loaded as the
/// mosaic, half size, with each demosaic method, and with the default develop (AHD and colour noise reduction). The comparisons
/// with LibRaw (Magick.NET) on real camera files live in egami-compare. The scene is a smooth gradient with fine stripes, so
/// the entropy coder and the demosaicing both meet detail.
/// </summary>
[MemoryDiagnoser]
public class RawBenchmarks
{
    private const int Width = 3000;
    private const int Height = 2000;
    private const int Tile = 256;
    private readonly EgamiContext _context = EgamiContext.Default.WithRaw() with { MaxParallelism = 1 };
    private byte[] _file = [];
    private RawReaderOptions _options = new();

    [Params("Mosaic", "HalfSize", "Bilinear", "Malvar", "Ahd", "Default")]
    public string Develop { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _file = Dng();
        _options = Develop switch
        {
            "Mosaic" => new RawReaderOptions { Output = RawOutput.Mosaic },
            "HalfSize" => new RawReaderOptions { Output = RawOutput.HalfSize, ColorNoiseReduction = 0 },
            "Bilinear" => new RawReaderOptions { Demosaic = RawDemosaicMethod.Bilinear, ColorNoiseReduction = 0 },
            "Malvar" => new RawReaderOptions { Demosaic = RawDemosaicMethod.Malvar, ColorNoiseReduction = 0 },
            "Ahd" => new RawReaderOptions { ColorNoiseReduction = 0 },
            _ => new RawReaderOptions(),
        };
    }

    [Benchmark]
    public int Load()
    {
        using Raster raster = Raster.Load(_file, _context, _options);
        return raster.Width;
    }

    /// <summary>Writes the DNG: IFD0 with the camera tags, one raw sub-IFD, then the tiles.</summary>
    private static byte[] Dng()
    {
        List<byte[]> tiles = [];
        ushort[] samples = new ushort[Tile * Tile];
        for (int ty = 0; ty < Height; ty += Tile)
        {
            for (int tx = 0; tx < Width; tx += Tile)
            {
                for (int y = 0; y < Tile; y++)
                {
                    for (int x = 0; x < Tile; x++)
                    {
                        int gx = Math.Min(tx + x, Width - 1), gy = Math.Min(ty + y, Height - 1);
                        int colour = ((gy & 1) << 1) | (gx & 1);
                        double level = 0.15 + (0.6 * gx / Width) + (0.15 * Math.Sin(gx * 0.9) * Math.Cos(gy * 0.07));
                        double gain = colour == 0 ? 0.55 : colour == 3 ? 0.45 : 1;
                        samples[(y * Tile) + x] = (ushort)Math.Clamp(512 + (level * gain * 15000), 0, 16383);
                    }
                }

                tiles.Add(LosslessJpeg(samples, Tile, Tile, 14));
            }
        }

        // Header, IFD0 (16 entries), raw IFD (18 entries), their values, then the tiles.
        using MemoryStream file = new();
        using BinaryWriter w = new(file);
        int ifd0 = 8, raw = ifd0 + 2 + (16 * 12) + 4, values = raw + 2 + (18 * 12) + 4;
        int matrix = values, neutral = matrix + 72, offsets = neutral + 24, counts = offsets + (tiles.Count * 4), data = counts + (tiles.Count * 4);
        w.Write("II"u8);
        w.Write((ushort)42);
        w.Write(ifd0);
        Entries(w, [(254, 4, 1, 1), (256, 4, 1, 1), (257, 4, 1, 1), (258, 3, 1, 8), (259, 3, 1, 1), (262, 3, 1, 1), (271, 2, 4, 0x00006745),
            (272, 2, 4, 0x00006E42), (273, 4, 1, 0), (277, 3, 1, 1), (279, 4, 1, 1), (330, 4, 1, raw), (50706, 1, 4, 0x00000401),
            (50721, 10, 9, matrix), (50728, 5, 3, neutral), (50778, 3, 1, 21)]);
        Entries(w, [(254, 4, 1, 0), (256, 4, 1, Width), (257, 4, 1, Height), (258, 3, 1, 14), (259, 3, 1, 7), (262, 3, 1, 32803),
            (277, 3, 1, 1), (322, 4, 1, Tile), (323, 4, 1, Tile), (324, 4, tiles.Count, offsets), (325, 4, tiles.Count, counts),
            (33421, 3, 2, 0x00020002), (33422, 1, 4, 0x02010100), (50710, 1, 3, 0x00020100), (50711, 3, 1, 1), (50713, 3, 2, 0x00010001),
            (50714, 3, 1, 512), (50717, 3, 1, 16383)]);
        foreach (double v in (ReadOnlySpan<double>)[3.2404542, -1.5371385, -0.4985314, -0.9692660, 1.8760108, 0.0415560, 0.0556434, -0.2040259, 1.0572252])
        {
            w.Write((int)Math.Round(v * 1_000_000));
            w.Write(1_000_000);
        }

        foreach (double v in (ReadOnlySpan<double>)[0.55, 1, 0.45])
        {
            w.Write((uint)Math.Round(v * 10000));
            w.Write(10000u);
        }

        int at = data;
        foreach (byte[] tile in tiles)
        {
            w.Write(at);
            at += tile.Length;
        }

        foreach (byte[] tile in tiles)
        {
            w.Write(tile.Length);
        }

        foreach (byte[] tile in tiles)
        {
            w.Write(tile);
        }

        return file.ToArray();
    }

    private static void Entries(BinaryWriter w, (int Tag, int Type, int Count, int Value)[] entries)
    {
        w.Write((ushort)entries.Length);
        foreach ((int tag, int type, int count, int value) in entries)
        {
            w.Write((ushort)tag);
            w.Write((ushort)type);
            w.Write(count);
            w.Write(value);
        }

        w.Write(0);
    }

    /// <summary>A lossless JPEG (ITU-T T.81 process 14) of two interleaved components, predictor 1, every category a 5-bit code.</summary>
    private static byte[] LosslessJpeg(ushort[] samples, int width, int height, int precision)
    {
        int columns = width / 2;
        List<byte> o = [0xFF, 0xD8, 0xFF, 0xC3, 0, 14, (byte)precision, (byte)(height >> 8), (byte)height, (byte)(columns >> 8), (byte)columns, 2, 1, 0x11, 0, 2, 0x11, 0];
        o.AddRange([0xFF, 0xC4, 0, 36, 0x00, 0, 0, 0, 0, 17, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        for (int i = 0; i <= 16; i++)
        {
            o.Add((byte)i);
        }

        o.AddRange([0xFF, 0xDA, 0, 10, 2, 1, 0, 2, 0, 1, 0, 0]);
        ulong buffer = 0;
        int count = 0;
        void Put(int value, int n)
        {
            buffer = (buffer << n) | (uint)(value & ((1 << n) - 1));
            count += n;
            while (count >= 8)
            {
                byte b = (byte)(buffer >> (count - 8));
                o.Add(b);
                if (b == 0xFF)
                {
                    o.Add(0);
                }

                count -= 8;
            }
        }

        for (int i = 0; i < samples.Length; i++)
        {
            int prediction = i < 2 ? 1 << (precision - 1) : i < width ? samples[i - 2] : i % width < 2 ? samples[i - width] : samples[i - 2];
            int diff = samples[i] - prediction;
            int category = diff == 0 ? 0 : 32 - BitOperations(Math.Abs(diff));
            Put(category, 5);
            if (category > 0)
            {
                Put(diff < 0 ? diff - 1 : diff, category);
            }
        }

        while (count != 0)
        {
            Put(1, 1);
        }

        o.AddRange([0xFF, 0xD9]);
        return [.. o];
    }

    private static int BitOperations(int value) => System.Numerics.BitOperations.LeadingZeroCount((uint)value);
}
