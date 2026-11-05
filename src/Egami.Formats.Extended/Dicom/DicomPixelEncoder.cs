using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;

using Egami.Formats.Extended.JpegLs;
using Egami.Formats.Jpeg;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// The Image Pixel module of pixel data to write: its size and sample layout, with the photometric interpretation of the
/// samples as given (interleaved, chroma not subsampled).
/// </summary>
internal sealed record DicomPixelLayout(int Rows, int Columns, int SamplesPerPixel, int BitsAllocated, int BitsStored, int HighBit, bool IsSigned,
    string Photometric, int Frames);

/// <summary>What encoding the pixel data gives: the elements to set and whether the coding lost anything.</summary>
internal sealed record DicomEncodedPixels(List<DicomElement> Elements, bool IsLossy, string? Method, double Ratio);

/// <summary>
/// Encodes frames of stored values as pixel data in a transfer syntax: native (PS3.5 section 8.1), or encapsulated one fragment a
/// frame (annex A.4) as RLE (annex G), JPEG baseline, extended or lossless (section 8.2.1), JPEG-LS (8.2.3) or JPEG XL (8.2.15),
/// with the Basic or Extended Offset Table. Lossless codings keep every value, signed ones as the two's complement of their
/// stored bits; the lossy ones take unsigned values of up to 8 bits (12 for JPEG Extended).
/// </summary>
internal static class DicomPixelEncoder
{
    private static readonly DicomTag PhotometricTag = new(0x0028, 0x0004);
    private static readonly DicomTag PlanarTag = new(0x0028, 0x0006);
    private static readonly DicomTag BitsAllocatedTag = new(0x0028, 0x0100);
    private static readonly DicomTag BitsStoredTag = new(0x0028, 0x0101);
    private static readonly DicomTag HighBitTag = new(0x0028, 0x0102);
    private static readonly DicomTag ExtendedOffsets = new(0x7FE0, 0x0001);
    private static readonly DicomTag ExtendedLengths = new(0x7FE0, 0x0002);

    /// <summary>
    /// Encodes every frame <paramref name="frame"/> gives (interleaved stored values, signed ones with their sign) as the pixel
    /// data of <paramref name="syntax"/>, with the Image Pixel elements that change.
    /// </summary>
    /// <exception cref="NotSupportedException">The transfer syntax cannot hold the pixel data, such as signed values in JPEG Baseline.</exception>
    public static DicomEncodedPixels Encode(DicomPixelLayout layout, Func<int, int[]> frame, DicomTransferSyntax syntax, DicomWriterOptions options, EgamiContext context)
    {
        DicomPixelDecoder.DicomCodec codec = DicomPixelDecoder.CodecFor(syntax.Uid);
        long raw = (long)layout.Rows * layout.Columns * layout.SamplesPerPixel * layout.Frames * Math.Max(layout.BitsAllocated, 8) / 8;
        if (codec == DicomPixelDecoder.DicomCodec.Native)
        {
            return new DicomEncodedPixels([.. Common(layout, layout.Photometric), Native(layout, frame)], false, null, 1);
        }

        Coder coder = Choose(layout, syntax, options, context);
        List<ReadOnlyMemory<byte>> fragments = new(layout.Frames);
        long total = 0;
        for (int f = 0; f < layout.Frames; f++)
        {
            ReadOnlyMemory<byte> data = coder.Encode(frame(f));
            fragments.Add(data);
            total += data.Length + (data.Length & 1) + 8;
        }

        DicomPixelLayout written = coder.Layout;
        List<DicomElement> elements = [.. Common(written, written.Photometric)];
        if (written.BitsAllocated != layout.BitsAllocated || written.BitsStored != layout.BitsStored)
        {
            elements.Add(DicomValue.UShort(BitsAllocatedTag, written.BitsAllocated));
            elements.Add(DicomValue.UShort(BitsStoredTag, written.BitsStored));
            elements.Add(DicomValue.UShort(HighBitTag, written.HighBit));
        }

        DicomOffsetTable table = options.OffsetTable;
        if (table == DicomOffsetTable.Basic && total > uint.MaxValue)
        {
            table = DicomOffsetTable.Extended;
        }

        uint[] basic = [];
        if (table != DicomOffsetTable.None && layout.Frames > 0)
        {
            ulong[] offsets = new ulong[fragments.Count];
            ulong[] lengths = new ulong[fragments.Count];
            ulong at = 0;
            for (int i = 0; i < fragments.Count; i++)
            {
                int length = fragments[i].Length;
                offsets[i] = at;
                lengths[i] = (ulong)(length + (length & 1));
                at += lengths[i] + 8;
            }

            if (table == DicomOffsetTable.Basic)
            {
                basic = [.. offsets.Select(o => (uint)o)];
            }
            else
            {
                elements.Add(DicomValue.Bytes(ExtendedOffsets, DicomVR.OV, Words(offsets)));
                elements.Add(DicomValue.Bytes(ExtendedLengths, DicomVR.OV, Words(lengths)));
            }
        }

        elements.Add(new DicomElement(DicomTag.PixelData, DicomVR.OB, ReadOnlyMemory<byte>.Empty, false, DicomCharacterSet.Default, -1,
            fragments: new DicomFragments(basic, [.. fragments])));
        double ratio = total == 0 ? 1 : (double)raw / fragments.Sum(f => (long)f.Length);
        return new DicomEncodedPixels(elements, coder.IsLossy, coder.Method, ratio);
    }

    /// <summary>Returns the Photometric Interpretation and, for three samples, Planar Configuration 0 (interleaved).</summary>
    private static IEnumerable<DicomElement> Common(DicomPixelLayout layout, string photometric)
    {
        yield return DicomValue.Text(PhotometricTag, DicomVR.CS, photometric);
        if (layout.SamplesPerPixel > 1)
        {
            yield return DicomValue.UShort(PlanarTag, 0);
        }
    }

    /// <summary>Packs every frame as native pixel data: 1-bit samples a bit each, lowest first, the others in little-endian words.</summary>
    private static DicomElement Native(DicomPixelLayout layout, Func<int, int[]> frame)
    {
        int bits = layout.BitsAllocated;
        long count = (long)layout.Rows * layout.Columns * layout.SamplesPerPixel;
        long bytes = ((count * layout.Frames * bits) + 7) / 8;
        if (bytes + (bytes & 1) > uint.MaxValue - 1 || bytes > Array.MaxLength - 1)
        {
            throw new NotSupportedException($"The native pixel data would be {bytes} bytes, more than one element holds; write an encapsulated transfer syntax.");
        }

        byte[] data = new byte[bytes + (bytes & 1)];
        int shift = layout.HighBit + 1 - layout.BitsStored;
        uint mask = layout.BitsStored >= 32 ? uint.MaxValue : (1u << layout.BitsStored) - 1;
        for (int f = 0; f < layout.Frames; f++)
        {
            int[] samples = frame(f);
            switch (bits)
            {
                case 1:
                    long bit = count * f;
                    for (int i = 0; i < samples.Length; i++, bit++)
                    {
                        if ((samples[i] & 1) != 0)
                        {
                            data[bit >> 3] |= (byte)(1 << (int)(bit & 7));
                        }
                    }

                    break;
                case 8:
                    Span<byte> b8 = data.AsSpan((int)(count * f), (int)count);
                    for (int i = 0; i < b8.Length; i++)
                    {
                        b8[i] = (byte)(((uint)samples[i] & mask) << shift);
                    }

                    break;
                case 16:
                    Span<ushort> b16 = MemoryMarshal.Cast<byte, ushort>(data.AsSpan((int)(count * 2 * f), (int)count * 2));
                    for (int i = 0; i < b16.Length; i++)
                    {
                        b16[i] = (ushort)(((uint)samples[i] & mask) << shift);
                    }

                    if (!BitConverter.IsLittleEndian)
                    {
                        BinaryPrimitives.ReverseEndianness(b16, b16);
                    }

                    break;
                case 32:
                    Span<uint> b32 = MemoryMarshal.Cast<byte, uint>(data.AsSpan((int)(count * 4 * f), (int)count * 4));
                    for (int i = 0; i < b32.Length; i++)
                    {
                        b32[i] = ((uint)samples[i] & mask) << shift;
                    }

                    if (!BitConverter.IsLittleEndian)
                    {
                        BinaryPrimitives.ReverseEndianness(b32, b32);
                    }

                    break;
                default:
                    throw new NotSupportedException($"Bits Allocated {bits} is not written; native pixel data is written at 1, 8, 16 and 32 bits.");
            }
        }

        return DicomValue.Bytes(DicomTag.PixelData, bits <= 8 ? DicomVR.OB : DicomVR.OW, data);
    }

    private static byte[] Words(ulong[] values)
    {
        byte[] bytes = new byte[values.Length * 8];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), values[i]);
        }

        return bytes;
    }

    /// <summary>Picks the coder of a transfer syntax, or throws when it cannot hold the pixel data.</summary>
    private static Coder Choose(DicomPixelLayout layout, DicomTransferSyntax syntax, DicomWriterOptions options, EgamiContext context)
    {
        if (layout.SamplesPerPixel is not (1 or 3))
        {
            throw new NotSupportedException($"Samples per Pixel {layout.SamplesPerPixel} is not encoded; gray, palette and three-sample colour images are.");
        }

        if (layout.Photometric is "YBR_PARTIAL_422" or "YBR_PARTIAL_420")
        {
            throw new NotSupportedException($"{layout.Photometric} pixel data is not re-encoded.");
        }

        switch (syntax.Uid)
        {
            case "1.2.840.10008.1.2.5":
                return new RleCoder(layout);
            case "1.2.840.10008.1.2.4.70":
                return new LosslessJpegCoder(layout);
            case "1.2.840.10008.1.2.4.80":
                return new JpegLsCoder(layout, 0);
            case "1.2.840.10008.1.2.4.81":
                return new JpegLsCoder(layout, options.NearLossless);
            case "1.2.840.10008.1.2.4.50":
                return new JpegCoder(layout, extended: false, options.Quality, context);
            case "1.2.840.10008.1.2.4.51":
                return new JpegCoder(layout, extended: true, options.Quality, context);
            case "1.2.840.10008.1.2.4.110":
                return new JxlCoder(layout, lossless: true, options.Quality, context);
            case "1.2.840.10008.1.2.4.112":
                return new JxlCoder(layout, lossless: false, options.Quality, context);
            default:
                throw new NotSupportedException($"Pixel data is not written in {syntax}; Egami writes native, RLE, JPEG Baseline, Extended and Lossless (SV1), JPEG-LS and JPEG XL.");
        }
    }

    /// <summary>Returns a first guess at a frame's coded size, so the output grows seldom.</summary>
    private static int Capacity(int samples) => (int)Math.Min(Array.MaxLength, Math.Max(256, (long)samples * 3 / 4));

    /// <summary>Returns the low <paramref name="bits"/> of each sample, a signed value as its two's complement.</summary>
    private static ushort[] Unsigned(int[] samples, int bits)
    {
        ushort[] values = new ushort[samples.Length];
        uint mask = (1u << bits) - 1;
        for (int i = 0; i < samples.Length; i++)
        {
            values[i] = (ushort)((uint)samples[i] & mask);
        }

        return values;
    }

    /// <summary>One transfer syntax's coding of a frame, and the layout it gives the pixel data.</summary>
    private abstract class Coder(DicomPixelLayout layout)
    {
        public virtual DicomPixelLayout Layout => layout;

        public virtual bool IsLossy => false;

        public virtual string? Method => null;

        protected DicomPixelLayout Source => layout;

        public abstract ReadOnlyMemory<byte> Encode(int[] samples);
    }

    private sealed class RleCoder(DicomPixelLayout layout) : Coder(layout)
    {
        public override ReadOnlyMemory<byte> Encode(int[] samples)
        {
            return DicomRle.Encode(samples, Source.SamplesPerPixel, Source.Rows, Source.Columns, Source.BitsAllocated, Source.BitsStored, Source.HighBit);
        }
    }

    private sealed class LosslessJpegCoder : Coder
    {
        public LosslessJpegCoder(DicomPixelLayout layout)
            : base(layout)
        {
            if (layout.BitsStored is < 2 or > 16)
            {
                throw new NotSupportedException($"JPEG Lossless codes 2 to 16 bits; Bits Stored is {layout.BitsStored}.");
            }
        }

        public override ReadOnlyMemory<byte> Encode(int[] samples)
        {
            using MemoryStream output = new(Capacity(samples.Length));
            JpegLosslessWriter.Write(output, Unsigned(samples, Source.BitsStored), Source.SamplesPerPixel, Source.Columns, Source.BitsStored, predictor: 1);
            return output.GetBuffer().AsMemory(0, (int)output.Length);
        }
    }

    private sealed class JpegLsCoder : Coder
    {
        private readonly int _near;

        public JpegLsCoder(DicomPixelLayout layout, int near)
            : base(layout)
        {
            if (layout.BitsStored is < 2 or > 16)
            {
                throw new NotSupportedException($"JPEG-LS codes 2 to 16 bits; Bits Stored is {layout.BitsStored}.");
            }

            if (near > 0 && layout.Photometric == "PALETTE COLOR")
            {
                throw new NotSupportedException("Palette indices are not coded lossy: near-lossless would change colours, not shades.");
            }

            _near = Math.Min(near, Math.Min(255, ((1 << layout.BitsStored) - 1) / 2));
        }

        public override bool IsLossy => _near > 0;

        public override string? Method => _near > 0 ? "ISO_14495_1" : null;

        public override ReadOnlyMemory<byte> Encode(int[] samples)
        {
            using MemoryStream output = new(Capacity(samples.Length));
            JpegLsWriterOptions options = new() { Near = _near, Interleave = Source.SamplesPerPixel > 1 ? JpegLsInterleave.Line : JpegLsInterleave.None };
            JpegLsWriter.Write(output, Unsigned(samples, Source.BitsStored), Source.SamplesPerPixel, Source.Columns, Source.BitsStored, options);
            return output.GetBuffer().AsMemory(0, (int)output.Length);
        }
    }

    /// <summary>JPEG Baseline (8 bits) and Extended (8 or 12 bits) through the JPEG writer: colour as YCbCr 4:2:2 (YBR_FULL_422).</summary>
    private sealed class JpegCoder : Coder
    {
        private readonly int _precision;
        private readonly int _quality;
        private readonly EgamiContext _context;

        public JpegCoder(DicomPixelLayout layout, bool extended, int quality, EgamiContext context)
            : base(layout)
        {
            CheckLossy(layout, "JPEG");
            int most = extended ? 12 : 8;
            if (layout.BitsStored > most)
            {
                throw new NotSupportedException($"JPEG {(extended ? "Extended" : "Baseline")} codes up to {most} bits; Bits Stored is {layout.BitsStored}."
                    + (extended ? " Use a lossless transfer syntax." : " Use JPEG Extended or a lossless transfer syntax."));
            }

            _precision = layout.BitsStored > 8 ? 12 : 8;
            _quality = quality;
            _context = context;
        }

        public override DicomPixelLayout Layout => Source with
        {
            BitsAllocated = _precision == 8 ? 8 : 16,
            BitsStored = _precision,
            HighBit = _precision - 1,
            Photometric = Source.SamplesPerPixel == 3 ? "YBR_FULL_422" : Source.Photometric,
        };

        public override bool IsLossy => true;

        public override string Method => "ISO_10918_1";

        public override ReadOnlyMemory<byte> Encode(int[] samples)
        {
            using Raster raster = ToRaster(Source, samples, _precision == 12, scale12: true, _context);
            using MemoryStream output = new(Capacity(samples.Length));
            raster.Save(output, JpegFormat.Info, new JpegWriterOptions
            {
                Quality = _quality,
                Precision = _precision,
                Subsampling = ChromaSubsampling.Subsample422,
                ColorType = Source.SamplesPerPixel == 1 ? JpegColorType.Gray : JpegColorType.Auto,
                StripMetadata = true,
            });
            return output.GetBuffer().AsMemory(0, (int)output.Length);
        }
    }

    /// <summary>JPEG XL through its writer: lossless keeps every value; lossy takes up to 8 bits. DICOM reads JPEG XL as RGB.</summary>
    private sealed class JxlCoder : Coder
    {
        private readonly bool _lossless;
        private readonly int _quality;
        private readonly EgamiContext _context;

        public JxlCoder(DicomPixelLayout layout, bool lossless, int quality, EgamiContext context)
            : base(layout)
        {
            if (lossless)
            {
                if (layout.BitsStored > 16)
                {
                    throw new NotSupportedException($"JPEG XL is written up to 16 bits; Bits Stored is {layout.BitsStored}.");
                }

                if (layout.SamplesPerPixel == 3 && layout.Photometric != "RGB")
                {
                    throw new NotSupportedException($"{layout.Photometric} samples are not kept by JPEG XL Lossless, which DICOM reads as RGB; use JPEG-LS or JPEG Lossless.");
                }
            }
            else
            {
                CheckLossy(layout, "JPEG XL");
                if (layout.BitsStored > 8)
                {
                    throw new NotSupportedException($"Lossy JPEG XL is written for up to 8 bits; Bits Stored is {layout.BitsStored}. Use JPEG XL Lossless.");
                }
            }

            _lossless = lossless;
            _quality = quality;
            _context = context;
        }

        public override DicomPixelLayout Layout => _lossless ? Source with { Photometric = Photometric(Source) } : Source with
        {
            BitsAllocated = 8,
            BitsStored = 8,
            HighBit = 7,
            Photometric = Photometric(Source),
        };

        public override bool IsLossy => !_lossless;

        public override string? Method => _lossless ? null : "ISO_18181_1";

        public override ReadOnlyMemory<byte> Encode(int[] samples)
        {
            int[] values = _lossless && Source.IsSigned ? [.. Unsigned(samples, Source.BitsStored).Select(v => (int)v)] : samples;
            using Raster raster = ToRaster(Source, values, Source.BitsStored > 8, scale12: false, _context);
            using MemoryStream output = new(Capacity(samples.Length));
            raster.Save(output, JxlFormat.Info, new JxlWriterOptions { Lossless = _lossless, Quality = _quality, StripMetadata = true });
            return output.GetBuffer().AsMemory(0, (int)output.Length);
        }

        private static string Photometric(DicomPixelLayout layout) => layout.SamplesPerPixel == 3 ? "RGB" : layout.Photometric;
    }

    private static void CheckLossy(DicomPixelLayout layout, string codec)
    {
        if (layout.IsSigned)
        {
            throw new NotSupportedException($"Signed pixel data is not written as lossy {codec}; use a lossless transfer syntax.");
        }

        if (layout.Photometric == "PALETTE COLOR")
        {
            throw new NotSupportedException($"Palette indices are not written as lossy {codec}: it would change colours, not shades.");
        }
    }

    /// <summary>
    /// Puts a frame's values in a raster for an image codec: Gray8 or Rgb8, or Gray16 or Rgb16 when <paramref name="wide"/> (12-bit
    /// values widened to 16 bits with <paramref name="scale12"/>, as the JPEG writer narrows them back). YBR_FULL samples become RGB.
    /// </summary>
    private static Raster ToRaster(DicomPixelLayout layout, int[] samples, bool wide, bool scale12, EgamiContext context)
    {
        int width = layout.Columns, height = layout.Rows;
        bool ybr = layout.SamplesPerPixel == 3 && layout.Photometric is "YBR_FULL" or "YBR_FULL_422";
        int max = wide && scale12 ? 4095 : (1 << Math.Min(layout.BitsStored, 16)) - 1;
        if (ybr)
        {
            samples = ToRgb(samples, layout.BitsStored, max);
        }

        if (layout.SamplesPerPixel == 1)
        {
            if (!wide)
            {
                Raster<Gray8> gray = Raster.Create<Gray8>(width, height, context);
                for (int y = 0; y < height; y++)
                {
                    Span<byte> row = MemoryMarshal.AsBytes(gray.Row(y));
                    ReadOnlySpan<int> source = samples.AsSpan(y * width, width);
                    for (int x = 0; x < width; x++)
                    {
                        row[x] = (byte)Math.Clamp(source[x], 0, 255);
                    }
                }

                return gray;
            }

            Raster<Gray16> gray16 = Raster.Create<Gray16>(width, height, context);
            for (int y = 0; y < height; y++)
            {
                Span<ushort> row = MemoryMarshal.Cast<Gray16, ushort>(gray16.Row(y));
                ReadOnlySpan<int> source = samples.AsSpan(y * width, width);
                for (int x = 0; x < width; x++)
                {
                    row[x] = Wide(source[x], max, scale12);
                }
            }

            return gray16;
        }

        if (!wide)
        {
            Raster<Rgb8> rgb = Raster.Create<Rgb8>(width, height, context);
            for (int y = 0; y < height; y++)
            {
                Span<byte> row = MemoryMarshal.AsBytes(rgb.Row(y));
                ReadOnlySpan<int> source = samples.AsSpan(y * width * 3, width * 3);
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] = (byte)Math.Clamp(source[i], 0, 255);
                }
            }

            return rgb;
        }

        Raster<Rgb16> rgb16 = Raster.Create<Rgb16>(width, height, context);
        for (int y = 0; y < height; y++)
        {
            Span<ushort> row = MemoryMarshal.Cast<Rgb16, ushort>(rgb16.Row(y));
            ReadOnlySpan<int> source = samples.AsSpan(y * width * 3, width * 3);
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = Wide(source[i], max, scale12);
            }
        }

        return rgb16;
    }

    /// <summary>Returns a value for a 16-bit raster: a 12-bit value spread over 16 bits, which the JPEG writer narrows back to it exactly, or the value itself.</summary>
    private static ushort Wide(int value, int max, bool scale12)
    {
        int v = Math.Clamp(value, 0, max);
        return scale12 ? (ushort)((v << 4) | (v >> 8)) : (ushort)v;
    }

    /// <summary>Converts full-range YCbCr (YBR_FULL, PS3.3 C.7.6.3.1.2) of <paramref name="bits"/>-bit samples to RGB.</summary>
    private static int[] ToRgb(int[] samples, int bits, int max)
    {
        int[] rgb = new int[samples.Length];
        double half = 1 << (bits - 1);
        int top = Math.Min(max, (1 << bits) - 1);
        for (int i = 0; i < samples.Length; i += 3)
        {
            double l = samples[i], cb = samples[i + 1] - half, cr = samples[i + 2] - half;
            rgb[i] = Math.Clamp((int)Math.Round(l + (1.402 * cr)), 0, top);
            rgb[i + 1] = Math.Clamp((int)Math.Round(l - (0.344136 * cb) - (0.714136 * cr)), 0, top);
            rgb[i + 2] = Math.Clamp((int)Math.Round(l + (1.772 * cb)), 0, top);
        }

        return rgb;
    }

    /// <summary>Returns a ratio as a Decimal String of at most 16 characters.</summary>
    internal static string Ratio(double ratio) => Math.Round(ratio, ratio >= 100 ? 1 : 3).ToString("0.###", CultureInfo.InvariantCulture);
}
