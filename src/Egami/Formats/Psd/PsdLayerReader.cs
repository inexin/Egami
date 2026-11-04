using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Psd;

/// <summary>
/// Reads a PSD's layers with their pixels for <see cref="PsdImage.ReadLayers(Stream, ReaderOptions?, EgamiContext?)"/>: the
/// records, then each layer's channels in file order, turned into a raster in the layer's natural type with straight alpha
/// (or an <see cref="InkRaster"/> for CMYK) and the user mask into a gray raster of its own size.
/// </summary>
internal static class PsdLayerReader
{
    public static PsdDocument Read(EgamiInput input, ReaderOptions options, EgamiContext context, Func<Raster, Raster>? convert)
    {
        Dictionary<int, (Raster? Pixels, InkRaster? Ink, PsdMask? Mask)> built = [];
        Dictionary<int, Raster> encoded = [];
        InkSet? inks = null;
        try
        {
            PsdFile file = PsdFile.Read(input, options, layers: true, channels: (f, i, end) =>
                PsdChannels.Read(i, f, end, options, (index, planes) =>
                {
                    inks ??= f.Mode == PsdColorMode.Cmyk ? InksOf(f) : null;
                    built[index] = Build(planes, f, i, inks, convert);
                    if (f.Mode == PsdColorMode.Lab && HasPixels(planes))
                    {
                        // The flattener blends Lab as stored; the public pixels are sRGB.
                        encoded[index] = Pixels(planes, f, i, encodedLab: true);
                    }
                }));
            RasterMetadata metadata = PsdReader.Metadata(input, file, options);
            if (file.Records.Count == 0 && file.Mode is not (PsdColorMode.Bitmap or PsdColorMode.Indexed or PsdColorMode.Multichannel))
            {
                // A file without layers is its stored image: it comes as one background layer, so it flattens to itself.
                built[0] = Background(input, file, options, metadata, convert, ref inks, out Raster? lab);
                if (lab is not null)
                {
                    encoded[0] = lab;
                }
            }

            List<PsdLayerImage> layers = [];
            List<List<int>> open = [];
            int[] imageOf = new int[file.Records.Count];
            for (int r = 0; r < file.Records.Count; r++)
            {
                PsdLayerRecord record = file.Records[r];
                (Raster? pixels, InkRaster? ink, PsdMask? mask) = built.Remove(r, out var made) ? made : default;
                if (record.Info.Kind == PsdLayerKind.GroupEnd)
                {
                    if (encoded.Remove(r, out Raster? unused))
                    {
                        unused.Dispose();
                    }

                    pixels?.Dispose();
                    ink?.Dispose();
                    mask?.Release();
                    open.Add([]);
                    imageOf[r] = -1;
                    continue;
                }

                imageOf[r] = layers.Count;
                PsdLayerImage layer = new(record, layers.Count, pixels, ink, mask) { Encoded = encoded.Remove(r, out Raster? lab) ? lab : null };
                if (record.Info.Kind != PsdLayerKind.Layer && open.Count > 0)
                {
                    foreach (int child in open[^1])
                    {
                        layers[child].ParentIndex = layer.Index;
                    }

                    open.RemoveAt(open.Count - 1);
                }

                if (open.Count > 0)
                {
                    open[^1].Add(layer.Index);
                }

                layers.Add(layer);
            }

            return new PsdDocument(file, [.. layers], imageOf, metadata, context);
        }
        catch
        {
            foreach ((Raster? pixels, InkRaster? ink, PsdMask? mask) in built.Values)
            {
                pixels?.Dispose();
                ink?.Dispose();
                mask?.Release();
            }

            foreach (Raster lab in encoded.Values)
            {
                lab.Dispose();
            }

            throw;
        }
    }

    /// <summary>Reads the stored composite of a file without layers as its one layer, named Background, in the layer's natural type.</summary>
    private static (Raster? Pixels, InkRaster? Ink, PsdMask? Mask) Background(
        EgamiInput input, PsdFile file, ReaderOptions options, RasterMetadata metadata, Func<Raster, Raster>? convert, ref InkSet? inks, out Raster? lab)
    {
        lab = null;
        file.Records.Add(new PsdLayerRecord
        {
            Info = new PsdLayer("Background", new Rect(0, 0, file.Width, file.Height), 1, "norm", Operations.BlendMode.SourceOver, true, false, PsdLayerKind.Layer, 0),
            Right = file.Width,
            Bottom = file.Height,
        });
        using RowSource rows = PsdReader.RowsOf(input, file, options, new RasterMetadata { FormatName = PsdFormat.Info.Name }, ownsInput: false);
        rows.NextFrame();
        if (file.Mode == PsdColorMode.Cmyk)
        {
            inks ??= InksOf(file);
            InkRaster ink = new(inks, file.Width, file.Height, file.Depth == 16 ? InkDepth.Unorm16 : InkDepth.Unorm8, input.Context);
            try
            {
                Cmyk16[] row = new Cmyk16[file.Width];
                for (int y = 0; y < file.Height; y++)
                {
                    rows.ReadRow(y, 0, row.AsSpan());
                    for (int x = 0; x < file.Width; x++)
                    {
                        ushort[] values = [row[x].C, row[x].M, row[x].Y, row[x].K];
                        for (int p = 0; p < 4; p++)
                        {
                            if (ink.Depth == InkDepth.Unorm16)
                            {
                                ink.Plates[p].Row16(y)[x] = values[p];
                            }
                            else
                            {
                                ink.Plates[p].Row8(y)[x] = (byte)((values[p] + 128) / 257);
                            }
                        }

                        if (ink.Depth == InkDepth.Unorm16)
                        {
                            ink.Alpha.Row16(y)[x] = 65535;
                        }
                        else
                        {
                            ink.Alpha.Row8(y)[x] = 255;
                        }
                    }
                }

                return (convert is null ? null : convert(InkToRgba(ink, input.Context)), ink, null);
            }
            catch
            {
                ink.Dispose();
                throw;
            }
        }

        Raster pixels = (file.ColorChannels == 1 && file.Depth != 32, file.Depth) switch
        {
            (true, 16) => Read<GrayAlpha16>(input, rows, file),
            (true, _) => Read<GrayAlpha8>(input, rows, file),
            (false, 16) => Read<Rgba16>(input, rows, file),
            (false, 32) => Read<RgbaF32>(input, rows, file),
            _ => Read<Rgba8>(input, rows, file),
        };
        if (file.Mode == PsdColorMode.Lab)
        {
            lab = EncodeLab(pixels, file.Depth, input);
        }

        if (convert is not null)
        {
            Raster converted = convert(pixels);
            pixels.Dispose();
            pixels = converted;
        }

        return (pixels, null, null);

        static Raster<TPixel> Read<TPixel>(EgamiInput input, RowSource rows, PsdFile file)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Raster<TPixel> raster = input.CreateRaster<TPixel>(file.Width, file.Height);
            for (int y = 0; y < file.Height; y++)
            {
                rows.ReadRow(y, 0, raster.Row(y));
            }

            return raster;
        }
    }

    /// <summary>Returns the inks of a CMYK file: its embedded profile's when it has a CMYK one, else FOGRA39's.</summary>
    internal static InkSet InksOf(PsdFile file)
    {
        foreach ((int id, _, byte[] data) in file.Resources)
        {
            if (id == 1039)
            {
                try
                {
                    ColorProfile profile = ColorProfile.FromIcc(data);
                    if (profile.Model == ColorModel.Cmyk)
                    {
                        return InkSet.FromProfile(profile);
                    }
                }
                catch (Exception e) when (e is CorruptDataException or UnsupportedFormatException or ArgumentException)
                {
                    file.Warnings.Add("Psd.ProfileUnreadable", $"The embedded ICC profile could not be read ({e.Message}); the inks are FOGRA39's.");
                }
            }
        }

        return InkSet.FromProfile(ColorProfile.Fogra39);
    }

    /// <summary>Turns one layer's planes into its pixels and mask.</summary>
    /// <remarks>With <paramref name="encodedLab"/> a Lab layer's pixels are its Lab values as stored, which the flattener blends.</remarks>
    internal static (Raster? Pixels, InkRaster? Ink, PsdMask? Mask) Build(
        PsdPlaneSet planes, PsdFile file, EgamiInput input, InkSet? inks, Func<Raster, Raster>? convert, bool encodedLab = false)
    {
        PsdMask? mask = MaskOf(planes, file, input);
        Raster? pixels = null;
        InkRaster? ink = null;
        try
        {
            if (HasPixels(planes))
            {
                if (file.Mode == PsdColorMode.Cmyk)
                {
                    ink = Ink(planes, inks!, input.Context);
                    if (convert is not null)
                    {
                        pixels = convert(InkToRgba(ink, input.Context));
                    }
                }
                else
                {
                    pixels = Pixels(planes, file, input, encodedLab);
                    if (convert is not null)
                    {
                        Raster converted = convert(pixels);
                        pixels.Dispose();
                        pixels = converted;
                    }
                }
            }

            return (pixels, ink, mask);
        }
        catch
        {
            pixels?.Dispose();
            ink?.Dispose();
            mask?.Release();
            throw;
        }
    }

    private static bool HasPixels(PsdPlaneSet planes) => planes.Width > 0 && planes.Height > 0 && planes.Record.Info.Kind == PsdLayerKind.Layer
        && (planes.Alpha is not null || Array.Exists(planes.Color, c => c is not null));

    /// <summary>
    /// Returns sRGB pixels as Lab encoded as Photoshop stores it at a depth, alpha kept, in floats so that the flattener gets
    /// back the same sRGB.
    /// </summary>
    private static Raster<RgbaF32> EncodeLab(Raster pixels, int depth, EgamiInput input)
    {
        Raster<RgbaF32> raster = input.CreateRaster<RgbaF32>(pixels.Width, pixels.Height);
        try
        {
            for (int y = 0; y < pixels.Height; y++)
            {
                Span<RgbaF32> row = raster.Row(y);
                pixels.ReadRow(y, 0, row);
                for (int x = 0; x < row.Length; x++)
                {
                    (double l, double a, double b) = PsdLab.FromSrgb(row[x].R, row[x].G, row[x].B);
                    (l, a, b) = PsdLab.Encode(depth, l, a, b);
                    row[x] = new RgbaF32((float)l, (float)a, (float)b, row[x].A);
                }
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    private static PsdMask? MaskOf(PsdPlaneSet planes, PsdFile file, EgamiInput input)
    {
        PsdLayerRecord layer = planes.Record;
        if (layer.Mask is not PsdMaskRecord record || planes.Mask is null)
        {
            return null;
        }

        Raster pixels = MaskPixels(planes.Mask, record, file, input);
        Raster? real = null;
        try
        {
            PsdMask? combined = null;
            if (layer.RealMask is PsdMaskRecord r && planes.RealMask is not null && r.Size.Width > 0 && r.Size.Height > 0)
            {
                real = MaskPixels(planes.RealMask, r, file, input);
                combined = new PsdMask(real, new Rect(r.Left, r.Top, r.Size.Width, r.Size.Height), r.DefaultColor, r.Disabled, 1, 0);
            }

            return new PsdMask(pixels, new Rect(record.Left, record.Top, record.Size.Width, record.Size.Height), record.DefaultColor, record.Disabled, layer.UserMaskDensity / 255f, layer.UserMaskFeather)
            {
                Real = combined,
            };
        }
        catch
        {
            pixels.Dispose();
            real?.Dispose();
            throw;
        }
    }

    /// <summary>Reads a mask plane into a gray raster of the file's depth.</summary>
    private static Raster MaskPixels(byte[] plane, PsdMaskRecord record, PsdFile file, EgamiInput input)
    {
        (int width, int height) = record.Size;
        Raster raster;
        int rowBytes = PsdChannels.RowBytes(width, file.Depth);
        switch (file.Depth)
        {
            case 16:
                {
                    Raster<Gray16> gray = input.CreateRaster<Gray16>(width, height);
                    for (int y = 0; y < height; y++)
                    {
                        Span<ushort> row = MemoryMarshal.Cast<Gray16, ushort>(gray.Row(y));
                        ReadOnlySpan<byte> source = plane.AsSpan(y * rowBytes, rowBytes);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = BinaryPrimitives.ReadUInt16BigEndian(source[(2 * x)..]);
                        }
                    }

                    raster = gray;
                    break;
                }

            case 32:
                {
                    Raster<GrayF32> gray = input.CreateRaster<GrayF32>(width, height);
                    for (int y = 0; y < height; y++)
                    {
                        Span<float> row = MemoryMarshal.Cast<GrayF32, float>(gray.Row(y));
                        ReadOnlySpan<byte> source = plane.AsSpan(y * rowBytes, rowBytes);
                        for (int x = 0; x < width; x++)
                        {
                            row[x] = BinaryPrimitives.ReadSingleBigEndian(source[(4 * x)..]);
                        }
                    }

                    raster = gray;
                    break;
                }

            default:
                {
                    Raster<Gray8> gray = input.CreateRaster<Gray8>(width, height);
                    for (int y = 0; y < height; y++)
                    {
                        plane.AsSpan(y * rowBytes, width).CopyTo(MemoryMarshal.AsBytes(gray.Row(y)));
                    }

                    raster = gray;
                    break;
                }
        }

        return raster;
    }

    /// <summary>Builds the layer's raster in its natural type with straight alpha; missing color channels are 0, a missing transparency opaque.</summary>
    /// <remarks>With <paramref name="encodedLab"/> a Lab layer keeps its Lab values as stored instead of sRGB.</remarks>
    private static Raster Pixels(PsdPlaneSet planes, PsdFile file, EgamiInput input, bool encodedLab = false)
    {
        bool lab = file.Mode == PsdColorMode.Lab && !encodedLab;
        int width = planes.Width, height = planes.Height, rowBytes = planes.RowBytes;
        bool gray = file.Mode is PsdColorMode.Grayscale or PsdColorMode.Duotone or PsdColorMode.Multichannel or PsdColorMode.Bitmap;
        switch (file.Depth)
        {
            case 1:
            case 8:
                {
                    if (gray)
                    {
                        Raster<GrayAlpha8> raster = input.CreateRaster<GrayAlpha8>(width, height);
                        for (int y = 0; y < height; y++)
                        {
                            Span<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
                            for (int x = 0; x < width; x++)
                            {
                                int i = (y * rowBytes) + x;
                                row[2 * x] = file.Depth == 1 ? Bit(planes.Color[0], (y * rowBytes) + (x >> 3), x) : planes.Color[0]?[i] ?? 0;
                                row[(2 * x) + 1] = planes.Alpha is null ? (byte)255 : file.Depth == 1 ? Bit(planes.Alpha, (y * rowBytes) + (x >> 3), x) : planes.Alpha[i];
                            }
                        }

                        return raster;
                    }

                    Raster<Rgba8> color = input.CreateRaster<Rgba8>(width, height);
                    for (int y = 0; y < height; y++)
                    {
                        Span<byte> row = MemoryMarshal.AsBytes(color.Row(y));
                        for (int x = 0; x < width; x++)
                        {
                            int i = (y * rowBytes) + x;
                            byte c0 = planes.Color[0]?[i] ?? 0;
                            switch (file.Mode)
                            {
                                case PsdColorMode.Indexed:
                                    row[4 * x] = file.ColorData[c0];
                                    row[(4 * x) + 1] = file.ColorData[256 + c0];
                                    row[(4 * x) + 2] = file.ColorData[512 + c0];
                                    break;
                                case PsdColorMode.Lab when lab:
                                    (double r, double g, double b) = PsdLab.ToSrgb(c0 * 100.0 / 255, (planes.Color[1]?[i] ?? 128) - 128.0, (planes.Color[2]?[i] ?? 128) - 128.0);
                                    row[4 * x] = PsdLab.Unorm8(r);
                                    row[(4 * x) + 1] = PsdLab.Unorm8(g);
                                    row[(4 * x) + 2] = PsdLab.Unorm8(b);
                                    break;
                                default:
                                    row[4 * x] = c0;
                                    row[(4 * x) + 1] = planes.Color[1]?[i] ?? 0;
                                    row[(4 * x) + 2] = planes.Color[2]?[i] ?? 0;
                                    break;
                            }

                            row[(4 * x) + 3] = planes.Alpha?[i] ?? 255;
                        }
                    }

                    return color;
                }

            case 16:
                {
                    if (gray)
                    {
                        Raster<GrayAlpha16> raster = input.CreateRaster<GrayAlpha16>(width, height);
                        for (int y = 0; y < height; y++)
                        {
                            Span<ushort> row = MemoryMarshal.Cast<GrayAlpha16, ushort>(raster.Row(y));
                            for (int x = 0; x < width; x++)
                            {
                                int i = (y * rowBytes) + (2 * x);
                                row[2 * x] = Sample16(planes.Color[0], i, 0);
                                row[(2 * x) + 1] = Sample16(planes.Alpha, i, 65535);
                            }
                        }

                        return raster;
                    }

                    Raster<Rgba16> color = input.CreateRaster<Rgba16>(width, height);
                    for (int y = 0; y < height; y++)
                    {
                        Span<ushort> row = MemoryMarshal.Cast<Rgba16, ushort>(color.Row(y));
                        for (int x = 0; x < width; x++)
                        {
                            int i = (y * rowBytes) + (2 * x);
                            if (lab)
                            {
                                (double r, double g, double b) = PsdLab.ToSrgb(
                                    Sample16(planes.Color[0], i, 0) * 100.0 / 65535, (Sample16(planes.Color[1], i, 32768) - 32768.0) / 256, (Sample16(planes.Color[2], i, 32768) - 32768.0) / 256);
                                row[4 * x] = PsdLab.Unorm16(r);
                                row[(4 * x) + 1] = PsdLab.Unorm16(g);
                                row[(4 * x) + 2] = PsdLab.Unorm16(b);
                            }
                            else
                            {
                                row[4 * x] = Sample16(planes.Color[0], i, 0);
                                row[(4 * x) + 1] = Sample16(planes.Color[1], i, 0);
                                row[(4 * x) + 2] = Sample16(planes.Color[2], i, 0);
                            }

                            row[(4 * x) + 3] = Sample16(planes.Alpha, i, 65535);
                        }
                    }

                    return color;
                }

            default:
                {
                    Raster<RgbaF32> color = input.CreateRaster<RgbaF32>(width, height);
                    for (int y = 0; y < height; y++)
                    {
                        Span<float> row = MemoryMarshal.Cast<RgbaF32, float>(color.Row(y));
                        for (int x = 0; x < width; x++)
                        {
                            int i = (y * rowBytes) + (4 * x);
                            for (int c = 0; c < 3; c++)
                            {
                                row[(4 * x) + c] = Sample32(planes.Color[gray ? 0 : c], i, 0);
                            }

                            row[(4 * x) + 3] = Sample32(planes.Alpha, i, 1);
                        }
                    }

                    return color;
                }
        }
    }

    /// <summary>Builds a CMYK layer's ink raster: the stored values count down from no ink, and the planes are premultiplied by the alpha.</summary>
    private static InkRaster Ink(PsdPlaneSet planes, InkSet inks, EgamiContext context)
    {
        int width = planes.Width, height = planes.Height, rowBytes = planes.RowBytes;
        bool wide = planes.Depth == 16;
        InkRaster ink = new(inks, width, height, wide ? InkDepth.Unorm16 : InkDepth.Unorm8, context);
        try
        {
            for (int y = 0; y < height; y++)
            {
                if (wide)
                {
                    Span<ushort> alpha = ink.Alpha.Row16(y);
                    for (int x = 0; x < width; x++)
                    {
                        alpha[x] = Sample16(planes.Alpha, (y * rowBytes) + (2 * x), 65535);
                    }

                    for (int c = 0; c < 4; c++)
                    {
                        Span<ushort> plate = ink.Plates[c].Row16(y);
                        for (int x = 0; x < width; x++)
                        {
                            uint amount = 65535u - Sample16(planes.Color[c], (y * rowBytes) + (2 * x), 65535);
                            plate[x] = (ushort)(((amount * alpha[x]) + 32767) / 65535);
                        }
                    }
                }
                else
                {
                    Span<byte> alpha = ink.Alpha.Row8(y);
                    for (int x = 0; x < width; x++)
                    {
                        alpha[x] = planes.Alpha?[(y * rowBytes) + x] ?? 255;
                    }

                    for (int c = 0; c < 4; c++)
                    {
                        Span<byte> plate = ink.Plates[c].Row8(y);
                        for (int x = 0; x < width; x++)
                        {
                            int amount = 255 - (planes.Color[c]?[(y * rowBytes) + x] ?? 255);
                            plate[x] = (byte)(((amount * alpha[x]) + 127) / 255);
                        }
                    }
                }
            }

            return ink;
        }
        catch
        {
            ink.Dispose();
            throw;
        }
    }

    /// <summary>Returns an ink raster's process inks as straight RGBA, through the naive CMYK formula Egami's CMYK pixels use.</summary>
    internal static Raster<RgbaF32> InkToRgba(InkRaster ink, EgamiContext context)
    {
        Raster<RgbaF32> raster = Raster.Create<RgbaF32>(ink.Width, ink.Height, context);
        float max = ink.Depth == InkDepth.Unorm8 ? 255 : 65535;
        for (int y = 0; y < ink.Height; y++)
        {
            Span<RgbaF32> row = raster.Row(y);
            for (int x = 0; x < ink.Width; x++)
            {
                float a = Value(ink.Alpha, y, x) / max;
                float k = a > 0 ? Value(ink.Plates[3], y, x) / max / a : 0;
                float Channel(int c) => a > 0 ? (1 - Math.Min(1, Value(ink.Plates[c], y, x) / max / a)) * (1 - Math.Min(1, k)) : 0;
                row[x] = new RgbaF32(Channel(0), Channel(1), Channel(2), a);
            }
        }

        return raster;

        static int Value(InkPlate plate, int y, int x) => plate.Depth == InkDepth.Unorm8 ? plate.Row8(y)[x] : plate.Row16(y)[x];
    }

    private static byte Bit(byte[]? plane, int at, int x) => plane is null ? (byte)0 : (plane[at] & (0x80 >> (x & 7))) != 0 ? (byte)0 : (byte)255;

    private static ushort Sample16(byte[]? plane, int at, ushort missing) => plane is null ? missing : BinaryPrimitives.ReadUInt16BigEndian(plane.AsSpan(at));

    private static float Sample32(byte[]? plane, int at, float missing) => plane is null ? missing : BinaryPrimitives.ReadSingleBigEndian(plane.AsSpan(at));
}
