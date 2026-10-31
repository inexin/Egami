using System.Buffers.Binary;

using Egami.Formats.Extended.Raw;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Raw;

/// <summary>
/// RAW Build Plan tasks 8 and 9: maker files built in code. Nikon's initial predictors, and RAF's container with the
/// Fujifilm compressed codec round-tripped through a test-only encoder built from the specification's section 10.
/// </summary>
public class RawMakerTests
{
    private static EgamiContext Context => TestImages.Context;

    private static readonly RawReaderOptions Mosaic = new() { Output = RawOutput.Mosaic };

    [Fact]
    public void NikonInitialPredictorsFollowTheSpecOrder()
    {
        // Lossless 12-bit (tree 2), every difference 0: each pixel is its initial predictor. The spec's order is (even row,
        // even column), (even, odd), (odd, even), (odd, odd); the two middle values differ so the other order fails here.
        byte[] meta = new byte[600];
        meta[0] = 0x46;
        meta[1] = 0x30;
        ushort[] seeds = [100, 200, 300, 400];
        for (int i = 0; i < 4; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(meta.AsSpan(2 + (2 * i)), seeds[i]);
        }

        // Tree 2's code for symbol 0 is 11110; eight samples give 40 bits (and a size that is not the uncompressed one).
        BitWriter bits = new();
        for (int i = 0; i < 8; i++)
        {
            bits.Write(0b11110, 5);
        }

        byte[] file = NikonFile(meta, bits.ToArray());

        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, Mosaic);
        Assert.Equal([100, 200, 100, 200], Enumerable.Range(0, 4).Select(x => (int)mosaic.Row(0)[x].L));
        Assert.Equal([300, 400, 300, 400], Enumerable.Range(0, 4).Select(x => (int)mosaic.Row(1)[x].L));
    }

    [Theory]
    [InlineData(true, 768, 12, 14)]
    [InlineData(true, 792, 18, 14)]
    [InlineData(false, 1536, 6, 16)]
    [InlineData(false, 792, 12, 12)]
    public void FujiCompressedRoundTrips(bool xtrans, int width, int height, int bits)
    {
        ushort[] samples = Scene(width, height, bits);
        byte[] file = Raf(width, height, bits, xtrans, FujiEncoder.Encode(samples, width, height, xtrans, bits));
        Assert.Equal("RAW", Raster.Probe(file, Context).Format.Name);
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, Mosaic);
        Assert.Equal((width, height), (mosaic.Width, mosaic.Height));
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Assert.True(samples[(y * width) + x] == mosaic.Row(y)[x].L, $"({x}, {y}): expected {samples[(y * width) + x]}, got {mosaic.Row(y)[x].L}.");
            }
        }

        RawDetails details = (RawDetails)mosaic.Metadata.FormatDetails!;
        Assert.Equal(xtrans ? 6 : 2, details.CfaWidth);
    }

    [Fact]
    public void RafWithAByteCountPastTheEndReadsStrictly()
    {
        // Review finding: the X-H2S records a strip byte count past the end of the file, which is not damage.
        const int Width = 768, Height = 6;
        ushort[] samples = Scene(Width, Height, 14);
        byte[] file = Raf(Width, Height, 14, true, FujiEncoder.Encode(samples, Width, Height, true, 14), countExtra: 64);
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, new RawReaderOptions { Output = RawOutput.Mosaic, Recovery = RecoveryMode.Strict });
        Assert.Equal(samples[^1], mosaic.Row(Height - 1)[Width - 1].L);
        Assert.DoesNotContain(mosaic.Metadata.Warnings, w => w.Id.EndsWith("DataDamaged", StringComparison.Ordinal));
    }

    [Fact]
    public void UncompressedRafReads()
    {
        const int Width = 48, Height = 12;
        ushort[] samples = Scene(Width, Height, 14);
        byte[] data = new byte[Width * Height * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2 * i), samples[i]);
        }

        using Raster<Gray16> mosaic = Raster.Load<Gray16>(Raf(Width, Height, 14, true, data), Context, Mosaic);
        Assert.Equal(samples[(5 * Width) + 7], mosaic.Row(5)[7].L);
        Assert.Equal(samples[^1], mosaic.Row(Height - 1)[Width - 1].L);
    }

    [Fact]
    public void RafWithAnotherXTransPhaseIsRefusedWhenCompressed()
    {
        const int Width = 768, Height = 6;
        byte[] stream = FujiEncoder.Encode(Scene(Width, Height, 14), Width, Height, true, 14);
        byte[] shifted = [.. Enumerable.Range(0, 36).Select(i => FujiDecoder.XTrans[((i / 6) * 6) + ((i + 1) % 6)])];
        byte[] file = Raf(Width, Height, 14, true, stream, shifted);
        Assert.ThrowsAny<NotSupportedException>(() => Raster.Load<Gray16>(file, Context, Mosaic).Dispose());
    }

    [Fact]
    public void TruncatedRw2DecodesWithBlackForTheMissingPart()
    {
        // Review finding: a last Panasonic block shorter than its 0x1FF8 split crashed instead of reading as black.
        TiffBuilder.Ifd ifd0 = new() { OffsetsTag = 0x0118, CountsTag = 0xC6FF };
        ifd0.Tags.AddRange([TiffTag.Ascii(271, "Panasonic"), TiffTag.Ascii(272, "DMC-TEST"), TiffTag.Short(0x0002, 64), TiffTag.Short(0x0003, 64),
            TiffTag.Short(0x000A, 12), TiffTag.Short(0x002D, 4)]);
        ifd0.Units.Add([.. Enumerable.Range(0, 100).Select(i => (byte)(i * 37))]);
        byte[] file = TiffBuilder.Write(ifd0);
        file[2] = 0x55;
        using Raster<Gray16> mosaic = Raster.Load<Gray16>(file, Context, new RawReaderOptions { Output = RawOutput.Mosaic, Recovery = RecoveryMode.Lenient });
        Assert.Equal((64, 64), (mosaic.Width, mosaic.Height));
        Assert.Equal(0, mosaic.Row(63)[63].L);
    }

    [Fact]
    public void NikonCurveWithMorePointsThanValuesIsNotADivisionByZero()
    {
        // Review finding: a stepped curve (0x44, 0x20) with more points than sample values made its step 0.
        byte[] meta = new byte[600];
        meta[0] = 0x44;
        meta[1] = 0x20;
        BinaryPrimitives.WriteUInt16LittleEndian(meta.AsSpan(10), 5000);
        Exception? error = Record.Exception(() => Raster.Load<Gray16>(NikonFile(meta, [0, 0, 0, 0, 0, 0, 0, 0, 0]), Context, Mosaic).Dispose());
        Assert.True(error is null or CorruptDataException, error?.ToString());
    }

    [Theory]
    [InlineData(0xFFFFFFF0u)]
    [InlineData(0x7FFFFFFEu)]
    public void HeadersWithFarIfdOffsetsAreNotRaw(uint first)
    {
        // Fuzz finding: an IFD offset near 4 GB wrapped the header sniffing's arithmetic.
        byte[] header = new byte[64];
        "II*\0"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), first);
        Assert.False(RawDetect.Matches(header));
    }

    /// <summary>A sensor-like test scene: smooth gradients, an edge and some noise, within <paramref name="bits"/>.</summary>
    private static ushort[] Scene(int width, int height, int bits)
    {
        int max = (1 << bits) - 1;
        Random random = new(width + height + bits);
        ushort[] s = new ushort[width * height];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double v = (0.2 + (0.5 * x / width) + (x > width / 2 ? 0.2 : 0)) * max;
                s[(y * width) + x] = (ushort)Math.Clamp((int)(v + random.Next(-40, 41)), 0, max);
            }
        }

        s[3] = 0;
        s[4] = (ushort)max;
        return s;
    }

    /// <summary>A 4 × 2 NEF: a Nikon maker note with tag 0x0096 (<paramref name="meta"/>) and Huffman-coded <paramref name="data"/>.</summary>
    private static byte[] NikonFile(byte[] meta, byte[] data)
    {
        TiffBuilder.Ifd raw = new();
        raw.Tags.AddRange([TiffTag.Long(254, 0), TiffTag.Long(256, 4), TiffTag.Long(257, 2), TiffTag.Short(258, 12), TiffTag.Short(259, 34713),
            TiffTag.Short(262, 32803), TiffTag.Short(277, 1), TiffTag.Long(278, 2), TiffTag.Short(33421, 2, 2), TiffTag.Bytes(33422, 0, 1, 1, 2)]);
        raw.Units.Add(data);

        byte[] note = [.. "Nikon\0\x02\x10\0\0"u8, .. NikonNoteTiff(meta)];
        TiffBuilder.Ifd exif = new();
        exif.Tags.Add(TiffTag.Undefined(0x927C, note));
        TiffBuilder.Ifd ifd0 = new();
        ifd0.Tags.AddRange([TiffTag.Ascii(271, "NIKON CORPORATION"), TiffTag.Ascii(272, "NIKON TEST"), TiffTag.Long(256, 4), TiffTag.Long(257, 2)]);
        ifd0.SubIfds.Add(raw);
        ifd0.Exif = exif;
        return TiffBuilder.Write(ifd0);
    }

    /// <summary>The maker note's own TIFF (offsets from its start) with tag 0x0096.</summary>
    private static byte[] NikonNoteTiff(byte[] meta)
    {
        TiffBuilder.Ifd note = new();
        note.Tags.Add(TiffTag.Undefined(0x0096, meta));
        return TiffBuilder.Write(note);
    }

    /// <summary>A RAF: header, meta directory with the X-Trans cells (stored last first) and a CFA-section TIFF.</summary>
    private static byte[] Raf(int width, int height, int bits, bool xtrans, byte[] data, byte[]? pattern = null, int countExtra = 0)
    {
        List<byte> meta = [0, 0, 0, 0];
        int records = 0;
        void Record(int tag, byte[] value)
        {
            meta.AddRange([(byte)(tag >> 8), (byte)tag, (byte)(value.Length >> 8), (byte)value.Length, .. value]);
            records++;
        }

        Record(0x0100, [(byte)(height >> 8), (byte)height, (byte)(width >> 8), (byte)width]);
        if (xtrans)
        {
            Record(0x0131, [.. (pattern ?? FujiDecoder.XTrans).Reverse()]);
        }

        BinaryPrimitives.WriteUInt32BigEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(meta), (uint)records);

        // The CFA section: IFD0 with 0xF000 (type IFD) pointing at the raw IFD; the strip follows at 128.
        byte[] cfa = new byte[128 + data.Length];
        "II*\0"u8.CopyTo(cfa);
        BinaryPrimitives.WriteUInt32LittleEndian(cfa.AsSpan(4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(cfa.AsSpan(8), 1);
        Entry(cfa, 10, 0xF000, 13, 26);
        int at = 26;
        BinaryPrimitives.WriteUInt16LittleEndian(cfa.AsSpan(at), 5);
        Entry(cfa, at + 2, 0xF001, 4, width);
        Entry(cfa, at + 14, 0xF002, 4, height);
        Entry(cfa, at + 26, 0xF003, 4, bits);
        Entry(cfa, at + 38, 0xF007, 4, 128);
        Entry(cfa, at + 50, 0xF008, 4, data.Length + countExtra);
        data.CopyTo(cfa, 128);

        byte[] header = new byte[108];
        "FUJIFILMCCD-RAW "u8.CopyTo(header);
        "X-TEST"u8.CopyTo(header.AsSpan(28));
        "0100"u8.CopyTo(header.AsSpan(60));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(92), 108);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(96), (uint)meta.Count);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(100), (uint)(108 + meta.Count));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(104), (uint)cfa.Length);
        return [.. header, .. meta, .. cfa];
    }

    private static void Entry(byte[] b, int at, int tag, int type, int value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at), (ushort)tag);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at + 2), (ushort)type);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at + 8), (uint)value);
    }

    private sealed class BitWriter
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

        public byte[] ToArray()
        {
            List<byte> bytes = [.. _bytes];
            if (_count > 0)
            {
                bytes.Add((byte)(_current << (8 - _count)));
            }

            return [.. bytes, 0, 0, 0, 0];
        }
    }

    /// <summary>
    /// A lossless Fujifilm compressed encoder for tests, the specification's section 10: each cell, in the decoder's order,
    /// gets the decoder's prediction and context, and the residual is written as the decoder will read it.
    /// </summary>
    private static class FujiEncoder
    {
        private const int Block = 768;
        private const int R0 = 0, G0 = 5, B0 = 13;
        private static readonly (int A, int B)[] Passes = [(R0 + 2, G0 + 2), (G0 + 3, B0 + 2), (R0 + 3, G0 + 4), (G0 + 5, B0 + 3), (R0 + 4, G0 + 6), (G0 + 7, B0 + 4)];

        public static byte[] Encode(ushort[] samples, int width, int height, bool xtrans, int bits)
        {
            int blocks = (width + Block - 1) / Block, lines = height / 6;
            List<byte[]> strips = [];
            for (int b = 0; b < blocks; b++)
            {
                strips.Add(Strip(samples, width, b, lines, xtrans, bits));
            }

            byte[] header = new byte[16];
            BinaryPrimitives.WriteUInt16BigEndian(header, 0x4953);
            header[2] = 1;
            header[3] = (byte)(xtrans ? 16 : 0);
            header[4] = (byte)bits;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(5), (ushort)height);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(7), (ushort)(blocks * Block));
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(9), (ushort)width);
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(11), Block);
            header[13] = (byte)blocks;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(14), (ushort)lines);
            byte[] table = new byte[((4 * blocks) + 15) & ~15];
            for (int b = 0; b < blocks; b++)
            {
                BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(4 * b), (uint)strips[b].Length);
            }

            return [.. header, .. table, .. strips.SelectMany(s => s)];
        }

        private static byte[] Strip(ushort[] samples, int width, int strip, int lines, bool xtrans, int bits)
        {
            int w = xtrans ? 512 : 384, stride = w + 2, max = (1 << bits) - 1, total = max + 1;
            int maxBits = 4 * (32 - System.Numerics.BitOperations.LeadingZeroCount((uint)max)), rawBits = bits;
            int[] q = new int[(2 * max) + 1];
            for (int v = -max; v <= max; v++)
            {
                q[v + max] = v <= -276 ? -4 : v <= -67 ? -3 : v <= -18 ? -2 : v < 0 ? -1 : v == 0 ? 0 : v < 18 ? 1 : v < 67 ? 2 : v < 276 ? 3 : 4;
            }

            int[] cells = new int[18 * stride];
            int[] a = new int[6 * 41], n = new int[6 * 41];
            Array.Fill(a, Math.Max(2, (total + 32) >> 6));
            Array.Fill(n, 1);
            int?[] target = new int?[18 * w];
            BitWriter writer = new();
            int Get(int line, int p) => cells[(line * stride) + p + 1];
            void Set(int line, int p, int v) => cells[(line * stride) + p + 1] = v;
            for (int line = 0; line < lines; line++)
            {
                // Which sample each cell of this line's colour lines holds (the decoder's copy, inverted).
                Array.Clear(target);
                for (int r = 0; r < 6; r++)
                {
                    for (int x = 0; x < Block; x++)
                    {
                        int gx = (strip * Block) + x;
                        int colour = xtrans ? FujiDecoder.XTrans[(r * 6) + (x % 6)] : (((r & 1) << 1) | (x & 1)) switch { 0 => 0, 3 => 2, _ => 1 };
                        int source = colour switch { 0 => R0 + 2 + (r / 2), 1 => G0 + 2 + r, _ => B0 + 2 + (r / 2) };
                        int idx = xtrans ? (2 * (x / 3)) + (x % 3 == 0 ? 0 : 1) : x / 2;
                        target[(source * w) + idx] = gx < width ? samples[(((line * 6) + r) * width) + gx] : 0;
                    }
                }

                for (int k = 0; k < 6; k++)
                {
                    (int la, int lb) = Passes[k];
                    foreach (int l in new[] { la, lb })
                    {
                        Set(l, -1, Get(l - 1, 0));
                        Set(l, w, Get(l - 1, w - 1));
                    }

                    for (int i = 0; i < (w / 2) + 4; i++)
                    {
                        if (i < w / 2)
                        {
                            Cell(la, 2 * i, k);
                            Cell(lb, 2 * i, k);
                        }

                        if (i >= 4 && i - 4 < w / 2)
                        {
                            Cell(la, (2 * (i - 4)) + 1, k);
                            Cell(lb, (2 * (i - 4)) + 1, k);
                        }
                    }
                }

                foreach ((int from, int to) in new[] { (R0 + 3, R0), (R0 + 4, R0 + 1), (G0 + 6, G0), (G0 + 7, G0 + 1), (B0 + 3, B0), (B0 + 4, B0 + 1) })
                {
                    Array.Copy(cells, from * stride, cells, to * stride, stride);
                }
            }

            return writer.ToArray();

            void Cell(int line, int p, int k)
            {
                int nn = Get(line - 1, p), nw = Get(line - 1, p - 1), ne = Get(line - 1, p + 1), n2 = Get(line - 2, p);
                int pred, v1, v2;
                bool even = (p & 1) == 0;
                if (even)
                {
                    int dnw = Math.Abs(nw - nn), dnn = Math.Abs(n2 - nn), dne = Math.Abs(ne - nn);
                    int s = dnw > dnn && dnw > dne ? n2 + ne + (2 * nn) : dne > dnw && dne > dnn ? n2 + nw + (2 * nn) : ne + nw + (2 * nn);
                    pred = s >> 2;
                    v1 = nn - n2;
                    v2 = nw - nn;
                    if (target[(line * w) + p] is null)
                    {
                        Assert.True(xtrans, "Only X-Trans has predictor-only cells.");
                        Set(line, p, pred);
                        return;
                    }
                }
                else
                {
                    int wc = Get(line, p - 1), ec = Get(line, p + 1);
                    pred = (nn > nw && nn > ne) || (nn < nw && nn < ne) ? (wc + ec + (2 * nn)) >> 2 : (wc + ec) >> 1;
                    v1 = nn - nw;
                    v2 = nw - wc;
                }

                int value = target[(line * w) + p] ?? throw new InvalidOperationException("An odd cell has no sample.");
                int g = (9 * q[v1 + max]) + q[v2 + max];
                int ctx = ((even ? 0 : 3) + (k % 3)) * 41 + Math.Abs(g);
                int e = g >= 0 ? value - pred : pred - value;
                if (e < -(total / 2))
                {
                    e += total;
                }
                else if (e >= total / 2)
                {
                    e -= total;
                }

                int code = e >= 0 ? 2 * e : (-2 * e) - 1;
                int kk = 0;
                while (kk < 15 && n[ctx] << kk < a[ctx])
                {
                    kk++;
                }

                int threshold = maxBits - rawBits - 1;
                if (code >> kk < threshold)
                {
                    writer.Write(0, code >> kk);
                    writer.Write(1, 1);
                    writer.Write(code & ((1 << kk) - 1), kk);
                }
                else
                {
                    writer.Write(0, threshold);
                    writer.Write(1, 1);
                    writer.Write(code - 1, rawBits);
                }

                a[ctx] += Math.Abs(e);
                if (n[ctx] == 64)
                {
                    a[ctx] >>= 1;
                    n[ctx] >>= 1;
                }

                n[ctx]++;
                Set(line, p, value);
            }
        }
    }
}
