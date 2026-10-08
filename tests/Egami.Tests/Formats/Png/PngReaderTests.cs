using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

using Egami.Formats;
using Egami.Formats.Png;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Png;

public class PngReaderTests
{
    private static readonly ReaderOptions Strict = new() { Recovery = RecoveryMode.Strict };

    public static TheoryData<PngColorType, int, bool, bool> AllForms()
    {
        TheoryData<PngColorType, int, bool, bool> data = [];
        (PngColorType Type, int[] Depths)[] forms =
        [
            (PngColorType.Gray, [1, 2, 4, 8, 16]),
            (PngColorType.Rgb, [8, 16]),
            (PngColorType.Palette, [1, 2, 4, 8]),
            (PngColorType.GrayAlpha, [8, 16]),
            (PngColorType.Rgba, [8, 16]),
        ];
        foreach ((PngColorType type, int[] depths) in forms)
        {
            foreach (int depth in depths)
            {
                foreach (bool interlaced in new[] { false, true })
                {
                    data.Add(type, depth, interlaced, false);
                    if (type is PngColorType.Gray or PngColorType.Rgb or PngColorType.Palette)
                    {
                        data.Add(type, depth, interlaced, true);
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllForms))]
    public void DecodesEveryColorTypeBitDepthAndInterlace(PngColorType colorType, int bitDepth, bool interlaced, bool transparency)
    {
        // Odd sizes exercise partial bytes, every Adam7 pass edge and every filter type on every row position.
        foreach ((int width, int height) in new[] { (1, 1), (3, 2), (13, 11), (33, 9) })
        {
            PngBuilder builder = Build(colorType, bitDepth, interlaced, transparency, width, height, seed: width * height);
            byte[] file = builder.Build();

            using Raster natural = Raster.Load(file);
            Assert.Equal(ExpectedLayout(builder), natural.Layout);

            using Raster<Rgba16> wide = Raster.Load<Rgba16>(file, options: Strict);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Rgba16 expected = Expected(builder, x, y);
                    Assert.True(expected == wide.GetPixel(x, y), $"{width}×{height} pixel ({x}, {y}): expected {expected}, got {wide.GetPixel(x, y)}");
                }
            }

            using Raster<Rgba16> viaNatural = natural switch
            {
                Raster<Rgba16> r => r.Copy(),
                _ => natural.As<Rgba16>(),
            };
            for (int y = 0; y < height; y++)
            {
                Assert.Equal(wide.Row(y).ToArray(), viaNatural.Row(y).ToArray());
            }
        }
    }

    [Fact]
    public void ProbeReadsTheHeaderAndMetadataBeforeImageData()
    {
        PngBuilder builder = Build(PngColorType.Rgb, 8, false, transparency: true, 40, 30, seed: 1);
        builder.ChunksBeforeImageData.Add(("tEXt", PngBuilder.Latin1("Title", "Probe")));
        RasterInfo info = Raster.Probe(builder.Build());
        Assert.Same(PngFormat.Info, info.Format);
        Assert.Equal(40, info.Width);
        Assert.Equal(30, info.Height);
        Assert.Equal(Rgba8.Layout, info.Layout);
        Assert.Equal(1, info.FrameCount);
        Assert.True(info.HasMetadata);
        Assert.Equal(40 * 30 * 4, info.EstimatedBytes);
    }

    [Fact]
    public void ReadsMetadataChunks()
    {
        PngBuilder builder = Build(PngColorType.Rgba, 8, false, false, 4, 4, seed: 2);
        byte[] icc = Encoding.ASCII.GetBytes(new string('i', 300));
        builder.ChunksBeforePalette.Add(("gAMA", BigEndian(45455)));
        builder.ChunksBeforePalette.Add(("iCCP", [.. "Test profile"u8, 0, 0, .. PngBuilder.Compress(icc)]));
        builder.ChunksBeforeImageData.Add(("pHYs", [.. BigEndian(3780), .. BigEndian(3780), 1]));
        builder.ChunksBeforeImageData.Add(("tEXt", PngBuilder.Latin1("Title", "Café")));
        builder.ChunksBeforeImageData.Add(("zTXt", [.. "Comment"u8, 0, 0, .. PngBuilder.Compress(Encoding.Latin1.GetBytes("compressed text"))]));
        builder.ChunksBeforeImageData.Add(("bKGD", [0, 1, 0, 2, 0, 3]));
        builder.ChunksAfterImageData.Add(("iTXt", [.. "Author"u8, 0, 1, 0, .. "nb"u8, 0, .. "Forfatter"u8, 0, .. PngBuilder.Compress("Lars Ødegård"u8.ToArray())]));
        builder.ChunksAfterImageData.Add(("eXIf", [0x4D, 0x4D, 0, 42]));
        builder.ChunksAfterImageData.Add(("prVT", [9, 9]));   // unknown, not safe to copy: dropped
        builder.ChunksAfterImageData.Add(("prVu", [7]));      // unknown, safe to copy: kept

        using Raster raster = Raster.Load(builder.Build(), options: Strict);
        RasterMetadata metadata = raster.Metadata;
        PngMetadata png = Assert.IsType<PngMetadata>(metadata.FormatDetails);

        Assert.Equal("PNG", metadata.FormatName);
        Assert.Equal(icc, metadata.IccProfile.ToArray());
        Assert.Equal("Test profile", png.IccProfileName);
        Assert.Equal(ColorSpaceHint.EmbeddedIcc, metadata.ColorSpace);
        Assert.Equal(45455u, png.Gamma);
        Assert.Equal(new Resolution(3780, 3780, ResolutionUnit.PerMeter), metadata.Resolution);
        Assert.Equal(96.012, metadata.Resolution!.Value.ToPerInch().X, 3);
        Assert.Equal(
            [new TextEntry("Title", "Café"), new TextEntry("Comment", "compressed text"), new TextEntry("Author", "Lars Ødegård", "nb")],
            metadata.Text);
        Assert.Equal(["tEXt", "zTXt", "iTXt"], png.TextSources.Select(s => s.ChunkType));
        Assert.Equal([0x4D, 0x4D, 0, 42], metadata.Exif.ToArray());
        Assert.Equal(["bKGD", "prVu"], png.OtherChunks.Select(c => c.Type));
        Assert.Equal(PngChunkPosition.BeforePalette, png.OtherChunks[0].Position);
        Assert.Equal(PngChunkPosition.AfterImageData, png.OtherChunks[1].Position);
        Assert.Equal(PngColorType.Rgba, png.ColorType);
        Assert.Empty(metadata.Warnings);
    }

    [Fact]
    public void ColorSpaceHintFollowsTheSpecPrecedence()
    {
        Assert.Equal(ColorSpaceHint.Srgb, HintFor(("sRGB", [0]), ("gAMA", BigEndian(45455))));
        Assert.Equal(ColorSpaceHint.Linear, HintFor(("gAMA", BigEndian(100000))));
        Assert.Equal(ColorSpaceHint.Srgb, HintFor(("cICP", [1, 13, 0, 1]), ("gAMA", BigEndian(100000))));

        PngBuilder builder = Build(PngColorType.Gray, 8, false, false, 2, 2, seed: 3);
        builder.ChunksBeforePalette.Add(("gAMA", BigEndian(45455)));
        using Raster raster = Raster.Load(builder.Build());
        Assert.Equal(ColorSpaceHint.Gamma, raster.Metadata.ColorSpace);
        Assert.Equal(2.2, raster.Metadata.Gamma!.Value, 3);

        static ColorSpaceHint HintFor(params (string Type, byte[] Data)[] chunks)
        {
            PngBuilder b = Build(PngColorType.Gray, 8, false, false, 2, 2, seed: 3);
            b.ChunksBeforePalette.AddRange(chunks);
            using Raster r = Raster.Load(b.Build());
            return r.Metadata.ColorSpace;
        }
    }

    [Fact]
    public async Task ProbeAsyncReadsOnlyWhatTheHeaderNeeds()
    {
        // A large ICC profile before the image data forces more than the first 64 KB to be read.
        PngBuilder builder = Build(PngColorType.Rgb, 8, false, false, 300, 200, seed: 21);
        byte[] icc = new byte[200_000];
        new Random(5).NextBytes(icc);
        builder.ChunksBeforePalette.Add(("iCCP", [.. "Big"u8, 0, 0, .. PngBuilder.Compress(icc)]));
        byte[] file = builder.Build();

        using CountingReadStream stream = new(file);
        RasterInfo info = await Raster.ProbeAsync(stream, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(300, info.Width);
        Assert.True(info.HasMetadata);
        Assert.True(stream.BytesRead < file.Length, $"read {stream.BytesRead} of {file.Length} bytes");

        using CountingReadStream small = new(Build(PngColorType.Gray, 8, false, false, 2000, 2000, seed: 22).Build());
        Assert.Equal(2000, (await Raster.ProbeAsync(small, cancellationToken: TestContext.Current.CancellationToken)).Height);
        Assert.Equal(64 * 1024, small.BytesRead);
    }

    private sealed class CountingReadStream(byte[] data) : MemoryStream(data, writable: false)
    {
        public long BytesRead { get; private set; }

        // MemoryStream's span overloads call back into Read(byte[], int, int) for derived types, so only that one counts.
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = base.Read(buffer, offset, count);
            BytesRead += n;
            return n;
        }
    }

    [Fact]
    public void ReadMetadataFalseSkipsMetadata()
    {
        PngBuilder builder = Build(PngColorType.Gray, 8, false, false, 2, 2, seed: 4);
        builder.ChunksBeforeImageData.Add(("tEXt", PngBuilder.Latin1("Title", "x")));
        using Raster raster = Raster.Load(builder.Build(), options: new ReaderOptions { ReadMetadata = false });
        Assert.Empty(raster.Metadata.Text);
    }

    [Fact]
    public void HugeHeaderFailsBeforeAllocating()
    {
        PngBuilder builder = new(100_000, 100_000, PngColorType.Rgba, 8) { IdatChunks = 1 };
        byte[] file = Truncate(builder, 100_000, 100_000);
        CountingSource memory = new();
        EgamiContext context = EgamiContext.Default with { Memory = memory };

        LimitExceededException e = Assert.Throws<LimitExceededException>(() => Raster.Load(file, context));
        Assert.Equal("MaxWidth", e.LimitName);
        Assert.Equal(0, memory.Allocations);

        context = context with { Limits = SafetyLimits.Default with { MaxWidth = 1_000_000, MaxHeight = 1_000_000 } };
        Assert.Equal("MaxPixels", Assert.Throws<LimitExceededException>(() => Raster.Load(file, context)).LimitName);
        Assert.Equal(0, memory.Allocations);
    }

    [Fact]
    public void LoadLeavesTheStreamJustPastIend()
    {
        byte[] file = Build(PngColorType.Rgb, 8, false, false, 3, 3, seed: 5).Build();
        using MemoryStream stream = new([.. file, .. "after"u8]);
        using (Raster.Load(stream))
        {
            Assert.Equal(file.Length, stream.Position);
        }
    }

    // ---- Strict versus lenient: one test per row of the design's table. ----

    [Fact]
    public void BadCrcOnACriticalChunkAlwaysFails()
    {
        foreach (string chunk in new[] { "IHDR", "PLTE", "IDAT", "IEND" })
        {
            PngBuilder builder = Build(PngColorType.Palette, 8, false, false, 4, 4, seed: 6);
            builder.BadCrcChunk = chunk;
            byte[] file = builder.Build();
            CorruptDataException e = Assert.Throws<CorruptDataException>(() => Raster.Load(file));
            Assert.Contains(chunk, e.Message, StringComparison.Ordinal);
            Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: Strict));
        }
    }

    [Fact]
    public void BadCrcOnAnAncillaryChunk()
    {
        PngBuilder builder = Build(PngColorType.Gray, 8, false, false, 4, 4, seed: 7);
        builder.ChunksBeforeImageData.Add(("tEXt", PngBuilder.Latin1("Title", "x")));
        builder.BadCrcChunk = "tEXt";
        byte[] file = builder.Build();

        Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: Strict));
        using Raster raster = Raster.Load(file);
        Assert.Empty(raster.Metadata.Text);
        Assert.Contains(raster.Metadata.Warnings, w => w.Contains("tEXt", StringComparison.Ordinal));
    }

    [Fact]
    public void BadAdlerChecksum()
    {
        PngBuilder builder = Build(PngColorType.Rgb, 8, false, false, 5, 5, seed: 8);
        builder.EditCompressed = data =>
        {
            data[^1] ^= 0xFF;
            return data;
        };
        byte[] file = builder.Build();

        Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: Strict));
        using Raster<Rgba16> raster = Raster.Load<Rgba16>(file);
        Assert.Single(raster.Metadata.Warnings);
        Assert.Equal(Expected(builder, 4, 4), raster.GetPixel(4, 4));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ImageDataEndsEarly(bool interlaced)
    {
        PngBuilder builder = Build(PngColorType.Rgba, 8, interlaced, false, 16, 16, seed: 9);
        builder.Filter = _ => 0;
        builder.EditCompressed = _ =>
        {
            // Keep only the first rows' worth of uncompressed data, as a valid zlib stream that ends too soon.
            byte[] raw = builder.RawImageData();
            return PngBuilder.Compress(raw[..(raw.Length / 3)]);
        };
        byte[] file = builder.Build();

        Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: Strict));
        using Raster<Rgba16> raster = Raster.Load<Rgba16>(file);
        Assert.Contains(raster.Metadata.Warnings, w => w.Contains("ends early", StringComparison.Ordinal));
        Assert.Equal(Expected(builder, 0, 0), raster.GetPixel(0, 0));
        Assert.Equal(default, raster.GetPixel(15, 15));
    }

    [Fact]
    public void FileEndsInsideImageData()
    {
        PngBuilder builder = Build(PngColorType.Rgb, 8, false, false, 30, 30, seed: 10);
        builder.IdatChunks = 1;
        byte[] whole = builder.Build();
        byte[] file = whole[..(whole.Length - 12 - 4 - 20)];

        Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: Strict));
        using Raster raster = Raster.Load(file);
        Assert.NotEmpty(raster.Metadata.Warnings);
    }

    [Fact]
    public void PaletteIndexOutOfRange()
    {
        PngBuilder builder = Build(PngColorType.Palette, 8, false, false, 4, 4, seed: 11);
        builder.SetSample(2, 3, 0, 200);
        byte[] file = builder.Build();

        Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: Strict));
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(file);
        Assert.Equal(Rgba8.Black, raster.GetPixel(2, 3));
        Assert.Contains(raster.Metadata.Warnings, w => w.Contains("palette", StringComparison.Ordinal));
    }

    [Fact]
    public void OutOfOrderAncillaryChunk()
    {
        PngBuilder builder = Build(PngColorType.Palette, 8, false, false, 4, 4, seed: 12);
        builder.ChunksBeforeImageData.Add(("gAMA", BigEndian(45455)));   // after PLTE: not allowed
        byte[] file = builder.Build();

        Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: Strict));
        using Raster raster = Raster.Load(file);
        Assert.Null(((PngMetadata)raster.Metadata.FormatDetails!).Gamma);
        Assert.Contains(raster.Metadata.Warnings, w => w.Contains("gAMA", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingIend()
    {
        PngBuilder builder = Build(PngColorType.Gray, 8, false, false, 4, 4, seed: 13);
        builder.OmitIend = true;
        byte[] file = builder.Build();

        Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: Strict));
        using Raster raster = Raster.Load(file);
        Assert.Contains(raster.Metadata.Warnings, w => w.Contains("IEND", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidHeaderUnknownCriticalChunkAndBadFilterAlwaysFail()
    {
        // Bit depth 3 is not allowed.
        byte[] file = Build(PngColorType.Gray, 8, false, false, 2, 2, seed: 14).Build();
        file[24] = 3;
        FixCrc(file, 12, 13);
        AssertAlwaysCorrupt(file);

        PngBuilder unknown = Build(PngColorType.Gray, 8, false, false, 2, 2, seed: 15);
        unknown.ChunksBeforeImageData.Add(("ABCD", [1]));
        AssertAlwaysCorrupt(unknown.Build());

        PngBuilder badFilter = Build(PngColorType.Gray, 8, false, false, 2, 2, seed: 16);
        badFilter.EditCompressed = _ =>
        {
            byte[] raw = badFilter.RawImageData();
            raw[0] = 5;
            return PngBuilder.Compress(raw);
        };
        AssertAlwaysCorrupt(badFilter.Build());

        PngBuilder notConsecutive = Build(PngColorType.Gray, 8, false, false, 2, 2, seed: 17);
        notConsecutive.ChunksAfterImageData.Add(("tEXt", PngBuilder.Latin1("Title", "x")));
        notConsecutive.ChunksAfterImageData.Add(("IDAT", [0]));
        AssertAlwaysCorrupt(notConsecutive.Build());

        static void AssertAlwaysCorrupt(byte[] file)
        {
            Assert.Throws<CorruptDataException>(() => Raster.Load(file));
            Assert.Throws<CorruptDataException>(() => Raster.Load(file, options: Strict));
        }
    }

    [Fact]
    public void CompressedMetadataIsCappedByTheLimit()
    {
        PngBuilder builder = Build(PngColorType.Gray, 8, false, false, 2, 2, seed: 18);
        builder.ChunksBeforeImageData.Add(("zTXt", [.. "Bomb"u8, 0, 0, .. PngBuilder.Compress(new byte[2_000_000])]));
        EgamiContext context = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxMetadataBytes = 1_000_000 } };
        Assert.Equal("MaxMetadataBytes", Assert.Throws<LimitExceededException>(() => Raster.Load(builder.Build(), context)).LimitName);
    }

    [Fact]
    public void ExtraImageDataIsIgnoredWithAWarning()
    {
        PngBuilder builder = Build(PngColorType.Gray, 8, false, false, 3, 3, seed: 19);
        builder.EditCompressed = _ => PngBuilder.Compress([.. builder.RawImageData(), 0, 0, 0, 0]);
        using Raster raster = Raster.Load(builder.Build(), options: Strict);
        Assert.Contains(raster.Metadata.Warnings, w => w.Contains("extra", StringComparison.Ordinal));
    }

    [Fact]
    public void CancellationIsCheckedBetweenRows()
    {
        byte[] file = Build(PngColorType.Rgb, 8, false, false, 64, 64, seed: 20).Build();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Raster.LoadAsync(new MemoryStream(file), cancellationToken: cancelled.Token).GetAwaiter().GetResult());
    }

    internal static PngBuilder Build(PngColorType colorType, int bitDepth, bool interlaced, bool transparency, int width, int height, int seed)
    {
        Random random = new(seed + ((int)colorType * 100) + bitDepth);
        PngBuilder builder = new(width, height, colorType, bitDepth) { Interlaced = interlaced };
        if (colorType == PngColorType.Palette)
        {
            int entries = Math.Min(1 << bitDepth, 200);
            builder.Palette = new byte[entries * 3];
            random.NextBytes(builder.Palette);
            if (transparency)
            {
                builder.Transparency = new byte[Math.Max(1, entries / 2)];
                random.NextBytes(builder.Transparency);
            }
        }

        builder.Fill(random);
        if (transparency && colorType != PngColorType.Palette)
        {
            // Use an existing pixel's color as the key so some pixels really are transparent.
            int channels = colorType == PngColorType.Gray ? 1 : 3;
            builder.Transparency = new byte[channels * 2];
            for (int c = 0; c < channels; c++)
            {
                BinaryPrimitives.WriteUInt16BigEndian(builder.Transparency.AsSpan(c * 2), (ushort)builder.Sample(0, 0, c));
            }
        }

        return builder;
    }

    /// <summary>The pixel the specification defines, as 16-bit RGBA, worked out from the builder's samples.</summary>
    internal static Rgba16 Expected(PngBuilder b, int x, int y)
    {
        int max = b.MaxSample;
        ushort Scale(int sample) => b.BitDepth == 16 ? (ushort)sample : (ushort)(sample * 255 / max * 257);

        bool KeyMatches(int channels)
        {
            if (b.Transparency is null)
            {
                return false;
            }

            for (int c = 0; c < channels; c++)
            {
                if (BinaryPrimitives.ReadUInt16BigEndian(b.Transparency.AsSpan(c * 2)) != b.Sample(x, y, c))
                {
                    return false;
                }
            }

            return true;
        }

        switch (b.ColorType)
        {
            case PngColorType.Gray:
                ushort l = Scale(b.Sample(x, y, 0));
                return new Rgba16(l, l, l, KeyMatches(1) ? (ushort)0 : ushort.MaxValue);
            case PngColorType.Rgb:
                return new Rgba16(Scale(b.Sample(x, y, 0)), Scale(b.Sample(x, y, 1)), Scale(b.Sample(x, y, 2)), KeyMatches(3) ? (ushort)0 : ushort.MaxValue);
            case PngColorType.Palette:
                int i = b.Sample(x, y, 0);
                byte alpha = b.Transparency is not null && i < b.Transparency.Length ? b.Transparency[i] : (byte)255;
                return new Rgba16((ushort)(b.Palette![i * 3] * 257), (ushort)(b.Palette[(i * 3) + 1] * 257), (ushort)(b.Palette[(i * 3) + 2] * 257), (ushort)(alpha * 257));
            case PngColorType.GrayAlpha:
                ushort g = Scale(b.Sample(x, y, 0));
                return new Rgba16(g, g, g, Scale(b.Sample(x, y, 1)));
            default:
                return new Rgba16(Scale(b.Sample(x, y, 0)), Scale(b.Sample(x, y, 1)), Scale(b.Sample(x, y, 2)), Scale(b.Sample(x, y, 3)));
        }
    }

    private static PixelLayoutInfo ExpectedLayout(PngBuilder b)
    {
        bool trns = b.Transparency is not null && (b.ColorType != PngColorType.Palette || b.Transparency.Any(a => a != 255));
        return (b.ColorType, b.BitDepth) switch
        {
            (PngColorType.Gray, 16) => trns ? Rgba16.Layout : Gray16.Layout,
            (PngColorType.Gray, _) => trns ? GrayAlpha8.Layout : Gray8.Layout,
            (PngColorType.Rgb, 16) or (PngColorType.GrayAlpha, 16) or (PngColorType.Rgba, 16) => Rgba16.Layout,
            (PngColorType.Rgb, _) or (PngColorType.Palette, _) => trns ? Rgba8.Layout : Rgb8.Layout,
            (PngColorType.GrayAlpha, _) => GrayAlpha8.Layout,
            _ => Rgba8.Layout,
        };
    }

    private static byte[] BigEndian(uint value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    /// <summary>Builds just the signature, IHDR and the start of an IDAT for a huge image.</summary>
    private static byte[] Truncate(PngBuilder builder, int width, int height)
    {
        PngBuilder small = new(1, 1, builder.ColorType, builder.BitDepth);
        byte[] file = small.Build();
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(20), (uint)height);
        FixCrc(file, 12, 13);
        return file;
    }

    /// <summary>Recomputes the CRC of the chunk whose type starts at <paramref name="typeOffset"/>.</summary>
    private static void FixCrc(byte[] file, int typeOffset, int length)
    {
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(typeOffset + 4 + length), Crc32.Compute(file.AsSpan(typeOffset, 4 + length)));
    }

    private sealed class CountingSource : IPixelMemorySource
    {
        private int _allocations;

        public int MaxBlockBytes => PooledPixelMemorySource.Shared.MaxBlockBytes;

        public int Allocations => _allocations;

        public System.Buffers.IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            Interlocked.Increment(ref _allocations);
            return PooledPixelMemorySource.Shared.Allocate(byteLength, clear);
        }
    }
}
