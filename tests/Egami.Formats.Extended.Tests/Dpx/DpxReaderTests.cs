using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats.Extended.Dpx;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Dpx;

/// <summary>DPX reading: every depth, packing and byte order, descriptors, elements, orientation, log, run-length coding and damage.</summary>
public sealed class DpxReaderTests
{
    private const int W = 7;
    private const int H = 3;

    public static TheoryData<int, int, bool> Packings()
    {
        TheoryData<int, int, bool> data = [];
        foreach (bool big in (bool[])[true, false])
        {
            foreach ((int bits, int packing) in ((int, int)[])[(8, 0), (8, 1), (10, 0), (10, 1), (10, 2), (12, 0), (12, 1), (12, 2), (16, 0), (16, 1)])
            {
                data.Add(bits, packing, big);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Packings))]
    public void ReadsEveryPackingOfRgb(int bits, int packing, bool big)
    {
        uint max = (1u << bits) - 1;
        uint[] samples = [.. Enumerable.Range(0, W * H * 3).Select(i => (uint)(((i * 37) + 11) * ((max / 60) + 1)) % (max + 1))];
        byte[] file = DpxFiles.Build(W, H, big, 0, new DpxFiles.Element(50, bits, packing, samples));
        Assert.Equal(Scaled(samples, bits), Samples(file));
    }

    [Theory]
    [InlineData(10, 1, true)]
    [InlineData(10, 1, false)]
    [InlineData(10, 2, true)]
    [InlineData(10, 0, true)]
    [InlineData(12, 1, true)]
    [InlineData(16, 1, false)]
    [InlineData(8, 1, true)]
    public void ReadsLumaWithTheFirstSampleOfAWordInItsLowBits(int bits, int packing, bool big)
    {
        uint max = (1u << bits) - 1;
        uint[] samples = [.. Enumerable.Range(0, W * H).Select(i => (uint)((i * 131) + 5) % (max + 1))];
        byte[] file = DpxFiles.Build(W, H, big, 0, new DpxFiles.Element(6, bits, packing, samples));
        RasterInfo info = Raster.Probe(file, TestImages.Context);
        Assert.Equal(PixelChannelOrder.Gray, info.Layout.ChannelOrder);
        Assert.Equal(Scaled(samples, bits), Samples(file));
    }

    [Fact]
    public void ReadsOneBitSamplesAsBlackAndWhite()
    {
        uint[] samples = [.. Enumerable.Range(0, 37 * 2).Select(i => (uint)((i * 7 / 3) % 2))];
        byte[] file = DpxFiles.Build(37, 2, true, 0, new DpxFiles.Element(6, 1, 0, samples));
        using Raster<Gray8> raster = Raster.Load<Gray8>(file, TestImages.Context);
        Assert.Equal(samples.Select(s => (byte)(s * 255)), Enumerable.Range(0, 2).SelectMany(y => raster.Row(y).ToArray().Select(p => p.L)));
    }

    [Fact]
    public void ReadsRgbaAbgrAndFloat()
    {
        uint[] rgba = [.. Enumerable.Range(0, W * H * 4).Select(i => (uint)((i * 59) % 1024))];
        byte[] file = DpxFiles.Build(W, H, true, 0, new DpxFiles.Element(51, 10, 1, rgba));
        Assert.Equal(Scaled(rgba, 10), Samples(file));

        // ABGR reorders to RGBA.
        uint[] abgr = [.. Enumerable.Range(0, W * H).SelectMany(p => rgba.AsSpan(p * 4, 4).ToArray().Reverse())];
        Assert.Equal(Scaled(rgba, 10), Samples(DpxFiles.Build(W, H, true, 0, new DpxFiles.Element(52, 10, 1, abgr))));

        float[] values = [.. Enumerable.Range(0, W * H * 3).Select(i => (i * 0.37f) - 2)];
        byte[] floats = DpxFiles.Build(W, H, false, 0, new DpxFiles.Element(50, 32, 0, [.. values.Select(BitConverter.SingleToUInt32Bits)]));
        using Raster<RgbF32> raster = Raster.Load<RgbF32>(floats, TestImages.Context);
        Assert.Equal(values, Enumerable.Range(0, H).SelectMany(y => MemoryMarshal.Cast<RgbF32, float>(raster.Row(y)).ToArray()));
    }

    [Fact]
    public void ConvertsCbYCrToRgb()
    {
        // Video levels: Y 64 to 940, Cb and Cr 64 to 960 around 512 (10 bits). Black, white, and BT.709 red (Y 250, Cb 409, Cr 960).
        uint[][] pixels = [[512, 64, 512], [512, 940, 512], [409, 250, 960]];
        uint[] samples = [.. Enumerable.Range(0, 6).SelectMany(i => pixels[i % 3])];
        byte[] file = DpxFiles.Build(3, 2, true, 0, new DpxFiles.Element(102, 10, 1, samples, Colorimetric: 6));
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(file, TestImages.Context);
        Assert.Equal(new Rgb16(0, 0, 0), raster.Row(0)[0]);
        Assert.Equal(new Rgb16(65535, 65535, 65535), raster.Row(0)[1]);
        Rgb16 red = raster.Row(1)[2];
        Assert.InRange(red.R, 65000, 65535);
        Assert.InRange(red.G, 0, 600);
        Assert.InRange(red.B, 0, 600);

        // 4:2:2: Cb Y Cr Y; two pixels share the chroma.
        uint[] pairs = [512, 64, 512, 940, 409, 250, 960, 250];
        using Raster<Rgb16> subsampled = Raster.Load<Rgb16>(DpxFiles.Build(4, 1, true, 0, new DpxFiles.Element(100, 10, 1, pairs, Colorimetric: 6)), TestImages.Context);
        Assert.Equal(new Rgb16(0, 0, 0), subsampled.Row(0)[0]);
        Assert.Equal(new Rgb16(65535, 65535, 65535), subsampled.Row(0)[1]);
        Assert.Equal(red, subsampled.Row(0)[2]);
        Assert.Equal(red, subsampled.Row(0)[3]);
    }

    [Fact]
    public void ReadsEachElementAsAnImage()
    {
        uint[] rgb = [.. Enumerable.Range(0, W * H * 3).Select(i => (uint)((i * 41) % 1024))];
        uint[] alpha = [.. Enumerable.Range(0, W * H).Select(i => (uint)(i * 9))];
        byte[] file = DpxFiles.Build(W, H, true, 0, new DpxFiles.Element(50, 10, 1, rgb), new DpxFiles.Element(4, 8, 1, alpha));
        RasterInfo info = Raster.Probe(file, TestImages.Context);
        Assert.Equal(2, info.ImageCount);
        using RasterList all = Raster.LoadAll(file, TestImages.Context);
        Assert.Equal(2, all.Count);
        Assert.Equal(PixelChannelOrder.Rgb, all[0].Layout.ChannelOrder);
        Assert.Equal(1, ((DpxDetails)all[1].Metadata.FormatDetails!).Element);

        using Raster<Gray8> second = Raster.Load<Gray8>(file, TestImages.Context, new DpxReaderOptions { Element = 1 });
        Assert.Equal(alpha.Select(a => (byte)a), Enumerable.Range(0, H).SelectMany(y => second.Row(y).ToArray().Select(p => p.L)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Raster.Load(file, TestImages.Context, new DpxReaderOptions { Element = 2 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DpxReaderOptions { Element = -1 });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void AppliesFlipsOfTheOrientation(int orientation)
    {
        uint[] samples = [.. Enumerable.Range(0, W * H).Select(i => (uint)(i * 11))];
        byte[] file = DpxFiles.Build(W, H, true, orientation, new DpxFiles.Element(6, 8, 1, samples));
        using Raster<Gray8> raster = Raster.Load<Gray8>(file, TestImages.Context);
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int sx = (orientation & 1) != 0 ? W - 1 - x : x;
                int sy = (orientation & 2) != 0 ? H - 1 - y : y;
                Assert.Equal(samples[(sy * W) + sx], raster.Row(y)[x].L);
            }
        }
    }

    [Fact]
    public void LinearizesPrintingDensity()
    {
        // Reference black 95 is 0, reference white 685 is 1, and above white is brighter.
        uint[] samples = [95, 95, 95, 685, 685, 685, 1023, 1023, 1023];
        byte[] file = DpxFiles.Build(3, 1, true, 0, new DpxFiles.Element(50, 10, 1, samples, Transfer: 1));
        using Raster stored = Raster.Load(file, TestImages.Context);
        Assert.Equal(PixelChannelOrder.Rgb, stored.Layout.ChannelOrder);
        Assert.Equal(16, stored.Layout.BitsPerChannel);
        using Raster<RgbF32> linear = Raster.Load<RgbF32>(file, TestImages.Context, new DpxReaderOptions { Linearize = true });
        Assert.Equal(0, linear.Row(0)[0].R, 5);
        Assert.Equal(1, linear.Row(0)[1].G, 5);
        Assert.InRange(linear.Row(0)[2].B, 13, 14);
        Assert.Equal(Metadata.ColorSpaceHint.Linear, linear.Metadata.ColorSpace);

        // Linear elements are left as they are.
        byte[] notLog = DpxFiles.Build(3, 1, true, 0, new DpxFiles.Element(50, 10, 1, samples, Transfer: 2));
        using Raster plain = Raster.Load(notLog, TestImages.Context, new DpxReaderOptions { Linearize = true });
        Assert.Equal(16, plain.Layout.BitsPerChannel);
    }

    [Fact]
    public void ReadsRunLengthCodedElements()
    {
        // Each run starts with a flag word: count << 1, low bit 1 for one pixel repeated, 0 for that many pixels as they are.
        uint[] coded = [(5 << 1) | 1, 10, 20, 30, 2 << 1, 1, 2, 3, 4, 5, 6, (7 << 1) | 1, 7, 8, 9, (7 << 1) | 1, 0, 0, 0];
        byte[] file = DpxFiles.Build(7, 3, true, 0, new DpxFiles.Element(50, 16, 0, coded, Encoding: 1));
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(file, TestImages.Context);
        Assert.Equal(new Rgb16(10, 20, 30), raster.Row(0)[4]);
        Assert.Equal(new Rgb16(1, 2, 3), raster.Row(0)[5]);
        Assert.Equal(new Rgb16(4, 5, 6), raster.Row(0)[6]);
        Assert.Equal(new Rgb16(7, 8, 9), raster.Row(1)[6]);
        Assert.Equal(new Rgb16(0, 0, 0), raster.Row(2)[0]);
        Assert.True(((DpxDetails)raster.Metadata.FormatDetails!).RunLength);

        // A run of length 0 is damage: black from there, with a warning, or an error when strict.
        uint[] broken = [(5 << 1) | 1, 10, 20, 30, 0];
        byte[] damaged = DpxFiles.Build(7, 3, true, 0, new DpxFiles.Element(50, 16, 0, broken, Encoding: 1));
        using Raster<Rgb16> partial = Raster.Load<Rgb16>(damaged, TestImages.Context);
        Assert.Contains(partial.Metadata.Warnings, w => w.Id == "Dpx.RunDamaged");
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(damaged, TestImages.Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));
    }

    [Fact]
    public void KeepsTheHeaderFields()
    {
        byte[] file = DpxFiles.Build(W, H, false, 0, new DpxFiles.Element(50, 10, 1, new uint[W * H * 3], Transfer: 1, Colorimetric: 1));
        using Raster raster = Raster.Load(file, TestImages.Context);
        DpxDetails details = Assert.IsType<DpxDetails>(raster.Metadata.FormatDetails);
        Assert.False(details.BigEndian);
        Assert.Equal("V2.0", details.Version);
        Assert.Equal((50, 10, 1), (details.Descriptor, details.BitsPerSample, details.Packing));
        Assert.Equal(DpxTransfer.PrintingDensity, details.Transfer);
        Assert.Equal("01:02:03:04", details.Timecode);
        Assert.Equal(24f, details.FrameRate);
        Assert.Equal("DPX", raster.Metadata.FormatName);
    }

    [Fact]
    public void KeepsRowsBeforeTheEndOfAShortFile()
    {
        uint[] samples = [.. Enumerable.Range(0, 40 * 6 * 3).Select(i => (uint)(i % 1024))];
        byte[] file = DpxFiles.Build(40, 6, true, 0, new DpxFiles.Element(50, 10, 1, samples));
        byte[] cut = file[..(file.Length - 300)];
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(cut, TestImages.Context);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Dpx.RowsMissing");
        Assert.Equal(Scaled(samples, 10)[..(40 * 3 * 3)], Samples(cut)[..(40 * 3 * 3)]);
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(cut, TestImages.Context, new ReaderOptions { Recovery = RecoveryMode.Strict }));

        // A bottom-up file loses its top rows instead.
        byte[] upsideDown = DpxFiles.Build(40, 6, true, 2, new DpxFiles.Element(50, 10, 1, samples))[..(file.Length - 300)];
        using Raster<Rgb16> flipped = Raster.Load<Rgb16>(upsideDown, TestImages.Context);
        Assert.Contains(flipped.Metadata.Warnings, w => w.Id == "Dpx.RowsMissing");
    }

    [Fact]
    public void SkipsTheEndOfLinePadding()
    {
        uint[] samples = [.. Enumerable.Range(0, 40 * 6 * 3).Select(i => (uint)((i * 7) % 1024))];
        byte[] plain = DpxFiles.Build(40, 6, true, 0, new DpxFiles.Element(50, 10, 1, samples));
        int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(plain.AsSpan(4));
        const int RowBytes = 40 * 4;
        List<byte> padded = [.. plain[..offset]];
        for (int y = 0; y < 6; y++)
        {
            padded.AddRange(plain.AsSpan(offset + (y * RowBytes), RowBytes).ToArray());
            padded.AddRange(Enumerable.Repeat((byte)0xEE, 8));
        }

        byte[] file = [.. padded];
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(16), (uint)file.Length);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(780 + 32), 8);
        Assert.Equal(Scaled(samples, 10), Samples(file));

        // Padding a header claims is far larger than the file is read past, not held in memory: the first row loads and
        // the rest are missing.
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(780 + 32), 0x78000000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(file, TestImages.Context);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 16 << 20);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Dpx.RowsMissing");
        Assert.Equal(Scaled(samples, 10)[..(40 * 3)], Samples(file)[..(40 * 3)]);
    }

    [Fact]
    public void RefusesWhatItCannotRead()
    {
        byte[] file = DpxFiles.Build(W, H, true, 0, new DpxFiles.Element(50, 10, 1, new uint[W * H * 3]));
        Assert.True(DpxFormat.Info.Matches!(file));
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(file.AsMemory(0, 900).ToArray(), TestImages.Context));

        // User-defined descriptors and other depths.
        byte[] odd = DpxFiles.Build(W, H, true, 0, new DpxFiles.Element(150, 8, 1, new uint[W * H * 2]));
        Assert.Throws<UnsupportedFormatException>(() => Raster.Load(odd, TestImages.Context));
        byte[] mixed = DpxFiles.Build(W, H, true, 0, new DpxFiles.Element(50, 8, 1, new uint[W * H * 3]), new DpxFiles.Element(150, 8, 1, new uint[W * H * 2]));
        Assert.Equal(1, Raster.Probe(mixed, TestImages.Context).ImageCount);
    }

    [Fact]
    public void StreamsFromAForwardOnlyStream()
    {
        uint[] samples = [.. Enumerable.Range(0, W * H * 3).Select(i => (uint)((i * 97) % 1024))];
        byte[] file = DpxFiles.Build(W, H, true, 0, new DpxFiles.Element(50, 10, 1, samples));
        Assert.Equal(RasterAccess.Streamed, Raster.Probe(file, TestImages.Context).Access);
        using Raster<Rgb16> raster = Raster.Load<Rgb16>(new ForwardOnlyStream(file), TestImages.Context);
        Assert.Equal(Scaled(samples, 10), Enumerable.Range(0, H).SelectMany(y => MemoryMarshal.Cast<Rgb16, ushort>(raster.Row(y)).ToArray()));
    }

    /// <summary>Scales code values to the 8 or 16 bits Egami loads them at.</summary>
    internal static ushort[] Scaled(uint[] samples, int bits)
    {
        if (bits is 8 or 16)
        {
            return [.. samples.Select(s => (ushort)s)];
        }

        long max = (1L << bits) - 1;
        return [.. samples.Select(s => (ushort)(((s * 65535L) + (max >> 1)) / max))];
    }

    /// <summary>Loads a file in its natural type and returns its samples, 8-bit ones widened to ushort.</summary>
    internal static ushort[] Samples(byte[] file)
    {
        using Raster raster = Raster.Load(file, TestImages.Context);
        List<ushort> samples = [];
        bool wide = raster.Layout.BitsPerChannel > 8;
        switch (raster.Layout.ChannelOrder, wide)
        {
            case (PixelChannelOrder.Gray, false):
                Add<Gray8>(raster, samples, false);
                break;
            case (PixelChannelOrder.Gray, true):
                Add<Gray16>(raster, samples, true);
                break;
            case (PixelChannelOrder.Rgb, false):
                Add<Rgb8>(raster, samples, false);
                break;
            case (PixelChannelOrder.Rgb, true):
                Add<Rgb16>(raster, samples, true);
                break;
            case (_, false):
                Add<Rgba8>(raster, samples, false);
                break;
            default:
                Add<Rgba16>(raster, samples, true);
                break;
        }

        return [.. samples];
    }

    private static void Add<TPixel>(Raster raster, List<ushort> samples, bool wide)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> typed = raster.As<TPixel>();
        for (int y = 0; y < typed.Height; y++)
        {
            ReadOnlySpan<byte> row = MemoryMarshal.AsBytes(typed.Row(y));
            if (wide)
            {
                samples.AddRange(MemoryMarshal.Cast<byte, ushort>(row).ToArray());
            }
            else
            {
                samples.AddRange(row.ToArray().Select(b => (ushort)b));
            }
        }
    }
}
