using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// Turns a frame's stored values into loaded pixels (PS3.3 C.11 and C.7.6.3): for monochrome images the Modality LUT or rescale,
/// then the VOI window or VOI LUT (or the frame's range when the file gives neither), then MONOCHROME1 or Presentation LUT
/// Shape inversion; palette indices through the colour tables; YBR samples to RGB.
/// </summary>
internal sealed class DicomDisplay
{
    private readonly DicomImageInfo _info;
    private readonly DicomFrameParameters _parameters;
    private readonly DicomReaderOptions _options;
    private readonly DicomLut? _red;
    private readonly DicomLut? _green;
    private readonly DicomLut? _blue;

    public DicomDisplay(DicomImageInfo info, int frame, DicomReaderOptions options)
    {
        _info = info;
        _options = options;
        _parameters = DicomFrameParameters.Read(info, frame);
        if ((options.WindowCenter is null) != (options.WindowWidth is null))
        {
            throw new ArgumentException("DicomReaderOptions.WindowCenter and WindowWidth are set together.", nameof(options));
        }

        if (info.IsPalette)
        {
            DicomDataset data = info.File.Dataset;
            bool big = info.File.TransferSyntax.IsBigEndian;
            _red = Palette(data, 0x1101, 0x1201, 0x1221, info.IsSigned, big);
            _green = Palette(data, 0x1102, 0x1202, 0x1222, info.IsSigned, big);
            _blue = Palette(data, 0x1103, 0x1203, 0x1223, info.IsSigned, big);
        }

        Layout = PickLayout();
        Details = new DicomDetails
        {
            Frame = frame,
            FrameCount = info.Frames,
            TransferSyntax = info.File.TransferSyntax,
            PhotometricInterpretation = info.Photometric,
            BitsStored = info.BitsStored,
            IsSigned = info.IsSigned,
            Values = options.Values,
            FrameTime = _parameters.FrameTime,
            FrameRate = _parameters.FrameRate,
        };
    }

    /// <summary>Gets the pixel layout the load gives.</summary>
    public PixelLayoutInfo Layout { get; }

    /// <summary>Gets what was read and done; the transforms are filled in by <see cref="Render"/>.</summary>
    public DicomDetails Details { get; }

    /// <summary>Gets whether the palette tables are there, so indices show as colour.</summary>
    private bool ShowsPalette => _info.IsPalette && _options.Values != DicomValues.Stored && _red is not null && _green is not null && _blue is not null;

    /// <summary>Returns the layout a load of this image gives, without decoding it.</summary>
    public static PixelLayoutInfo LayoutOf(DicomImageInfo info, DicomReaderOptions options)
    {
        return new DicomDisplay(info, 0, options).Layout;
    }

    private PixelLayoutInfo PickLayout()
    {
        if (_info.SamplesPerPixel >= 3)
        {
            return _info.BitsStored > 8 ? Rgb16.Layout : Rgb8.Layout;
        }

        if (ShowsPalette)
        {
            return Math.Max(_red!.Bits, Math.Max(_green!.Bits, _blue!.Bits)) > 8 ? Rgb16.Layout : Rgb8.Layout;
        }

        if (_options.Values != DicomValues.Display || _info.IsPalette)
        {
            return GrayF32.Layout;
        }

        return _info.BitsStored > 8 ? Gray16.Layout : Gray8.Layout;
    }

    /// <summary>Returns the pixels of a decoded frame, row by row, as <see cref="Layout"/> says.</summary>
    public DicomRender Render(DicomSamples frame, Action<string, string> warn)
    {
        if (_info.SamplesPerPixel >= 3)
        {
            return Colour(frame);
        }

        if (ShowsPalette)
        {
            return new PaletteRender(frame.Samples, _info.Columns, _red!, _green!, _blue!, Layout.BitsPerChannel > 8);
        }

        if (_info.IsPalette && _options.Values != DicomValues.Stored)
        {
            warn("PaletteMissing", "The palette colour tables are missing or malformed; the indices load as GrayF32.");
        }

        return Monochrome(frame.Samples, warn);
    }

    private static DicomLut? Palette(DicomDataset data, ushort descriptor, ushort plain, ushort segmented, bool signed, bool big)
    {
        DicomElement? d = data[new DicomTag(0x0028, descriptor)];
        return DicomLut.Read(d, data[new DicomTag(0x0028, plain)], signed, big)
            ?? DicomLut.ReadSegmented(d, data[new DicomTag(0x0028, segmented)], signed, big);
    }

    private ColourRender Colour(DicomSamples frame)
    {
        int bits = _info.BitsStored;
        int max = (1 << Math.Min(bits, 16)) - 1;
        bool wide = Layout.BitsPerChannel > 8;
        int target = wide ? 65535 : 255;
        string photometric = _info.Photometric;
        bool convert = _options.Values != DicomValues.Stored && !frame.IsRgb;
        DicomYbr ybr = !convert ? DicomYbr.None
            : photometric is "YBR_FULL" or "YBR_FULL_422" ? DicomYbr.Full
            : photometric is "YBR_PARTIAL_422" or "YBR_PARTIAL_420" ? DicomYbr.Partial
            : DicomYbr.None;
        return new ColourRender(frame.Samples, _info.Columns, max, target, ybr, Math.Max(0, bits - 16));
    }

    private DicomRender Monochrome(int[] samples, Action<string, string> warn)
    {
        int bits = _info.BitsStored;

        // Unsigned 32-bit samples hold their bits as they are; values above int.MaxValue read negative unless taken as uint.
        bool unsigned32 = !_info.IsSigned && bits == 32;
        (long min, long max) = bits <= 16
            ? (_info.IsSigned ? (-(1 << (bits - 1)), (1 << (bits - 1)) - 1) : (0, (1 << bits) - 1))
            : Range(samples, unsigned32);
        Func<long, double> transform = Transform(() => Range(samples, unsigned32), warn);

        // The table covers every value the stored bits hold (inputs outside it, from a codestream deeper than Bits Stored, are
        // clamped to it); wider data is scanned for its range.
        bool float32 = Layout.ChannelType == PixelChannelType.Float32;
        long span = max - min + 1;
        if (span <= 1 << 22 && !unsigned32)
        {
            if (float32)
            {
                float[] table = new float[span];
                for (long i = 0; i < span; i++)
                {
                    table[i] = (float)transform(min + i);
                }

                return new TableRender<float>(samples, _info.Columns, (int)min, table);
            }

            ushort[] levels = new ushort[span];
            for (long i = 0; i < span; i++)
            {
                levels[i] = (ushort)transform(min + i);
            }

            return Layout.BitsPerChannel > 8
                ? new TableRender<ushort>(samples, _info.Columns, (int)min, levels)
                : new TableRender<byte>(samples, _info.Columns, (int)min, [.. levels.Select(v => (byte)v)]);
        }

        return new FunctionRender(samples, _info.Columns, transform, float32 ? 32 : Layout.BitsPerChannel, unsigned32);
    }

    /// <summary>
    /// Returns the shown level of stored values <paramref name="first"/> to <paramref name="first"/> + <paramref name="count"/> - 1,
    /// for an image shown as Gray8 or Gray16; <paramref name="range"/> gives the frame's smallest and largest stored values,
    /// asked for only when the frame gives no window.
    /// </summary>
    public ushort[] Levels(int first, int count, Func<(long Min, long Max)> range, Action<string, string> warn)
    {
        Func<long, double> transform = Transform(range, warn);
        ushort[] levels = new ushort[count];
        for (int i = 0; i < count; i++)
        {
            levels[i] = (ushort)transform(first + i);
        }

        return levels;
    }

    /// <summary>Returns the whole transform from a stored value to a loaded value, for <see cref="DicomReaderOptions.Values"/>.</summary>
    private Func<long, double> Transform(Func<(long Min, long Max)> range, Action<string, string> warn)
    {
        DicomFrameParameters p = _parameters;
        DicomLut? modality = p.ModalityLut;
        double slope = modality is null ? p.Slope : 1;
        double intercept = modality is null ? p.Intercept : 0;
        Details.RescaleSlope = slope;
        Details.RescaleIntercept = intercept;
        Details.AppliedModalityLut = modality is not null;
        Func<long, double> toModality = modality is not null ? x => modality.Lookup(x) : x => (x * slope) + intercept;
        Func<double, double> value;
        if (_options.Values == DicomValues.Stored)
        {
            value = static m => m;
            toModality = static x => x;
        }
        else if (_options.Values == DicomValues.Modality)
        {
            value = static m => m;
        }
        else
        {
            value = Voi(toModality, range, warn);
        }

        return x => value(toModality(x));
    }

    /// <summary>Returns the smallest and largest of the samples, taken as unsigned when <paramref name="unsigned32"/>.</summary>
    private static (long Min, long Max) Range(int[] samples, bool unsigned32)
    {
        if (samples.Length == 0)
        {
            return (0, 0);
        }

        long min = long.MaxValue;
        long max = long.MinValue;
        foreach (int v in samples)
        {
            long value = unsigned32 ? (uint)v : v;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
        }

        return (min, max);
    }

    /// <summary>Returns the VOI and presentation transform, from a modality value to a shown level of the output's range.</summary>
    private Func<double, double> Voi(Func<long, double> toModality, Func<(long Min, long Max)> range, Action<string, string> warn)
    {
        DicomFrameParameters p = _parameters;
        double top = Layout.BitsPerChannel > 8 ? 65535 : 255;
        bool invert = p.PresentationShape is string shape ? shape == "INVERSE" : _info.Photometric == "MONOCHROME1";
        Details.Inverted = invert;
        Func<double, double> voi;
        int index = _options.WindowIndex;
        if (_options.WindowCenter is double center && _options.WindowWidth is double width)
        {
            voi = Window(center, width, _options.WindowFunction ?? p.Function, top);
        }
        else if (p.VoiLuts.Count > 0 && !(_options.PreferWindow && p.Centers.Length > 0))
        {
            DicomLut lut = p.VoiLuts[Math.Min(index, p.VoiLuts.Count - 1)];
            double scale = top / lut.Max;
            voi = m => lut.Lookup((long)Math.Round(m)) * scale;
            Details.AppliedVoiLut = true;
        }
        else if (p.Centers.Length > 0 && Valid(p, Math.Min(index, p.Centers.Length - 1)) is int pick)
        {
            voi = Window(p.Centers[pick], p.Widths[pick], p.Function, top);
        }
        else
        {
            if (p.Centers.Length > 0)
            {
                warn("WindowInvalid", "The file's window width is not greater than 0; the frame's range is shown instead.");
            }

            (long min, long max) = range();
            double low = Math.Min(toModality(min), toModality(max));
            double high = Math.Max(toModality(min), toModality(max));
            if (_parameters.ModalityLut is not null)
            {
                low = double.MaxValue;
                high = double.MinValue;
                for (long x = min; x <= max && x - min < 1 << 22; x++)
                {
                    double m = toModality(x);
                    low = Math.Min(low, m);
                    high = Math.Max(high, m);
                }
            }

            double spread = high - low;
            voi = spread > 0 ? m => (m - low) / spread * top : _ => 0;
            Details.AppliedMinMax = true;
        }

        return invert ? m => top - Level(voi(m), top) : m => Level(voi(m), top);
    }

    /// <summary>Returns the window to use: the one asked for, or the first with a usable width.</summary>
    private static int? Valid(DicomFrameParameters p, int index)
    {
        if (p.Widths[index] > 0 || (p.Function == DicomWindowFunction.Linear && p.Widths[index] >= 0))
        {
            return index;
        }

        for (int i = 0; i < p.Widths.Length; i++)
        {
            if (p.Widths[i] > 0)
            {
                return i;
            }
        }

        return null;
    }

    private Func<double, double> Window(double c, double w, DicomWindowFunction function, double top)
    {
        Details.WindowCenter = c;
        Details.WindowWidth = w;
        Details.WindowFunction = function;
        switch (function)
        {
            case DicomWindowFunction.LinearExact:
                return x => x <= c - (w / 2) ? 0 : x > c + (w / 2) ? top : ((x - c) / w + 0.5) * top;
            case DicomWindowFunction.Sigmoid:
                return x => top / (1 + Math.Exp(-4 * (x - c) / w));
            default:
                double span = Math.Max(w - 1, 0);
                double low = c - 0.5 - (span / 2);
                double high = c - 0.5 + (span / 2);
                return span == 0
                    ? x => x <= low ? 0 : top
                    : x => x <= low ? 0 : x > high ? top : (((x - (c - 0.5)) / span) + 0.5) * top;
        }
    }

    /// <summary>Rounds a shown value to a level of the output.</summary>
    private static double Level(double y, double top) => double.IsNaN(y) ? 0 : Math.Clamp(Math.Floor(y + 0.5), 0, top);
}

/// <summary>How three-sample frames are converted to RGB.</summary>
internal enum DicomYbr
{
    None,
    Full,
    Partial,
}

/// <summary>Writes one row of loaded pixels from a frame's samples.</summary>
internal abstract class DicomRender
{
    /// <summary>Writes row <paramref name="y"/>, in the natural layout, to <paramref name="row"/>.</summary>
    public abstract void Row(int y, Span<byte> row);
}

/// <summary>Monochrome values through a table indexed by stored value − <c>min</c>.</summary>
internal sealed class TableRender<T>(int[] samples, int width, int min, T[] table) : DicomRender
    where T : unmanaged
{
    public override void Row(int y, Span<byte> row)
    {
        Span<T> target = MemoryMarshal.Cast<byte, T>(row)[..width];
        ReadOnlySpan<int> source = samples.AsSpan(y * width, width);
        T[] t = table;
        int last = t.Length - 1;
        for (int x = 0; x < target.Length; x++)
        {
            int at = source[x] - min;
            if ((uint)at > (uint)last)
            {
                at = at < 0 ? 0 : last;
            }

            target[x] = t[at];
        }
    }
}

/// <summary>RGB pixels a codec already decoded, copied row by row.</summary>
internal sealed class RasterRender(Raster<Rgb8> raster) : DicomRender, IDisposable
{
    public override void Row(int y, Span<byte> row) => MemoryMarshal.AsBytes(raster.Row(y)).CopyTo(row);

    public void Dispose() => raster.Dispose();
}

/// <summary>Monochrome values computed one by one, for stored ranges too wide for a table.</summary>
internal sealed class FunctionRender(int[] samples, int width, Func<long, double> transform, int bits, bool unsigned32) : DicomRender
{
    public override void Row(int y, Span<byte> row)
    {
        ReadOnlySpan<int> source = samples.AsSpan(y * width, width);
        Func<int, double> value = unsigned32 ? v => transform((uint)v) : v => transform(v);
        if (bits == 32)
        {
            Span<float> target = MemoryMarshal.Cast<byte, float>(row);
            for (int x = 0; x < width; x++)
            {
                target[x] = (float)value(source[x]);
            }
        }
        else if (bits > 8)
        {
            Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(row);
            for (int x = 0; x < width; x++)
            {
                target[x] = (ushort)value(source[x]);
            }
        }
        else
        {
            for (int x = 0; x < width; x++)
            {
                row[x] = (byte)value(source[x]);
            }
        }
    }
}

/// <summary>Palette indices through the red, green and blue tables.</summary>
internal sealed class PaletteRender : DicomRender
{
    private readonly int[] _samples;
    private readonly int _width;
    private readonly DicomLut _red;
    private readonly DicomLut _green;
    private readonly DicomLut _blue;
    private readonly bool _wide;
    private readonly ushort[] _scaleRed;
    private readonly ushort[] _scaleGreen;
    private readonly ushort[] _scaleBlue;

    public PaletteRender(int[] samples, int width, DicomLut red, DicomLut green, DicomLut blue, bool wide)
    {
        _samples = samples;
        _width = width;
        _red = red;
        _green = green;
        _blue = blue;
        _wide = wide;
        _scaleRed = Scale(red, wide);
        _scaleGreen = Scale(green, wide);
        _scaleBlue = Scale(blue, wide);
    }

    public override void Row(int y, Span<byte> row)
    {
        ReadOnlySpan<int> source = _samples.AsSpan(y * _width, _width);
        if (_wide)
        {
            Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(row);
            for (int x = 0, at = 0; x < source.Length; x++, at += 3)
            {
                target[at] = _scaleRed[Index(_red, source[x])];
                target[at + 1] = _scaleGreen[Index(_green, source[x])];
                target[at + 2] = _scaleBlue[Index(_blue, source[x])];
            }
        }
        else
        {
            for (int x = 0, at = 0; x < source.Length; x++, at += 3)
            {
                row[at] = (byte)_scaleRed[Index(_red, source[x])];
                row[at + 1] = (byte)_scaleGreen[Index(_green, source[x])];
                row[at + 2] = (byte)_scaleBlue[Index(_blue, source[x])];
            }
        }
    }

    private static int Index(DicomLut lut, int value)
    {
        long at = (long)value - lut.First;
        return at < 0 ? 0 : at >= lut.Entries.Length ? lut.Entries.Length - 1 : (int)at;
    }

    /// <summary>Returns each entry scaled from the table's bits to the output's.</summary>
    private static ushort[] Scale(DicomLut lut, bool wide)
    {
        double max = lut.Max;
        double target = wide ? 65535 : 255;
        ushort[] scaled = new ushort[lut.Entries.Length];
        for (int i = 0; i < scaled.Length; i++)
        {
            scaled[i] = (ushort)Math.Clamp(Math.Floor((lut.Entries[i] * target / max) + 0.5), 0, target);
        }

        return scaled;
    }
}

/// <summary>Three-sample pixels, converted from YBR when asked, scaled to 8 or 16 bits.</summary>
internal sealed class ColourRender : DicomRender
{
    private readonly int[] _samples;
    private readonly int _width;
    private readonly int _max;
    private readonly int _target;
    private readonly DicomYbr _ybr;
    private readonly ushort[] _scale;
    private readonly int _shift;

    /// <summary>Renders colour samples of up to 16 bits; wider ones are first shifted right by <paramref name="shift"/> bits.</summary>
    public ColourRender(int[] samples, int width, int max, int target, DicomYbr ybr, int shift)
    {
        _shift = shift;
        _samples = samples;
        _width = width;
        _max = max;
        _target = target;
        _ybr = ybr;
        _scale = new ushort[max + 1];
        for (int v = 0; v <= max; v++)
        {
            _scale[v] = (ushort)((((long)v * target) + (max >> 1)) / max);
        }
    }

    public override void Row(int y, Span<byte> row)
    {
        int count = _width * 3;
        ReadOnlySpan<int> source = _samples.AsSpan(y * count, count);
        bool wide = _target > 255;
        Span<ushort> wideRow = wide ? MemoryMarshal.Cast<byte, ushort>(row) : default;
        int max = _max;
        double half = (max + 1) / 2;
        for (int i = 0; i < count; i += 3)
        {
            int r = source[i];
            int g = source[i + 1];
            int b = source[i + 2];
            if (_shift > 0)
            {
                // Samples wider than 16 bits keep their top 16 (32-bit ones taken as unsigned, which their bits are).
                r = (int)((uint)r >> _shift);
                g = (int)((uint)g >> _shift);
                b = (int)((uint)b >> _shift);
            }
            if (_ybr != DicomYbr.None)
            {
                double l = r;
                double cb = g - half;
                double cr = b - half;
                if (_ybr == DicomYbr.Partial)
                {
                    // ITU-R BT.601 studio range: Y from 16 to 235, Cb and Cr from 16 to 240, at 8 bits.
                    double unit = (max + 1) / 256.0;
                    l = (l - (16 * unit)) * 255.0 / 219.0;
                    cb *= 255.0 / 224.0;
                    cr *= 255.0 / 224.0;
                }

                r = Clamp(l + (1.402 * cr), max);
                g = Clamp(l - (0.344136 * cb) - (0.714136 * cr), max);
                b = Clamp(l + (1.772 * cb), max);
            }

            r = Math.Clamp(r, 0, max);
            g = Math.Clamp(g, 0, max);
            b = Math.Clamp(b, 0, max);
            if (wide)
            {
                wideRow[i] = _scale[r];
                wideRow[i + 1] = _scale[g];
                wideRow[i + 2] = _scale[b];
            }
            else
            {
                row[i] = (byte)_scale[r];
                row[i + 1] = (byte)_scale[g];
                row[i + 2] = (byte)_scale[b];
            }
        }
    }

    private static int Clamp(double v, int max) => (int)Math.Clamp(Math.Floor(v + 0.5), 0, max);
}
