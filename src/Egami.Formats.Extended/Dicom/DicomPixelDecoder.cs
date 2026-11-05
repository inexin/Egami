using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using Egami.Formats.Extended.JpegLs;
using Egami.Formats.Jpeg;
using Egami.Formats.Jpeg2000;
using Egami.Formats.Jxl;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// One decoded frame: <see cref="DicomImageInfo.Rows"/> by <see cref="DicomImageInfo.Columns"/> pixels of
/// <see cref="Components"/> stored values each, interleaved, signed values with their sign.
/// </summary>
internal sealed class DicomSamples(int components, int[] samples, bool isRgb)
{
    /// <summary>Gets the samples a pixel has: 1 or 3.</summary>
    public int Components { get; } = components;

    /// <summary>Gets the samples, row after row, each pixel's components together.</summary>
    public int[] Samples { get; } = samples;

    /// <summary>Gets whether a three-sample frame is already RGB, whatever the photometric interpretation says (a JPEG 2000 component transform undone, JPEG XL).</summary>
    public bool IsRgb { get; } = isRgb;
}

/// <summary>
/// Decodes the pixel data of a frame (PS3.5 section 8 and annex A.4): native data at 1, 8, 16 or 32 bits allocated, planar or
/// interleaved, with the stored bits taken from under the high bit and sign-extended; encapsulated data split into frames by
/// the Extended or Basic Offset Table, or by where each codestream starts, then decoded by RLE (annex G), deflate, JPEG,
/// JPEG-LS, JPEG 2000 (High-Throughput JPEG 2000 included) or JPEG XL.
/// </summary>
internal sealed class DicomPixelDecoder
{
    private readonly DicomImageInfo _info;
    private readonly EgamiInput _input;
    private readonly ReaderOptions _options;
    private readonly Action<string, string> _warn;
    private readonly bool _strict;
    private int[]? _frameStarts;

    public DicomPixelDecoder(DicomImageInfo info, EgamiInput input, ReaderOptions options, Action<string, string> warn)
    {
        _info = info;
        _input = input;
        _options = options;
        _warn = warn;
        _strict = options.Recovery == RecoveryMode.Strict;
        Syntax = info.File.TransferSyntax;
        Codec = CodecFor(Syntax.Uid);
    }

    /// <summary>The ways frames are coded.</summary>
    internal enum DicomCodec
    {
        Native,
        EncapsulatedNative,
        Deflate,
        Rle,
        Jpeg,
        JpegLs,
        Jpeg2000,
        JpegXl,
        Unsupported,
    }

    public DicomTransferSyntax Syntax { get; }

    public DicomCodec Codec { get; }

    /// <summary>Returns how a transfer syntax codes its frames.</summary>
    internal static DicomCodec CodecFor(string uid)
    {
        switch (uid)
        {
            case "1.2.840.10008.1.2":
            case "1.2.840.10008.1.2.1":
            case "1.2.840.10008.1.2.1.99":
            case "1.2.840.10008.1.2.2":
                return DicomCodec.Native;
            case "1.2.840.10008.1.2.1.98":
                return DicomCodec.EncapsulatedNative;
            case "1.2.840.10008.1.2.8.1":
                return DicomCodec.Deflate;
            case "1.2.840.10008.1.2.5":
                return DicomCodec.Rle;
            case "1.2.840.10008.1.2.4.80":
            case "1.2.840.10008.1.2.4.81":
                return DicomCodec.JpegLs;
            case "1.2.840.10008.1.2.4.90":
            case "1.2.840.10008.1.2.4.91":
            case "1.2.840.10008.1.2.4.92":
            case "1.2.840.10008.1.2.4.93":
            case "1.2.840.10008.1.2.4.201":
            case "1.2.840.10008.1.2.4.202":
            case "1.2.840.10008.1.2.4.203":
                return DicomCodec.Jpeg2000;
            case "1.2.840.10008.1.2.4.110":
            case "1.2.840.10008.1.2.4.111":
            case "1.2.840.10008.1.2.4.112":
                return DicomCodec.JpegXl;
        }

        // JPEG processes 1 to 29 (.50 to .66) and SV1 (.70).
        if (uid.StartsWith("1.2.840.10008.1.2.4.", StringComparison.Ordinal)
            && int.TryParse(uid.AsSpan(20), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int process)
            && process is (>= 50 and <= 66) or 70)
        {
            return DicomCodec.Jpeg;
        }

        return DicomCodec.Unsupported;
    }

    /// <summary>Throws <see cref="NotSupportedException"/> when the transfer syntax's frames cannot be decoded.</summary>
    public void CheckSupported()
    {
        _info.CheckSupported();
        if (Codec == DicomCodec.Unsupported)
        {
            throw new NotSupportedException($"Pixel data in {Syntax} is not decoded: MPEG, H.264, HEVC and JPIP transfer syntaxes are not supported.");
        }
    }

    /// <summary>Decodes frame <paramref name="frame"/>.</summary>
    public DicomSamples Decode(int frame)
    {
        CheckSupported();
        int components = _info.SamplesPerPixel;
        DicomElement pixels = PixelData();
        if (Codec is DicomCodec.JpegLs or DicomCodec.Jpeg or DicomCodec.Jpeg2000 or DicomCodec.JpegXl)
        {
            ReadOnlyMemory<byte> codestream = FrameData(Fragments(pixels), frame, pixels.Offset);
            CodecFrame decoded = Codec switch
            {
                DicomCodec.JpegLs => JpegLsSamples(codestream),
                DicomCodec.Jpeg => Codestream(codestream, JpegFormat.Info),
                DicomCodec.Jpeg2000 => Codestream(codestream, Jpeg2000Format.Info),
                _ => Codestream(codestream, JxlFormat.Info),
            };
            return FromCodec(decoded, pixels.Offset);
        }

        int[] samples = Allocate(components);
        if (Codec == DicomCodec.Native)
        {
            if (pixels.Fragments is not null)
            {
                throw _input.Corrupt(pixels.Offset, $"The pixel data is encapsulated, but {Syntax} stores it native.");
            }

            UnpackNative(pixels.RawValue.Span, Syntax.IsBigEndian, FrameBits() * frame, samples, pixels.Offset);
            return new DicomSamples(components, samples, false);
        }

        ReadOnlyMemory<byte> data = FrameData(Fragments(pixels), frame, pixels.Offset);
        switch (Codec)
        {
            case DicomCodec.EncapsulatedNative:
                UnpackNative(data.Span, false, 0, samples, pixels.Offset);
                return new DicomSamples(components, samples, false);
            case DicomCodec.Deflate:
                UnpackNative(Inflate(data), false, 0, samples, pixels.Offset);
                return new DicomSamples(components, samples, false);
            default:
                DicomRle.Decode(data.Span, _info, samples, Damaged);
                return new DicomSamples(components, samples, false);
        }
    }

    /// <summary>
    /// Decodes an 8-bit JPEG frame coded in YBR_FULL or YBR_FULL_422 straight to RGB through the JPEG reader, whose JFIF
    /// conversion is the full-range one PS3.3 C.7.6.3.1.2 gives, or returns <see langword="null"/> for any other frame. With
    /// <paramref name="clean"/>, a frame the codec recovered from damage gives <see langword="null"/> too.
    /// </summary>
    public Raster<Pixels.Rgb8>? DecodeJpegRgb(int frame, bool clean = false)
    {
        return Codec == DicomCodec.Jpeg && _info.SamplesPerPixel == 3 && _info.BitsStored <= 8 && _info.Photometric is "YBR_FULL" or "YBR_FULL_422"
            ? DecodeJpeg<Pixels.Rgb8>(JpegFrame(frame), clean)
            : null;
    }

    /// <summary>
    /// Decodes a monochrome JPEG frame whose precision is Bits Stored through the JPEG reader and maps it in place to the levels
    /// <paramref name="display"/> shows, or returns <see langword="null"/> for any other frame or one the codec recovered from damage.
    /// </summary>
    public Raster? DecodeJpegGray(int frame, DicomDisplay display, Action<string, string> warn)
    {
        if (Codec != DicomCodec.Jpeg || !_info.IsMonochrome || _info.SamplesPerPixel != 1)
        {
            return null;
        }

        ReadOnlyMemory<byte> codestream = JpegFrame(frame);
        int precision = JpegPrecision(codestream.Span);
        bool wide = display.Layout.BitsPerChannel > 8;
        if (precision != _info.BitsStored || wide != precision > 8)
        {
            return null;
        }

        return wide ? MapGray<Pixels.Gray16, ushort>(codestream, precision, display, warn) : MapGray<Pixels.Gray8, byte>(codestream, precision, display, warn);
    }

    private Raster<TPixel>? MapGray<TPixel, TValue>(ReadOnlyMemory<byte> codestream, int precision, DicomDisplay display, Action<string, string> warn)
        where TPixel : unmanaged, Pixels.IPixelType<TPixel>
        where TValue : unmanaged, System.Numerics.IBinaryInteger<TValue>
    {
        Raster<TPixel>? raster = DecodeJpeg<TPixel>(codestream, clean: true);
        if (raster is null)
        {
            return null;
        }

        // The reader widens the codestream's values to the pixel type's range; each decoded value maps back to its stored value.
        int top = typeof(TValue) == typeof(byte) ? 255 : 65535;
        double scale = ((1L << precision) - 1) / (double)top;
        int[] stored = new int[top + 1];
        for (int v = 0; v <= top; v++)
        {
            int value = (int)((v * scale) + 0.5);
            stored[v] = _info.IsSigned && value >= 1 << (precision - 1) ? value - (1 << precision) : value;
        }

        (int Min, int Max) Range()
        {
            bool[] seen = new bool[top + 1];
            for (int y = 0; y < raster.Height; y++)
            {
                foreach (TValue v in MemoryMarshal.Cast<TPixel, TValue>(raster.Row(y)))
                {
                    seen[int.CreateTruncating(v)] = true;
                }
            }

            int min = int.MaxValue, most = int.MinValue;
            for (int v = 0; v <= top; v++)
            {
                if (seen[v])
                {
                    min = Math.Min(min, stored[v]);
                    most = Math.Max(most, stored[v]);
                }
            }

            return min > most ? (0, 0) : (min, most);
        }

        int first = _info.IsSigned ? -(1 << (precision - 1)) : 0;
        ushort[] levels = display.Levels(first, 1 << precision, Range, warn);
        TValue[] table = new TValue[top + 1];
        for (int v = 0; v <= top; v++)
        {
            table[v] = TValue.CreateTruncating(levels[stored[v] - first]);
        }

        for (int y = 0; y < raster.Height; y++)
        {
            Map(MemoryMarshal.Cast<TPixel, TValue>(raster.Row(y)), table);
        }

        return raster;
    }

    private static void Map<TValue>(Span<TValue> row, TValue[] table)
        where TValue : unmanaged, System.Numerics.IBinaryInteger<TValue>
    {
        ref TValue start = ref MemoryMarshal.GetReference(row);
        ref TValue entries = ref MemoryMarshal.GetArrayDataReference(table);
        for (int x = 0; x < row.Length; x++)
        {
            ref TValue value = ref Unsafe.Add(ref start, x);
            value = Unsafe.Add(ref entries, int.CreateTruncating(value));
        }
    }

    private ReadOnlyMemory<byte> JpegFrame(int frame)
    {
        CheckSupported();
        DicomElement pixels = PixelData();
        return FrameData(Fragments(pixels), frame, pixels.Offset);
    }

    /// <summary>Loads a JPEG codestream of the image's size, or returns <see langword="null"/>.</summary>
    private Raster<TPixel>? DecodeJpeg<TPixel>(ReadOnlyMemory<byte> codestream, bool clean)
        where TPixel : unmanaged, Pixels.IPixelType<TPixel>
    {
        Raster<TPixel> raster = Raster.Load<TPixel>(codestream, JpegFormat.Info, _input.Context, new ReaderOptions { Recovery = _options.Recovery, ReadMetadata = false });
        if (raster.Width != _info.Columns || raster.Height != _info.Rows || (clean && raster.Metadata.Warnings.Count > 0))
        {
            raster.Dispose();
            return null;
        }

        foreach (EgamiDiagnostic warning in raster.Metadata.Warnings)
        {
            _warn("Codestream", $"{warning.Id}: {warning.Message}");
        }

        return raster;
    }

    /// <summary>Returns the sample precision of a JPEG codestream's first frame header (ITU-T T.81 B.2.2), or 0.</summary>
    private static int JpegPrecision(ReadOnlySpan<byte> data)
    {
        int i = 2;
        while (i + 4 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                return 0;
            }

            byte marker = data[i + 1];
            if (marker == 0xFF)
            {
                i++;
                continue;
            }

            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return data[i + 4];
            }

            if (marker is 0x01 or (>= 0xD0 and <= 0xD9))
            {
                i += 2;
                continue;
            }

            i += 2 + BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
        }

        return 0;
    }

    private DicomElement PixelData()
    {
        return _info.File.Dataset[DicomTag.PixelData] ?? throw new UnsupportedFormatException("This DICOM file has no Pixel Data (7FE0,0010).");
    }

    private DicomFragments Fragments(DicomElement pixels)
    {
        return pixels.Fragments ?? throw _input.Corrupt(pixels.Offset, $"The pixel data is native, but {Syntax} encapsulates it.");
    }

    /// <summary>A codec's decoded frame.</summary>
    private readonly record struct CodecFrame(int Width, int Height, int Components, bool IsSigned, ReadOnlyMemory<int> Samples);

    /// <summary>Returns the bits one native frame takes.</summary>
    private long FrameBits()
    {
        long values = _info.IsHorizontal422
            ? (long)_info.Rows * ((_info.Columns + 1) / 2) * 4
            : (long)_info.Rows * _info.Columns * _info.SamplesPerPixel;
        return values * _info.BitsAllocated;
    }

    private int[] Allocate(int components)
    {
        long count = (long)_info.Rows * _info.Columns * components;
        _input.CheckFrameSize(_info.Columns, _info.Rows);
        _input.CheckMemory(count * sizeof(int));
        if (count > Array.MaxLength)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), Array.MaxLength, count);
        }

        return new int[count];
    }

    private void Damaged(string name, string problem)
    {
        if (_strict)
        {
            throw _input.Corrupt(0, problem);
        }

        _warn(name, problem);
    }

    /// <summary>
    /// Unpacks a native frame starting <paramref name="bitOffset"/> bits into <paramref name="data"/>: 1-bit data packed with the
    /// first pixel in the lowest bit, 8, 16 or 32-bit words in the file's byte order; planar frames (Planar Configuration 1) plane
    /// after plane; YBR_FULL_422 and YBR_PARTIAL_422 as two luma samples then the shared Cb and Cr, the chroma given to both.
    /// </summary>
    private void UnpackNative(ReadOnlySpan<byte> data, bool bigEndian, long bitOffset, int[] samples, long offset)
    {
        int bits = _info.BitsAllocated;
        long needed = FrameBits();
        long available = ((long)data.Length * 8) - bitOffset;
        if (available < needed)
        {
            string problem = available <= 0
                ? $"The pixel data holds no frame at bit {bitOffset}: it is {data.Length} bytes long."
                : $"The pixel data ends {(needed - available + 7) / 8} bytes into the frame's {(needed + 7) / 8}; the rest is left 0.";
            if (_strict)
            {
                throw _input.Corrupt(offset, problem);
            }

            _warn("PixelDataShort", problem);
        }

        int shift = _info.HighBit + 1 - _info.BitsStored;
        int stored = _info.BitsStored;
        bool signed = _info.IsSigned;
        int count = samples.Length;
        int components = _info.SamplesPerPixel;
        int pixels = count / components;
        long values = Math.Max(0, Math.Min(needed, available) / bits);

        if (_info.IsHorizontal422)
        {
            int width = _info.Columns;
            int pairs = (width + 1) / 2;
            for (int y = 0; y < _info.Rows; y++)
            {
                for (int p = 0; p < pairs; p++)
                {
                    long at = (((long)y * pairs) + p) * 4;
                    int y0 = Value(data, bigEndian, bitOffset, at, values, bits, shift, stored, signed);
                    int y1 = Value(data, bigEndian, bitOffset, at + 1, values, bits, shift, stored, signed);
                    int cb = Value(data, bigEndian, bitOffset, at + 2, values, bits, shift, stored, signed);
                    int cr = Value(data, bigEndian, bitOffset, at + 3, values, bits, shift, stored, signed);
                    int x = p * 2;
                    int target = ((y * width) + x) * 3;
                    samples[target] = y0;
                    samples[target + 1] = cb;
                    samples[target + 2] = cr;
                    if (x + 1 < width)
                    {
                        samples[target + 3] = y1;
                        samples[target + 4] = cb;
                        samples[target + 5] = cr;
                    }
                }
            }

            return;
        }

        bool planar = components > 1 && _info.PlanarConfiguration == 1;
        if (!planar && bitOffset % 8 == 0 && bits >= 8 && values >= count)
        {
            Fast(data[(int)(bitOffset / 8)..], bigEndian, bits, shift, stored, signed, samples);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            long at = planar ? ((long)(i % components) * pixels) + (i / components) : i;
            samples[i] = Value(data, bigEndian, bitOffset, at, values, bits, shift, stored, signed);
        }
    }

    /// <summary>Reads value <paramref name="at"/> of a frame, or 0 past what the data holds.</summary>
    private static int Value(ReadOnlySpan<byte> data, bool bigEndian, long bitOffset, long at, long values, int bits, int shift, int stored, bool signed)
    {
        if (at >= values)
        {
            return 0;
        }

        long bit = bitOffset + (at * bits);
        uint raw = bits switch
        {
            1 => (uint)(data[(int)(bit >> 3)] >> (int)(bit & 7)) & 1,
            8 => data[(int)(bit >> 3)],
            16 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(data[(int)(bit >> 3)..]) : BinaryPrimitives.ReadUInt16LittleEndian(data[(int)(bit >> 3)..]),
            _ => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(data[(int)(bit >> 3)..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[(int)(bit >> 3)..]),
        };
        return Stored(raw, shift, stored, signed);
    }

    /// <summary>Takes the stored bits from under the high bit, sign-extended when signed; unsigned 32-bit values above <see cref="int.MaxValue"/> are clamped.</summary>
    internal static int Stored(uint raw, int shift, int stored, bool signed)
    {
        uint value = raw >> shift;
        if (stored < 32)
        {
            value &= (1u << stored) - 1;
        }

        if (signed)
        {
            int unused = 32 - stored;
            return ((int)value << unused) >> unused;
        }

        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    /// <summary>Unpacks a whole interleaved frame of 8, 16 or 32-bit words.</summary>
    private static void Fast(ReadOnlySpan<byte> data, bool bigEndian, int bits, int shift, int stored, bool signed, int[] samples)
    {
        Span<int> target = samples;
        if (bits == 8)
        {
            if (shift == 0 && stored == 8 && !signed)
            {
                for (int i = 0; i < target.Length; i++)
                {
                    target[i] = data[i];
                }

                return;
            }

            for (int i = 0; i < target.Length; i++)
            {
                target[i] = Stored(data[i], shift, stored, signed);
            }
        }
        else if (bits == 16)
        {
            if (!bigEndian && shift == 0 && stored == 16)
            {
                ReadOnlySpan<ushort> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(data[..(target.Length * 2)]);
                if (signed)
                {
                    for (int i = 0; i < target.Length; i++)
                    {
                        target[i] = (short)words[i];
                    }
                }
                else
                {
                    for (int i = 0; i < target.Length; i++)
                    {
                        target[i] = words[i];
                    }
                }

                return;
            }

            for (int i = 0; i < target.Length; i++)
            {
                ReadOnlySpan<byte> at = data[(i * 2)..];
                uint raw = bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(at) : BinaryPrimitives.ReadUInt16LittleEndian(at);
                target[i] = Stored(raw, shift, stored, signed);
            }
        }
        else
        {
            for (int i = 0; i < target.Length; i++)
            {
                ReadOnlySpan<byte> at = data[(i * 4)..];
                uint raw = bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(at) : BinaryPrimitives.ReadUInt32LittleEndian(at);
                target[i] = Stored(raw, shift, stored, signed);
            }
        }
    }

    /// <summary>Inflates a deflated frame (Deflated Image Frame Compression, PS3.5 section 8.2.15) to its native bytes.</summary>
    private byte[] Inflate(ReadOnlyMemory<byte> data)
    {
        long size = (FrameBits() + 7) / 8;
        _input.CheckMemory(size);
        byte[] frame = new byte[size];
        int length = 0;
        try
        {
            using MemoryStream packed = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(data, out ArraySegment<byte> segment)
                ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, false)
                : new MemoryStream(data.ToArray(), false);
            using DeflateStream inflater = new(packed, CompressionMode.Decompress);
            int read;
            while (length < frame.Length && (read = inflater.Read(frame, length, frame.Length - length)) > 0)
            {
                length += read;
            }
        }
        catch (InvalidDataException e)
        {
            Damaged("Deflate", $"The deflated frame is damaged ({e.Message}); what inflated was kept.");
        }

        if (length < frame.Length)
        {
            Array.Resize(ref frame, length);
        }

        return frame;
    }

    /// <summary>Decodes a frame's JPEG, JPEG 2000 or JPEG XL codestream to its stored samples.</summary>
    private CodecFrame Codestream(ReadOnlyMemory<byte> data, FormatInfo format)
    {
        SampleImage image = SampleImage.Load(data, format, _input.Context, new ReaderOptions { Recovery = _options.Recovery });
        foreach (EgamiDiagnostic warning in image.Metadata.Warnings)
        {
            _warn("Codestream", $"{warning.Id}: {warning.Message}");
        }

        return new CodecFrame(image.Width, image.Height, image.Components.Count, image.Components.Count > 0 && image.Components[0].IsSigned, image.Samples);
    }

    /// <summary>Decodes a frame's JPEG-LS codestream to its stored samples.</summary>
    private CodecFrame JpegLsSamples(ReadOnlyMemory<byte> data)
    {
        (int width, int height, int components, int[] samples) = JpegLsStoredSamples.Decode(data, _input, _strict, _warn);
        return new CodecFrame(width, height, components, false, samples);
    }

    /// <summary>
    /// Takes a codec's samples into the frame: cropped or padded to Rows by Columns, and the stored bits of unsigned codestream
    /// samples sign-extended when Pixel Representation says the data is signed (PS3.5 section 8.2).
    /// </summary>
    private DicomSamples FromCodec(CodecFrame image, long offset)
    {
        int components = _info.SamplesPerPixel;
        if (image.Components < components)
        {
            throw _input.Corrupt(offset, $"The frame's codestream has {image.Components} components; Samples per Pixel is {components}.");
        }

        if (image.Width != _info.Columns || image.Height != _info.Rows)
        {
            Damaged("FrameSize", $"The frame's codestream is {image.Width}×{image.Height}; Rows and Columns say {_info.Columns}×{_info.Rows}. It was cropped or padded to fit.");
        }

        int stored = _info.BitsStored;
        bool extend = _info.IsSigned && !image.IsSigned;
        int mask = stored >= 32 ? -1 : (1 << stored) - 1;
        int unused = 32 - stored;
        int have = image.Components;

        // The codec's own samples serve as they are when they already fit.
        bool rgb = Codec == DicomCodec.JpegXl;
        if (have == components && !extend && image.Width == _info.Columns && image.Height == _info.Rows
            && MemoryMarshal.TryGetArray(image.Samples, out ArraySegment<int> whole) && whole.Offset == 0 && whole.Count == whole.Array!.Length)
        {
            return new DicomSamples(components, whole.Array, rgb);
        }

        int[] samples = Allocate(components);
        ReadOnlySpan<int> source = image.Samples.Span;
        int width = Math.Min(image.Width, _info.Columns);
        int height = Math.Min(image.Height, _info.Rows);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<int> row = source.Slice(y * image.Width * have, image.Width * have);
            Span<int> target = samples.AsSpan(y * _info.Columns * components, _info.Columns * components);
            if (have == components && !extend)
            {
                row[..(width * components)].CopyTo(target);
                continue;
            }

            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < components; c++)
                {
                    int v = row[(x * have) + c];
                    target[(x * components) + c] = extend ? ((v & mask) << unused) >> unused : v;
                }
            }
        }

        // JPEG XL decodes to RGB (XYB and recompressed JPEG's YCbCr undone); the other codecs give what was coded, which the
        // photometric interpretation names (a JPEG 2000 component transform is undone, as YBR_RCT and YBR_ICT say).
        return new DicomSamples(components, samples, rgb);
    }

    /// <summary>
    /// Returns the bytes of a frame's codestream: the fragments the Extended Offset Table or the Basic Offset Table gives it, or,
    /// without them, one fragment a frame when the counts match and otherwise the fragments from where its codestream starts.
    /// </summary>
    private ReadOnlyMemory<byte> FrameData(DicomFragments fragments, int frame, long offset)
    {
        IReadOnlyList<ReadOnlyMemory<byte>> parts = fragments.Fragments;
        if (parts.Count == 0)
        {
            throw _input.Corrupt(offset, "The encapsulated pixel data has no fragments.");
        }

        int frames = _info.Frames;
        if (fragments.ExtendedOffsetTable.Count > frame && fragments.ExtendedOffsetTableLengths.Count > frame)
        {
            int first = FragmentAt(parts, fragments.ExtendedOffsetTable[frame]);
            ulong length = fragments.ExtendedOffsetTableLengths[frame];
            if (first >= 0)
            {
                int end = first;
                ulong total = 0;
                while (end < parts.Count && total < length)
                {
                    total += (ulong)parts[end].Length;
                    end++;
                }

                return Join(parts, first, end, length);
            }

            _warn("OffsetTable", "The Extended Offset Table does not point at fragment starts; frames were found from the codestreams.");
        }
        else if (fragments.BasicOffsetTable.Count > frame)
        {
            int first = FragmentAt(parts, fragments.BasicOffsetTable[frame]);
            int end = frame + 1 < fragments.BasicOffsetTable.Count ? FragmentAt(parts, fragments.BasicOffsetTable[frame + 1]) : parts.Count;
            if (first >= 0 && end > first)
            {
                return Join(parts, first, end, ulong.MaxValue);
            }

            _warn("OffsetTable", "The Basic Offset Table does not point at fragment starts; frames were found from the codestreams.");
        }

        if (frames == 1)
        {
            return Join(parts, 0, parts.Count, ulong.MaxValue);
        }

        if (parts.Count == frames || Codec is DicomCodec.Rle or DicomCodec.Deflate or DicomCodec.EncapsulatedNative)
        {
            if (frame >= parts.Count)
            {
                throw _input.Corrupt(offset, $"Frame {frame} has no fragment: the pixel data has {parts.Count}.");
            }

            return parts[frame];
        }

        int[] starts = _frameStarts ??= FrameStarts(parts);
        if (frame >= starts.Length)
        {
            throw _input.Corrupt(offset, $"Frame {frame} was not found: the fragments hold {starts.Length} codestreams.");
        }

        return Join(parts, starts[frame], frame + 1 < starts.Length ? starts[frame + 1] : parts.Count, ulong.MaxValue);
    }

    /// <summary>Returns the fragment whose item tag is <paramref name="target"/> bytes after the first's, or −1.</summary>
    private static int FragmentAt(IReadOnlyList<ReadOnlyMemory<byte>> parts, ulong target)
    {
        ulong at = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            if (at == target)
            {
                return i;
            }

            at += 8 + (ulong)parts[i].Length;
            if (at > target)
            {
                return -1;
            }
        }

        return at == target ? parts.Count : -1;
    }

    /// <summary>Returns the fragments that start a codestream: JPEG and JPEG-LS SOI, a JPEG 2000 SOC or JP2 signature, a JPEG XL signature.</summary>
    private static int[] FrameStarts(IReadOnlyList<ReadOnlyMemory<byte>> parts)
    {
        List<int> starts = [];
        for (int i = 0; i < parts.Count; i++)
        {
            ReadOnlySpan<byte> b = parts[i].Span;
            bool start = b.Length >= 2 && ((b[0] == 0xFF && b[1] is 0xD8 or 0x4F or 0x0A)
                || (b.Length >= 8 && b[0] == 0 && b[1] == 0 && b[2] == 0 && b[3] == 0x0C && (b[4..8].SequenceEqual("jP  "u8) || b[4..8].SequenceEqual("JXL "u8))));
            if (start || i == 0)
            {
                starts.Add(i);
            }
        }

        return [.. starts];
    }

    /// <summary>Returns fragments <paramref name="first"/> to <paramref name="end"/> as one block, at most <paramref name="length"/> bytes.</summary>
    private ReadOnlyMemory<byte> Join(IReadOnlyList<ReadOnlyMemory<byte>> parts, int first, int end, ulong length)
    {
        if (end - first == 1)
        {
            ReadOnlyMemory<byte> only = parts[first];
            return length < (ulong)only.Length ? only[..(int)length] : only;
        }

        long total = 0;
        for (int i = first; i < end; i++)
        {
            total += parts[i].Length;
        }

        total = (long)Math.Min((ulong)total, length);
        _input.CheckMemory(total);
        byte[] joined = new byte[total];
        int at = 0;
        for (int i = first; i < end && at < joined.Length; i++)
        {
            ReadOnlySpan<byte> part = parts[i].Span;
            int take = Math.Min(part.Length, joined.Length - at);
            part[..take].CopyTo(joined.AsSpan(at));
            at += take;
        }

        return joined;
    }
}
