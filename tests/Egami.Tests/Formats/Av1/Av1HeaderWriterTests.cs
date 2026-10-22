using Egami.Formats.Av1;
using Egami.Formats.Heif;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// The AV1 header writer: sequence headers and key frame headers it writes read back through Egami's header readers with the
/// same fields, over the corpus's sequence headers and over random frame parameters.
/// </summary>
public class Av1HeaderWriterTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");

    [Fact]
    public void BitWriterRoundTripsEveryDescriptor()
    {
        Random random = new(5);
        List<(int Kind, uint Value, int N)> written = [];
        Av1BitWriter w = new();
        for (int i = 0; i < 4000; i++)
        {
            int kind = random.Next(4);
            int n = random.Next(1, 33);
            uint value = kind switch
            {
                0 => (uint)(random.NextInt64(0, 1L << n)),
                1 => (uint)random.Next(-(1 << (Math.Min(n, 31) - 1)), 1 << (Math.Min(n, 31) - 1)),
                2 => (uint)random.Next(0, Math.Max(1, (int)Math.Min(1L << Math.Min(n, 30), int.MaxValue))),
                _ => (uint)random.Next(0, 1 << 20),
            };
            n = kind == 1 ? Math.Min(n, 31) : n;
            switch (kind)
            {
                case 0:
                    w.Bits(value, n);
                    break;
                case 1:
                    w.Signed((int)value, n);
                    break;
                case 2:
                    n = (int)value + 1 + random.Next(100);
                    w.NonSymmetric(value, (uint)n);
                    break;
                default:
                    w.Uvlc(value);
                    break;
            }

            written.Add((kind, value, n));
        }

        int bits = w.Position;
        byte[] data = w.ToArray();
        Assert.Equal((bits + 7) / 8, data.Length);
        Av1BitReader r = new(data, "AV1");
        foreach ((int kind, uint value, int n) in written)
        {
            uint read = kind switch
            {
                0 => r.Bits(n),
                1 => (uint)r.Signed(n),
                2 => r.NonSymmetric((uint)n),
                _ => r.Uvlc(),
            };
            Assert.Equal(value, read);
        }

        Assert.Equal(bits, r.Position);
    }

    [Fact]
    public void Leb128RoundTrips()
    {
        foreach (ulong value in new ulong[] { 0, 1, 127, 128, 16383, 16384, 1u << 28, uint.MaxValue })
        {
            List<byte> bytes = [];
            Av1BitWriter.Leb128(bytes, value);
            Assert.Equal((value, bytes.Count), Av1BitReader.Leb128([.. bytes]));
            bytes.Clear();
            Av1BitWriter.Leb128(bytes, value, 8);
            Assert.Equal((value, 8), Av1BitReader.Leb128([.. bytes]));
        }
    }

    public static TheoryData<string> CorpusFiles() => [.. Directory.EnumerateFiles(Corpus, "*.avif").Select(p => Path.GetFileName(p)!).Order()];

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void SequenceHeadersRoundTrip(string file)
    {
        HeifFile heif;
        try
        {
            heif = HeifFile.Parse(File.ReadAllBytes(Path.Combine(Corpus, file)), "AVIF");
        }
        catch (CorruptDataException)
        {
            return;
        }

        foreach (HeifItem item in heif.Items.Where(i => i.Type == IsoTypes.Of("av01") && i.Located))
        {
            Av1SequenceHeader? sequence;
            try
            {
                sequence = Av1SequenceHeader.Find(heif.ItemData(item).Span, "AVIF");
            }
            catch (CorruptDataException)
            {
                continue;
            }

            if (sequence is null)
            {
                continue;
            }

            // The writer leaves out timing and decoder model information and frame ids.
            Av1SequenceHeader writable = sequence with
            {
                TimingInfoPresent = false,
                DecoderModelInfoPresent = false,
                BufferDelayLength = 0,
                BufferRemovalTimeLength = 0,
                FramePresentationTimeLength = 0,
                EqualPictureInterval = false,
                OperatingPoints = [.. sequence.OperatingPoints.Select(p => p with { DecoderModelPresent = false })],
                FrameIdNumbersPresent = false,
                DeltaFrameIdLength = 0,
                AdditionalFrameIdLength = 0,
            };
            Av1SequenceHeader read = Av1SequenceHeader.Parse(Av1ObuWriter.SequenceHeader(writable), "AVIF");
            Assert.Equal(writable.Describe(), read.Describe());
        }
    }

    [Theory]
    [InlineData(0, true, 8, false, 1, 1)]
    [InlineData(1, true, 10, false, 0, 0)]
    [InlineData(2, false, 12, false, 1, 0)]
    [InlineData(2, true, 12, false, 1, 1)]
    [InlineData(0, true, 10, true, 1, 1)]
    [InlineData(0, false, 8, false, 1, 1)]
    public void FrameHeadersRoundTrip(int profile, bool reduced, int bitDepth, bool monochrome, int ssx, int ssy)
    {
        Random random = new((profile * 100) + bitDepth + (reduced ? 7 : 0));
        for (int iteration = 0; iteration < 300; iteration++)
        {
            int width = random.Next(1, 3000), height = random.Next(1, 3000);
            bool sb128 = random.Next(2) == 0;
            Av1SequenceHeader s = new()
            {
                Profile = profile,
                StillPicture = true,
                ReducedStillPictureHeader = reduced,
                OperatingPoints = [new Av1OperatingPoint(0, random.Next(0, 24), 0)],
                FrameWidthBits = 16,
                FrameHeightBits = 16,
                MaxFrameWidth = reduced ? width : 4000,
                MaxFrameHeight = reduced ? height : 4000,
                Use128x128Superblock = sb128,
                EnableFilterIntra = true,
                EnableIntraEdgeFilter = true,
                ForceScreenContentTools = reduced ? 2 : random.Next(3),
                ForceIntegerMv = 2,
                EnableCdef = random.Next(4) != 0,
                EnableRestoration = random.Next(4) != 0,
                BitDepth = bitDepth,
                Monochrome = monochrome,
                ColorDescriptionPresent = true,
                ColorPrimaries = 9,
                TransferCharacteristics = 16,
                MatrixCoefficients = 9,
                FullRange = random.Next(2) == 0,
                SubsamplingX = monochrome ? 1 : ssx,
                SubsamplingY = monochrome ? 1 : ssy,
                ChromaSamplePosition = !monochrome && ssx == 1 && ssy == 1 ? random.Next(3) : 0,
                SeparateUvDeltaQ = random.Next(2) == 0,
            };
            s = Av1SequenceHeader.Parse(Av1ObuWriter.SequenceHeader(s), "AV1");
            Av1FrameParameters f = RandomParameters(random, s, width, height);
            Av1BitWriter w = new();
            Av1ObuWriter.FrameHeader(w, s, f);
            int bits = w.Position;
            w.TrailingBits();
            Av1FrameHeader h = Av1FrameHeader.Parse(w.ToArray(), s, 0, 0, "AV1");
            Assert.Equal(bits, h.BitLength);
            Check(s, f, h);
        }
    }

    private static Av1FrameParameters RandomParameters(Random random, Av1SequenceHeader s, int width, int height)
    {
        Av1FrameParameters f = new()
        {
            Width = width,
            Height = height,
            DisableCdfUpdate = random.Next(4) == 0,
            BaseQIdx = random.Next(4) == 0 ? 0 : random.Next(256),
            ReducedTxSet = random.Next(2) == 0,
            TxModeSelect = random.Next(2) == 0,
            DisableFrameEndUpdateCdf = random.Next(2) == 0,
        };
        bool screen = s.ForceScreenContentTools == 2 ? random.Next(2) == 0 : s.ForceScreenContentTools == 1;
        f.AllowScreenContentTools = screen;
        f.ForceIntegerMv = screen && random.Next(2) == 0;
        f.AllowIntrabc = screen && random.Next(3) == 0;
        if (random.Next(3) != 0)
        {
            f.DeltaQYDc = random.Next(-64, 64);
            f.DeltaQUDc = random.Next(-64, 64);
            f.DeltaQUAc = random.Next(-64, 64);
            (f.DeltaQVDc, f.DeltaQVAc) = s.SeparateUvDeltaQ && random.Next(2) == 0 ? (random.Next(-64, 64), random.Next(-64, 64)) : (f.DeltaQUDc, f.DeltaQUAc);
        }

        f.UsingQmatrix = random.Next(2) == 0;
        f.QmY = random.Next(16);
        f.QmU = random.Next(16);
        f.QmV = s.SeparateUvDeltaQ ? random.Next(16) : f.QmU;
        f.SegmentationEnabled = random.Next(3) == 0;
        int[] max = [255, 63, 63, 63, 63, 7, 0, 0];
        for (int i = 0; f.SegmentationEnabled && i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                f.FeatureEnabled[i, j] = random.Next(4) == 0;
                f.FeatureData[i, j] = j < 5 ? random.Next(-max[j], max[j] + 1) : random.Next(max[j] + 1);
            }
        }

        f.DeltaQPresent = random.Next(2) == 0;
        f.DeltaQRes = random.Next(4);
        f.DeltaLfPresent = random.Next(2) == 0;
        f.DeltaLfRes = random.Next(4);
        f.DeltaLfMulti = random.Next(2) == 0;
        for (int i = 0; i < 4; i++)
        {
            f.LoopFilterLevel[i] = random.Next(64);
        }

        if (random.Next(3) == 0)
        {
            f.LoopFilterLevel[0] = f.LoopFilterLevel[1] = 0;
        }

        f.LoopFilterSharpness = random.Next(8);
        f.LoopFilterDeltaEnabled = random.Next(2) == 0;
        if (random.Next(2) == 0)
        {
            f.LoopFilterRefDeltas = [.. Enumerable.Range(0, 8).Select(_ => random.Next(-63, 64))];
            f.LoopFilterModeDeltas = [random.Next(-63, 64), random.Next(-63, 64)];
        }

        f.CdefDamping = random.Next(3, 7);
        f.CdefBits = random.Next(4);
        int[] secondary = [0, 1, 2, 4];
        for (int i = 0; i < 8; i++)
        {
            f.CdefYPriStrength[i] = random.Next(16);
            f.CdefYSecStrength[i] = secondary[random.Next(4)];
            f.CdefUvPriStrength[i] = random.Next(16);
            f.CdefUvSecStrength[i] = secondary[random.Next(4)];
        }

        for (int i = 0; i < 3; i++)
        {
            f.LrType[i] = (Av1RestorationType)random.Next(4);
        }

        f.LrUnitShift = s.Use128x128Superblock ? random.Next(1, 3) : random.Next(3);
        f.LrUvShift = random.Next(2) == 0;

        // A tile layout the frame allows.
        bool sb128 = s.Use128x128Superblock;
        int miCols = 2 * ((width + 7) >> 3), miRows = 2 * ((height + 7) >> 3);
        int sbCols = sb128 ? (miCols + 31) >> 5 : (miCols + 15) >> 4, sbRows = sb128 ? (miRows + 31) >> 5 : (miRows + 15) >> 4;
        int sbSize = (sb128 ? 5 : 4) + 2;
        int minLog2Cols = Av1FrameHeader.TileLog2(4096 >> sbSize, sbCols);
        int maxLog2Cols = Av1FrameHeader.TileLog2(1, Math.Min(sbCols, 64));
        int maxLog2Rows = Av1FrameHeader.TileLog2(1, Math.Min(sbRows, 64));
        int minLog2Tiles = Math.Max(minLog2Cols, Av1FrameHeader.TileLog2((4096 * 2304) >> (2 * sbSize), sbRows * sbCols));
        f.UniformTileSpacing = random.Next(3) != 0;
        if (f.UniformTileSpacing)
        {
            f.TileColsLog2 = random.Next(minLog2Cols, maxLog2Cols + 1);
            f.TileRowsLog2 = random.Next(Math.Max(minLog2Tiles - f.TileColsLog2, 0), maxLog2Rows + 1);
        }
        else
        {
            List<int> cols = [];
            for (int start = 0; start < sbCols;)
            {
                int w = random.Next(1, Math.Min(sbCols - start, 4096 >> sbSize) + 1);
                cols.Add(w);
                start += w;
            }

            int maxArea = minLog2Tiles > 0 ? (sbRows * sbCols) >> (minLog2Tiles + 1) : sbRows * sbCols;
            int maxHeight = Math.Max(maxArea / cols.Max(), 1);
            List<int> rows = [];
            for (int start = 0; start < sbRows;)
            {
                int h = random.Next(1, Math.Min(sbRows - start, maxHeight) + 1);
                rows.Add(h);
                start += h;
            }

            f.TileColWidthsSb = [.. cols];
            f.TileRowHeightsSb = [.. rows];
        }

        int tileBits = f.UniformTileSpacing ? f.TileColsLog2 + f.TileRowsLog2
            : Av1FrameHeader.TileLog2(1, f.TileColWidthsSb.Length) + Av1FrameHeader.TileLog2(1, f.TileRowHeightsSb.Length);
        int tiles = f.UniformTileSpacing ? -1 : f.TileColWidthsSb.Length * f.TileRowHeightsSb.Length;
        f.ContextUpdateTileId = tileBits == 0 ? 0 : random.Next(tiles < 0 ? 1 << tileBits : Math.Min(tiles, 1 << tileBits));
        f.TileSizeBytes = random.Next(1, 5);
        return f;
    }

    private static void Check(Av1SequenceHeader s, Av1FrameParameters f, Av1FrameHeader h)
    {
        Assert.Equal(Av1FrameType.Key, h.FrameType);
        Assert.True(h.ShowFrame);
        Assert.Equal(f.Width, h.UpscaledWidth);
        Assert.Equal(f.Height, h.FrameHeight);
        Assert.Equal(f.DisableCdfUpdate, h.DisableCdfUpdate);
        Assert.Equal(f.AllowScreenContentTools, h.AllowScreenContentTools);
        Assert.Equal(f.AllowIntrabc, h.AllowIntrabc);
        Assert.Equal(s.ReducedStillPictureHeader || f.DisableCdfUpdate || f.DisableFrameEndUpdateCdf, h.DisableFrameEndUpdateCdf);
        Assert.Equal(f.UniformTileSpacing, h.UniformTileSpacing);
        if (f.UniformTileSpacing)
        {
            Assert.Equal(f.TileColsLog2, h.TileColsLog2);
            Assert.Equal(f.TileRowsLog2, h.TileRowsLog2);
        }
        else
        {
            Assert.Equal(f.TileColWidthsSb.Length, h.TileCols);
            Assert.Equal(f.TileRowHeightsSb.Length, h.TileRows);
        }

        if (h.TileColsLog2 + h.TileRowsLog2 > 0)
        {
            Assert.Equal(f.ContextUpdateTileId, h.ContextUpdateTileId);
            Assert.Equal(f.TileSizeBytes, h.TileSizeBytes);
        }

        Assert.Equal(f.BaseQIdx, h.BaseQIdx);
        Assert.Equal(f.DeltaQYDc, h.DeltaQYDc);
        if (!s.Monochrome)
        {
            Assert.Equal((f.DeltaQUDc, f.DeltaQUAc, f.DeltaQVDc, f.DeltaQVAc), (h.DeltaQUDc, h.DeltaQUAc, h.DeltaQVDc, h.DeltaQVAc));
        }

        Assert.Equal(f.UsingQmatrix, h.UsingQmatrix);
        if (f.UsingQmatrix)
        {
            Assert.Equal((f.QmY, f.QmU, f.QmV), (h.QmY, h.QmU, h.QmV));
        }

        Assert.Equal(f.SegmentationEnabled, h.SegmentationEnabled);
        for (int i = 0; f.SegmentationEnabled && i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                Assert.Equal(f.FeatureEnabled[i, j], h.FeatureEnabled[i, j]);
                Assert.Equal(f.FeatureEnabled[i, j] ? f.FeatureData[i, j] : 0, h.FeatureData[i, j]);
            }
        }

        bool deltaQ = f.BaseQIdx > 0 && f.DeltaQPresent;
        Assert.Equal(deltaQ, h.DeltaQPresent);
        if (deltaQ)
        {
            Assert.Equal(f.DeltaQRes, h.DeltaQRes);
            bool deltaLf = !f.AllowIntrabc && f.DeltaLfPresent;
            Assert.Equal(deltaLf, h.DeltaLfPresent);
            if (deltaLf)
            {
                Assert.Equal((f.DeltaLfRes, f.DeltaLfMulti), (h.DeltaLfRes, h.DeltaLfMulti));
            }
        }

        Assert.Equal(f.CodedLossless(s.Monochrome), h.CodedLossless);
        bool filtered = !h.CodedLossless && !f.AllowIntrabc;
        if (filtered)
        {
            Assert.Equal(f.LoopFilterLevel[0], h.LoopFilterLevel[0]);
            Assert.Equal(f.LoopFilterLevel[1], h.LoopFilterLevel[1]);
            if (!s.Monochrome && (f.LoopFilterLevel[0] != 0 || f.LoopFilterLevel[1] != 0))
            {
                Assert.Equal(f.LoopFilterLevel[2], h.LoopFilterLevel[2]);
                Assert.Equal(f.LoopFilterLevel[3], h.LoopFilterLevel[3]);
            }

            Assert.Equal(f.LoopFilterSharpness, h.LoopFilterSharpness);
            Assert.Equal(f.LoopFilterDeltaEnabled, h.LoopFilterDeltaEnabled);
            if (f.LoopFilterDeltaEnabled && f.LoopFilterRefDeltas is int[] refs)
            {
                Assert.Equal(refs, h.LoopFilterRefDeltas);
                Assert.Equal(f.LoopFilterModeDeltas, h.LoopFilterModeDeltas);
            }

            if (s.EnableCdef)
            {
                Assert.Equal(f.CdefDamping, h.CdefDamping);
                Assert.Equal(f.CdefBits, h.CdefBits);
                for (int i = 0; i < 1 << f.CdefBits; i++)
                {
                    Assert.Equal((f.CdefYPriStrength[i], f.CdefYSecStrength[i]), (h.CdefYPriStrength[i], h.CdefYSecStrength[i]));
                    if (!s.Monochrome)
                    {
                        Assert.Equal((f.CdefUvPriStrength[i], f.CdefUvSecStrength[i]), (h.CdefUvPriStrength[i], h.CdefUvSecStrength[i]));
                    }
                }
            }
        }

        if (!h.AllLossless && !f.AllowIntrabc && s.EnableRestoration)
        {
            int planes = s.Monochrome ? 1 : 3;
            for (int i = 0; i < planes; i++)
            {
                Assert.Equal(f.LrType[i], h.LrType[i]);
            }

            if (h.UsesLr)
            {
                Assert.Equal(64 << f.LrUnitShift, h.LoopRestorationSize[0]);
                bool chroma = planes > 1 && (f.LrType[1] != Av1RestorationType.None || f.LrType[2] != Av1RestorationType.None);
                bool halved = chroma && s.SubsamplingX == 1 && s.SubsamplingY == 1 && f.LrUvShift;
                Assert.Equal((64 << f.LrUnitShift) >> (halved ? 1 : 0), h.LoopRestorationSize[1]);
            }
        }

        Assert.Equal(h.CodedLossless ? Av1TxMode.Only4x4 : f.TxModeSelect ? Av1TxMode.Select : Av1TxMode.Largest, h.TxMode);
        Assert.Equal(f.ReducedTxSet, h.ReducedTxSet);
    }

    [Fact]
    public void FrameObusSplitIntoTheirTiles()
    {
        Av1SequenceHeader s = Av1SequenceHeader.Parse(Av1ObuWriter.SequenceHeader(new Av1SequenceHeader
        {
            StillPicture = true,
            ReducedStillPictureHeader = true,
            OperatingPoints = [new Av1OperatingPoint(0, 8, 0)],
            FrameWidthBits = 11,
            FrameHeightBits = 11,
            MaxFrameWidth = 2000,
            MaxFrameHeight = 300,
            BitDepth = 8,
            SubsamplingX = 1,
            SubsamplingY = 1,
            ForceScreenContentTools = 2,
            ForceIntegerMv = 2,
        }), "AV1");
        Av1FrameParameters f = new() { Width = 2000, Height = 300, BaseQIdx = 100, TileColsLog2 = 2, TileRowsLog2 = 1 };
        byte[][] tiles = [.. Enumerable.Range(0, 8).Select(i => Enumerable.Range(0, 10 + (i * 70)).Select(b => (byte)(b + i)).ToArray())];
        f.TileSizeBytes = Av1ObuWriter.TileSizeBytesFor(tiles);
        Assert.Equal(2, f.TileSizeBytes);
        byte[] payload = Av1ObuWriter.Frame(s, f, tiles);
        List<byte> stream = [];
        Av1ObuWriter.Obu(stream, Av1ObuType.SequenceHeader, Av1ObuWriter.SequenceHeader(s));
        Av1ObuWriter.Obu(stream, Av1ObuType.Frame, payload);
        byte[] data = [.. stream];

        List<Av1Obu> obus = Av1Obu.Split(data, "AV1");
        Assert.Equal([Av1ObuType.SequenceHeader, Av1ObuType.Frame], obus.Select(o => o.Type));
        Av1FrameHeader h = Av1FrameHeader.Parse(data.AsSpan(obus[1].PayloadStart, obus[1].PayloadLength), s, 0, 0, "AV1");
        Assert.Equal(8, h.TileCols * h.TileRows);
        Av1Frame frame = new(h, "AV1", reconstruct: false);
        int skip = (h.BitLength + 7) >> 3;
        List<(int Tile, int Offset, int Size)> found = frame.TileGroupTiles(data, obus[1].PayloadStart + skip, obus[1].PayloadLength - skip);
        Assert.Equal(8, found.Count);
        for (int i = 0; i < 8; i++)
        {
            Assert.Equal(i, found[i].Tile);
            Assert.Equal(tiles[i], data.AsSpan(found[i].Offset, found[i].Size).ToArray());
        }
    }
}
