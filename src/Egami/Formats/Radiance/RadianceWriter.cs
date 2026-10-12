using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Text;

using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Radiance;

/// <summary>
/// Writes Radiance pictures: RGBE, top row first ("-Y height +X width"), each row in the per-channel run-length encoding
/// when it is 8 to 32,767 pixels wide. A pixel's largest component v gives the shared exponent e = frexp(v) + 128, and each
/// mantissa is the component × 256 / 2^(e − 128), truncated, as Greg Ward's float2rgbe does; negative and NaN components
/// write as 0, and values below 2^-127 keep unnormalized mantissas under exponent byte 1 rather than flush to 0.
/// </summary>
internal sealed class RadianceWriter : IFormatWriter, IRowFormatWriter
{
    internal static readonly RadianceWriter Instance = new();

    /// <summary>Runs shorter than this are written as literals, as Radiance's own writer does.</summary>
    private const int MinRun = 4;

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return new Encoder<TPixel>(output, start);
    }

    /// <summary>Converts linear floats to RGBE pixels, as four planes: the red mantissas, then green, blue and the exponents.</summary>
    internal static unsafe void ToRgbe(ReadOnlySpan<RgbF32> source, Span<byte> planes)
    {
        int n = source.Length;
        ref byte red = ref MemoryMarshal.GetReference(planes);
        int i = 0;
        if (Avx2.IsSupported && n >= 8)
        {
            // Eight pixels at a time: gathered to planes, the shared exponent from the largest component, and every mantissa
            // a product with an exact power of two. A group with a value outside the usual range goes the scalar way.
            Vector256<int> offsets = Vector256.Create(0, 3, 6, 9, 12, 15, 18, 21);
            Vector256<float> zero = Vector256<float>.Zero;
            fixed (RgbF32* pixels = source)
            {
                float* f = (float*)pixels;
                for (; i <= n - 8; i += 8)
                {
                    float* at = f + (3 * i);
                    Vector256<float> r = Avx.Max(Avx2.GatherVector256(at, offsets, 4), zero);
                    Vector256<float> g = Avx.Max(Avx2.GatherVector256(at + 1, offsets, 4), zero);
                    Vector256<float> b = Avx.Max(Avx2.GatherVector256(at + 2, offsets, 4), zero);
                    Vector256<float> v = Avx.Max(Avx.Max(r, g), b);
                    Vector256<int> biased = Avx2.ShiftRightLogical(v.AsInt32(), 23);
                    Vector256<int> isZero = Avx2.CompareEqual(v.AsInt32(), Vector256<int>.Zero);
                    Vector256<int> usual = Avx2.AndNot(
                        Avx2.Or(Avx2.CompareGreaterThan(Vector256.Create(9), biased), Avx2.CompareGreaterThan(biased, Vector256.Create(253))),
                        Vector256<int>.AllBitsSet);
                    if (!Avx2.Or(usual, isZero).Equals(Vector256<int>.AllBitsSet))
                    {
                        for (int k = i; k < i + 8; k++)
                        {
                            Pixel(ref red, n, k, source[k]);
                        }

                        continue;
                    }

                    Vector256<float> scale = Avx2.ShiftLeftLogical(Avx2.Subtract(Vector256.Create(261), biased), 23).AsSingle();
                    Store(ref Unsafe.Add(ref red, i), Avx.ConvertToVector256Int32WithTruncation(Avx.Multiply(r, scale)));
                    Store(ref Unsafe.Add(ref red, n + i), Avx.ConvertToVector256Int32WithTruncation(Avx.Multiply(g, scale)));
                    Store(ref Unsafe.Add(ref red, (2 * n) + i), Avx.ConvertToVector256Int32WithTruncation(Avx.Multiply(b, scale)));
                    Store(ref Unsafe.Add(ref red, (3 * n) + i), Avx2.AndNot(isZero, Avx2.Add(biased, Vector256.Create(2))));
                }
            }
        }

        for (; i < n; i++)
        {
            Pixel(ref red, n, i, source[i]);
        }

        // Writes eight ints, each 0 to 255, as eight bytes.
        static void Store(ref byte target, Vector256<int> values)
        {
            Vector128<short> shorts = Vector128.Narrow(values.GetLower(), values.GetUpper());
            Unsafe.WriteUnaligned(ref target, Vector128.Narrow(shorts, shorts).AsUInt64().ToScalar());
        }
    }

    /// <summary>Converts pixel <paramref name="i"/> of a scanline of <paramref name="n"/> pixels into the planes at <paramref name="red"/>.</summary>
    private static void Pixel(ref byte red, int n, int i, RgbF32 p)
    {
        ref byte green = ref Unsafe.Add(ref red, n);
        ref byte blue = ref Unsafe.Add(ref red, 2 * n);
        ref byte exponent = ref Unsafe.Add(ref red, 3 * n);
        float r = p.R > 0 ? p.R : 0, g = p.G > 0 ? p.G : 0, b = p.B > 0 ? p.B : 0;
        float v = r > g ? r : g;
        v = v > b ? v : b;
        if (!(v > 0))
        {
            Unsafe.Add(ref red, i) = 0;
            Unsafe.Add(ref green, i) = 0;
            Unsafe.Add(ref blue, i) = 0;
            Unsafe.Add(ref exponent, i) = 0;
            return;
        }

        int biased = (BitConverter.SingleToInt32Bits(v) >> 23) & 0xFF;
        if (biased is >= 9 and <= 253)
        {
            // The usual case: v = m × 2^(biased − 127) with m in [1, 2), so the stored exponent is biased + 2 and the scale
            // 2^(134 − biased) is a float; every product is exact, and below 256.
            float s = BitConverter.Int32BitsToSingle((261 - biased) << 23);
            Unsafe.Add(ref red, i) = (byte)(int)(r * s);
            Unsafe.Add(ref green, i) = (byte)(int)(g * s);
            Unsafe.Add(ref blue, i) = (byte)(int)(b * s);
            Unsafe.Add(ref exponent, i) = (byte)(biased + 2);
            return;
        }

        if (float.IsInfinity(v))
        {
            // The largest RGBE value; the infinite components saturate.
            v = float.MaxValue;
            r = MathF.Min(r, v);
            g = MathF.Min(g, v);
            b = MathF.Min(b, v);
        }

        // frexp: v = m × 2^exponent with m in [0.5, 1), so 256 × m / v = 2^(8 − exponent). Values too small for exponent byte
        // 1 keep what precision they can with unnormalized mantissas there; the largest ones saturate at 255.
        int stored = Math.Clamp(Exponent(v) + 128, 1, 255);
        // In double: the scale for exponent byte 1 is 2^135, past float's range; products of a float and a power of two stay exact.
        double scale = Math.ScaleB(1.0, 136 - stored);
        Unsafe.Add(ref red, i) = (byte)Math.Min(r * scale, 255);
        Unsafe.Add(ref green, i) = (byte)Math.Min(g * scale, 255);
        Unsafe.Add(ref blue, i) = (byte)Math.Min(b * scale, 255);
        Unsafe.Add(ref exponent, i) = (byte)stored;
    }

    /// <summary>Returns frexp's exponent: the e with v = m × 2^e and m in [0.5, 1), for a positive finite v.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Exponent(float v)
    {
        int bits = BitConverter.SingleToInt32Bits(v);
        int biased = (bits >> 23) & 0xFF;
        if (biased == 0)
        {
            // Subnormal: normalize through a multiply that is exact.
            bits = BitConverter.SingleToInt32Bits(v * 16777216f);
            return ((bits >> 23) & 0xFF) - 126 - 24;
        }

        return biased - 126;
    }

    /// <summary>Run-length encodes one channel of a scanline: runs of 4 or more as 128 + count and the byte, the rest as literals.</summary>
    private static int EncodeChannel(ReadOnlySpan<byte> plane, Span<byte> output)
    {
        int length = plane.Length;
        int written = 0;
        int x = 0;
        while (x < length)
        {
            int runStart = FindRun(plane, x);

            // Literals up to the run.
            while (x < runStart)
            {
                int count = Math.Min(128, runStart - x);
                output[written++] = (byte)count;
                plane.Slice(x, count).CopyTo(output[written..]);
                written += count;
                x += count;
            }

            if (runStart < length)
            {
                byte value = plane[runStart];
                int runLength = 1;
                while (runLength < 127 && runStart + runLength < length && plane[runStart + runLength] == value)
                {
                    runLength++;
                }

                output[written++] = (byte)(128 + runLength);
                output[written++] = value;
                x = runStart + runLength;
            }
        }

        return written;
    }

    /// <summary>
    /// Returns where the first run of <see cref="MinRun"/> equal bytes at or after <paramref name="from"/> starts, or the length
    /// when there is none: the first byte of a run, as a scan that steps over shorter runs finds it.
    /// </summary>
    private static int FindRun(ReadOnlySpan<byte> plane, int from)
    {
        int length = plane.Length;
        int i = from;
        if (Vector256.IsHardwareAccelerated)
        {
            // Bit k says byte i + k equals byte i + k + 1; three such bits in a row start a run of four. Bits 0 to 29 are known.
            ref byte start = ref MemoryMarshal.GetReference(plane);
            for (; i + 33 <= length; i += 30)
            {
                uint equal = Vector256.Equals(Vector256.LoadUnsafe(ref start, (nuint)i), Vector256.LoadUnsafe(ref start, (nuint)(i + 1))).ExtractMostSignificantBits();
                uint runs = equal & (equal >> 1) & (equal >> 2) & ((1u << 30) - 1);
                if (runs != 0)
                {
                    return i + BitOperations.TrailingZeroCount(runs);
                }
            }
        }

        for (; i + MinRun <= length; i++)
        {
            byte value = plane[i];
            if (plane[i + 1] == value && plane[i + 2] == value && plane[i + 3] == value)
            {
                return i;
            }
        }

        return length;
    }

    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly StreamBufferWriter _writer;
        private readonly int _width;
        private readonly bool _rle;
        private readonly RgbF32[] _floats;
        private readonly byte[] _rgbe;

        internal Encoder(Stream output, EncodeStart<TPixel> start)
        {
            RadianceWriterOptions options = start.Options as RadianceWriterOptions ?? RadianceWriterOptions.Default;
            SaveLosses.Report(start, "Radiance", SaveKeeps.Wide | SaveKeeps.Float);
            _width = start.Width;
            _rle = options.Compress && _width is >= 8 and <= 0x7FFF;
            _writer = new StreamBufferWriter(output);
            StringBuilder header = new("#?RADIANCE\n");
            if (start.Metadata.FormatDetails is RadianceDetails { Software: string software } && !options.StripMetadata && software.IndexOf('\n') < 0)
            {
                header.Append("SOFTWARE=").Append(software).Append('\n');
            }

            if (options.Exposure is double exposure)
            {
                header.Append(CultureInfo.InvariantCulture, $"EXPOSURE={exposure:R}\n");
            }

            header.Append("FORMAT=32-bit_rle_rgbe\n\n");
            header.Append(CultureInfo.InvariantCulture, $"-Y {start.Height} +X {start.Width}\n");
            string text = header.ToString();
            Encoding.ASCII.GetBytes(text, _writer.GetSpan(text.Length));
            _writer.Advance(text.Length);
            _floats = ArrayPool<RgbF32>.Shared.Rent(_width);
            _rgbe = ArrayPool<byte>.Shared.Rent(_width * 4);
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            ReadOnlySpan<RgbF32> floats;
            if (typeof(TPixel) == typeof(RgbF32))
            {
                floats = MemoryMarshal.Cast<TPixel, RgbF32>(row);
            }
            else
            {
                PixelConvert.Convert(row, _floats.AsSpan(0, _width));
                floats = _floats.AsSpan(0, _width);
            }

            Span<byte> rgbe = _rgbe.AsSpan(0, _width * 4);
            ToRgbe(floats, rgbe);
            if (!_rle)
            {
                Span<byte> flat = _writer.GetSpan(rgbe.Length);
                for (int i = 0; i < _width; i++)
                {
                    flat[i * 4] = rgbe[i];
                    flat[(i * 4) + 1] = rgbe[_width + i];
                    flat[(i * 4) + 2] = rgbe[(2 * _width) + i];
                    flat[(i * 4) + 3] = rgbe[(3 * _width) + i];
                }

                _writer.Advance(rgbe.Length);
                return;
            }

            // A channel never takes more than width + width / 128 + 1 bytes encoded.
            Span<byte> out4 = _writer.GetSpan(4 + (4 * (_width + (_width / 128) + 2)));
            out4[0] = 2;
            out4[1] = 2;
            out4[2] = (byte)(_width >> 8);
            out4[3] = (byte)_width;
            int written = 4;
            for (int channel = 0; channel < 4; channel++)
            {
                written += EncodeChannel(rgbe.Slice(channel * _width, _width), out4[written..]);
            }

            _writer.Advance(written);
        }

        public override void Complete()
        {
            _writer.Flush();
        }

        protected override void Dispose(bool disposing)
        {
            _writer.Dispose();
            ArrayPool<RgbF32>.Shared.Return(_floats);
            ArrayPool<byte>.Shared.Return(_rgbe);
            base.Dispose(disposing);
        }
    }
}
