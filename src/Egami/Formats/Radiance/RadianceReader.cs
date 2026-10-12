using System.Buffers;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Radiance;

/// <summary>
/// Reads Radiance pictures, written from Greg Ward's description of the RGBE format in Graphics Gems II and the Radiance file
/// format notes: a text header ending in a blank line, a resolution line naming the scan order, then scanlines of four-byte
/// pixels, each scanline flat, in the old repeat-pixel encoding, or in the newer per-channel run-length encoding. Pixels load
/// as <see cref="RgbF32"/>, linear and unclamped; a mantissa m with exponent e is m × 2^(e − 136), as stb_image computes it.
/// </summary>
internal sealed class RadianceReader : IFormatReader
{
    internal static readonly RadianceReader Instance = new();

    /// <summary>The longest header Egami reads, so a file without a blank line cannot make it read forever.</summary>
    private const int MaxHeaderBytes = 64 * 1024;

    /// <summary>Scanlines from 8 to 32,767 pixels may use the per-channel run-length encoding; others are flat or old-style.</summary>
    private const int MinRleLength = 8;
    private const int MaxRleLength = 0x7FFF;

    /// <summary>Radiance's standard primaries, with the equal-energy white its RGB and XYZ share; XYZE pixels convert to them.</summary>
    internal static readonly ColorPrimaries StandardPrimaries = new(new(0.640, 0.330), new(0.290, 0.600), new(0.150, 0.060), new(1.0 / 3, 1.0 / 3));

    /// <summary>2^(e − 136) for each exponent byte e, and 0 for e = 0.</summary>
    private static readonly float[] s_scale = BuildScale();

    /// <summary>Returns whether the data starts with "#?RADIANCE" or "#?RGBE".</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.StartsWith("#?RADIANCE"u8) || header.StartsWith("#?RGBE"u8);
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input, null);
        return new RasterInfo
        {
            Format = RadianceFormat.Info,
            Width = header.Width,
            Height = header.Height,
            Layout = RgbF32.Layout,
            FrameCount = 1,
            HasMetadata = true,
            EstimatedBytes = (long)header.Width * header.Height * 12,
            WorkingBytes = (long)header.ScanLength * 4,
        };
    }

    public Raster Read(EgamiInput input, ReaderOptions options) => Read<RgbF32>(input, options);

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RadianceDetails details = new();
        Header header = ReadHeader(input, details);
        Raster<TPixel> raster = input.CreateRaster<TPixel>(header.Width, header.Height);
        try
        {
            RasterMetadata metadata = raster.Metadata;
            metadata.FormatDetails = details;
            metadata.ColorSpace = ColorSpaceHint.Linear;
            if (details.PixelFormat == RadiancePixelFormat.Xyze)
            {
                metadata.Primaries = details.Primaries ?? StandardPrimaries;
            }
            else if (details.Primaries is not null)
            {
                metadata.Primaries = details.Primaries;
            }

            float peak = Decode(input, header, details, raster, options.Recovery == RecoveryMode.Strict);
            if (peak > 0 && float.IsFinite(peak))
            {
                metadata.Hdr = new HdrMetadata { MaxContentLightLevel = peak * 203.0 };
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Reads the header and the resolution line, leaving the input at the first scanline.</summary>
    private static Header ReadHeader(EgamiInput input, RadianceDetails? details)
    {
        string first = ReadLine(input, out _);
        if (first is not ("#?RADIANCE" or "#?RGBE") && !first.StartsWith("#?", StringComparison.Ordinal))
        {
            throw input.Corrupt(0, "The Radiance signature is wrong.");
        }

        RadiancePixelFormat format = RadiancePixelFormat.Rgbe;
        int total = first.Length;
        while (true)
        {
            long at = input.Position;
            string line = ReadLine(input, out int length);
            total += length;
            if (total > MaxHeaderBytes)
            {
                throw input.Corrupt(at, $"The header is longer than {MaxHeaderBytes} bytes without the blank line that ends it.");
            }

            if (line.Length == 0)
            {
                break;
            }

            details?.HeaderLines.Add(line);
            if (line.StartsWith('#'))
            {
                continue;
            }

            int equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = line[..equals].Trim();
            string value = line[(equals + 1)..].Trim();
            switch (key)
            {
                case "FORMAT":
                    format = value switch
                    {
                        "32-bit_rle_rgbe" => RadiancePixelFormat.Rgbe,
                        "32-bit_rle_xyze" => RadiancePixelFormat.Xyze,
                        _ => throw new UnsupportedFormatException($"The Radiance pixel format '{value}' is not one Egami reads; it reads 32-bit_rle_rgbe and 32-bit_rle_xyze."),
                    };
                    break;
                case "EXPOSURE" when details is not null && Number(value) is double exposure && exposure > 0:
                    details.Exposure = (details.Exposure ?? 1) * exposure;
                    break;
                case "PIXASPECT" when details is not null && Number(value) is double aspect && aspect > 0:
                    details.PixelAspectRatio = (details.PixelAspectRatio ?? 1) * aspect;
                    break;
                case "COLORCORR" when details is not null && Numbers(value, 3) is double[] c:
                    (double r, double g, double b) = details.ColorCorrection ?? (1, 1, 1);
                    details.ColorCorrection = (r * c[0], g * c[1], b * c[2]);
                    break;
                case "PRIMARIES" when details is not null && Numbers(value, 8) is double[] p:
                    details.Primaries = new ColorPrimaries(new(p[0], p[1]), new(p[2], p[3]), new(p[4], p[5]), new(p[6], p[7]));
                    break;
                case "SOFTWARE" when details is not null:
                    details.Software = value;
                    break;
                case "VIEW" when details is not null:
                    details.View = value;
                    break;
            }
        }

        long resolutionAt = input.Position;
        string resolution = ReadLine(input, out _);
        Header header = ParseResolution(input, resolution, resolutionAt, format);
        if (details is not null)
        {
            details.PixelFormat = format;
            details.Orientation = resolution;
        }

        return header;
    }

    /// <summary>Parses "-Y 512 +X 768" and the other seven scan orders.</summary>
    private static Header ParseResolution(EgamiInput input, string line, long at, RadiancePixelFormat format)
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 4 || parts[0].Length != 2 || parts[2].Length != 2 || parts[0][0] is not ('+' or '-') || parts[2][0] is not ('+' or '-')
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int major) || major < 1
            || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out int minor) || minor < 1)
        {
            throw input.Corrupt(at, $"The resolution line '{line}' is not like '-Y 512 +X 768'.");
        }

        char majorAxis = parts[0][1], minorAxis = parts[2][1];
        if (!((majorAxis == 'Y' && minorAxis == 'X') || (majorAxis == 'X' && minorAxis == 'Y')))
        {
            throw input.Corrupt(at, $"The resolution line '{line}' must name one Y and one X axis.");
        }

        bool columns = majorAxis == 'X';
        bool majorDown = parts[0][0] == (columns ? '+' : '-');
        bool minorForward = parts[2][0] == (columns ? '-' : '+');
        return columns
            ? new Header(major, minor, format, Columns: true, MajorForward: majorDown, MinorForward: minorForward)
            : new Header(minor, major, format, Columns: false, MajorForward: majorDown, MinorForward: minorForward);
    }

    /// <summary>
    /// Decodes every scanline into the raster. Returns the largest value decoded, after any conversion to RGB, for the light level.
    /// </summary>
    private static float Decode<TPixel>(EgamiInput input, Header header, RadianceDetails details, Raster<TPixel> raster, bool strict)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int length = header.ScanLength;
        int scans = header.ScanCount;
        bool direct = typeof(TPixel) == typeof(RgbF32) && !header.Columns && header.MinorForward;
        bool xyze = header.Format == RadiancePixelFormat.Xyze;
        Matrix3x3f toRgb = xyze ? Matrix3x3f.From(((details.Primaries ?? StandardPrimaries).ToXyz()).Inverse()) : default;

        // Column-major files are decoded into a whole RGB image first, then turned into rows.
        RgbF32[]? whole = header.Columns ? ArrayPool<RgbF32>.Shared.Rent(checked(header.Width * header.Height)) : null;
        byte[] rgbe = ArrayPool<byte>.Shared.Rent(length * 4);
        RgbF32[] line = ArrayPool<RgbF32>.Shared.Rent(length);
        ScanReader reader = new(input);
        float peak = 0;
        int scan = 0;
        try
        {
            for (; scan < scans; scan++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                Span<byte> pixels = rgbe.AsSpan(0, length * 4);
                if (!reader.ReadScanline(pixels, length))
                {
                    if (strict)
                    {
                        throw new CorruptDataException(RadianceFormat.Info.Name, input.Position, "The data ends unexpectedly.") { IsTruncation = true };
                    }

                    break;
                }

                int major = header.MajorForward ? scan : scans - 1 - scan;
                Span<RgbF32> target = direct ? MemoryMarshal.Cast<TPixel, RgbF32>(raster.Row(major)) : line.AsSpan(0, length);
                peak = Math.Max(peak, xyze ? ToRgb(pixels, target, toRgb) : ToFloat(pixels, target));
                if (!header.MinorForward)
                {
                    target.Reverse();
                }

                if (whole is not null)
                {
                    // A scanline is column `major`, top to bottom.
                    for (int y = 0; y < length; y++)
                    {
                        whole[(y * header.Width) + major] = target[y];
                    }
                }
                else if (!direct)
                {
                    PixelConvert.Convert<RgbF32, TPixel>(target, raster.Row(major));
                }
            }

            reader.Finish();
            if (scan < scans)
            {
                Missing(raster, header, scan, whole);
                raster.Metadata.WarningList.Add("Radiance.RowsMissing", $"The file ends after {scan} of {scans} scanlines; the rest is left black.", input.Position);
            }

            if (whole is not null)
            {
                for (int y = 0; y < header.Height; y++)
                {
                    PixelConvert.Convert<RgbF32, TPixel>(whole.AsSpan(y * header.Width, header.Width), raster.Row(y));
                }
            }

            return peak;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rgbe);
            ArrayPool<RgbF32>.Shared.Return(line);
            if (whole is not null)
            {
                ArrayPool<RgbF32>.Shared.Return(whole);
            }
        }
    }

    /// <summary>Clears what the missing scanlines would have filled.</summary>
    private static void Missing<TPixel>(Raster<TPixel> raster, Header header, int decoded, RgbF32[]? whole)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        for (int scan = decoded; scan < header.ScanCount; scan++)
        {
            int major = header.MajorForward ? scan : header.ScanCount - 1 - scan;
            if (whole is null)
            {
                raster.Row(major).Clear();
            }
            else
            {
                for (int y = 0; y < header.Height; y++)
                {
                    whole[(y * header.Width) + major] = default;
                }
            }
        }
    }

    /// <summary>
    /// Converts a scanline of RGBE pixels held as four planes (all red mantissas, then green, blue and the exponents) to floats;
    /// returns the largest component.
    /// </summary>
    internal static float ToFloat(ReadOnlySpan<byte> planes, Span<RgbF32> target)
    {
        int n = target.Length;
        ref float scale = ref MemoryMarshal.GetArrayDataReference(s_scale);
        ref byte red = ref MemoryMarshal.GetReference(planes);
        ref byte green = ref Unsafe.Add(ref red, n);
        ref byte blue = ref Unsafe.Add(ref red, 2 * n);
        ref byte exponent = ref Unsafe.Add(ref red, 3 * n);
        ref float dst = ref Unsafe.As<RgbF32, float>(ref MemoryMarshal.GetReference(target));
        float peak = 0;
        for (int i = 0; i < n; i++)
        {
            float f = Unsafe.Add(ref scale, Unsafe.Add(ref exponent, i));
            byte m = Math.Max(Unsafe.Add(ref red, i), Math.Max(Unsafe.Add(ref green, i), Unsafe.Add(ref blue, i)));
            Unsafe.Add(ref dst, 3 * i) = Unsafe.Add(ref red, i) * f;
            Unsafe.Add(ref dst, (3 * i) + 1) = Unsafe.Add(ref green, i) * f;
            Unsafe.Add(ref dst, (3 * i) + 2) = Unsafe.Add(ref blue, i) * f;
            peak = MathF.Max(peak, m * f);
        }

        return peak;
    }

    /// <summary>Converts XYZE pixels to RGB floats through <paramref name="toRgb"/>; returns the largest component.</summary>
    private static float ToRgb(ReadOnlySpan<byte> xyze, Span<RgbF32> target, Matrix3x3f toRgb)
    {
        ToFloat(xyze, target);
        float peak = 0;
        for (int i = 0; i < target.Length; i++)
        {
            RgbF32 xyz = target[i];
            RgbF32 rgb = new(
                (toRgb.M00 * xyz.R) + (toRgb.M01 * xyz.G) + (toRgb.M02 * xyz.B),
                (toRgb.M10 * xyz.R) + (toRgb.M11 * xyz.G) + (toRgb.M12 * xyz.B),
                (toRgb.M20 * xyz.R) + (toRgb.M21 * xyz.G) + (toRgb.M22 * xyz.B));
            target[i] = rgb;
            peak = Math.Max(peak, Math.Max(rgb.R, Math.Max(rgb.G, rgb.B)));
        }

        return peak;
    }

    /// <summary>Reads a header line up to its newline, which is not included; <paramref name="length"/> counts the newline.</summary>
    private static string ReadLine(EgamiInput input, out int length)
    {
        StringBuilder? text = null;
        length = 0;
        while (true)
        {
            ReadOnlySpan<byte> window = input.Peek(4096);
            if (window.IsEmpty)
            {
                throw input.Corrupt(input.Position, "The file ends inside the header.");
            }

            int newline = window.IndexOf((byte)'\n');
            int take = newline < 0 ? window.Length : newline;
            if (length + take > MaxHeaderBytes)
            {
                throw input.Corrupt(input.Position, $"A header line is longer than {MaxHeaderBytes} bytes.");
            }

            text ??= new StringBuilder();
            text.Append(Encoding.Latin1.GetString(window[..take]));
            length += take;
            if (newline >= 0)
            {
                input.Skip(take + 1);
                length++;
                return text.ToString().TrimEnd('\r');
            }

            input.Skip(take);
        }
    }

    private static double? Number(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
    }

    private static double[]? Numbers(string text, int count)
    {
        string[] parts = text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != count)
        {
            return null;
        }

        double[] values = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (Number(parts[i]) is not double v)
            {
                return null;
            }

            values[i] = v;
        }

        return values;
    }

    private static float[] BuildScale()
    {
        float[] scale = new float[256];
        for (int e = 1; e < 256; e++)
        {
            scale[e] = (float)Math.ScaleB(1.0, e - 136);
        }

        return scale;
    }

    /// <summary>The image size and scan order: scanlines are rows, or columns when <see cref="Columns"/>.</summary>
    /// <param name="Width">The image width.</param>
    /// <param name="Height">The image height.</param>
    /// <param name="Format">The pixel format.</param>
    /// <param name="Columns">Whether each scanline is a column.</param>
    /// <param name="MajorForward">Whether scanlines go top to bottom (rows) or left to right (columns).</param>
    /// <param name="MinorForward">Whether pixels within a scanline go left to right (rows) or top to bottom (columns).</param>
    private readonly record struct Header(int Width, int Height, RadiancePixelFormat Format, bool Columns, bool MajorForward, bool MinorForward)
    {
        public int ScanLength => Columns ? Height : Width;

        public int ScanCount => Columns ? Width : Height;
    }

    /// <summary>A 3 × 3 matrix in single precision.</summary>
    private readonly record struct Matrix3x3f(float M00, float M01, float M02, float M10, float M11, float M12, float M20, float M21, float M22)
    {
        public static Matrix3x3f From(Matrix3d m) => new(
            (float)m[0, 0], (float)m[0, 1], (float)m[0, 2], (float)m[1, 0], (float)m[1, 1], (float)m[1, 2], (float)m[2, 0], (float)m[2, 1], (float)m[2, 2]);
    }

    /// <summary>Reads scanlines through the input's window, so most bytes are read straight from its buffer.</summary>
    private ref struct ScanReader(EgamiInput input)
    {
        private const int Window = 64 * 1024;
        private ReadOnlySpan<byte> _data = input.Peek(Window);
        private int _used;

        private readonly ReadOnlySpan<byte> Data => _data;

        /// <summary>
        /// Reads one scanline of <paramref name="length"/> pixels into <paramref name="rgbe"/> as four planes of
        /// <paramref name="length"/> bytes (red, green, blue, exponent); false when the data ends first.
        /// </summary>
        public bool ReadScanline(Span<byte> rgbe, int length)
        {
            // The next scanline needs at most 4 × length bytes when flat and a little more when run-length encoded, so a
            // window refill before each scanline is cheap and keeps the inner loops free of refills for usual widths.
            Refill();
            ReadOnlySpan<byte> data = Data;
            if (length < MinRleLength || length > MaxRleLength)
            {
                return ReadOld(rgbe, length);
            }

            if (data.Length - _used < 4)
            {
                return ReadOld(rgbe, length);
            }

            ReadOnlySpan<byte> start = data.Slice(_used, 4);
            if (start[0] != 2 || start[1] != 2 || (start[2] & 0x80) != 0)
            {
                return ReadOld(rgbe, length);
            }

            if (((start[2] << 8) | start[3]) != length)
            {
                throw input.Corrupt(input.Position + _used, $"A run-length encoded scanline says it holds {(start[2] << 8) | start[3]} pixels; the image has {length}.");
            }

            _used += 4;
            for (int channel = 0; channel < 4; channel++)
            {
                int x = 0;
                while (x < length)
                {
                    if (!Ensure(2) && !(Ensure(1) && Data[_used] <= 128))
                    {
                        return false;
                    }

                    data = Data;
                    int count = data[_used];
                    if (count > 128)
                    {
                        count -= 128;
                        if (count > length - x)
                        {
                            throw input.Corrupt(input.Position + _used, "A run goes past the end of its scanline.");
                        }

                        rgbe.Slice((channel * length) + x, count).Fill(data[_used + 1]);
                        _used += 2;
                        x += count;
                    }
                    else
                    {
                        if (count == 0 || count > length - x)
                        {
                            throw input.Corrupt(input.Position + _used, count == 0 ? "A literal run has length 0." : "A literal run goes past the end of its scanline.");
                        }

                        if (!Ensure(1 + count))
                        {
                            return false;
                        }

                        Data.Slice(_used + 1, count).CopyTo(rgbe[((channel * length) + x)..]);
                        _used += 1 + count;
                        x += count;
                    }
                }
            }

            return true;
        }

        /// <summary>Moves the input past what was used.</summary>
        public void Finish()
        {
            input.Skip(_used);
            _used = 0;
            _data = default;
        }

        /// <summary>
        /// Reads a flat or old-style scanline: four-byte pixels, where a pixel 1, 1, 1, n repeats the one before n times, n shifted
        /// left 8 bits more for each repeat pixel that follows another.
        /// </summary>
        private bool ReadOld(Span<byte> planes, int length)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(length * 4);
            try
            {
                Span<byte> rgbe = buffer.AsSpan(0, length * 4);
                if (!ReadInterleaved(rgbe, length))
                {
                    return false;
                }

                for (int i = 0; i < length; i++)
                {
                    planes[i] = rgbe[i * 4];
                    planes[length + i] = rgbe[(i * 4) + 1];
                    planes[(2 * length) + i] = rgbe[(i * 4) + 2];
                    planes[(3 * length) + i] = rgbe[(i * 4) + 3];
                }

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>Reads a flat or old-style scanline as four-byte pixels.</summary>
        private bool ReadInterleaved(Span<byte> rgbe, int length)
        {
            int x = 0;
            int shift = 0;
            while (x < length)
            {
                if (!Ensure(4))
                {
                    return false;
                }

                ReadOnlySpan<byte> p = Data.Slice(_used, 4);
                _used += 4;
                if (p[0] == 1 && p[1] == 1 && p[2] == 1)
                {
                    if (x == 0)
                    {
                        throw input.Corrupt(input.Position + _used - 4, "A scanline starts with a repeat of the pixel before it.");
                    }

                    long repeat = (long)p[3] << shift;
                    if (repeat > length - x)
                    {
                        throw input.Corrupt(input.Position + _used - 4, "A repeated pixel goes past the end of its scanline.");
                    }

                    Span<byte> previous = rgbe.Slice((x - 1) * 4, 4);
                    for (long i = 0; i < repeat; i++)
                    {
                        previous.CopyTo(rgbe[(x * 4)..]);
                        x++;
                    }

                    shift += 8;
                    if (shift > 24)
                    {
                        shift = 24;
                    }
                }
                else
                {
                    p.CopyTo(rgbe[(x * 4)..]);
                    x++;
                    shift = 0;
                }
            }

            return true;
        }

        /// <summary>Makes <paramref name="count"/> bytes past the used ones available; false when the data ends first.</summary>
        private bool Ensure(int count)
        {
            if (Data.Length - _used >= count)
            {
                return true;
            }

            Refill();
            return Data.Length - _used >= count;
        }

        private void Refill()
        {
            if (_used > 0)
            {
                input.Skip(_used);
                _used = 0;
                _data = input.Peek(Window);
            }
        }
    }
}
