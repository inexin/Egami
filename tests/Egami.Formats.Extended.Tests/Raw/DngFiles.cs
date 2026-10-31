using System.Buffers.Binary;
using System.IO.Compression;

namespace Egami.Formats.Extended.Tests.Raw;

/// <summary>One TIFF tag for <see cref="TiffBuilder"/>: its number, TIFF type and values.</summary>
internal sealed record TiffTag(int Tag, int Type, double[] Values, byte[]? Raw = null)
{
    public static TiffTag Short(int tag, params int[] v) => new(tag, 3, [.. v.Select(x => (double)x)]);

    public static TiffTag Long(int tag, params long[] v) => new(tag, 4, [.. v.Select(x => (double)x)]);

    public static TiffTag Rational(int tag, params double[] v) => new(tag, 5, v);

    public static TiffTag SRational(int tag, params double[] v) => new(tag, 10, v);

    public static TiffTag Float(int tag, params double[] v) => new(tag, 11, v);

    public static TiffTag Double(int tag, params double[] v) => new(tag, 12, v);

    public static TiffTag Bytes(int tag, params byte[] v) => new(tag, 1, [], v);

    public static TiffTag Undefined(int tag, byte[] v) => new(tag, 7, [], v);

    public static TiffTag Ascii(int tag, string s) => new(tag, 2, [], [.. System.Text.Encoding.ASCII.GetBytes(s), 0]);
}

/// <summary>
/// Writes TIFF files from a tree of IFDs, from TIFF 6.0: IFD0 and its chain, sub-IFDs through SubIFDs, and the image data each
/// one carries, every offset filled in. Byte order either way.
/// </summary>
internal sealed class TiffBuilder
{
    public sealed class Ifd
    {
        public List<TiffTag> Tags { get; } = [];

        public List<Ifd> SubIfds { get; } = [];

        public Ifd? Exif { get; set; }

        /// <summary>Gets the strips or tiles, written after the IFDs; their offsets and byte counts go in the given tags.</summary>
        public List<byte[]> Units { get; } = [];

        public int OffsetsTag { get; set; } = 273;

        public int CountsTag { get; set; } = 279;
    }

    public static byte[] Write(Ifd ifd0, bool bigEndian = false, Ifd? next = null)
    {
        Writer w = new(bigEndian);
        w.Bytes(bigEndian ? "MM"u8 : "II"u8);
        w.U16(42);
        int first = w.Reserve4();
        w.Patch4(first, w.Position);
        int link = w.WriteIfd(ifd0);
        if (next is not null)
        {
            w.Patch4(link, w.Position);
            w.WriteIfd(next);
        }

        return w.Finish();
    }

    private sealed class Writer(bool bigEndian)
    {
        private readonly List<byte> _bytes = [];
        private readonly List<(int At, byte[] Data)> _deferred = [];

        public int Position => _bytes.Count;

        public void Bytes(ReadOnlySpan<byte> b) => _bytes.AddRange(b.ToArray());

        public void U16(int v)
        {
            Span<byte> b = stackalloc byte[2];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)v);
            }

            Bytes(b);
        }

        public int Reserve4()
        {
            int at = Position;
            _bytes.AddRange([0, 0, 0, 0]);
            return at;
        }

        public void Patch4(int at, long v)
        {
            Span<byte> b = stackalloc byte[4];
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v);
            }

            for (int i = 0; i < 4; i++)
            {
                _bytes[at + i] = b[i];
            }
        }

        /// <summary>Writes an IFD and everything under it; returns where its next-IFD link is.</summary>
        public int WriteIfd(Ifd ifd)
        {
            List<TiffTag> tags = [.. ifd.Tags];
            if (ifd.Units.Count > 0)
            {
                tags.Add(TiffTag.Long(ifd.OffsetsTag, new long[ifd.Units.Count]));
                tags.Add(TiffTag.Long(ifd.CountsTag, [.. ifd.Units.Select(u => (long)u.Length)]));
            }

            if (ifd.SubIfds.Count > 0)
            {
                tags.Add(TiffTag.Long(330, new long[ifd.SubIfds.Count]));
            }

            if (ifd.Exif is not null)
            {
                tags.Add(TiffTag.Long(34665, 0));
            }

            tags.Sort((a, b) => a.Tag.CompareTo(b.Tag));
            int start = Position;
            U16(tags.Count);
            Dictionary<int, int> valueAt = [];
            List<(int Entry, byte[] Value)> outside = [];
            foreach (TiffTag tag in tags)
            {
                byte[] value = Encode(tag);
                U16(tag.Tag);
                U16(tag.Type);
                int count = tag.Raw is not null ? tag.Raw.Length / Size(tag.Type) : tag.Values.Length;
                Patch4(Reserve4(), count);
                int field = Reserve4();
                if (value.Length <= 4)
                {
                    for (int i = 0; i < value.Length; i++)
                    {
                        _bytes[field + i] = value[i];
                    }

                    valueAt[tag.Tag] = field;
                }
                else
                {
                    outside.Add((field, value));
                    valueAt[tag.Tag] = -field;
                }
            }

            int link = Reserve4();
            foreach ((int field, byte[] value) in outside)
            {
                Patch4(field, Position);
                Bytes(value);
                if ((Position & 1) != 0)
                {
                    _bytes.Add(0);
                }
            }

            int Where(int tag) => valueAt[tag] >= 0 ? valueAt[tag] : (int)ReadField(-valueAt[tag]);

            for (int i = 0; i < ifd.SubIfds.Count; i++)
            {
                Patch4(Where(330) + (ifd.SubIfds.Count == 1 ? 0 : i * 4), Position);
                WriteIfd(ifd.SubIfds[i]);
            }

            if (ifd.Exif is not null)
            {
                Patch4(Where(34665), Position);
                WriteIfd(ifd.Exif);
            }

            for (int i = 0; i < ifd.Units.Count; i++)
            {
                Patch4(Where(ifd.OffsetsTag) + (ifd.Units.Count == 1 ? 0 : i * 4), Position);
                Bytes(ifd.Units[i]);
            }

            _ = start;
            return link;
        }

        private uint ReadField(int at)
        {
            byte[] b = [_bytes[at], _bytes[at + 1], _bytes[at + 2], _bytes[at + 3]];
            return bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b);
        }

        private byte[] Encode(TiffTag tag)
        {
            if (tag.Raw is not null)
            {
                return tag.Raw;
            }

            int size = Size(tag.Type);
            byte[] b = new byte[tag.Values.Length * size];
            for (int i = 0; i < tag.Values.Length; i++)
            {
                Span<byte> s = b.AsSpan(i * size);
                double v = tag.Values[i];
                switch (tag.Type)
                {
                    case 3:
                        Put16(s, (ushort)v);
                        break;
                    case 4:
                        Put32(s, (uint)v);
                        break;
                    case 5:
                        Put32(s, (uint)Math.Round(v * 1_000_000));
                        Put32(s[4..], 1_000_000);
                        break;
                    case 10:
                        Put32(s, (uint)(int)Math.Round(v * 1_000_000));
                        Put32(s[4..], 1_000_000);
                        break;
                    case 11:
                        Put32(s, (uint)BitConverter.SingleToInt32Bits((float)v));
                        break;
                    case 12:
                        if (bigEndian)
                        {
                            BinaryPrimitives.WriteDoubleBigEndian(s, v);
                        }
                        else
                        {
                            BinaryPrimitives.WriteDoubleLittleEndian(s, v);
                        }

                        break;
                }
            }

            return b;
        }

        private void Put16(Span<byte> s, ushort v)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt16BigEndian(s, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt16LittleEndian(s, v);
            }
        }

        private void Put32(Span<byte> s, uint v)
        {
            if (bigEndian)
            {
                BinaryPrimitives.WriteUInt32BigEndian(s, v);
            }
            else
            {
                BinaryPrimitives.WriteUInt32LittleEndian(s, v);
            }
        }

        public byte[] Finish() => [.. _bytes];
    }

    public static int Size(int type) => type switch
    {
        1 or 2 or 6 or 7 => 1,
        3 or 8 => 2,
        4 or 9 or 11 => 4,
        _ => 8,
    };
}

/// <summary>
/// Builds small DNG files for the tests, from the Adobe DNG Specification 1.7.1: a camera whose ColorMatrix is the inverse of
/// linear sRGB under D65, so a neutral, as-shot develop gives back the stored values as sRGB.
/// </summary>
internal sealed class DngFile
{
    /// <summary>XYZ (D65) to linear sRGB, the camera's ColorMatrix, so camera RGB is linear sRGB.</summary>
    public static readonly double[] SrgbCamera = [3.2404542, -1.5371385, -0.4985314, -0.9692660, 1.8760108, 0.0415560, 0.0556434, -0.2040259, 1.0572252];

    public string Make { get; set; } = "Egami";

    public string Model { get; set; } = "Test Camera";

    public int Width { get; set; } = 16;

    public int Height { get; set; } = 16;

    public int Bits { get; set; } = 16;

    /// <summary>Gets or sets the samples, row by row (one per pixel for a CFA, three for LinearRaw).</summary>
    public ushort[] Samples { get; set; } = [];

    public float[]? FloatSamples { get; set; }

    public byte[] Cfa { get; set; } = [0, 1, 1, 2];

    public bool LinearRaw { get; set; }

    public int Compression { get; set; } = 1;

    public int Predictor { get; set; } = 1;

    public bool BigEndian { get; set; }

    /// <summary>Gets or sets the tile size, or 0 for one strip per <see cref="RowsPerStrip"/> rows.</summary>
    public int Tile { get; set; }

    public int RowsPerStrip { get; set; }

    public double[] Black { get; set; } = [0];

    public int[] BlackRepeat { get; set; } = [1, 1];

    public double White { get; set; } = 65535;

    public double[] Neutral { get; set; } = [1, 1, 1];

    public double[]? ColorMatrix { get; set; } = SrgbCamera;

    public int Illuminant { get; set; } = 21;

    public int[]? ActiveArea { get; set; }

    public double[]? Crop { get; set; }

    public ushort[]? Linearization { get; set; }

    public double BaselineExposure { get; set; }

    public byte[]? Opcodes1 { get; set; }

    public byte[]? Opcodes2 { get; set; }

    public byte[]? Opcodes3 { get; set; }

    public byte[]? Preview { get; set; }

    public (int Width, int Height) PreviewSize { get; set; }

    public int Orientation { get; set; } = 1;

    public List<TiffTag> ExtraIfd0 { get; } = [];

    public List<TiffTag> ExtraRaw { get; } = [];

    /// <summary>Gets the tags of an EXIF IFD, written when there are any.</summary>
    public List<TiffTag> Exif { get; } = [];

    public double[]? NoiseProfile { get; set; }

    public int SamplesPerPixel => LinearRaw ? 3 : 1;

    /// <summary>Fills every pixel with a function of its position and sample.</summary>
    public DngFile Fill(Func<int, int, int, int> value)
    {
        int spp = SamplesPerPixel;
        Samples = new ushort[Width * Height * spp];
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                for (int s = 0; s < spp; s++)
                {
                    Samples[(((y * Width) + x) * spp) + s] = (ushort)value(x, y, s);
                }
            }
        }

        return this;
    }

    public byte[] Build()
    {
        TiffBuilder.Ifd ifd0 = new();
        ifd0.Tags.Add(TiffTag.Long(254, 1));
        ifd0.Tags.Add(TiffTag.Ascii(271, Make));
        ifd0.Tags.Add(TiffTag.Ascii(272, Model));
        ifd0.Tags.Add(TiffTag.Short(274, Orientation));
        ifd0.Tags.Add(TiffTag.Bytes(50706, 1, 4, 0, 0));
        ifd0.Tags.Add(TiffTag.Ascii(50708, "Egami Test Camera"));
        if (ColorMatrix is not null)
        {
            ifd0.Tags.Add(TiffTag.SRational(50721, ColorMatrix));
            ifd0.Tags.Add(TiffTag.Short(50778, Illuminant));
        }

        ifd0.Tags.Add(TiffTag.Rational(50728, Neutral));
        if (BaselineExposure != 0)
        {
            ifd0.Tags.Add(TiffTag.SRational(50730, BaselineExposure));
        }

        ifd0.Tags.AddRange(ExtraIfd0);
        if (Preview is not null)
        {
            ifd0.Tags.Add(TiffTag.Long(256, PreviewSize.Width));
            ifd0.Tags.Add(TiffTag.Long(257, PreviewSize.Height));
            ifd0.Tags.Add(TiffTag.Short(258, 8, 8, 8));
            ifd0.Tags.Add(TiffTag.Short(259, 7));
            ifd0.Tags.Add(TiffTag.Short(262, 6));
            ifd0.Tags.Add(TiffTag.Short(277, 3));
            ifd0.Units.Add(Preview);
        }

        ifd0.SubIfds.Add(RawIfd());
        if (Exif.Count > 0)
        {
            TiffBuilder.Ifd exif = new();
            exif.Tags.AddRange(Exif);
            ifd0.Exif = exif;
        }
        return TiffBuilder.Write(ifd0, BigEndian);
    }

    private TiffBuilder.Ifd RawIfd()
    {
        TiffBuilder.Ifd raw = new();
        int spp = SamplesPerPixel;
        bool isFloat = FloatSamples is not null;
        raw.Tags.Add(TiffTag.Long(254, 0));
        raw.Tags.Add(TiffTag.Long(256, Width));
        raw.Tags.Add(TiffTag.Long(257, Height));
        raw.Tags.Add(TiffTag.Short(258, [.. Enumerable.Repeat(Bits, spp)]));
        raw.Tags.Add(TiffTag.Short(259, Compression));
        raw.Tags.Add(TiffTag.Short(262, LinearRaw ? 34892 : 32803));
        raw.Tags.Add(TiffTag.Short(277, spp));
        if (isFloat)
        {
            raw.Tags.Add(TiffTag.Short(339, [.. Enumerable.Repeat(3, spp)]));
        }

        if (Predictor != 1)
        {
            raw.Tags.Add(TiffTag.Short(317, Predictor));
        }

        if (!LinearRaw)
        {
            int side = (int)Math.Sqrt(Cfa.Length);
            raw.Tags.Add(TiffTag.Short(33421, side, side));
            raw.Tags.Add(TiffTag.Bytes(33422, Cfa));
            raw.Tags.Add(TiffTag.Bytes(50710, 0, 1, 2));
            raw.Tags.Add(TiffTag.Short(50711, 1));
        }

        raw.Tags.Add(TiffTag.Short(50713, BlackRepeat));
        raw.Tags.Add(TiffTag.Rational(50714, Black));
        raw.Tags.Add(TiffTag.Long(50717, [.. Enumerable.Repeat((long)White, spp)]));
        if (ActiveArea is not null)
        {
            raw.Tags.Add(TiffTag.Long(50829, [.. ActiveArea.Select(v => (long)v)]));
        }

        if (Crop is not null)
        {
            raw.Tags.Add(TiffTag.Rational(50719, Crop[0], Crop[1]));
            raw.Tags.Add(TiffTag.Rational(50720, Crop[2], Crop[3]));
        }

        if (Linearization is not null)
        {
            raw.Tags.Add(TiffTag.Short(50712, [.. Linearization.Select(v => (int)v)]));
        }

        if (NoiseProfile is not null)
        {
            raw.Tags.Add(TiffTag.Double(51041, NoiseProfile));
        }

        raw.Tags.AddRange(ExtraRaw);

        foreach ((int tag, byte[]? list) in new[] { (51008, Opcodes1), (51009, Opcodes2), (51022, Opcodes3) })
        {
            if (list is not null)
            {
                raw.Tags.Add(TiffTag.Undefined(tag, list));
            }
        }

        int unitW = Tile > 0 ? Tile : Width;
        int unitH = Tile > 0 ? Tile : RowsPerStrip > 0 ? RowsPerStrip : Height;
        if (Tile > 0)
        {
            raw.Tags.Add(TiffTag.Long(322, Tile));
            raw.Tags.Add(TiffTag.Long(323, Tile));
            raw.OffsetsTag = 324;
            raw.CountsTag = 325;
        }
        else
        {
            raw.Tags.Add(TiffTag.Long(278, unitH));
        }

        for (int uy = 0; uy < Height; uy += unitH)
        {
            for (int ux = 0; ux < Width; ux += unitW)
            {
                raw.Units.Add(Unit(ux, uy, unitW, Tile > 0 ? unitH : Math.Min(unitH, Height - uy)));
            }
        }

        return raw;
    }

    /// <summary>Encodes one strip or tile; samples outside the image repeat the edge (tiles are padded).</summary>
    private byte[] Unit(int x0, int y0, int w, int h)
    {
        int spp = SamplesPerPixel;
        ushort[] samples = new ushort[w * h * spp];
        float[] floats = new float[w * h * spp];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int sx = Math.Min(x0 + x, Width - 1), sy = Math.Min(y0 + y, Height - 1);
                for (int s = 0; s < spp; s++)
                {
                    int from = (((sy * Width) + sx) * spp) + s;
                    samples[(((y * w) + x) * spp) + s] = Samples.Length > 0 ? Samples[from] : (ushort)0;
                    floats[(((y * w) + x) * spp) + s] = FloatSamples?[from] ?? 0;
                }
            }
        }

        if (Compression == 7)
        {
            return LosslessJpegEncoder.Encode(samples, w, h, spp, Bits);
        }

        byte[] packed = FloatSamples is not null ? PackFloats(floats, w, h, spp) : Pack(samples, w, h, spp);
        if (Compression == 8)
        {
            using MemoryStream output = new();
            using (ZLibStream z = new(output, CompressionLevel.Optimal))
            {
                z.Write(packed);
            }

            return output.ToArray();
        }

        return packed;
    }

    private byte[] Pack(ushort[] samples, int w, int h, int spp)
    {
        int rowSamples = w * spp;
        int rowBytes = ((rowSamples * Bits) + 7) / 8;
        byte[] data = new byte[rowBytes * h];
        for (int y = 0; y < h; y++)
        {
            Span<byte> row = data.AsSpan(y * rowBytes, rowBytes);
            ReadOnlySpan<ushort> values = samples.AsSpan(y * rowSamples, rowSamples);
            if (Bits == 16)
            {
                ushort[] diffs = [.. values];
                if (Predictor == 2)
                {
                    for (int i = rowSamples - 1; i >= spp; i--)
                    {
                        diffs[i] = (ushort)(diffs[i] - diffs[i - spp]);
                    }
                }

                for (int i = 0; i < rowSamples; i++)
                {
                    if (BigEndian)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(row[(i * 2)..], diffs[i]);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(row[(i * 2)..], diffs[i]);
                    }
                }

                continue;
            }

            if (Bits == 8)
            {
                for (int i = 0; i < rowSamples; i++)
                {
                    row[i] = (byte)values[i];
                }

                continue;
            }

            long bit = 0;
            foreach (ushort v in values)
            {
                for (int b = Bits - 1; b >= 0; b--, bit++)
                {
                    if (((v >> b) & 1) != 0)
                    {
                        row[(int)(bit >> 3)] |= (byte)(0x80 >> (int)(bit & 7));
                    }
                }
            }
        }

        return data;
    }

    /// <summary>Packs floats with the floating-point predictor of TIFF Technical Note 3 when it is asked for, else in file order.</summary>
    private byte[] PackFloats(float[] values, int w, int h, int spp)
    {
        int bytesPer = Bits / 8;
        int n = w * spp;
        byte[] data = new byte[n * bytesPer * h];
        for (int y = 0; y < h; y++)
        {
            Span<byte> row = data.AsSpan(y * n * bytesPer, n * bytesPer);
            byte[] be = new byte[n * bytesPer];
            for (int i = 0; i < n; i++)
            {
                float v = values[(y * n) + i];
                if (bytesPer == 2)
                {
                    BinaryPrimitives.WriteHalfBigEndian(be.AsSpan(i * 2), (Half)v);
                }
                else
                {
                    BinaryPrimitives.WriteSingleBigEndian(be.AsSpan(i * 4), v);
                }
            }

            if (Predictor == 3)
            {
                for (int i = 0; i < n; i++)
                {
                    for (int k = 0; k < bytesPer; k++)
                    {
                        row[(k * n) + i] = be[(i * bytesPer) + k];
                    }
                }

                for (int i = row.Length - 1; i >= spp; i--)
                {
                    row[i] -= row[i - spp];
                }
            }
            else
            {
                for (int i = 0; i < n; i++)
                {
                    for (int k = 0; k < bytesPer; k++)
                    {
                        row[(i * bytesPer) + k] = BigEndian ? be[(i * bytesPer) + k] : be[(i * bytesPer) + bytesPer - 1 - k];
                    }
                }
            }
        }

        return data;
    }
}

/// <summary>
/// A lossless JPEG encoder for the tests (ITU-T T.81 process 14): predictor 1 (predictor 2 on the first column), one
/// Huffman table in which every difference category has a 5-bit code, components interleaved, the sample count split into
/// two components when it is even so the decoder's multi-component path is exercised.
/// </summary>
internal static class LosslessJpegEncoder
{
    public static byte[] Encode(ushort[] samples, int width, int height, int spp, int precision, int predictor = 1, int restart = 0)
    {
        int components = (width * spp) % 2 == 0 && spp == 1 ? 2 : spp;
        int columns = width * spp / components;
        List<byte> o = [0xFF, 0xD8];
        o.AddRange([0xFF, 0xC3, 0, (byte)(8 + (3 * components)), (byte)precision, (byte)(height >> 8), (byte)height, (byte)(columns >> 8), (byte)columns, (byte)components]);
        for (int c = 0; c < components; c++)
        {
            o.AddRange([(byte)(c + 1), 0x11, 0]);
        }

        o.AddRange([0xFF, 0xC4, 0, 3 + 16 + 17, 0x00]);
        for (int i = 0; i < 16; i++)
        {
            o.Add((byte)(i == 4 ? 17 : 0));
        }

        for (int i = 0; i <= 16; i++)
        {
            o.Add((byte)i);
        }

        if (restart > 0)
        {
            o.AddRange([0xFF, 0xDD, 0, 4, (byte)(restart >> 8), (byte)restart]);
        }

        o.AddRange([0xFF, 0xDA, 0, (byte)(6 + (2 * components)), (byte)components]);
        for (int c = 0; c < components; c++)
        {
            o.AddRange([(byte)(c + 1), 0x00]);
        }

        o.AddRange([(byte)predictor, 0, 0]);
        List<byte> bits = [];
        int buffer = 0, count = 0;
        void Put(int value, int n)
        {
            for (int b = n - 1; b >= 0; b--)
            {
                buffer = (buffer << 1) | ((value >> b) & 1);
                if (++count == 8)
                {
                    bits.Add((byte)buffer);
                    if (buffer == 0xFF)
                    {
                        bits.Add(0);
                    }

                    buffer = 0;
                    count = 0;
                }
            }
        }

        void Flush()
        {
            while (count != 0)
            {
                Put(1, 1);
            }
        }

        int stride = columns * components;
        int mcu = 0, rst = 0;
        bool reset = true, firstLine = true;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < columns; x++)
            {
                if (restart > 0 && mcu > 0 && mcu % restart == 0)
                {
                    Flush();
                    bits.AddRange([0xFF, (byte)(0xD0 + (rst++ & 7))]);
                    reset = true;
                    firstLine = true;
                }

                mcu++;
                for (int c = 0; c < components; c++)
                {
                    int i = (y * stride) + (x * components) + c;
                    int value = samples[i];
                    int prediction;
                    if (reset)
                    {
                        prediction = 1 << (precision - 1);
                    }
                    else if (firstLine)
                    {
                        prediction = samples[i - components];
                    }
                    else if (x == 0)
                    {
                        prediction = samples[i - stride];
                    }
                    else
                    {
                        int a = samples[i - components], b = samples[i - stride], cc = samples[i - stride - components];
                        prediction = predictor switch
                        {
                            1 => a,
                            2 => b,
                            3 => cc,
                            4 => a + b - cc,
                            5 => a + ((b - cc) >> 1),
                            6 => b + ((a - cc) >> 1),
                            _ => (a + b) >> 1,
                        };
                    }

                    int diff = (short)(ushort)(value - prediction);
                    if (((value - prediction) & 0xFFFF) == 0x8000)
                    {
                        Put(16, 5);
                        continue;
                    }

                    int category = diff == 0 ? 0 : 32 - int.LeadingZeroCount(Math.Abs(diff));
                    Put(category, 5);
                    if (category > 0)
                    {
                        Put(diff < 0 ? diff - 1 : diff, category);
                    }
                }

                reset = false;
            }

            firstLine = false;
        }

        Flush();
        o.AddRange(bits);
        o.AddRange([0xFF, 0xD9]);
        return [.. o];
    }
}

/// <summary>Builds DNG opcode lists, big-endian as the specification says.</summary>
internal sealed class OpcodeList
{
    private readonly List<byte[]> _opcodes = [];

    public OpcodeList Add(int id, Action<OpcodeParams> parameters, int flags = 0)
    {
        OpcodeParams p = new();
        parameters(p);
        byte[] data = p.ToArray();
        byte[] head = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(head, (uint)id);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(4), 0x01030000);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(8), (uint)flags);
        BinaryPrimitives.WriteUInt32BigEndian(head.AsSpan(12), (uint)data.Length);
        _opcodes.Add([.. head, .. data]);
        return this;
    }

    public byte[] Build()
    {
        byte[] count = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(count, (uint)_opcodes.Count);
        return [.. count, .. _opcodes.SelectMany(o => o)];
    }
}

internal sealed class OpcodeParams
{
    private readonly List<byte> _bytes = [];

    public OpcodeParams U32(params long[] values)
    {
        foreach (long v in values)
        {
            byte[] b = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v);
            _bytes.AddRange(b);
        }

        return this;
    }

    public OpcodeParams U16(params int[] values)
    {
        foreach (int v in values)
        {
            byte[] b = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
            _bytes.AddRange(b);
        }

        return this;
    }

    public OpcodeParams F32(params float[] values)
    {
        foreach (float v in values)
        {
            byte[] b = new byte[4];
            BinaryPrimitives.WriteSingleBigEndian(b, v);
            _bytes.AddRange(b);
        }

        return this;
    }

    public OpcodeParams F64(params double[] values)
    {
        foreach (double v in values)
        {
            byte[] b = new byte[8];
            BinaryPrimitives.WriteDoubleBigEndian(b, v);
            _bytes.AddRange(b);
        }

        return this;
    }

    /// <summary>Writes an area: top, left, bottom, right, plane, planes, row pitch, column pitch.</summary>
    public OpcodeParams Area(int top, int left, int bottom, int right, int plane = 0, int planes = 1, int rowPitch = 1, int colPitch = 1)
    {
        return U32(top, left, bottom, right, plane, planes, rowPitch, colPitch);
    }

    public byte[] ToArray() => [.. _bytes];
}
