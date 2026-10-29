using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Bmp;
using Egami.Formats.Gif;
using Egami.Formats.Ico;
using Egami.Formats.Jpeg;
using Egami.Formats.Png;
using Egami.Formats.Tiff;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Quantization;
using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// Image.FromStream and Image.Save on Egami's readers and writers, choosing pixel formats, palettes, flags, resolution, frames
/// and property items as GDI+ does for each file type (measured in egami-compare, sysdrawing CodecProbes).
/// </summary>
internal static partial class Codecs
{
    private const int ReadOnlyFlags = (int)(ImageFlags.ReadOnly | ImageFlags.HasRealPixelSize);

    /// <summary>Decodes image data, or returns null when Egami cannot read it.</summary>
    public static Image? Load(byte[] data)
    {
        if (MetafileScan.IsMetafile(data))
        {
            try
            {
                return new Metafile(data);
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                return null;
            }
        }

        RasterInfo info;
        RasterList all;
        try
        {
            info = Raster.Probe(data);
            all = Raster.LoadAll(data);
        }
        catch (Exception e) when (e is EgamiException or ArgumentException or InvalidDataException or NotSupportedException)
        {
            return null;
        }

        using (all)
        {
            if (all.Count == 0)
            {
                return null;
            }

            string name = info.Format.Name;
            Raster first = all[0];
            if (name is "ICO" or "CUR")
            {
                // GDI+'s icon decoder takes the last image of the directory.
                first = all.LastOrDefault(r => r.Metadata.FormatDetails is IcoDetails d && d.Index == all.Count - 1) ?? all[^1];
            }

            Guid raw = name switch
            {
                "PNG" => ImageFormat.Png.Guid,
                "JPEG" => ImageFormat.Jpeg.Guid,
                "GIF" => ImageFormat.Gif.Guid,
                "BMP" => ImageFormat.Bmp.Guid,
                "TIFF" => ImageFormat.Tiff.Guid,
                "ICO" or "CUR" => ImageFormat.Icon.Guid,
                "WebP" => ImageFormat.Webp.Guid,
                "HEIF" or "HEIC" or "AVIF" => ImageFormat.Heif.Guid,
                _ => ImageFormat.MemoryBmp.Guid,
            };

            List<Frame> frames = [];
            Guid dimension = FrameDimension.Page.Guid;
            bool gray = false;
            int? transparent = null;
            if (name == "GIF")
            {
                dimension = FrameDimension.Time.Guid;
                using Raster<Rgba8> rgba = first.As<Rgba8>();
                if (rgba.Frames[0].Metadata.FormatDetails is GifFrameDetails { TransparentIndex: int index })
                {
                    transparent = index;
                }

                if (rgba.FrameCount == 1)
                {
                    frames.Add(GifFrame(rgba, 0));
                }
                else
                {
                    for (int f = 0; f < rgba.FrameCount; f++)
                    {
                        Frame frame = FromRgba(rgba, f, PixelFormat.Format32bppArgb);
                        frame.Delay = (int)Math.Round(rgba.Frames[f].Metadata.Delay.TotalMilliseconds / 10);
                        frames.Add(frame);
                    }
                }
            }
            else if (name == "TIFF")
            {
                foreach (Raster page in all)
                {
                    (Frame frame, bool isGray) = TiffFrame(page);
                    frames.Add(frame);
                    gray |= isGray && frames.Count == 1;
                }
            }
            else
            {
                (Frame frame, bool isGray) = name switch
                {
                    "PNG" => PngFrame(first),
                    "JPEG" => JpegFrame(first),
                    "BMP" => BmpFrame(first, data),
                    "ICO" or "CUR" => (IconFrame(first), false),
                    _ => (FromRgba(first, first.Layout.HasAlpha ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb), false),
                };
                frames.Add(frame);
                gray = isGray;
            }

            Bitmap bitmap = new(frames[0]);
            bitmap.SetFrames(frames, dimension);
            RasterMetadata metadata = first.Metadata;
            (float dpiX, float dpiY, bool realDpi) = Dpi(metadata);
            PixelFormat format = frames[0].Format;
            bool alphaFlag = name == "GIF" ? transparent is not null : GdiPixels.HasAlpha(format);
            int flags = ReadOnlyFlags | (int)(gray ? ImageFlags.ColorSpaceGray : ImageFlags.ColorSpaceRgb)
                | (alphaFlag ? (int)ImageFlags.HasAlpha : 0) | (realDpi || name == "GIF" ? (int)ImageFlags.HasRealDpi : 0);
            bitmap.SetFileState(raw, flags, dpiX, dpiY);
            AddProperties(bitmap, metadata, frames, name, data, transparent);
            return bitmap;
        }
    }

    /// <summary>
    /// The resolution in pixels per inch, computed in single precision as GDI+ computes it (3780 pixels a meter is 96.01199), or
    /// 96 when the file states none; and whether it does.
    /// </summary>
    private static (float X, float Y, bool Real) Dpi(RasterMetadata metadata)
    {
        Resolution? stated = metadata.Resolution;
        if (stated is not Resolution r || r.Unit == ResolutionUnit.None || r.X <= 0 || r.Y <= 0)
        {
            return (96, 96, false);
        }

        // BMP and PNG store pixels a meter, which GDI+ turns into pixels an inch in single precision; a resolution that is not a
        // whole number of pixels a meter (TIFF's and JPEG's 72 or 300 an inch) is kept as it is.
        double perMeterX = r.Unit switch { ResolutionUnit.PerMeter => r.X, ResolutionUnit.PerCentimeter => r.X * 100, _ => r.X / 0.0254 };
        double perMeterY = r.Unit switch { ResolutionUnit.PerMeter => r.Y, ResolutionUnit.PerCentimeter => r.Y * 100, _ => r.Y / 0.0254 };
        Resolution inch = r.ToPerInch();
        static float Gdi(double perMeter, double perInch)
        {
            if (Math.Abs(perMeter - Math.Round(perMeter)) >= 1e-6)
            {
                return (float)perInch;
            }

            // Whole pixels a meter times 0.0254f, rounded toward zero (3780 gives 96.01199, 2835 gives 72.008995).
            double exact = Math.Round(perMeter) * (double)0.0254f;
            float dpi = (float)exact;
            return dpi > exact ? MathF.BitDecrement(dpi) : dpi;
        }
        return (Gdi(perMeterX, inch.X), Gdi(perMeterY, inch.Y), true);
    }

    /// <summary>The settings each encoder lists, as GDI+ lists them.</summary>
    public static EncoderParameters? ParameterList(Guid encoder)
    {
        ImageCodecInfo? codec = ImageCodecInfo.EncoderById(encoder) ?? throw GdiErrors.InvalidParameter();
        if (codec.FormatID == ImageFormat.Bmp.Guid)
        {
            throw new NotImplementedException();
        }

        EncoderParameters list = codec.FormatID == ImageFormat.Jpeg.Guid ? new EncoderParameters(5)
            : codec.FormatID == ImageFormat.Tiff.Guid ? new EncoderParameters(4)
            : codec.FormatID == ImageFormat.Gif.Guid ? new EncoderParameters(2) : new EncoderParameters(1);
        if (codec.FormatID == ImageFormat.Jpeg.Guid)
        {
            list.Param[0] = new EncoderParameter(Encoder.Transformation, [(long)EncoderValue.TransformRotate90, (long)EncoderValue.TransformRotate180, (long)EncoderValue.TransformRotate270, (long)EncoderValue.TransformFlipHorizontal, (long)EncoderValue.TransformFlipVertical]);
            list.Param[1] = new EncoderParameter(Encoder.Quality, 0L, 100L);
            list.Param[2] = new EncoderParameter(Encoder.LuminanceTable, Array.Empty<short>());
            list.Param[3] = new EncoderParameter(Encoder.ChrominanceTable, Array.Empty<short>());
            list.Param[4] = new EncoderParameter(Encoder.ImageItems, 0, EncoderParameterValueType.ValueTypePointer, new IntPtr(1));
        }
        else if (codec.FormatID == ImageFormat.Tiff.Guid)
        {
            list.Param[0] = new EncoderParameter(Encoder.Compression, [(long)EncoderValue.CompressionLZW, (long)EncoderValue.CompressionCCITT3, (long)EncoderValue.CompressionRle, (long)EncoderValue.CompressionCCITT4, (long)EncoderValue.CompressionNone]);
            list.Param[1] = new EncoderParameter(Encoder.ColorDepth, [1L, 4, 8, 24, 32]);
            list.Param[2] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
            list.Param[3] = new EncoderParameter(Encoder.SaveAsCmyk, 1L);
        }
        else if (codec.FormatID == ImageFormat.Gif.Guid)
        {
            list.Param[0] = new EncoderParameter(Encoder.ImageItems, 0, EncoderParameterValueType.ValueTypePointer, new IntPtr(1));
            list.Param[1] = new EncoderParameter(Encoder.SaveFlag, (long)EncoderValue.MultiFrame);
        }
        else
        {
            list.Param[0] = new EncoderParameter(Encoder.ImageItems, 0, EncoderParameterValueType.ValueTypePointer, new IntPtr(1));
        }

        return list;
    }

    /// <summary>Saves the image's current frame, or starts a multi-frame TIFF when the settings ask for one.</summary>
    public static void Save(Image image, string? path, Stream? stream, ImageCodecInfo codec, EncoderParameters? parameters)
    {
        if (ImageCodecInfo.EncoderById(codec.Clsid) is null)
        {
            throw new ArgumentException("No Egami encoder has this class id.", nameof(codec));
        }

        if (parameters?.Number(Encoder.SaveFlag) == (long)EncoderValue.MultiFrame && codec.FormatID == ImageFormat.Tiff.Guid)
        {
            image.PendingSave = new MultiFrameSave(path, stream, parameters);
            image.PendingSave.Pages.Add(Page((Bitmap)image, parameters));
            return;
        }

        byte[] bytes = Encode((Bitmap)image, codec.FormatID, parameters);
        if (path is not null)
        {
            File.WriteAllBytes(path, bytes);
        }
        else
        {
            stream!.Write(bytes);
        }
    }

    /// <summary>Adds a page to a multi-frame save, or with the Flush flag writes the file.</summary>
    public static void SaveAdd(Image owner, Image? page, EncoderParameters parameters)
    {
        MultiFrameSave save = owner.PendingSave ?? throw GdiErrors.Generic();
        if (parameters.Number(Encoder.SaveFlag) == (long)EncoderValue.Flush)
        {
            Finish(owner);
            return;
        }

        save.Pages.Add(Page((Bitmap)(page ?? owner), parameters));
    }

    /// <summary>Writes a pending multi-frame TIFF.</summary>
    public static void Finish(Image owner)
    {
        if (owner.PendingSave is not MultiFrameSave save)
        {
            return;
        }

        owner.PendingSave = null;
        using MemoryStream output = new();
        Raster.SaveAll(save.Pages, output, TiffFormat.Info, TiffOptions(save.Parameters));
        foreach (Raster page in save.Pages)
        {
            page.Dispose();
        }

        if (save.Path is not null)
        {
            File.WriteAllBytes(save.Path, output.ToArray());
        }
        else
        {
            save.Stream!.Write(output.ToArray());
        }
    }

    /// <summary>Encodes a bitmap's current frame in a format.</summary>
    public static byte[] Encode(Bitmap bitmap, Guid format, EncoderParameters? parameters)
    {
        Frame frame = bitmap.Current;
        using MemoryStream output = new();
        if (format == ImageFormat.Bmp.Guid)
        {
            WriteBmp(bitmap, output);
            return output.ToArray();
        }

        if (format == ImageFormat.Gif.Guid)
        {
            // GDI+ writes other formats with the default 8-bit palette, as a lock in 8bppIndexed maps them.
            Frame indexed = frame.Format == PixelFormat.Format8bppIndexed ? frame : frame.Convert(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format8bppIndexed, lockPalette: true);
            using Raster<Rgba8> colors = Colors(indexed);
            Palette palette = Palette.FromColors([.. indexed.Palette.Select(c => new Rgba8((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24)))]);
            colors.Save(output, GifFormat.Info, new GifWriterOptions { Palette = new PaletteOptions { FixedPalette = palette, Dither = DitherMode.None }, StripMetadata = true });
            return output.ToArray();
        }

        using Raster raster = ToRaster(bitmap, frame, format == ImageFormat.Jpeg.Guid);
        WriterOptions options = format switch
        {
            _ when format == ImageFormat.Jpeg.Guid => new JpegWriterOptions { Quality = Math.Clamp((int)(parameters?.Number(Encoder.Quality) ?? 75), 1, 100), StripMetadata = false },
            _ when format == ImageFormat.Tiff.Guid => TiffOptions(parameters),
            _ when format == ImageFormat.Png.Guid => new PngWriterOptions(),
            _ => throw new ArgumentException("No Egami encoder writes this format."),
        };
        FormatInfo info = format == ImageFormat.Jpeg.Guid ? JpegFormat.Info : format == ImageFormat.Tiff.Guid ? TiffFormat.Info : PngFormat.Info;
        if (format == ImageFormat.Tiff.Guid)
        {
            using Raster page = Page(bitmap, parameters);
            page.Save(output, info, options);
        }
        else
        {
            raster.Metadata.Resolution = new Resolution(bitmap.HorizontalResolution, bitmap.VerticalResolution, ResolutionUnit.PerInch);
            raster.Save(output, info, options);
        }

        return output.ToArray();
    }

    private static TiffWriterOptions TiffOptions(EncoderParameters? parameters) => new()
    {
        Reduce = parameters?.Number(Encoder.ColorDepth) is 1 or 4 or 8 || parameters?.Number(Encoder.Compression) is (long)EncoderValue.CompressionCCITT3 or (long)EncoderValue.CompressionCCITT4,
        Compression = (EncoderValue?)(int?)parameters?.Number(Encoder.Compression) switch
        {
            EncoderValue.CompressionNone => TiffCompression.None,
            EncoderValue.CompressionRle => TiffCompression.PackBits,
            EncoderValue.CompressionCCITT3 => TiffCompression.CcittGroup3,
            EncoderValue.CompressionCCITT4 => TiffCompression.CcittGroup4,
            _ => TiffCompression.Lzw,
        },
    };

    /// <summary>A TIFF page: the depth the ColorDepth setting or CCITT compression asks for, else the bitmap's own.</summary>
    private static Raster Page(Bitmap bitmap, EncoderParameters? parameters)
    {
        Frame frame = bitmap.Current;
        long? depth = parameters?.Number(Encoder.ColorDepth);
        long? compression = parameters?.Number(Encoder.Compression);
        if (compression is (long)EncoderValue.CompressionCCITT3 or (long)EncoderValue.CompressionCCITT4)
        {
            depth = 1;
        }

        Frame source = depth switch
        {
            1 => frame.Convert(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format1bppIndexed),
            4 => frame.Convert(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format4bppIndexed),
            8 => frame.Convert(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format8bppIndexed),
            24 => frame.Convert(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format24bppRgb),
            32 => frame.Convert(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format32bppArgb),
            _ => frame,
        };
        Raster raster = depth == 1 ? Gray(source) : ToRaster(bitmap, source, false);
        raster.Metadata.Resolution = new Resolution(bitmap.HorizontalResolution, bitmap.VerticalResolution, ResolutionUnit.PerInch);
        return raster;
    }

    /// <summary>A frame as an Egami raster: opaque formats as Rgb8, the others as Rgba8; JPEG always Rgb8.</summary>
    private static Raster ToRaster(Bitmap bitmap, Frame frame, bool opaque)
    {
        using Raster<Rgba8> colors = Colors(frame);
        bool alpha = !opaque && (GdiPixels.HasAlpha(frame.Format) || (GdiPixels.IsIndexed(frame.Format) && frame.Palette.Any(c => c >> 24 != 255)));
        return alpha ? colors.As<Rgba8>() : colors.As<Rgb8>();
    }

    /// <summary>A 1-bit frame as 8-bit gray, black or white, for bilevel TIFF.</summary>
    private static Raster<Gray8> Gray(Frame frame)
    {
        Raster<Gray8> gray = Egami.Raster.Create<Gray8>(frame.Width, frame.Height);
        for (int y = 0; y < frame.Height; y++)
        {
            Span<Gray8> row = gray.Row(y);
            for (int x = 0; x < frame.Width; x++)
            {
                uint c = frame.Palette[GdiPixels.Index(frame.Format, frame.Row(y), x)];
                row[x] = new Gray8((byte)((((c >> 16) & 0xFF) + ((c >> 8) & 0xFF) + (c & 0xFF)) >= 384 ? 255 : 0));
            }
        }

        return gray;
    }

    /// <summary>A frame's colors as Rgba8, converted as GetPixel converts.</summary>
    private static Raster<Rgba8> Colors(Frame frame)
    {
        Frame argb = frame.Format == PixelFormat.Format16bppGrayScale ? Transforms.Gray16ToArgb(frame) : frame.Convert(new Rectangle(0, 0, frame.Width, frame.Height), PixelFormat.Format32bppArgb);
        Raster<Rgba8> raster = Egami.Raster.Create<Rgba8>(frame.Width, frame.Height);
        for (int y = 0; y < frame.Height; y++)
        {
            PixelConvert.Convert<Bgra8, Rgba8>(MemoryMarshal.Cast<byte, Bgra8>(argb.Row(y)), raster.Row(y));
        }

        return raster;
    }

    /// <summary>BMP as GDI+ writes it: a BITMAPINFOHEADER and the rows as they are in memory, bottom-up; 32-bit formats without masks.</summary>
    private static void WriteBmp(Bitmap bitmap, Stream output)
    {
        Frame frame = bitmap.Current;
        PixelFormat format = frame.Format switch
        {
            PixelFormat.Format1bppIndexed or PixelFormat.Format4bppIndexed or PixelFormat.Format8bppIndexed or PixelFormat.Format16bppRgb555
                or PixelFormat.Format16bppRgb565 or PixelFormat.Format24bppRgb or PixelFormat.Format32bppRgb or PixelFormat.Format32bppArgb => frame.Format,
            PixelFormat.Format48bppRgb or PixelFormat.Format16bppGrayScale => PixelFormat.Format24bppRgb,
            _ => PixelFormat.Format32bppArgb,
        };
        Frame source = format == frame.Format ? frame : (frame.Format == PixelFormat.Format16bppGrayScale ? Transforms.Gray16ToArgb(frame) : frame).Convert(new Rectangle(0, 0, frame.Width, frame.Height), format);
        int bits = GdiPixels.Bits(format);
        bool masks = format == PixelFormat.Format16bppRgb565;
        int colors = GdiPixels.IsIndexed(format) ? source.Palette.Length : 0;
        int headerSize = 40 + (masks ? 12 : 0) + (colors * 4);
        int stride = GdiPixels.Stride(frame.Width, format);
        int imageSize = stride * frame.Height;
        byte[] header = new byte[14 + headerSize];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), 14 + headerSize + imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10), 14 + headerSize);
        Span<byte> info = header.AsSpan(14);
        BinaryPrimitives.WriteInt32LittleEndian(info, 40);
        BinaryPrimitives.WriteInt32LittleEndian(info[4..], frame.Width);
        BinaryPrimitives.WriteInt32LittleEndian(info[8..], frame.Height);
        BinaryPrimitives.WriteInt16LittleEndian(info[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(info[14..], (short)bits);
        BinaryPrimitives.WriteInt32LittleEndian(info[16..], masks ? 3 : 0);
        BinaryPrimitives.WriteInt32LittleEndian(info[20..], imageSize);
        BinaryPrimitives.WriteInt32LittleEndian(info[24..], (int)Math.Round(bitmap.HorizontalResolution / 0.0254));
        BinaryPrimitives.WriteInt32LittleEndian(info[28..], (int)Math.Round(bitmap.VerticalResolution / 0.0254));
        BinaryPrimitives.WriteInt32LittleEndian(info[32..], colors);
        int at = 40;
        if (masks)
        {
            BinaryPrimitives.WriteInt32LittleEndian(info[40..], 0xF800);
            BinaryPrimitives.WriteInt32LittleEndian(info[44..], 0x07E0);
            BinaryPrimitives.WriteInt32LittleEndian(info[48..], 0x001F);
            at = 52;
        }

        for (int i = 0; i < colors; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(info[(at + (i * 4))..], source.Palette[i] & 0xFFFFFF);
        }

        output.Write(header);
        byte[] row = new byte[stride];
        for (int y = frame.Height - 1; y >= 0; y--)
        {
            Array.Clear(row);
            source.Row(y).CopyTo(row);
            output.Write(row);
        }
    }

    private static (Frame Frame, bool Gray) PngFrame(Raster raster)
    {
        PngDetails? png = raster.Metadata.FormatDetails as PngDetails;
        int depth = png?.BitDepth ?? 8;
        switch (png?.ColorType)
        {
            case PngColorType.Palette when depth is 1 or 4 or 8 && png.Transparency.IsEmpty:
                {
                    PixelFormat format = depth == 1 ? PixelFormat.Format1bppIndexed : depth == 4 ? PixelFormat.Format4bppIndexed : PixelFormat.Format8bppIndexed;
                    ReadOnlySpan<Rgba8> palette = png.Palette.Span;
                    uint[] entries = new uint[palette.Length];
                    for (int i = 0; i < palette.Length; i++)
                    {
                        Rgba8 c = palette[i];
                        entries[i] = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                    }

                    return (Indexed(raster, format, entries, 0), false);
                }

            case PngColorType.Gray when depth == 1 && png.Transparency.IsEmpty:
                return (Indexed(raster, PixelFormat.Format1bppIndexed, [0xFF000000, 0xFFFFFFFF], (int)PaletteFlags.GrayScale), false);
            case PngColorType.Rgb when depth == 8 && png.Transparency.IsEmpty:
                return (FromRgba(raster, PixelFormat.Format24bppRgb), false);
            default:
                // 16-bit samples keep their high byte, as GDI+ reads them.
                return (depth == 16 ? Truncate16(raster) : FromRgba(raster, PixelFormat.Format32bppArgb), png?.ColorType == PngColorType.Gray);
        }
    }

    /// <summary>A 16-bit raster as 32-bit ARGB from the high byte of each sample.</summary>
    private static Frame Truncate16(Raster raster)
    {
        using Raster<Rgba16> wide = raster.As<Rgba16>();
        Frame frame = new(wide.Width, wide.Height, PixelFormat.Format32bppArgb);
        for (int y = 0; y < wide.Height; y++)
        {
            Span<Rgba16> row = wide.Row(y);
            Span<byte> target = frame.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                Rgba16 c = row[x];
                GdiPixels.Write(PixelFormat.Format32bppArgb, target, x, ((uint)(c.A >> 8) << 24) | ((uint)(c.R >> 8) << 16) | ((uint)(c.G >> 8) << 8) | (uint)(c.B >> 8));
            }
        }

        return frame;
    }

    /// <summary>A 16-bit raster as 48-bit linear RGB: each sample through the sRGB curve to 0 to 8192.</summary>
    private static Frame Linear16(Raster raster)
    {
        using Raster<Rgba16> wide = raster.As<Rgba16>();
        Frame frame = new(wide.Width, wide.Height, PixelFormat.Format48bppRgb);
        for (int y = 0; y < wide.Height; y++)
        {
            Span<Rgba16> row = wide.Row(y);
            Span<byte> target = frame.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                GdiPixels.WriteLinear(PixelFormat.Format48bppRgb, target, x, new Linear(Lin(row[x].R), Lin(row[x].G), Lin(row[x].B), 8192));
            }
        }

        return frame;

        static short Lin(ushort v)
        {
            double c = v / 65535.0;
            return (short)(int)(((c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4)) * 8192) + 0.5);
        }
    }

    private static (Frame Frame, bool Gray) JpegFrame(Raster raster)
    {
        if (raster.Metadata.FormatDetails is JpegDetails { ColorSpace: JpegColorSpace.Gray })
        {
            return (Indexed(raster, PixelFormat.Format8bppIndexed, GrayRamp(256), (int)PaletteFlags.GrayScale), true);
        }

        return (FromRgba(raster, PixelFormat.Format24bppRgb), false);
    }

    private static (Frame Frame, bool Gray) BmpFrame(Raster raster, byte[] data)
    {
        BmpDetails? bmp = raster.Metadata.FormatDetails as BmpDetails;
        int bits = bmp?.BitsPerPixel ?? 24;
        if (bits == 16 && bmp!.Compression is BmpCompression.None or BmpCompression.Bitfields or BmpCompression.AlphaBitfields)
        {
            return (Bmp16(data, bmp), false);
        }

        if (bits is 1 or 4 or 8 && bmp!.Compression == BmpCompression.None && !bmp.Palette.IsEmpty)
        {
            PixelFormat format = bits == 1 ? PixelFormat.Format1bppIndexed : bits == 4 ? PixelFormat.Format4bppIndexed : PixelFormat.Format8bppIndexed;
            uint[] entries = [.. bmp.Palette.ToArray().Select(c => 0xFF000000 | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B)];
            return (Indexed(raster, format, entries, 0), false);
        }

        // GDI+ keeps alpha only from a 32-bit file whose bit fields name an alpha mask; plain 32-bit files load as 32bppRgb.
        bool masked = bmp?.Compression is BmpCompression.Bitfields or BmpCompression.AlphaBitfields || bmp?.HeaderSize >= 56;
        PixelFormat direct = bits == 24 && bmp?.Compression == BmpCompression.None ? PixelFormat.Format24bppRgb
            : raster.Layout.HasAlpha && bits == 32 && masked ? PixelFormat.Format32bppArgb : PixelFormat.Format32bppRgb;
        return (FromRgba(raster, direct), false);
    }

    /// <summary>
    /// A 16-bit BMP as 32bppRgb, its 5 and 6-bit channels widened as GDI+ widens them (repeating the high bits): the pixels are
    /// read straight into Bgr565 or Bgr555, whose memory is the file's.
    /// </summary>
    private static Frame Bmp16(byte[] data, BmpDetails bmp)
    {
        bool is565 = bmp.Compression != BmpCompression.None && data.Length >= 14 + 52 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14 + 40)) == 0xF800
            && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14 + 44)) == 0x07E0 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(14 + 48)) == 0x001F;
        PixelFormat source = is565 ? PixelFormat.Format16bppRgb565 : PixelFormat.Format16bppRgb555;
        if (is565)
        {
            using Raster<Bgr565> raster = Egami.Raster.Load<Bgr565>(data);
            return Widen16(raster.Width, raster.Height, y => MemoryMarshal.AsBytes(raster.Row(y)).ToArray(), source);
        }

        using Raster<Bgr555> raster555 = Egami.Raster.Load<Bgr555>(data);
        return Widen16(raster555.Width, raster555.Height, y => MemoryMarshal.AsBytes(raster555.Row(y)).ToArray(), source);
    }

    private static Frame Widen16(int width, int height, Func<int, byte[]> row, PixelFormat source)
    {
        Frame frame = new(width, height, PixelFormat.Format32bppRgb);
        for (int y = 0; y < height; y++)
        {
            byte[] values = row(y);
            Span<byte> target = frame.Row(y);
            for (int x = 0; x < width; x++)
            {
                GdiPixels.Write(PixelFormat.Format32bppRgb, target, x, GdiPixels.Read(source, values, x, []));
            }
        }

        return frame;
    }

    private static (Frame Frame, bool Gray) TiffFrame(Raster raster)
    {
        TiffDetails? tiff = raster.Metadata.FormatDetails as TiffDetails;
        int bits = tiff?.BitsPerSample ?? 8;
        TiffPhotometric photometric = tiff?.Photometric ?? TiffPhotometric.Rgb;
        bool alpha = raster.Layout.HasAlpha;
        if (photometric is TiffPhotometric.WhiteIsZero or TiffPhotometric.BlackIsZero)
        {
            if (bits == 1 && !alpha)
            {
                return (Indexed(raster, PixelFormat.Format1bppIndexed, [0xFF000000, 0xFFFFFFFF], (int)PaletteFlags.GrayScale), false);
            }

            return bits <= 8 && !alpha ? (Indexed(raster, PixelFormat.Format8bppIndexed, GrayRamp(256), (int)PaletteFlags.GrayScale), true) : (FromRgba(raster, PixelFormat.Format32bppArgb), true);
        }

        if (photometric == TiffPhotometric.Palette)
        {
            PixelFormat format = bits <= 4 ? PixelFormat.Format4bppIndexed : PixelFormat.Format8bppIndexed;
            return (Indexed(raster, format, null, 0, 1 << bits), false);
        }

        if (bits == 16)
        {
            return (Linear16(raster), false);
        }

        bool associated = tiff?.ExtraSamples.Contains(TiffExtraSample.Associated) == true;
        return (FromRgba(raster, alpha ? (associated ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb) : PixelFormat.Format24bppRgb), false);
    }

    /// <summary>A single GIF frame as 8-bit indexed with its color table, the transparent index transparent.</summary>
    private static Frame GifFrame(Raster<Rgba8> raster, int frame)
    {
        GifDetails? gif = raster.Metadata.FormatDetails as GifDetails;
        GifFrameDetails? local = raster.Frames[frame].Metadata.FormatDetails as GifFrameDetails;
        ReadOnlySpan<Rgb8> table = local is { LocalPalette.IsEmpty: false } ? local.LocalPalette.Span : gif is not null ? gif.GlobalPalette.Span : default;
        if (table.IsEmpty)
        {
            return FromRgba(raster, PixelFormat.Format32bppArgb);
        }

        uint[] entries = new uint[table.Length];
        for (int i = 0; i < table.Length; i++)
        {
            entries[i] = (local?.TransparentIndex == i ? 0u : 0xFF000000) | ((uint)table[i].R << 16) | ((uint)table[i].G << 8) | table[i].B;
        }

        return Indexed(raster, PixelFormat.Format8bppIndexed, entries, local?.TransparentIndex is null ? 0 : (int)PaletteFlags.HasAlpha);
    }

    private static uint[] GrayRamp(int count) => [.. Enumerable.Range(0, count).Select(i => 0xFF000000 | (uint)(i * 0x010101))];

    /// <summary>
    /// An indexed frame whose indices are found by looking each decoded color up in the palette (the first entry with that color);
    /// with no palette given, the image's own colors become one, in the order they appear, padded to <paramref name="size"/>.
    /// </summary>
    private static Frame Indexed(Raster raster, PixelFormat format, uint[]? palette, int flags, int size = 0)
    {
        using Raster<Rgba8> rgba = raster.As<Rgba8>();
        List<uint> entries = palette is null ? [] : [.. palette];
        Dictionary<uint, int> lookup = [];
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            lookup[entries[i]] = i;
        }

        Frame frame = new(rgba.Width, rgba.Height, format);
        for (int y = 0; y < rgba.Height; y++)
        {
            Span<Rgba8> row = rgba.Row(y);
            Span<byte> target = frame.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                Rgba8 c = row[x];
                uint argb = ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;
                if (!lookup.TryGetValue(argb, out int index))
                {
                    if (palette is null && entries.Count < (1 << GdiPixels.Bits(format)))
                    {
                        index = entries.Count;
                        entries.Add(argb);
                        lookup[argb] = index;
                    }
                    else
                    {
                        index = GdiPalettes.Nearest(CollectionsMarshal.AsSpan(entries), argb);
                    }
                }

                GdiPixels.SetIndex(format, target, x, index);
            }
        }

        while (entries.Count < size)
        {
            entries.Add(0xFF000000);
        }

        frame.Palette = [.. entries];
        frame.PaletteFlags = flags;
        return frame;
    }

    /// <summary>An icon image as 32-bit ARGB; transparent pixels are transparent black, as GDI+ gives them.</summary>
    internal static Frame IconFrame(Raster raster)
    {
        Frame frame = FromRgba(raster, PixelFormat.Format32bppArgb);
        for (int y = 0; y < frame.Height; y++)
        {
            foreach (ref uint p in MemoryMarshal.Cast<byte, uint>(frame.Row(y)))
            {
                if (p >> 24 == 0)
                {
                    p = 0;
                }
            }
        }

        return frame;
    }

    /// <summary>A direct-color frame from a raster's colors, written as SetPixel writes them.</summary>
    internal static Frame FromRgba(Raster raster, PixelFormat format) => FromRgba(raster.As<Rgba8>(), 0, format, dispose: true);

    private static Frame FromRgba(Raster<Rgba8> rgba, int frameIndex, PixelFormat format, bool dispose = false)
    {
        try
        {
            Frame frame = new(rgba.Width, rgba.Height, format);
            for (int y = 0; y < rgba.Height; y++)
            {
                Span<Rgba8> row = rgba.Frames[frameIndex].Row(y);
                Span<byte> target = frame.Row(y);
                for (int x = 0; x < row.Length; x++)
                {
                    Rgba8 c = row[x];
                    GdiPixels.Write(format, target, x, ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B);
                }
            }

            return frame;
        }
        finally
        {
            if (dispose)
            {
                rgba.Dispose();
            }
        }
    }

    /// <summary>Property items: the EXIF tags, values little-endian; a GIF's frame delays and loop count.</summary>
    private static void AddProperties(Image image, RasterMetadata metadata, List<Frame> frames, string format, byte[] data, int? transparent)
    {
        if (format == "TIFF")
        {
            image.Properties.AddRange(TiffTags(data));
            return;
        }

        if (format == "PNG" && metadata.FormatDetails is PngDetails png)
        {
            if (png.FileGamma is uint gamma)
            {
                image.Properties.Add(Rational(0x0301, 100000, (int)gamma));
            }

            (int ppuX, int ppuY) = metadata.Resolution is Resolution { Unit: ResolutionUnit.PerMeter } res ? ((int)Math.Round(res.X), (int)Math.Round(res.Y)) : (0, 0);
            image.Properties.Add(new PropertyItem { Id = 0x5110, Type = 1, Len = 1, Value = [1] });
            image.Properties.Add(new PropertyItem { Id = 0x5111, Type = 4, Len = 4, Value = BitConverter.GetBytes(ppuX) });
            image.Properties.Add(new PropertyItem { Id = 0x5112, Type = 4, Len = 4, Value = BitConverter.GetBytes(ppuY) });
        }


        if (metadata.Exif is ExifData exif)
        {
            foreach (ExifEntry entry in exif.Entries)
            {
                byte[] value = entry.RawValue.ToArray();
                if (entry.IsBigEndian)
                {
                    Swap(value, entry.Type);
                }

                image.Properties.Add(new PropertyItem { Id = entry.Key.Tag, Type = (short)entry.Type, Len = value.Length, Value = value });
            }
        }

        if (format == "JPEG")
        {
            image.Properties.AddRange(QuantizationTables(data));
        }

        if (format == "GIF")
        {
            byte[] delays = new byte[frames.Count * 4];
            for (int i = 0; i < frames.Count; i++)
            {
                BinaryPrimitives.WriteInt32LittleEndian(delays.AsSpan(i * 4), frames[i].Delay);
            }

            GifDetails? gif = metadata.FormatDetails as GifDetails;
            image.Properties.Add(new PropertyItem { Id = 0x5100, Type = 4, Len = delays.Length, Value = delays });
            if (metadata.Text.FirstOrDefault(t => t.Key == "Comment") is TextEntry comment)
            {
                byte[] text = [.. System.Text.Encoding.Latin1.GetBytes(comment.Value), 0];
                image.Properties.Add(new PropertyItem { Id = 0x9286, Type = 2, Len = text.Length, Value = text });
            }

            image.Properties.Add(new PropertyItem { Id = 0x5101, Type = 3, Len = 2, Value = BitConverter.GetBytes((short)(gif?.RepetitionCount ?? 1)) });
            byte[] global = gif is null ? [] : MemoryMarshal.AsBytes(gif.GlobalPalette.Span).ToArray();
            image.Properties.Add(new PropertyItem { Id = 0x5102, Type = 1, Len = global.Length, Value = global });
            image.Properties.Add(new PropertyItem { Id = 0x5103, Type = 1, Len = 1, Value = [gif?.BackgroundIndex ?? 0] });
            if (transparent is int index)
            {
                image.Properties.Add(new PropertyItem { Id = 0x5104, Type = 1, Len = 1, Value = [(byte)index] });
            }
        }
    }

    private static PropertyItem Rational(int id, int numerator, int denominator)
    {
        byte[] value = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(value, numerator);
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(4), denominator);
        return new PropertyItem { Id = id, Type = 5, Len = 8, Value = value };
    }

    /// <summary>Reverses each value's bytes, by the size of the TIFF type.</summary>
    private static void Swap(byte[] value, ExifType type)
    {
        // Rationals swap each 4-byte half; doubles swap all 8 bytes.
        int size = (int)type switch { 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 => 4, 12 => 8, _ => 1 };
        if (size == 1)
        {
            return;
        }

        for (int i = 0; i + size <= value.Length; i += size)
        {
            Array.Reverse(value, i, size);
        }
    }
}

/// <summary>A multi-frame TIFF save waiting for its pages.</summary>
internal sealed class MultiFrameSave(string? path, Stream? stream, EncoderParameters parameters)
{
    public string? Path { get; } = path;

    public Stream? Stream { get; } = stream;

    public EncoderParameters Parameters { get; } = parameters;

    public List<Raster> Pages { get; } = [];
}
