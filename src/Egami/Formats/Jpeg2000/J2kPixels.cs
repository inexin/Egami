using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Jpeg2000;

/// <summary>The pixel type a JPEG 2000 image loads as (design question 7).</summary>
internal enum J2kNatural
{
    Gray8,
    Gray16,
    GrayF32,
    GrayAlpha8,
    GrayAlpha16,
    Rgb8,
    Rgb16,
    RgbF32,
    Rgba8,
    Rgba16,
    RgbaF32,
    Cmyk8,
    Cmyk16,
}

/// <summary>The color model the channels are in before conversion to the pixel type.</summary>
internal enum J2kModel
{
    Gray,
    Rgb,
    Sycc,
    Lab,
    Cmyk,
}

/// <summary>
/// Turns decoded components into pixels: the palette and component mapping (pclr, cmap), the channel definitions (cdef), the
/// color space (sYCC and CIELab to sRGB), widening to the pixel type by bit replication, and subsampled components repeated
/// over the grid points they cover.
/// </summary>
internal sealed class J2kPixels
{
    /// <summary>One channel: the component it comes from and, through a palette, which column it takes.</summary>
    private readonly record struct Channel(int Component, int Column, int Precision, bool Signed);

    private readonly Channel[] _color;
    private readonly Channel? _alpha;
    private readonly bool _premultiplied;
    private readonly Jp2Palette? _palette;
    private readonly int _depth;
    private readonly double[] _lab = [];
    private readonly Colors.Vector3d _labWhite;
    private readonly Colors.Matrix3d _labToRgb;

    private J2kPixels(J2kModel model, Channel[] color, Channel? alpha, bool premultiplied, Jp2Palette? palette, uint[] labParameters)
    {
        Model = model;
        _color = color;
        _alpha = alpha;
        _premultiplied = premultiplied;
        _palette = palette;
        int bits = 1;
        foreach (Channel channel in color)
        {
            bits = Math.Max(bits, channel.Precision);
        }

        if (alpha is { } a)
        {
            bits = Math.Max(bits, a.Precision);
        }

        _depth = bits <= 8 ? 8 : bits <= 16 ? 16 : 32;
        if (model is J2kModel.Sycc or J2kModel.Lab && _depth == 32)
        {
            _depth = 16;
        }

        if (model == J2kModel.Cmyk && _depth == 32)
        {
            _depth = 16;
        }

        Natural = (model, _depth, alpha is not null) switch
        {
            (J2kModel.Gray, 8, false) => J2kNatural.Gray8,
            (J2kModel.Gray, 16, false) => J2kNatural.Gray16,
            (J2kModel.Gray, _, false) => J2kNatural.GrayF32,
            (J2kModel.Gray, 8, true) => J2kNatural.GrayAlpha8,
            (J2kModel.Gray, 16, true) => J2kNatural.GrayAlpha16,
            (J2kModel.Gray, _, true) => J2kNatural.RgbaF32,
            (J2kModel.Cmyk, 8, _) => J2kNatural.Cmyk8,
            (J2kModel.Cmyk, _, _) => J2kNatural.Cmyk16,
            (_, 8, false) => J2kNatural.Rgb8,
            (_, 16, false) => J2kNatural.Rgb16,
            (_, _, false) => J2kNatural.RgbF32,
            (_, 8, true) => J2kNatural.Rgba8,
            (_, 16, true) => J2kNatural.Rgba16,
            _ => J2kNatural.RgbaF32,
        };

        if (model == J2kModel.Lab)
        {
            // CIELab parameters (T.801 M.11.7.4): ranges and offsets of L, a and b, then the illuminant; defaults for an absent EP.
            _lab = labParameters.Length >= 6 ? [labParameters[0], labParameters[1], labParameters[2], labParameters[3], labParameters[4], labParameters[5]] : [];
            _labWhite = Colors.Chromaticity.D50.ToXyz();
            _labToRgb = Colors.ColorValueMath.XyzToRgb * Colors.Matrix3d.Bradford(_labWhite, Colors.Chromaticity.D65.ToXyz());
        }
    }

    internal J2kModel Model { get; }

    internal J2kNatural Natural { get; }

    internal PixelLayoutInfo Layout => Natural switch
    {
        J2kNatural.Gray8 => Gray8.Layout,
        J2kNatural.Gray16 => Gray16.Layout,
        J2kNatural.GrayF32 => GrayF32.Layout,
        J2kNatural.GrayAlpha8 => GrayAlpha8.Layout,
        J2kNatural.GrayAlpha16 => GrayAlpha16.Layout,
        J2kNatural.Rgb8 => Rgb8.Layout,
        J2kNatural.Rgb16 => Rgb16.Layout,
        J2kNatural.RgbF32 => RgbF32.Layout,
        J2kNatural.Rgba8 => Rgba8.Layout,
        J2kNatural.Rgba16 => Rgba16.Layout,
        J2kNatural.RgbaF32 => RgbaF32.Layout,
        J2kNatural.Cmyk8 => Cmyk8.Layout,
        _ => Cmyk16.Layout,
    };

    /// <summary>
    /// Gets whether the image is sYCC straight from components 0, 1 and 2, unsigned, without palette or alpha: it has YUV planes.
    /// </summary>
    internal bool PlainSycc => Model == J2kModel.Sycc && _alpha is null && _palette is null
        && _color[0].Component == 0 && _color[1].Component == 1 && _color[2].Component == 2
        && !_color[0].Signed && !_color[1].Signed && !_color[2].Signed;

    /// <summary>Gets whether a signed component was offset to unsigned.</summary>
    internal bool OffsetSigned { get; private set; }

    /// <summary>Gets the number of channels dropped because they were neither color nor alpha.</summary>
    internal int Dropped { get; private set; }

    /// <summary>Works out the channels and color model from the file's boxes and the codestream's components.</summary>
    internal static J2kPixels Create(Jp2File file, J2kComponent[] components, Jpeg2000Components mode)
    {
        if (mode == Jpeg2000Components.Raw || file.Container == Jpeg2000Container.Codestream)
        {
            return Plain(components, file);
        }

        // Channels: the component mapping through the palette, else the components themselves (I.5.3.5).
        List<Channel> channels = [];
        Jp2Palette? palette = file.Palette;
        if (palette is not null && file.Mapping is { } mapping)
        {
            foreach ((int component, int type, int column) in mapping)
            {
                if (component >= components.Length)
                {
                    throw new InvalidDataException($"The component mapping names component {component}, which the codestream does not have.");
                }

                if (type == 1)
                {
                    if (column >= palette.Bits.Length)
                    {
                        throw new InvalidDataException($"The component mapping names palette column {column}, which the palette does not have.");
                    }

                    channels.Add(new Channel(component, column, palette.Bits[column], palette.Signed[column]));
                }
                else
                {
                    channels.Add(new Channel(component, -1, components[component].Precision, components[component].Signed));
                }
            }
        }
        else
        {
            palette = null;
            for (int c = 0; c < components.Length; c++)
            {
                channels.Add(new Channel(c, -1, components[c].Precision, components[c].Signed));
            }
        }

        J2kModel model;
        int colors;
        switch (file.ColorMethod == 1 ? file.EnumeratedColorSpace : -1)
        {
            case 17:
                (model, colors) = (J2kModel.Gray, 1);
                break;
            case 18:
                (model, colors) = (J2kModel.Sycc, 3);
                break;
            case 12:
                (model, colors) = (J2kModel.Cmyk, 4);
                break;
            case 14:
                (model, colors) = (J2kModel.Lab, 3);
                break;
            case 16 or 20 or 21:
                (model, colors) = (J2kModel.Rgb, 3);
                break;
            default:
                // ICC or unknown: the channels say; an ICC profile's data space would say too, but its pixels stay as stored.
                (model, colors) = IccModel(file) ?? (channels.Count switch
                {
                    1 or 2 => (J2kModel.Gray, 1),
                    4 when file.Channels is null && file.ColorMethod == 0 => (J2kModel.Cmyk, 4),
                    _ => (J2kModel.Rgb, 3),
                });
                break;
        }

        if (channels.Count < colors)
        {
            if (colors == 3 && channels.Count is 1 or 2)
            {
                (model, colors) = (J2kModel.Gray, 1);
            }
            else
            {
                throw new InvalidDataException($"The image has {channels.Count} channels, fewer than its color space's {colors}.");
            }
        }

        Channel[] color = new Channel[colors];
        Channel? alpha = null;
        bool premultiplied = false;
        int used = 0;
        if (file.Channels is { } definitions)
        {
            bool[] filled = new bool[colors];
            foreach ((int index, int type, int association) in definitions)
            {
                if (index >= channels.Count)
                {
                    continue;
                }

                if (type == 0 && association >= 1 && association <= colors && !filled[association - 1])
                {
                    color[association - 1] = channels[index];
                    filled[association - 1] = true;
                    used++;
                }
                else if (type is 1 or 2 && alpha is null && association is 0 or 65535 or >= 1)
                {
                    alpha = channels[index];
                    premultiplied = type == 2;
                    used++;
                }
            }

            for (int i = 0; i < colors; i++)
            {
                if (!filled[i])
                {
                    // An undefined color takes the channel in its place.
                    color[i] = channels[i];
                    used++;
                }
            }
        }
        else
        {
            for (int i = 0; i < colors; i++)
            {
                color[i] = channels[i];
            }

            used = colors;
            if (channels.Count == colors + 1)
            {
                alpha = channels[colors];
                used++;
            }
        }

        J2kPixels pixels = new(model, color, alpha, premultiplied, palette, file.LabParameters);
        pixels.Dropped = Math.Max(0, channels.Count - used);
        return pixels;
    }

    /// <summary>The model an ICC profile's data color space gives (bytes 16 to 19 of its header).</summary>
    private static (J2kModel, int)? IccModel(Jp2File file)
    {
        ReadOnlySpan<byte> icc = file.Icc.Span;
        if (file.ColorMethod is not (2 or 3) || icc.Length < 20)
        {
            return null;
        }

        return System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(icc[16..]) switch
        {
            0x47524159 => (J2kModel.Gray, 1),
            0x434D594B => (J2kModel.Cmyk, 4),
            0x52474220 => (J2kModel.Rgb, 3),
            _ => null,
        };
    }

    /// <summary>The components as stored: 1 gray, 2 gray and alpha, 3 RGB, 4 CMYK (design question 8).</summary>
    private static J2kPixels Plain(J2kComponent[] components, Jp2File file)
    {
        Channel Of(int c) => new(c, -1, components[c].Precision, components[c].Signed);
        J2kPixels pixels = components.Length switch
        {
            1 => new J2kPixels(J2kModel.Gray, [Of(0)], null, false, null, []),
            2 => new J2kPixels(J2kModel.Gray, [Of(0)], Of(1), false, null, []),
            3 => new J2kPixels(J2kModel.Rgb, [Of(0), Of(1), Of(2)], null, false, null, []),
            _ => new J2kPixels(J2kModel.Cmyk, [Of(0), Of(1), Of(2), Of(3)], null, false, null, []),
        };
        pixels.Dropped = Math.Max(0, components.Length - 4);
        return pixels;
    }

    /// <summary>
    /// Writes row <paramref name="y"/> of the image, at the decode's reduction, into <paramref name="row"/> in the natural
    /// layout, from column <paramref name="x0"/>.
    /// </summary>
    internal void Row(J2kImage image, J2kGrid grid, int y, int x0, Span<byte> row)
    {
        int width = row.Length / Layout.BytesPerPixel;
        int count = _color.Length + (_alpha is null ? 0 : 1);
        if (Model is J2kModel.Gray or J2kModel.Rgb or J2kModel.Cmyk && !_premultiplied && _depth <= 16)
        {
            // Integer path: each channel's samples widened by bit replication, channel by channel.
            for (int k = 0; k < count; k++)
            {
                Channel channel = k < _color.Length ? _color[k] : _alpha!.Value;
                int c = channel.Component;
                ReadOnlySpan<int> plane = image.Planes[c].AsSpan((grid.Rows[c][y] - image.RowOffsets[c]) * image.Widths[c], image.Widths[c]);
                ReadOnlySpan<int> columns = grid.Columns[c].AsSpan(x0, width);
                if (channel.Column < 0 && !channel.Signed && channel.Precision == _depth && grid.Columns[c][0] == 0 && image.Components[c].Dx == 1)
                {
                    // Stored at the pixel type's depth, one sample per pixel: a plain narrowing copy.
                    ReadOnlySpan<int> samples = plane.Slice(x0, width);
                    if (_depth == 8)
                    {
                        Narrow(samples, row, k, count);
                    }
                    else
                    {
                        Narrow(samples, MemoryMarshal.Cast<byte, ushort>(row), k, count);
                    }

                    continue;
                }

                if (_depth == 8)
                {
                    Span<byte> target = row;
                    for (int i = 0; i < width; i++)
                    {
                        target[(i * count) + k] = (byte)Widen(channel, plane[columns[i]], image.Components[c], 8);
                    }
                }
                else
                {
                    Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(row);
                    for (int i = 0; i < width; i++)
                    {
                        target[(i * count) + k] = (ushort)Widen(channel, plane[columns[i]], image.Components[c], 16);
                    }
                }
            }

            return;
        }

        Span<double> values = stackalloc double[5];
        for (int i = 0; i < width; i++)
        {
            int x = x0 + i;
            for (int k = 0; k < count; k++)
            {
                Channel channel = k < _color.Length ? _color[k] : _alpha!.Value;
                int c = channel.Component;
                int v = image.Planes[c][((grid.Rows[c][y] - image.RowOffsets[c]) * image.Widths[c]) + grid.Columns[c][x]];
                values[k] = Value(channel, v, image.Components[c]);
            }

            Store(values, count, row, i);
        }
    }

    private static void Narrow(ReadOnlySpan<int> samples, Span<byte> target, int k, int count)
    {
        if (count == 1)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                target[i] = (byte)samples[i];
            }

            return;
        }

        for (int i = 0, t = k; i < samples.Length; i++, t += count)
        {
            target[t] = (byte)samples[i];
        }
    }

    private static void Narrow(ReadOnlySpan<int> samples, Span<ushort> target, int k, int count)
    {
        for (int i = 0, t = k; i < samples.Length; i++, t += count)
        {
            target[t] = (ushort)samples[i];
        }
    }

    /// <summary>Returns a channel's sample, through the palette and offset to unsigned, widened to <paramref name="depth"/> bits by bit replication.</summary>
    private uint Widen(Channel channel, int v, J2kComponent component, int depth)
    {
        uint u = Unsigned(channel, v, component);
        int p = channel.Precision;
        if (p >= depth)
        {
            return u >> (p - depth);
        }

        uint r = 0;
        for (int shift = depth - p; shift > -p; shift -= p)
        {
            r |= shift >= 0 ? u << shift : u >> -shift;
        }

        return r;
    }

    /// <summary>Returns a channel's sample through the palette, signed values offset to unsigned and clamped to the channel's range.</summary>
    private uint Unsigned(Channel channel, int v, J2kComponent component)
    {
        if (channel.Column >= 0)
        {
            int entries = _palette!.Entries;
            int index = component.Signed ? v + (1 << (component.Precision - 1)) : v;
            v = _palette.Values[(channel.Column * entries) + Math.Clamp(index, 0, entries - 1)];
        }

        long max = (1L << channel.Precision) - 1;
        long u = v;
        if (channel.Signed)
        {
            u += 1L << (channel.Precision - 1);
            OffsetSigned = true;
        }

        return (uint)Math.Clamp(u, 0, max);
    }

    /// <summary>Returns a channel's value, 0 to 1, from a decoded sample: through the palette, signed samples offset.</summary>
    private double Value(Channel channel, int v, J2kComponent component)
    {
        return Unsigned(channel, v, component) / (double)((1L << channel.Precision) - 1);
    }

    /// <summary>Converts a pixel's channel values (0 to 1) to the model and stores it.</summary>
    private void Store(Span<double> v, int count, Span<byte> row, int i)
    {
        switch (Model)
        {
            case J2kModel.Sycc:
                {
                    double y = v[0];
                    double cb = v[1] - 0.5;
                    double cr = v[2] - 0.5;
                    v[0] = y + (1.402 * cr);
                    v[1] = y - (0.344136 * cb) - (0.714136 * cr);
                    v[2] = y + (1.772 * cb);
                    break;
                }

            case J2kModel.Lab:
                (v[0], v[1], v[2]) = Lab(v[0], v[1], v[2]);
                break;
        }

        if (_premultiplied && count == _color.Length + 1)
        {
            double a = v[count - 1];
            for (int k = 0; k < _color.Length; k++)
            {
                v[k] = a > 0 ? Math.Min(1, v[k] / a) : 0;
            }
        }

        switch (_depth)
        {
            case 8:
                {
                    Span<byte> target = row.Slice(i * count, count);
                    for (int k = 0; k < count; k++)
                    {
                        target[k] = (byte)Math.Round(Math.Clamp(v[k], 0, 1) * 255);
                    }

                    break;
                }

            case 16:
                {
                    Span<ushort> target = MemoryMarshal.Cast<byte, ushort>(row).Slice(i * count, count);
                    for (int k = 0; k < count; k++)
                    {
                        target[k] = (ushort)Math.Round(Math.Clamp(v[k], 0, 1) * 65535);
                    }

                    break;
                }

            default:
                {
                    // Float types: gray with alpha has no float type of its own and widens to RGBA.
                    int stride = Layout.BytesPerPixel / 4;
                    Span<float> target = MemoryMarshal.Cast<byte, float>(row).Slice(i * stride, stride);
                    if (Model == J2kModel.Gray && count == 2)
                    {
                        target[0] = target[1] = target[2] = (float)v[0];
                        target[3] = (float)v[1];
                    }
                    else
                    {
                        for (int k = 0; k < count; k++)
                        {
                            target[k] = (float)v[k];
                        }
                    }

                    break;
                }
        }
    }

    /// <summary>CIELab (scaled 0 to 1 by the sample range) to gamma-encoded sRGB, with T.801's default ranges unless EP gives them.</summary>
    private (double R, double G, double B) Lab(double l, double a, double b)
    {
        double rl = 100;
        double ol = 0;
        double ra = 170;
        double oa = 0.5;
        double rb = 200;
        double ob = 0.375;
        if (_lab.Length == 6 && _lab[0] != 0 && _lab[2] != 0 && _lab[4] != 0)
        {
            // EP gives ranges and offsets in sample units; the offsets are taken against the full scale.
            int bits = _color[0].Precision;
            double full = (1L << bits) - 1;
            rl = _lab[0];
            ol = _lab[1] / full;
            ra = _lab[2];
            oa = _lab[3] / full;
            rb = _lab[4];
            ob = _lab[5] / full;
        }

        double lightness = (l - ol) * rl;
        double ca = (a - oa) * ra;
        double cb = (b - ob) * rb;
        Colors.Vector3d xyz = Colors.Cie.LabToXyz(new Colors.Vector3d(lightness, ca, cb), _labWhite);
        Colors.Vector3d linear = _labToRgb * xyz;
        return (Encode(linear.X), Encode(linear.Y), Encode(linear.Z));

        static double Encode(double v) => Colors.ColorValueMath.Encode(Math.Clamp(v, 0, 1));
    }
}

/// <summary>Which sample of each component every pixel of the (reduced) image takes: subsampled components repeat.</summary>
internal sealed class J2kGrid
{
    internal required int Width { get; init; }

    internal required int Height { get; init; }

    internal required int[][] Columns { get; init; }

    internal required int[][] Rows { get; init; }

    /// <summary>Maps each grid point of the image area at <paramref name="reduce"/> to each component's samples.</summary>
    internal static J2kGrid Create(J2kCodestream stream, int reduce)
    {
        int x0 = J2kTier2.CeilShift(stream.OffsetX, reduce);
        int y0 = J2kTier2.CeilShift(stream.OffsetY, reduce);
        int width = J2kTier2.CeilShift(stream.Width, reduce) - x0;
        int height = J2kTier2.CeilShift(stream.Height, reduce) - y0;
        int count = stream.Components.Length;
        int[][] columns = new int[count][];
        int[][] rows = new int[count][];
        for (int c = 0; c < count; c++)
        {
            J2kComponent component = stream.Components[c];
            (int w, int h) = J2kDecoder.ComponentSize(stream, c, reduce);
            int cx0 = J2kTier2.CeilShift(J2kTier2.CeilDiv(stream.OffsetX, component.Dx), reduce);
            int cy0 = J2kTier2.CeilShift(J2kTier2.CeilDiv(stream.OffsetY, component.Dy), reduce);
            columns[c] = Map(x0, width, component.Dx, cx0, w);
            rows[c] = Map(y0, height, component.Dy, cy0, h);
        }

        return new J2kGrid { Width = width, Height = height, Columns = columns, Rows = rows };

        static int[] Map(int origin, int length, int step, int componentOrigin, int componentLength)
        {
            int[] map = new int[length];
            for (int i = 0; i < length; i++)
            {
                map[i] = Math.Clamp(((origin + i) / step) - componentOrigin, 0, Math.Max(componentLength - 1, 0));
            }

            return map;
        }
    }
}
