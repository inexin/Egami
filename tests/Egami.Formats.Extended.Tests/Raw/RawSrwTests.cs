using System.Buffers.Binary;
using System.Numerics;

using Egami.Formats.Extended.Raw;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Raw;

/// <summary>
/// RAW Build Plan tasks 13 and 14: Samsung SRW files built in code. The uncompressed layouts and the V0, V1 and V2 codecs
/// round-trip through test-only encoders written from the SRW specification's decoding rules, run backwards.
/// </summary>
public class RawSrwTests
{
    private const int Uncompressed = 32769, V0 = 32770, V1 = 32772, V2 = 32773;

    private static readonly RawReaderOptions Mosaic = new() { Output = RawOutput.Mosaic };

    private static EgamiContext Context => TestImages.Context;

    [Theory]
    [InlineData("EX1", 40, 6, 14, false)]
    [InlineData("NX10", 42, 5, 12, false)]
    [InlineData("NX1000", 5632, 2, 12, true)]
    public void UncompressedLayoutsDecode(string model, int width, int height, int bits, bool lsbFirst)
    {
        ushort[] image = Image(width, height, bits, 1);
        byte[] strip;
        int compression = V0;
        if (bits == 14)
        {
            // 16-bit words, little-endian in a big-endian file; a margin value past 14 bits is kept as Adobe keeps it.
            image[width - 1] = 65535;
            strip = new byte[2 * width * height];
            for (int i = 0; i < image.Length; i++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(strip.AsSpan(2 * i), image[i]);
            }

            compression = Uncompressed;
        }
        else
        {
            strip = new byte[width * height * 3 / 2];
            for (int i = 0, p = 0; i < image.Length; i += 2, p += 3)
            {
                int a = image[i], b = image[i + 1];
                (strip[p], strip[p + 1], strip[p + 2]) = lsbFirst
                    ? ((byte)a, (byte)((a >> 8) | (b << 4)), (byte)(b >> 4))
                    : ((byte)(a >> 4), (byte)((a << 4) | (b >> 8)), (byte)b);
            }
        }

        AssertEqual(image, width, height, Srw(model, width, height, bits, compression, strip, bigEndian: bits == 14));
    }

    [Theory]
    [InlineData(64, 6)]
    [InlineData(70, 7)]
    public void V0RoundTrips(int width, int height)
    {
        ushort[] image = Image(width, height, 12, 2);
        AssertEqual(image, width, height, Srw("NX300", width, height, 12, V0, EncodeV0(image, width, height), rowTable: true));
    }

    [Fact]
    public void V1RoundTrips()
    {
        const int Width = 64, Height = 6;
        ushort[] image = Image(Width, Height, 12, 3);
        AssertEqual(image, Width, Height, Srw("NX mini", Width, Height, 12, V1, EncodeV1(image, Width, Height)));
    }

    [Theory]
    [InlineData(0, 14)]
    [InlineData(7, 12)]
    [InlineData(2, 14)]
    [InlineData(5, 12)]
    public void V2RoundTrips(int flags, int bits)
    {
        const int Width = 656, Height = 436;
        ushort[] image = Image(Width, Height, bits, 4);
        AssertEqual(image, Width, Height, Srw("NX1", Width, Height, bits, V2, EncodeV2(image, Width, Height, bits, flags)));
    }

    [Fact]
    public void TheMakerNoteGivesLevelsAndWhiteBalanceLessItsKey()
    {
        const int Width = 42, Height = 4;
        byte[] note = Note(false, [5, 6, 7, 8], [2000, 1000, 1000, 800]);
        byte[] file = Srw("NX11", Width, Height, 12, V0, new byte[Width * Height * 3 / 2], note: note);
        using Raster mosaic = Raster.Load(file, Context, Mosaic);
        RawDetails details = Assert.IsType<RawDetails>(mosaic.Metadata.FormatDetails);
        Assert.Equal("BGGR", details.CfaPattern);
        // R G G B levels by cell of the BGGR pattern; white from the camera table; the neutral scaled so its largest is 1.
        Assert.Equal([8.0, 6, 7, 5], details.BlackLevels);
        Assert.Equal(3750.0, Assert.Single(details.WhiteLevels));
        Assert.Equal([0.4, 0.8, 1], details.AsShotNeutral.Select(v => Math.Round(v, 6)));
    }

    [Fact]
    public void TwelveBitPackingUnderCompression32769IsNotSupported()
    {
        byte[] file = Srw("NX10", 42, 4, 12, Uncompressed, new byte[42 * 4 * 3 / 2]);
        Assert.ThrowsAny<NotSupportedException>(() => Raster.Load<Gray16>(file, Context, Mosaic).Dispose());
    }

    [Fact]
    public void TruncatedV1DataIsCorrupt()
    {
        const int Width = 64, Height = 6;
        byte[] strip = EncodeV1(Image(Width, Height, 12, 3), Width, Height);
        byte[] file = Srw("NX mini", Width, Height, 12, V1, strip[..(strip.Length / 3)]);
        Assert.Throws<CorruptDataException>(() => Raster.Load<Gray16>(file, Context, Mosaic).Dispose());
    }

    [Fact]
    public void V0RowsOutOfOrderAreCorrupt()
    {
        const int Width = 64, Height = 6;
        byte[] strip = EncodeV0(Image(Width, Height, 12, 2), Width, Height);
        BinaryPrimitives.WriteUInt32LittleEndian(strip.AsSpan(8), BinaryPrimitives.ReadUInt32LittleEndian(strip) - 1);
        Assert.Throws<CorruptDataException>(() => Raster.Load<Gray16>(Srw("NX300", Width, Height, 12, V0, strip, rowTable: true), Context, Mosaic).Dispose());
    }

    [Fact]
    public void V2HeaderOfAnotherSizeIsCorrupt()
    {
        const int Width = 656, Height = 436;
        byte[] strip = EncodeV2(Image(Width, Height, 12, 4), Width, Height, 12, 0);
        Assert.Throws<CorruptDataException>(() => Raster.Load<Gray16>(Srw("NX1", Width, Height - 2, 12, V2, strip), Context, Mosaic).Dispose());
    }

    [Fact]
    public void SamsungMakesAreSrwButSamsungTechwinStaysPentax()
    {
        Assert.Equal(RawKind.Srw, MakerParser.Detect(Srw("NX1", 656, 436, 12, V2, new byte[16])));
        Assert.Equal(RawKind.Pef, MakerParser.Detect(Srw("GX-10", 656, 436, 12, V2, new byte[16], make: "SAMSUNG TECHWIN")));
        Assert.Contains("srw", RawFormat.Info.Extensions);
        Assert.Contains("image/x-samsung-srw", RawFormat.Info.MimeTypes);
    }

    [Fact]
    public void ConvertsToDng()
    {
        const int Width = 656, Height = 436;
        byte[] file = Srw("NX500", Width, Height, 12, V2, EncodeV2(Image(Width, Height, 12, 5), Width, Height, 12, 0), note: Note(false, [32, 32, 32, 32], [7000, 4096, 4096, 6000]));
        using MemoryStream dng = new();
        RawFormat.ConvertToDng(new MemoryStream(file), dng, null, Context);
        using Raster<Gray16> fromDng = Raster.Load<Gray16>(dng.ToArray(), Context, Mosaic);
        using Raster<Gray16> fromSrw = Raster.Load<Gray16>(file, Context, Mosaic);
        Assert.Equal((fromSrw.Width, fromSrw.Height), (fromDng.Width, fromDng.Height));
        for (int y = 0; y < fromSrw.Height; y++)
        {
            Assert.True(fromSrw.Row(y).SequenceEqual(fromDng.Row(y)), $"row {y}");
        }

        RawDetails details = Assert.IsType<RawDetails>(fromDng.Metadata.FormatDetails);
        Assert.Equal(4000.0, Assert.Single(details.WhiteLevels));
    }

    [Fact]
    [Trait("Category", "Allocation")]
    public void CodecsAllocateNothingPerRow()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        EgamiContext one = Context with { MaxParallelism = 1 };
        long Measure(byte[] file)
        {
            long fewest = long.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                Raster.Load<Gray16>(file, one, Mosaic).Dispose();
                fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return fewest;
        }

        // Twice the rows decode with the same allocations: the codecs keep nothing per row.
        foreach (Func<int, byte[]> build in new Func<int, byte[]>[]
        {
            h => Srw("NX300", 640, h, 12, V0, EncodeV0(Image(640, h, 12, 2), 640, h), rowTable: true),
            h => Srw("NX mini", 640, h, 12, V1, EncodeV1(Image(640, h, 12, 3), 640, h)),
            h => Srw("NX1", 656, h, 14, V2, EncodeV2(Image(656, h, 14, 4), 656, h, 14, 0)),
        })
        {
            long small = Measure(build(436)), large = Measure(build(872));
            Assert.True(Math.Abs(large - small) < 8 * 1024, $"{small} B, then {large} B for twice the rows");
        }
    }

    /// <summary>Writes the raw fuzz target's SRW seeds (tests/images/fuzz/raw-seeds) when EGAMI_WRITE_SEEDS names a folder.</summary>
    [Fact]
    public void WritesTheFuzzSeedsOnRequest()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAMI_WRITE_SEEDS");
        Assert.SkipUnless(folder is { Length: > 0 }, "Set EGAMI_WRITE_SEEDS to a folder to write the SRW fuzz seeds.");
        byte[] note = Note(false, [3, 2, 2, 3], [2000, 1000, 1000, 1500]);
        File.WriteAllBytes(Path.Combine(folder!, "srw-v0.srw"), Srw("NX300", 48, 8, 12, V0, EncodeV0(Image(48, 8, 12, 2), 48, 8), rowTable: true, note: note));
        File.WriteAllBytes(Path.Combine(folder!, "srw-v1.srw"), Srw("NX mini", 48, 8, 12, V1, EncodeV1(Image(48, 8, 12, 3), 48, 8), note: note));
        // V2 needs 646 × 436 at least: a flat image with a few textured rows codes small.
        ushort[] flat = Image(656, 436, 14, 4);
        for (int i = 656 * 4; i < flat.Length; i++)
        {
            flat[i] = 600;
        }

        File.WriteAllBytes(Path.Combine(folder!, "srw-v2.srw"), Srw("NX1", 656, 436, 14, V2, EncodeV2(flat, 656, 436, 14, 0), note: note));
        byte[] packed = new byte[48 * 4 * 3 / 2];
        new Random(5).NextBytes(packed);
        File.WriteAllBytes(Path.Combine(folder!, "srw-packed12.srw"), Srw("NX10", 48, 4, 12, V0, packed, note: note));
    }

    private static void AssertEqual(ushort[] expected, int width, int height, byte[] file)
    {
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, Mosaic);
        Assert.Equal((width, height), (mosaic.Width, mosaic.Height));
        for (int y = 0; y < height; y++)
        {
            Span<Gray16> row = mosaic.Row(y);
            for (int x = 0; x < width; x++)
            {
                Assert.True(expected[(y * width) + x] == row[x].L, $"({x}, {y}): {expected[(y * width) + x]} coded, {row[x].L} decoded");
            }
        }
    }

    /// <summary>A test image: gradients with a hashed texture, every value within <paramref name="bits"/>.</summary>
    private static ushort[] Image(int width, int height, int bits, int seed)
    {
        int max = (1 << bits) - 1;
        ushort[] image = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                uint hash = (uint)((x * 73856093) ^ (y * 19349663) ^ (seed * 83492791)) * 2654435761u;
                int v = (x * 5) + (y * 3) + (int)(hash >> 26) + (((x / 9) & 1) * (max / 3));
                image[(y * width) + x] = (ushort)(v % (max + 1));
            }
        }

        return image;
    }

    /// <summary>An SRW: IFD0 is the raw IFD; Make SAMSUNG; for V0 tag 0xA010 points at the row table at the strip's start.</summary>
    private static byte[] Srw(string model, int width, int height, int bits, int compression, byte[] strip, bool bigEndian = false, bool rowTable = false, byte[]? note = null, string make = "SAMSUNG")
    {
        TiffBuilder.Ifd ifd0 = new();
        ifd0.Tags.AddRange([TiffTag.Long(256, width), TiffTag.Long(257, height), TiffTag.Short(258, bits), TiffTag.Short(259, compression),
            TiffTag.Ascii(271, make), TiffTag.Ascii(272, model), TiffTag.Short(262, 32803), TiffTag.Long(278, height)]);
        if (rowTable)
        {
            ifd0.Tags.Add(TiffTag.Long(0xA010, 0));
        }

        ifd0.Units.Add(strip);
        if (note is not null)
        {
            TiffBuilder.Ifd exif = new();
            exif.Tags.Add(TiffTag.Undefined(0x927C, note));
            ifd0.Exif = exif;
        }

        byte[] file = TiffBuilder.Write(ifd0, bigEndian);
        if (rowTable)
        {
            Patch(file, bigEndian, 0xA010, (uint)file.AsSpan().IndexOf(strip));
        }

        return file;
    }

    /// <summary>Sets the inline value of an IFD0 entry.</summary>
    private static void Patch(byte[] file, bool bigEndian, int tag, uint value)
    {
        int first = (int)(bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(4)) : BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
        int count = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(file.AsSpan(first)) : BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(first));
        for (int i = 0; i < count; i++)
        {
            Span<byte> e = file.AsSpan(first + 2 + (12 * i), 12);
            if ((bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(e) : BinaryPrimitives.ReadUInt16LittleEndian(e)) == tag)
            {
                if (bigEndian)
                {
                    BinaryPrimitives.WriteUInt32BigEndian(e[8..], value);
                }
                else
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(e[8..], value);
                }

                return;
            }
        }

        throw new InvalidOperationException($"No tag {tag}.");
    }

    /// <summary>A Samsung maker note: a bare IFD whose offsets count from its start, with the key 0xA020 added to 0xA021 and 0xA028.</summary>
    private static byte[] Note(bool bigEndian, uint[] black, uint[] wb)
    {
        uint[] key = [305, 72, 737, 456, 1, 2, 3, 4, 5, 6, 7];
        (int Tag, uint[] Values)[] entries = [(0xA020, key), (0xA021, [.. wb.Select((v, i) => v + key[i])]), (0xA028, [.. black.Select((v, i) => v + key[i])])];
        List<byte> ifd = [], values = [];
        int start = 2 + (12 * entries.Length) + 4;
        void Add(List<byte> to, uint v, int n)
        {
            byte[] b = new byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, n == 2 ? v << 16 : v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            }

            to.AddRange(b[..n].ToArray());
        }

        Add(ifd, (uint)entries.Length, 2);
        foreach ((int tag, uint[] v) in entries)
        {
            Add(ifd, (uint)tag, 2);
            Add(ifd, 4, 2);
            Add(ifd, (uint)v.Length, 4);
            Add(ifd, (uint)(start + values.Count), 4);
            foreach (uint x in v)
            {
                Add(values, x, 4);
            }
        }

        Add(ifd, 0, 4);
        return [.. ifd, .. values];
    }

    /// <summary>Bits most significant first, packed into bytes in order, or into 32-bit words stored little-endian (SRW spec, section 2).</summary>
    private sealed class Bits
    {
        private readonly List<byte> _bytes = [];
        private int _current;
        private int _count;

        public void Write(long value, int n)
        {
            for (int i = n - 1; i >= 0; i--)
            {
                _current = (_current << 1) | (int)((value >> i) & 1);
                if (++_count == 8)
                {
                    _bytes.Add((byte)_current);
                    _current = _count = 0;
                }
            }
        }

        public byte[] Bytes()
        {
            List<byte> bytes = [.. _bytes];
            if (_count > 0)
            {
                bytes.Add((byte)(_current << (8 - _count)));
            }

            return [.. bytes];
        }

        /// <summary>The bits as 32-bit little-endian words, padded with zeros to a multiple of <paramref name="align"/> bytes.</summary>
        public byte[] Words(int align = 4)
        {
            List<byte> bytes = [.. Bytes()];
            while (bytes.Count % 4 != 0 || bytes.Count % align != 0)
            {
                bytes.Add(0);
            }

            for (int i = 0; i < bytes.Count; i += 4)
            {
                bytes.Reverse(i, 4);
            }

            return [.. bytes];
        }
    }

    /// <summary>The bits a two's-complement difference needs (0 for 0).</summary>
    private static int Needed(int d) => d == 0 ? 0 : d > 0 ? 33 - BitOperations.LeadingZeroCount((uint)d) : 33 - BitOperations.LeadingZeroCount((uint)~d);

    /// <summary>V0 (spec, section 4): the swap undone, then per row a table offset and groups of 16 with lengths and predictions.</summary>
    private static byte[] EncodeV0(ushort[] image, int width, int height)
    {
        int[] p = [.. image.Select(v => (int)v)];
        for (int y = 0; y + 1 < height; y += 2)
        {
            for (int x = 0; x + 1 < width; x += 2)
            {
                (p[(y * width) + x + 1], p[((y + 1) * width) + x]) = (p[((y + 1) * width) + x], p[(y * width) + x + 1]);
            }
        }

        List<byte[]> rows = [];
        int[] len = new int[4], diff = new int[16];
        for (int y = 0; y < height; y++)
        {
            Bits bits = new();
            Array.Fill(len, y < 2 ? 7 : 4);
            for (int g = 0; g < width; g += 16)
            {
                bool vertical = y >= 2 && (g / 16) % 2 == 1;
                int even = g == 0 ? 128 : p[(y * width) + g - 2], odd = g == 0 ? 128 : p[(y * width) + g - 1];
                for (int c = 0; c < 16; c++)
                {
                    int x = g + c;
                    int prediction = !vertical ? ((c & 1) == 0 ? even : odd) : x >= width ? 0 : p[((y - 1 - (c & 1)) * width) + x];
                    diff[c] = x < width ? p[(y * width) + x] - prediction : 0;
                }

                int[] ops = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    int need = 0;
                    for (int k = 0; k < 4; k++)
                    {
                        need = Math.Max(need, Needed(diff[(i < 2 ? 0 : 1) + (2 * ((4 * (i & 1)) + k))]));
                    }

                    (ops[i], len[i]) = need == len[i] - 1 ? (2, len[i] - 1) : need <= len[i] ? (0, len[i]) : need == len[i] + 1 ? (1, len[i] + 1) : (3, need);
                }

                bits.Write(vertical ? 1 : 0, 1);
                foreach (int op in ops)
                {
                    bits.Write(op, 2);
                }

                for (int i = 0; i < 4; i++)
                {
                    if (ops[i] == 3)
                    {
                        bits.Write(len[i], 4);
                    }
                }

                for (int k = 0; k < 16; k++)
                {
                    int c = k < 8 ? 2 * k : (2 * (k - 8)) + 1;
                    bits.Write(diff[c], len[k < 8 ? k >> 2 : 2 + ((k - 8) >> 2)]);
                }
            }

            rows.Add(bits.Words());
        }

        byte[] table = new byte[4 * height];
        int at = table.Length;
        for (int y = 0; y < height; y++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan(4 * y), (uint)at);
            at += rows[y].Length;
        }

        return [.. table, .. rows.SelectMany(r => r)];
    }

    private static readonly (int Code, int Length)[] V1Codes =
    [
        (0b110100, 6), (0b11011, 5), (0b1111, 4), (0b1100, 4), (0b000, 3), (0b10, 2), (0b01, 2), (0b001, 3), (0b1110, 4),
        (0b1101010, 7), (0b11010110, 8), (0b110101110, 9), (0b1101011110, 10), (0b1101011111, 10),
    ];

    /// <summary>V1 (spec, section 5): per sample the prefix code of the difference's length, then its JPEG-signed bits.</summary>
    private static byte[] EncodeV1(ushort[] image, int width, int height)
    {
        Bits bits = new();
        for (int y = 0; y < height; y++)
        {
            int[] pred = [y >= 2 ? image[(y - 2) * width] : 0, y >= 2 ? image[((y - 2) * width) + 1] : 0];
            for (int x = 0; x < width; x++)
            {
                int v = image[(y * width) + x], d = v - pred[x & 1];
                int n = d == 0 ? 0 : 32 - BitOperations.LeadingZeroCount((uint)Math.Abs(d));
                bits.Write(V1Codes[n].Code, V1Codes[n].Length);
                bits.Write(d >= 0 ? d : d + (1 << n) - 1, n);
                pred[x & 1] = v;
            }
        }

        return [.. bits.Bytes(), 0, 0, 0, 0];
    }

    /// <summary>V2 (spec, section 6): the header, then per row 16-byte-aligned blocks of motion, lengths and differences.</summary>
    private static byte[] EncodeV2(ushort[] image, int width, int height, int depth, int flags)
    {
        Bits header = new();
        header.Write(22067, 16);
        header.Write(0, 4);
        header.Write(depth - 1, 4);
        header.Write(0, 8);
        header.Write(width, 16);
        header.Write(height, 16);
        header.Write(0, 16);
        header.Write(0, 4);
        header.Write(flags, 4);
        header.Write(0, 26);
        header.Write(600, 14);
        List<byte> data = [.. header.Words()];
        int[] offsets = [-4, -2, -2, 0, 0, 2, 4];
        int[] prev2 = new int[3], prev1 = new int[3], len = new int[4], baseline = new int[16];
        int Sample(int y, int x) => image[(y * width) + x];
        for (int y = 0; y < height; y++)
        {
            Bits bits = new();
            Array.Fill(prev2, y < 2 ? 7 : 4);
            Array.Fill(prev1, y < 2 ? 7 : 4);
            int motion = 7;
            bool oddRow = (y & 1) == 1;
            for (int g = 0; g < width; g += 16)
            {
                int block = g / 16;
                if ((flags & 4) == 0 && g % 64 == 0)
                {
                    // Scale stays 0: set to 0 at the row's start, kept after.
                    bits.Write(g == 0 ? 3 : 0, 2);
                    if (g == 0)
                    {
                        bits.Write(0, 12);
                    }
                }

                int next;
                if ((flags & 2) != 0)
                {
                    next = y < 2 || block % 2 == 0 ? 7 : 3;
                    bits.Write(next == 3 ? 1 : 0, 1);
                }
                else
                {
                    next = y < 2 ? 7 : (block % 3) switch { 0 => 7, 1 => 3, _ => g + 32 <= width ? 4 : 3 };
                    bits.Write(next == motion ? 1 : 0, 1);
                    if (next != motion)
                    {
                        bits.Write(next, 3);
                    }
                }

                motion = next;
                for (int i = 0; i < 16; i++)
                {
                    if (motion == 7)
                    {
                        baseline[i] = g == 0 ? 600 : Sample(y, g - 2 + (i & 1));
                        continue;
                    }

                    bool fromTwoUp = ((y + i) & 1) == 1;
                    int c = g + i + offsets[motion] + (fromTwoUp ? 0 : (i & 1) == 0 ? 1 : -1);
                    int r = fromTwoUp ? y - 2 : y - 1;
                    baseline[i] = motion is 2 or 4 ? (Sample(r, c) + Sample(r, c + 2) + 1) >> 1 : Sample(r, c);
                }

                int P(int k) => oddRow ? (2 * (k & 7)) + 1 - (k >> 3) : (2 * (k & 7)) + (k >> 3);
                int[] need = new int[4];
                for (int k = 0; k < 16; k++)
                {
                    need[k >> 2] = Math.Max(need[k >> 2], Needed(Sample(y, g + P(k)) - baseline[P(k)]));
                }

                if ((flags & 1) == 0 && need.All(n => n == 0))
                {
                    bits.Write(1, 1);
                    Array.Clear(len);
                }
                else
                {
                    if ((flags & 1) == 0)
                    {
                        bits.Write(0, 1);
                    }

                    int[] codes = new int[4];
                    for (int j = 0; j < 4; j++)
                    {
                        int t = oddRow ? j >> 1 : ((j >> 1) + 2) % 3;
                        (codes[j], len[j]) = need[j] <= prev2[t] && need[j] == prev2[t] - 1 ? (2, prev2[t] - 1)
                            : need[j] <= prev2[t] ? (0, prev2[t])
                            : need[j] == prev2[t] + 1 && prev2[t] + 1 <= depth + 1 ? (1, prev2[t] + 1)
                            : (3, need[j]);
                        prev2[t] = prev1[t];
                        prev1[t] = len[j];
                    }

                    foreach (int code in codes)
                    {
                        bits.Write(code, 2);
                    }

                    for (int j = 0; j < 4; j++)
                    {
                        if (codes[j] == 3)
                        {
                            bits.Write(len[j], 4);
                        }
                    }
                }

                for (int k = 0; k < 16; k++)
                {
                    bits.Write(Sample(y, g + P(k)) - baseline[P(k)], len[k >> 2]);
                }
            }

            data.AddRange(bits.Words(16));
        }

        return [.. data];
    }
}
