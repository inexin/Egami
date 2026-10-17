using Egami.Colors;
using Egami.Formats.Jxl;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.3 task 17: the lossless encoder. The entropy coder's streams decode back through Egami's decoder.
/// </summary>
public class JxlEncodeTests
{
    [Theory]
    [InlineData(1, 1, 0, false)]
    [InlineData(1, 500, 1, false)]
    [InlineData(3, 2000, 2, true)]
    [InlineData(40, 5000, 3, false)]
    [InlineData(40, 5000, 3, true)]
    [InlineData(300, 20000, 4, true)]
    public void EntropyCodeRoundTrips(int contexts, int count, int seed, bool rle)
    {
        Random random = new(seed);
        List<List<JxlToken>> streams = [];
        for (int s = 0; s < 3; s++)
        {
            List<JxlToken> tokens = new(count);
            for (int i = 0; i < count; i++)
            {
                int context = random.Next(contexts);
                // Each context has its own spread of values, some large enough to need raw bits, and some runs.
                uint value = random.NextDouble() < 0.3 && i > 0 ? tokens[^1].Value
                    : (uint)(random.NextDouble() < 0.9 ? random.Next(1 + (context % 7 * 5)) : random.Next(int.MaxValue) >> random.Next(31));
                tokens.Add(new JxlToken(context, value));
            }

            streams.Add(tokens);
        }

        List<List<JxlToken>> original = [.. streams.Select(t => t.ToList())];
        JxlHistogramParams p = new() { Rle = rle, UintMethod = JxlHistogramParams.UintKind.Fast, Clustering = JxlHistogramParams.ClusteringKind.Best };
        JxlBitWriter w = new();
        w.Write(0b101, 3);
        JxlEntropyCode code = JxlEntropyCode.Write(w, p, contexts, streams);
        foreach (List<JxlToken> stream in streams)
        {
            code.WriteTokens(w, stream);
        }

        w.Write(0b11, 2);
        byte[] data = w.ToArray();

        JxlBitReader r = new(data, 0, data.Length);
        Assert.Equal(0b101u, r.ReadBits(3));
        JxlEntropyDecoder decoder = JxlEntropyDecoder.Read(r, contexts);
        foreach (List<JxlToken> tokens in original)
        {
            JxlEntropyDecoder d = decoder.Clone();
            d.Begin(r);
            for (int i = 0; i < tokens.Count; i++)
            {
                Assert.Equal(tokens[i].Value, d.ReadUint(r, tokens[i].Context));
            }

            d.Finish(r);
        }

        Assert.Equal(0b11u, r.ReadBits(2));
    }

    [Theory]
    [InlineData(1, 1, 1, false)]
    [InlineData(17, 9, 1, true)]
    [InlineData(64, 48, 2, false)]
    [InlineData(64, 48, 3, true)]
    [InlineData(100, 70, 4, false)]
    [InlineData(100, 70, 5, true)]
    [InlineData(120, 90, 6, false)]
    [InlineData(120, 90, 7, true)]
    [InlineData(600, 300, 3, false)]
    [InlineData(600, 300, 7, true)]
    public void LosslessRoundTrips(int width, int height, int effort, bool alpha)
    {
        Random random = new((width * 1000) + height + effort);
        int[][] color = [new int[width * height], new int[width * height], new int[width * height]];
        int[] a = new int[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Smooth gradients with noise, flat patches and a few jumps.
                int i = (y * width) + x;
                bool flat = (x / 16 % 3) == 0 && (y / 16 % 2) == 0;
                for (int c = 0; c < 3; c++)
                {
                    int v = flat ? 40 * c : (x * (c + 1)) + (y * (3 - c)) + random.Next(-6, 7);
                    color[c][i] = Math.Clamp(v & 0x1FF, 0, 255);
                }

                a[i] = flat ? 255 : (x + y) % 256;
            }
        }

        byte[] data = JxlLosslessEncoder.Encode(new JxlEncodeInput { Width = width, Height = height, Color = color, Alpha = alpha ? a : null, Effort = effort });
        using Raster<Rgba8> back = Raster.Load<Rgba8>(data);
        Assert.Equal(width, back.Width);
        Assert.Equal(height, back.Height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = back.Row(y);
            for (int x = 0; x < width; x++)
            {
                int i = (y * width) + x;
                Rgba8 expected = new((byte)color[0][i], (byte)color[1][i], (byte)color[2][i], alpha ? (byte)a[i] : (byte)255);
                Assert.True(expected.Equals(row[x]), $"({x}, {y}): {row[x]} instead of {expected}");
            }
        }
    }

    public static TheoryData<string> PixelTypes() => ["Rgb8", "Rgba8", "Gray8", "GrayAlpha8", "Rgb16", "Rgba16", "Gray16", "RgbF32", "RgbaF32", "RgbaF16"];

    [Theory]
    [MemberData(nameof(PixelTypes))]
    public void SaveRoundTripsEveryPixelType(string type)
    {
        switch (type)
        {
            case "Rgb8": RoundTrip<Rgb8>(); break;
            case "Rgba8": RoundTrip<Rgba8>(); break;
            case "Gray8": RoundTrip<Gray8>(); break;
            case "GrayAlpha8": RoundTrip<GrayAlpha8>(); break;
            case "Rgb16": RoundTrip<Rgb16>(); break;
            case "Rgba16": RoundTrip<Rgba16>(); break;
            case "Gray16": RoundTrip<Gray16>(); break;
            case "RgbF32": RoundTrip<RgbF32>(); break;
            case "RgbaF32": RoundTrip<RgbaF32>(); break;
            default: RoundTrip<RgbaF16>(); break;
        }
    }

    [Fact]
    public void SaveKeepsIccExifOrientationAndXmp()
    {
        using Raster<Rgba8> source = Noise<Rgba8>(40, 30, 7);
        source.Metadata.IccProfile = ColorProfile.DisplayP3.ToIcc();
        source.Metadata.ColorSpace = ColorSpaceHint.EmbeddedIcc;
        source.Metadata.Exif = new ExifData { Orientation = ImageOrientation.Rotate90 };
        source.Metadata.Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();
        using MemoryStream file = new();
        source.Save(file, JxlFormat.Info, new JxlWriterOptions { Lossless = true });
        using Raster<Rgba8> back = Raster.Load<Rgba8>(file.ToArray());
        Assert.Equal(source.Metadata.IccProfile.ToArray(), back.Metadata.IccProfile.ToArray());
        Assert.Equal(ImageOrientation.Rotate90, back.Metadata.Exif?.Orientation);
        Assert.Equal(source.Metadata.Xmp.ToArray(), back.Metadata.Xmp.ToArray());
        Assert.True(((JxlDetails)back.Metadata.FormatDetails!).IsContainer);
        for (int y = 0; y < source.Height; y++)
        {
            Assert.True(source.Row(y).SequenceEqual(back.Row(y)));
        }
    }

    private static void RoundTrip<T>()
        where T : unmanaged, IPixelType<T>
    {
        foreach (int effort in (int[])[1, 3, 5])
        {
            using Raster<T> source = Noise<T>(70, 45, effort);
            using MemoryStream file = new();
            source.Save(file, JxlFormat.Info, new JxlWriterOptions { Lossless = true, Effort = effort });
            using Raster<T> back = Raster.Load<T>(file.ToArray());
            for (int y = 0; y < source.Height; y++)
            {
                Assert.True(System.Runtime.InteropServices.MemoryMarshal.AsBytes(source.Row(y)).SequenceEqual(System.Runtime.InteropServices.MemoryMarshal.AsBytes(back.Row(y))), $"{typeof(T).Name} at effort {effort}, row {y}");
            }
        }
    }

    [Theory]
    [InlineData(2, 512, 512)]
    [InlineData(5, 512, 512)]
    [InlineData(7, 401, 300)]
    [InlineData(2, 512, 256)]
    public void GlobalStreamLeftToTheGroupsStillEndsWithTheAnsState(int effort, int width, int height)
    {
        // Noise defeats the global palette, so every channel is larger than a group and goes to the groups. libjxl writes the
        // global stream's ANS state anyway and its decoder skips it; jxl-oxide reads it, and failed at the section's end.
        Random random = new(effort);
        List<JxlChannel> channels = [];
        for (int c = 0; c < 3; c++)
        {
            JxlChannel ch = new(width, height, 0, 0);
            for (int i = 0; i < ch.Data.Length; i++)
            {
                ch.Data[i] = random.Next(256);
            }

            channels.Add(ch);
        }

        int shift = JxlModularEncoder.GroupShift(width, height);
        List<byte[]> sections = JxlModularEncoder.Encode(channels, 3, 8, effort, width, height, shift, 1);
        Assert.True(sections.Count > 1);

        // The LF global section ends with the state an empty ANS stream leaves, 0x130000 in 32 bits, then the zero padding:
        // the last set bit is the state's bit 20.
        byte[] global = sections[0];
        int bits = global.Length * 8;
        int last = bits - 1;
        while (last >= 0 && ((global[last >> 3] >> (last & 7)) & 1) == 0)
        {
            last--;
        }

        int start = last - 20;
        Assert.True(start >= 0 && start + 32 <= bits && start + 32 > bits - 8, "the state ends the section");
        uint state = 0;
        for (int b = 0; b < 32; b++)
        {
            state |= (uint)((global[(start + b) >> 3] >> ((start + b) & 7)) & 1) << b;
        }

        Assert.Equal(0x130000u, state);
    }

    /// <summary>A smooth image with noise, converted from 16-bit RGBA so every type gets values its precision holds.</summary>
    private static Raster<T> Noise<T>(int width, int height, int seed)
        where T : unmanaged, IPixelType<T>
    {
        Random random = new(seed);
        using Raster<RgbaF32> f = Raster.Create<RgbaF32>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<RgbaF32> row = f.Row(y);
            for (int x = 0; x < width; x++)
            {
                float n = (float)random.NextDouble() * 0.05f;
                row[x] = new RgbaF32((x / (float)width) + n, (y / (float)height) + n, 0.5f + n, x % 7 == 0 ? 0.5f : 1f);
            }
        }

        return f.As<T>();
    }
}
