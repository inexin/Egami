using System.Buffers.Binary;

using Egami.Formats.Extended.Raw;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Raw;

/// <summary>
/// RAW coverage Build Plan tasks 8 to 10: CR3 files with CRX sensor data, built in code by a small encoder written from the CRX
/// spec (sections 3 to 10). The encoder mirrors the decoder's state, so these tests pin the container, the headers, the line
/// modes, runs, the wavelet and the levels; agreement with Canon's own data is the egami-compare gate's job (bit-exact against
/// Adobe DNG Converter).
/// </summary>
public class RawCrxTests
{
    private static readonly RawReaderOptions Mosaic = new() { Output = RawOutput.Mosaic };

    private static readonly string[] Patterns = ["RGGB", "GRBG", "GBRG", "BGGR"];

    private static EgamiContext Context => TestImages.Context;

    [Theory]
    [InlineData(0, 1, true)]
    [InlineData(1, 1, true)]
    [InlineData(2, 2, true)]
    [InlineData(3, 2, true)]
    [InlineData(0, 2, false)]
    [InlineData(1, 1, false)]
    public void LosslessDecodesWhatWasCoded(int layout, int tilesAcross, bool partial)
    {
        Spec spec = new() { CfaLayout = layout, TilesAcross = tilesAcross, Partial = partial };
        ushort[] expected = Image(spec, 1);
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(Build(spec, expected), Context, Mosaic);
        AssertEqual(spec, expected, mosaic);
        RawDetails details = Assert.IsType<RawDetails>(mosaic.Metadata.FormatDetails);
        Assert.Equal(Patterns[layout], details.CfaPattern);
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(2, false, true)]
    [InlineData(3, false, false)]
    [InlineData(3, true, false)]
    [InlineData(3, false, true)]
    public void CRawDecodesWhatWasCoded(int levels, bool version2, bool perLineQ)
    {
        Spec spec = new() { Levels = levels, Version2 = version2, PerLineQ = perLineQ, Width = 120, Height = 100 };
        ushort[] expected = Image(spec, 2);
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(Build(spec, expected), Context, Mosaic);
        AssertEqual(spec, expected, mosaic);
    }

    [Fact]
    public void FlatImagesCodeAsRuns()
    {
        Spec spec = new() { TilesAcross = 2 };
        ushort[] expected = new ushort[spec.Width * spec.Height];
        expected.AsSpan().Fill(8192);
        expected[1234] = 9000;
        byte[] file = Build(spec, expected);
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, Mosaic);
        AssertEqual(spec, expected, mosaic);
        int mdat = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(file.AsSpan().IndexOf("mdat"u8) - 4));
        Assert.True(mdat < 1024, $"a flat image codes in {mdat} bytes");
    }

    [Fact]
    public void RollFramesTransformTheirPlanes()
    {
        // Encoding type 3 (CRX spec, section 10.2): the planes hold a luma-like base and differences; the decoder transforms them.
        Spec spec = new() { EncType = 3, MedianBits = 14 };
        int pw = spec.Width / 2, ph = spec.Height / 2;
        int[][] planes = [new int[pw * ph], new int[pw * ph], new int[pw * ph], new int[pw * ph]];
        Random random = new(5);
        for (int i = 0; i < pw * ph; i++)
        {
            planes[0][i] = random.Next(-2000, 2000);
            planes[1][i] = random.Next(-300, 300);
            planes[2][i] = random.Next(-100, 100);
            planes[3][i] = random.Next(-300, 300);
        }

        using Raster<Gray16> mosaic = Raster.Load<Gray16>(Build(spec, null, planes), Context, Mosaic);
        int m = 1 << 13;
        for (int y = 0; y < ph; y++)
        {
            for (int x = 0; x < pw; x++)
            {
                int i = (y * pw) + x;
                long b0 = ((long)m * 1024) + (planes[0][i] * 1024L);
                long g = b0 - (168L * planes[1][i]) - (585L * planes[3][i]);
                long gr = Math.Sign(g) * (((Math.Abs(g) + 512) >> 9) & ~1L);
                long[] rgb = [(b0 + (1510L * planes[3][i]) + 512) >> 10, (planes[2][i] + gr + 1) >> 1, (gr - planes[2][i] + 1) >> 1, (b0 + (1927L * planes[1][i]) + 512) >> 10];
                for (int p = 0; p < 4; p++)
                {
                    Assert.Equal((ushort)Math.Clamp(rgb[p], 0, 16383), mosaic.Row((2 * y) + (p >> 1))[(2 * x) + (p & 1)].L);
                }
            }
        }
    }

    [Fact]
    public void ReadsTheAreasLevelsAndWhiteBalance()
    {
        Spec spec = new();
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(Build(spec, Image(spec, 3)), Context, Mosaic);
        RawDetails details = Assert.IsType<RawDetails>(mosaic.Metadata.FormatDetails);
        Assert.Equal(new Rect(8, 4, 88, 60), details.ActiveArea);
        Assert.Equal(new[] { 510.0, 511, 512, 513 }, details.BlackLevels);
        Assert.Equal(15000.0, Assert.Single(details.WhiteLevels));
        Assert.Equal(1024 / 2048.0, details.AsShotNeutral[0], 6);
        Assert.Equal(1024 / 1536.0, details.AsShotNeutral[2], 6);
    }

    [Fact]
    public void AnOlderBodysOwnWhiteRaisesTheBaselineExposure()
    {
        // The table's M50 row is from a lossless file (white 11892, baseline exposure 0.02); Adobe gives a C-RAW file of white
        // 14338 a baseline exposure 0.27 EV higher, their ratio.
        Spec spec = new() { Model = "Canon EOS M50", White = 14338 };
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(Build(spec, Image(spec, 3)), Context, Mosaic);
        RawDetails details = Assert.IsType<RawDetails>(mosaic.Metadata.FormatDetails);
        Assert.Equal(14338.0, Assert.Single(details.WhiteLevels));
        Assert.Equal(0.02 + Math.Log2(14338 / 11892.0), details.BaselineExposure, 3);
    }

    [Fact]
    public void AWhiteAboveTheSampleBitsWidensTheDngsSamples()
    {
        // Adobe gives some CR3 bodies a white of 16384 over 14-bit data; WhiteLevel may not pass what BitsPerSample holds.
        Spec spec = new() { White = 16384 };
        byte[] file = Build(spec, Image(spec, 4));
        using MemoryStream dng = new();
        RawFormat.ConvertToDng(new MemoryStream(file), dng, null, Context);
        using Raster<Gray16> fromDng = Raster.Load<Gray16>(dng.ToArray(), Context, Mosaic);
        RawDetails details = Assert.IsType<RawDetails>(fromDng.Metadata.FormatDetails);
        Assert.Equal(16384.0, Assert.Single(details.WhiteLevels));
        Assert.True(details.BitsPerSample >= 15, $"{details.BitsPerSample} bits");
    }

    [Fact]
    public void DevelopsAndConvertsToDng()
    {
        Spec spec = new();
        byte[] file = Build(spec, Image(spec, 4));
        using (Raster<Rgb16> developed = Raster.Load<Rgb16>(file, Context))
        {
            Assert.True(developed.Width > 0 && developed.Height > 0);
        }

        using MemoryStream dng = new();
        RawFormat.ConvertToDng(new MemoryStream(file), dng, null, Context);
        using Raster<Gray16> fromDng = Raster.Load<Gray16>(dng.ToArray(), Context, Mosaic);
        using Raster<Gray16> fromCr3 = Raster.Load<Gray16>(file, Context, Mosaic);
        Assert.Equal((fromCr3.Width, fromCr3.Height), (fromDng.Width, fromDng.Height));
        for (int y = 0; y < fromCr3.Height; y++)
        {
            Assert.True(fromCr3.Row(y).SequenceEqual(fromDng.Row(y)), $"row {y}");
        }
    }

    [Theory]
    [InlineData(1, 4)]
    [InlineData(0, 1)]
    public void UnknownCodingsAreNotSupported(int encType, int planes)
    {
        Spec spec = new();
        byte[] file = Build(spec, Image(spec, 1));
        int cmp1 = file.AsSpan().IndexOf("CMP1"u8) + 4;
        file[cmp1 + 25] = (byte)((planes << 4) | (file[cmp1 + 25] & 15));
        file[cmp1 + 26] = (byte)((encType << 4) | (file[cmp1 + 26] & 15));
        if (planes == 1)
        {
            file[cmp1 + 24] = 8;
        }

        Assert.Throws<NotSupportedException>(() => Raster.Load<Gray16>(file, Context, Mosaic).Dispose());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DamagedFilesFailCleanly(bool crawFile)
    {
        Spec spec = crawFile ? new() { Levels = 3, Width = 120, Height = 100 } : new() { TilesAcross = 2 };
        byte[] good = Build(spec, Image(spec, 6));
        int data = good.AsSpan().IndexOf("mdat"u8) + 4;
        Random random = new(7);
        for (int trial = 0; trial < 300; trial++)
        {
            byte[] file = (byte[])good.Clone();
            if (trial % 3 == 0)
            {
                file = file[..random.Next(data, file.Length)];
            }
            else
            {
                for (int i = 0; i < 1 + (trial % 5); i++)
                {
                    file[random.Next(data, file.Length)] ^= (byte)(1 << random.Next(8));
                }
            }

            foreach (RecoveryMode recovery in new[] { RecoveryMode.Strict, RecoveryMode.Lenient })
            {
                Exception? error = Record.Exception(() => Raster.Load<Gray16>(file, Context, Mosaic with { Recovery = recovery }).Dispose());
                Assert.True(error is null or EgamiException, $"trial {trial}: {error}");
            }
        }
    }

    [Fact]
    [Trait("Category", "Allocation")]
    public void TilesAllocateNothingAfterSetup()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        EgamiContext one = Context with { MaxParallelism = 1 };
        long Measure(Spec spec)
        {
            byte[] file = Build(spec, Image(spec, 8));
            long fewest = long.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Raster.Load<Gray16>(file, one, Mosaic).Dispose();
                fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return fewest;
        }

        // Eight tiles against two of the same frame: the difference is the setup's tile descriptors only.
        long two = Measure(new Spec { Width = 352, Height = 88, TilesAcross = 2 });
        long eight = Measure(new Spec { Width = 352, Height = 88, TilesAcross = 8 });
        Assert.True(eight - two < 8 * 1024, $"two tiles {two} B, eight {eight} B");
        long crawTwo = Measure(new Spec { Width = 352, Height = 100, TilesAcross = 1, Levels = 3 });
        long crawAgain = Measure(new Spec { Width = 352, Height = 100, TilesAcross = 1, Levels = 3 });
        Assert.True(Math.Abs(crawAgain - crawTwo) < 4096, $"{crawTwo} B, then {crawAgain} B");
    }

    /// <summary>Writes the raw fuzz target's CRX seeds (tests/images/fuzz/raw-seeds) when EGAMI_WRITE_SEEDS names a folder.</summary>
    [Fact]
    public void WritesTheFuzzSeedsOnRequest()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAMI_WRITE_SEEDS");
        Assert.SkipUnless(folder is { Length: > 0 }, "Set EGAMI_WRITE_SEEDS to a folder to write the CRX fuzz seeds.");
        (string Name, Spec Spec)[] seeds =
        [
            ("crx-lossless", new Spec { TilesAcross = 2, CfaLayout = 1 }),
            ("crx-craw-v1", new Spec { Levels = 3, PerLineQ = true, Width = 120, Height = 100 }),
            ("crx-craw-v2", new Spec { Levels = 2, Version2 = true, Width = 120, Height = 100 }),
        ];
        foreach ((string name, Spec spec) in seeds)
        {
            File.WriteAllBytes(Path.Combine(folder!, name + ".cr3"), Build(spec, Image(spec, 9)));
        }

        Spec roll = new() { EncType = 3 };
        int[][] planes = [.. Enumerable.Range(0, 4).Select(p => Enumerable.Range(0, roll.Width * roll.Height / 4).Select(i => ((i * 7) % 61) - 30).ToArray())];
        File.WriteAllBytes(Path.Combine(folder!, "crx-roll.cr3"), Build(roll, null, planes));
    }

    private static void AssertEqual(Spec spec, ushort[] expected, Raster<Gray16> mosaic)
    {
        Assert.Equal((spec.Width, spec.Height), (mosaic.Width, mosaic.Height));
        for (int y = 0; y < spec.Height; y++)
        {
            Span<Gray16> row = mosaic.Row(y);
            for (int x = 0; x < spec.Width; x++)
            {
                Assert.True(expected[(y * spec.Width) + x] == row[x].L, $"({x}, {y}): {expected[(y * spec.Width) + x]} coded, {row[x].L} decoded");
            }
        }
    }

    /// <summary>A test image: smooth gradients, noise, flat patches and saturated spots, in 14 bits.</summary>
    private static ushort[] Image(Spec spec, int seed)
    {
        Random random = new(seed);
        ushort[] image = new ushort[spec.Width * spec.Height];
        for (int y = 0; y < spec.Height; y++)
        {
            for (int x = 0; x < spec.Width; x++)
            {
                int v = 2000 + (x * 40) + (y * 25) + random.Next(-30, 30);
                if (x % 37 < 9 && y % 23 < 7)
                {
                    v = 8192;
                }

                if (random.Next(500) == 0)
                {
                    v = random.Next(2) == 0 ? 0 : 16383;
                }

                image[(y * spec.Width) + x] = (ushort)Math.Clamp(v, 0, 16383);
            }
        }

        return image;
    }

    private sealed record Spec
    {
        public int Width { get; init; } = 96;

        public int Height { get; init; } = 64;

        public int TilesAcross { get; init; } = 1;

        public int CfaLayout { get; init; }

        public int Levels { get; init; }

        public bool Partial { get; init; } = true;

        public bool Version2 { get; init; }

        public bool PerLineQ { get; init; }

        public int EncType { get; init; }

        public int MedianBits { get; init; } = 14;

        public string Model { get; init; } = "Canon EOS CRX TEST";

        public int White { get; init; } = 15000;
    }

    /// <summary>
    /// Builds a CR3: ftyp, moov with the Canon box (CMT1 and a CMT3 maker note with ColorData) and a CRAW raw track (CMP1, CDI1),
    /// and mdat with the CRX sample, coded from <paramref name="mosaic"/> (or, for a roll frame, from the planes as given).
    /// </summary>
    private static byte[] Build(Spec spec, ushort[]? mosaic, int[][]? givenPlanes = null)
    {
        int pw = spec.Width / 2, ph = spec.Height / 2;
        int tileW = pw / spec.TilesAcross;
        int[] tileX = [.. Enumerable.Range(0, spec.TilesAcross).Select(t => t * tileW)];
        int[] tileWidth = [.. Enumerable.Range(0, spec.TilesAcross).Select(t => t == spec.TilesAcross - 1 ? pw - (t * tileW) : tileW)];
        int[][] planes = givenPlanes ?? new int[4][];
        if (givenPlanes is null)
        {
            for (int p = 0; p < 4; p++)
            {
                int cell = p ^ spec.CfaLayout;
                planes[p] = new int[pw * ph];
                for (int y = 0; y < ph; y++)
                {
                    for (int x = 0; x < pw; x++)
                    {
                        planes[p][(y * pw) + x] = mosaic![(((2 * y) + (cell >> 1)) * spec.Width) + (2 * x) + (cell & 1)] - 8192;
                    }
                }
            }
        }

        int subbands = (3 * spec.Levels) + 1;
        List<byte> header = [];
        List<byte> data = [];
        for (int t = 0; t < spec.TilesAcross; t++)
        {
            List<byte[]> planeData = [];
            List<int[]> bandSizes = [];
            for (int p = 0; p < 4; p++)
            {
                int[] values = new int[tileWidth[t] * ph];
                for (int y = 0; y < ph; y++)
                {
                    Array.Copy(planes[p], (y * pw) + tileX[t], values, y * tileWidth[t], tileWidth[t]);
                }

                List<(int[] Values, int Width, int Height)> bands = spec.Levels == 0 ? [(values, tileWidth[t], ph)] : Analyse(values, tileWidth[t], ph, spec.Levels);
                List<byte> plane = [];
                int[] sizes = new int[subbands];
                for (int s = 0; s < subbands; s++)
                {
                    bool predicted = s == 0 && spec.Partial;
                    byte[] coded = Encode(bands[s].Values, bands[s].Width, bands[s].Height, predicted, spec.PerLineQ && !spec.Version2 && spec.Levels > 0);
                    sizes[s] = coded.Length;
                    plane.AddRange(coded);
                }

                planeData.Add([.. plane]);
                bandSizes.Add(sizes);
            }

            byte[] qp = spec.Version2 && spec.Levels > 0 ? QuantiserGrid(tileWidth[t], ph) : [];
            int tileSize = qp.Length + planeData.Sum(d => d.Length);
            header.AddRange(Be16(spec.Version2 ? 0xFF11 : 0xFF01));
            header.AddRange(Be16(qp.Length > 0 ? 16 : 8));
            header.AddRange(Be32(tileSize));
            header.AddRange(Be16(t));
            header.AddRange(Be16(qp.Length > 0 ? 0x4000 : 0));
            if (qp.Length > 0)
            {
                header.AddRange([.. Be32(qp.Length), .. Be16(0), .. Be16(0)]);
            }

            for (int p = 0; p < 4; p++)
            {
                header.AddRange([.. Be16(spec.Version2 ? 0xFF12 : 0xFF02), .. Be16(8), .. Be32(planeData[p].Length)]);
                header.AddRange([(byte)((p << 4) | (spec.Partial ? 8 : 0)), 0, 0, 0]);
                for (int s = 0; s < subbands; s++)
                {
                    if (spec.Version2)
                    {
                        // qStepMult 0 and qStepBase 1: every step is 1.
                        header.AddRange([.. Be16(0xFF13), .. Be16(16), .. Be32(bandSizes[p][s]), .. Be16(s << 12), .. Be16(0), .. Be32(1), .. Be16(0), .. Be16(0)]);
                    }
                    else
                    {
                        // q = 4: a step of 1 (CRX spec, section 8.1).
                        uint field = ((uint)s << 28) | (spec.PerLineQ ? 1u << 27 : 0) | (4u << 19);
                        header.AddRange([.. Be16(0xFF03), .. Be16(8), .. Be32(bandSizes[p][s]), .. Be32((int)field)]);
                    }
                }
            }

            data.AddRange(qp);
            foreach (byte[] d in planeData)
            {
                data.AddRange(d);
            }
        }

        byte[] sample = [.. header, .. data];
        byte[] cmp1 = new byte[85];
        BinaryPrimitives.WriteUInt16BigEndian(cmp1, 0xFFFF);
        BinaryPrimitives.WriteUInt16BigEndian(cmp1.AsSpan(2), 0x30);
        BinaryPrimitives.WriteUInt16BigEndian(cmp1.AsSpan(4), (ushort)(spec.Version2 ? 0x200 : 0x100));
        BinaryPrimitives.WriteUInt32BigEndian(cmp1.AsSpan(8), (uint)spec.Width);
        BinaryPrimitives.WriteUInt32BigEndian(cmp1.AsSpan(12), (uint)spec.Height);
        BinaryPrimitives.WriteUInt32BigEndian(cmp1.AsSpan(16), (uint)(2 * tileW));
        BinaryPrimitives.WriteUInt32BigEndian(cmp1.AsSpan(20), (uint)spec.Height);
        cmp1[24] = 14;
        cmp1[25] = (byte)(0x40 | spec.CfaLayout);
        cmp1[26] = (byte)((spec.EncType << 4) | spec.Levels);
        cmp1[27] = (byte)(spec.TilesAcross > 1 ? 0x80 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(cmp1.AsSpan(28), (uint)header.Count);
        if (spec.EncType == 3)
        {
            cmp1[32] = 0x80;
            cmp1[56] = 0x40;
            cmp1[84] = (byte)spec.MedianBits;
        }

        // IAD1: the crop, the left and top optical black, the active area; inclusive left, top, right and bottom.
        byte[] iad1 = new byte[48];
        BinaryPrimitives.WriteUInt16BigEndian(iad1.AsSpan(4), (ushort)spec.Width);
        BinaryPrimitives.WriteUInt16BigEndian(iad1.AsSpan(6), (ushort)spec.Height);
        ushort[] areas = [10, 6, 93, 61, 0, 0, 7, 63, 8, 0, 95, 3, 8, 4, 95, 63];
        for (int i = 0; i < 16; i++)
        {
            BinaryPrimitives.WriteUInt16BigEndian(iad1.AsSpan(16 + (2 * i)), areas[i]);
        }

        byte[] cdi1 = Box("CDI1", new byte[4], Box("IAD1", iad1));
        ushort[] color = new ushort[1816];
        color[0x47] = 2048;
        color[0x48] = color[0x49] = 1024;
        color[0x4A] = 1536;
        (color[0x318], color[0x319], color[0x31A], color[0x31B]) = (510, 511, 512, 513);
        (color[0x31C], color[0x31D]) = (14000, (ushort)spec.White);
        TiffBuilder.Ifd ifd0 = new();
        ifd0.Tags.AddRange([TiffTag.Ascii(271, "Canon"), TiffTag.Ascii(272, spec.Model), TiffTag.Short(274, 1)]);
        TiffBuilder.Ifd note = new();
        note.Tags.Add(TiffTag.Short(0x4001, [.. color.Select(c => (int)c)]));
        byte[] Build(long rawAt)
        {
            byte[] canon = Box("uuid", Convert.FromHexString("85c0b687820f11e08111f4ce462b6a48"), Box("CMT1", TiffBuilder.Write(ifd0)), Box("CMT3", TiffBuilder.Write(note)));
            byte[] entry = new byte[82];
            BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(6), 1);
            BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(24), (ushort)spec.Width);
            BinaryPrimitives.WriteUInt16BigEndian(entry.AsSpan(26), (ushort)spec.Height);
            byte[] stsd = Box("stsd", Be32(0), Be32(1), Box("CRAW", entry, Box("CMP1", cmp1), cdi1));
            byte[] stbl = Box("stbl", stsd, Box("stsz", Be32(0), Be32(0), Be32(1), Be32(sample.Length)), Box("co64", Be32(0), Be32(1), Be64(rawAt)));
            byte[] moov = Box("moov", canon, Box("trak", Box("mdia", Box("minf", stbl))));
            return [.. Box("ftyp", "crx "u8.ToArray(), Be32(1), "crx isom"u8.ToArray()), .. moov, .. Box("mdat", sample)];
        }

        byte[] first = Build(0);
        return Build(first.Length - sample.Length);
    }

    /// <summary>The forward reversible 5/3 transform (T.800 Annex F), vertical then horizontal, the inverse of the decoder's order.</summary>
    private static List<(int[] Values, int Width, int Height)> Analyse(int[] values, int width, int height, int levels)
    {
        // Decode level d's bands come out finest last, so the list is filled LL first, then levels from the coarsest.
        List<(int[] Values, int Width, int Height)>[] perLevel = new List<(int[], int, int)>[levels];
        int[] current = values;
        int w = width, h = height;
        for (int l = 1; l <= levels; l++)
        {
            int loH = (h + 1) / 2, hiH = h / 2, loW = (w + 1) / 2, hiW = w / 2;
            int[] low = new int[w * loH], high = new int[w * hiH];
            int[] column = new int[h], lo = new int[loH], hi = new int[hiH];
            for (int x = 0; x < w; x++)
            {
                for (int y = 0; y < h; y++)
                {
                    column[y] = current[(y * w) + x];
                }

                Split(column, lo, hi);
                for (int y = 0; y < loH; y++)
                {
                    low[(y * w) + x] = lo[y];
                }

                for (int y = 0; y < hiH; y++)
                {
                    high[(y * w) + x] = hi[y];
                }
            }

            int[] ll = new int[loW * loH], hl = new int[hiW * loH], lh = new int[loW * hiH], hh = new int[hiW * hiH];
            int[] rowLo = new int[loW], rowHi = new int[hiW];
            for (int y = 0; y < loH; y++)
            {
                Split(low.AsSpan(y * w, w), rowLo, rowHi);
                rowLo.CopyTo(ll, y * loW);
                rowHi.CopyTo(hl, y * hiW);
            }

            for (int y = 0; y < hiH; y++)
            {
                Split(high.AsSpan(y * w, w), rowLo, rowHi);
                rowLo.CopyTo(lh, y * loW);
                rowHi.CopyTo(hh, y * hiW);
            }

            perLevel[l - 1] = [(hl, hiW, loH), (lh, loW, hiH), (hh, hiW, hiH)];
            current = ll;
            (w, h) = (loW, loH);
        }

        List<(int[] Values, int Width, int Height)> bands = [(current, w, h)];
        for (int l = levels; l >= 1; l--)
        {
            bands.AddRange(perLevel[l - 1]);
        }

        return bands;
    }

    /// <summary>One-dimensional 5/3 analysis with whole-sample symmetric extension.</summary>
    private static void Split(ReadOnlySpan<int> x, Span<int> lo, Span<int> hi)
    {
        int n = x.Length;
        if (n == 1)
        {
            lo[0] = x[0];
            return;
        }

        for (int i = 0; i < n / 2; i++)
        {
            int next = (2 * i) + 2 < n ? x[(2 * i) + 2] : x[2 * i];
            hi[i] = x[(2 * i) + 1] - ((x[2 * i] + next) >> 1);
        }

        for (int i = 0; i < (n + 1) / 2; i++)
        {
            int before = i > 0 ? hi[i - 1] : hi[0];
            int here = i < n / 2 ? hi[i] : hi[(n / 2) - 1];
            lo[i] = x[2 * i] + ((before + here + 2) >> 2);
        }
    }

    /// <summary>A version 2 tile's quantiser grid, all zero: one code of 0 (a lone one bit) per cell (CRX spec, section 8.2).</summary>
    private static byte[] QuantiserGrid(int tileWidth, int tileHeight)
    {
        Writer w = new();
        int cells = ((tileWidth + 7) / 8) * ((tileHeight + 1) / 2);
        for (int i = 0; i < cells; i++)
        {
            w.Bits(1, 1);
        }

        return w.ToArray();
    }

    /// <summary>Codes one band line by line, mirroring the decoder's modes (CRX spec, sections 5 to 7).</summary>
    private static byte[] Encode(int[] values, int width, int height, bool predicted, bool perLineQ)
    {
        Writer w = new();
        int k = 0, s = 0;
        int[] prev = new int[width + 2], cur = new int[width + 2], kk = new int[width + 1];
        for (int y = 0; y < height; y++)
        {
            if (perLineQ)
            {
                // A q change of 0 with k_q 0: one one bit.
                w.Bits(1, 1);
            }

            ReadOnlySpan<int> line = values.AsSpan(y * width, width);
            if (predicted)
            {
                Predicted(w, line, cur, prev, y == 0, ref k, ref s);
            }
            else
            {
                Coefficients(w, line, cur, prev, kk, y == 0, ref k, ref s);
            }

            cur[width + 1] = cur[width] + 1;
            (prev, cur) = (cur, prev);
        }

        return w.ToArray();
    }

    private static void Predicted(Writer w, ReadOnlySpan<int> line, int[] cur, int[] prev, bool first, ref int k, ref int s)
    {
        int width = line.Length;
        cur[0] = first ? 0 : prev[1];
        for (int x = 0; x < width; x++)
        {
            int a = cur[x], b = prev[x + 1], c = prev[x], d = prev[x + 2];
            int pred;
            bool ahead;
            if (first)
            {
                pred = a;
                if (x < width - 1 && a == 0)
                {
                    int n = Same(line, x, 0);
                    Run(w, n, width - x, ref s);
                    Fill(cur, x, n, 0);
                    x += n;
                    if (x == width)
                    {
                        break;
                    }

                    pred = 0;
                }

                ahead = false;
            }
            else if (x == width - 1 || a != b || a != d)
            {
                pred = Predict(a, b, c);
                ahead = x < width - 1;
            }
            else
            {
                int n = Same(line, x, a);
                Run(w, n, width - x, ref s);
                Fill(cur, x, n, a);
                x += n;
                if (x == width)
                {
                    break;
                }

                pred = prev[x + 1];
                b = prev[x + 1];
                d = prev[x + 2];
                ahead = x < width - 1;
            }

            uint v = Zig(line[x] - pred);
            Code(w, v, k);
            k = ahead ? Adapt(k, (v + (2 * (uint)Math.Abs(d - b))) >> 1, 15) : Adapt(k, v, 15);
            cur[x + 1] = line[x];
        }
    }

    private static void Coefficients(Writer w, ReadOnlySpan<int> line, int[] cur, int[] prev, int[] kk, bool first, ref int k, ref int s)
    {
        int width = line.Length;
        cur[0] = 0;
        for (int x = 0; x < width; x++)
        {
            bool last = x == width - 1;
            bool coded = first ? last || cur[x] != 0 : last || prev[x + 1] != 0 || prev[x + 2] != 0 || cur[x] != 0;
            uint v;
            if (coded)
            {
                v = Zig(line[x]);
                Code(w, v, k);
                k = first || last ? Adapt(k, v, 15) : Bump(Adapt(k, v, 0), kk[x + 1]);
            }
            else
            {
                int n = Same(line, x, 0);
                Run(w, n, width - x, ref s);
                Fill(cur, x, n, 0);
                Array.Clear(kk, x, n);
                x += n;
                if (x == width)
                {
                    break;
                }

                v = Zig(line[x]) - 1;
                Code(w, v, k);
                k = first || x == width - 1 ? Adapt(k, v, 15) : Bump(Adapt(k, v, 0), kk[x + 1]);
            }

            kk[x] = k;
            cur[x + 1] = line[x];
        }
    }

    private static int Same(ReadOnlySpan<int> line, int x, int value)
    {
        int n = 0;
        while (x + n < line.Length && line[x + n] == value)
        {
            n++;
        }

        return n;
    }

    private static void Fill(int[] cur, int x, int n, int value) => cur.AsSpan(x + 1, n).Fill(value);

    /// <summary>Codes a run of <paramref name="n"/> positions out of <paramref name="remaining"/> (CRX spec, section 5.3).</summary>
    private static void Run(Writer w, int n, int remaining, ref int s)
    {
        if (n == 0)
        {
            w.Bits(0, 1);
            return;
        }

        w.Bits(1, 1);
        int at = 1;
        while (at + (1 << Extra(s)) <= n)
        {
            w.Bits(1, 1);
            at += 1 << Extra(s);
            if (s < 31)
            {
                s++;
            }

            if (at == remaining)
            {
                return;
            }
        }

        w.Bits(0, 1);
        if (Extra(s) > 0)
        {
            w.Bits((uint)(n - at), Extra(s));
        }

        if (s > 0)
        {
            s--;
        }
    }

    private static int Extra(int s) => s < 16 ? s >> 2 : s < 24 ? 4 + ((s - 16) >> 1) : s - 16;

    private static void Code(Writer w, uint v, int k)
    {
        uint q = v >> k;
        if (q >= 41)
        {
            w.Bits(1, 42);
            w.Bits(v, 21);
            return;
        }

        w.Bits(1, (int)q + 1);
        if (k > 0)
        {
            w.Bits(v & ((1u << k) - 1), k);
        }
    }

    private static uint Zig(int u) => u >= 0 ? (uint)u * 2 : ((uint)(-u) * 2) - 1;

    private static int Predict(int a, int b, int c)
    {
        int g = b - c;
        bool s = g < 0;
        int i = ((c < a) ^ s ? 2 : 0) + ((a < b) ^ s ? 1 : 0);
        return i < 2 ? a + g : i == 2 ? a : b;
    }

    private static int Adapt(int k, uint v, int cap)
    {
        int next = k - (v < (uint)((1 << k) >> 1) ? 1 : 0) + ((v >> k) > 2 ? 1 : 0) + ((v >> k) > 5 ? 1 : 0);
        return cap > 0 && next > cap ? cap : next;
    }

    private static int Bump(int k, int up) => up - k > 1 ? k + 1 : Math.Min(k, 15);

    private static byte[] Box(string type, params byte[][] parts)
    {
        byte[] box = new byte[8 + parts.Sum(p => p.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        int at = 8;
        foreach (byte[] p in parts)
        {
            p.CopyTo(box, at);
            at += p.Length;
        }

        return box;
    }

    private static byte[] Be16(int v) => [(byte)(v >> 8), (byte)v];

    private static byte[] Be32(long v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static byte[] Be64(long v) => [.. Be32(v >> 32), .. Be32(v)];

    /// <summary>An MSB-first bit writer; the last byte is padded with zeros.</summary>
    private sealed class Writer
    {
        private readonly List<byte> _bytes = [];
        private int _acc;
        private int _count;

        /// <summary>Writes the low <paramref name="n"/> bits of <paramref name="value"/>; for n over 32, zeros then the value.</summary>
        public void Bits(uint value, int n)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                int bit = i < 32 ? (int)((value >> i) & 1) : 0;
                _acc = (_acc << 1) | bit;
                if (++_count == 8)
                {
                    _bytes.Add((byte)_acc);
                    _acc = 0;
                    _count = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            List<byte> bytes = [.. _bytes];
            if (_count > 0)
            {
                bytes.Add((byte)(_acc << (8 - _count)));
            }

            return [.. bytes];
        }
    }
}
