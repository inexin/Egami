using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jpeg2000;

/// <summary>
/// Reads JPEG 2000, written from ITU-T T.800 (Part 1), T.801's JPX boxes and the PDF specification's JPXDecode filter, and
/// checked against OpenJPEG's decodes and the ISO/IEC 15444-4 conformance codestreams.
/// </summary>
internal sealed partial class Jpeg2000Reader : IFormatReader
{
    internal static readonly Jpeg2000Reader Instance = new();

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        return Run(input, options, headerOnly: true, (file, stream) => Info(file, stream, options));
    }

    private static RasterInfo Info(Jp2File file, J2kCodestream stream, ReaderOptions options)
    {
        J2kPixels pixels = J2kPixels.Create(file, stream.Components, Mode(options));
        int width = stream.Width - stream.OffsetX;
        int height = stream.Height - stream.OffsetY;
        return new RasterInfo
        {
            Format = Jpeg2000Format.Info,
            Width = width,
            Height = height,
            Layout = pixels.Layout,
            FrameCount = 1,
            ImageCount = 1,
            HasMetadata = !file.Xmp.IsEmpty || !file.Icc.IsEmpty || file.Capture is not null || file.Display is not null,
            EstimatedBytes = (long)width * height * pixels.Layout.BytesPerPixel,
            Access = Holding(stream, pixels, options).Access,
            WorkingBytes = Holding(stream, pixels, options).Bytes,
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Run(input, options, headerOnly: false, (file, stream) => Decode<TPixel>(input, file, stream, options));
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        return Run(input, options, headerOnly: false, (file, stream) => J2kPixels.Create(file, stream.Components, Mode(options)).Natural switch
        {
            J2kNatural.Gray8 => Decode<Gray8>(input, file, stream, options),
            J2kNatural.Gray16 => Decode<Gray16>(input, file, stream, options),
            J2kNatural.GrayF32 => Decode<GrayF32>(input, file, stream, options),
            J2kNatural.GrayAlpha8 => Decode<GrayAlpha8>(input, file, stream, options),
            J2kNatural.GrayAlpha16 => Decode<GrayAlpha16>(input, file, stream, options),
            J2kNatural.Rgb8 => Decode<Rgb8>(input, file, stream, options),
            J2kNatural.Rgb16 => Decode<Rgb16>(input, file, stream, options),
            J2kNatural.RgbF32 => Decode<RgbF32>(input, file, stream, options),
            J2kNatural.Rgba8 => Decode<Rgba8>(input, file, stream, options),
            J2kNatural.Rgba16 => Decode<Rgba16>(input, file, stream, options),
            J2kNatural.RgbaF32 => (Raster)Decode<RgbaF32>(input, file, stream, options),
            J2kNatural.Cmyk8 => Decode<Cmyk8>(input, file, stream, options),
            _ => Decode<Cmyk16>(input, file, stream, options),
        });
    }

    /// <summary>Gets how subsampled chroma is upsampled: bilinearly at its siting, or repeated with <see cref="Jpeg2000ReaderOptions.FastUpsampling"/>.</summary>
    private static YuvConvertOptions Upsampling(ReaderOptions options) => new()
    {
        Upsampling = options is Jpeg2000ReaderOptions { FastUpsampling: true } ? ChromaUpsampling.Nearest : ChromaUpsampling.Bilinear,
    };

    /// <summary>Returns the reduction <see cref="Open"/> applies: what the plan asks for, as dropped wavelet levels.</summary>
    internal static int Reduction(J2kCodestream stream, ReaderOptions options)
    {
        int width = stream.Width - stream.OffsetX;
        int height = stream.Height - stream.OffsetY;
        int max = MaxNativeScale(stream);
        int scale = options.NativeScale is { } choose
            ? choose(width, height)
            : Streaming.ReadPlan.Create(width, height, options.Region, options.MinimumSize, max).NativeScale;
        return System.Numerics.BitOperations.Log2((uint)Math.Clamp(scale, 1, max));
    }

    /// <summary>
    /// Returns how <see cref="Open"/> serves rows and what it holds: streamed from one row of tiles' samples at a time, or,
    /// for subsampled sYCC whose chroma is interpolated across rows, buffered as the whole image in its natural type.
    /// </summary>
    internal static (RasterAccess Access, long Bytes) Holding(J2kCodestream stream, J2kPixels pixels, ReaderOptions options)
    {
        int reduce = Reduction(stream, options);
        bool yuv = pixels.Model == J2kModel.Sycc && PlaneFormat(stream, pixels) is (ChromaFormat format, _) && format != ChromaFormat.Yuv444;
        if (yuv)
        {
            long width = J2kTier2.CeilShift(stream.Width, reduce) - J2kTier2.CeilShift(stream.OffsetX, reduce);
            long height = J2kTier2.CeilShift(stream.Height, reduce) - J2kTier2.CeilShift(stream.OffsetY, reduce);
            return (RasterAccess.Buffered, width * height * pixels.Layout.BytesPerPixel);
        }

        (int[] starts, _) = Bands(stream, reduce, Banded(stream));
        return (RasterAccess.Streamed, WorkingBytesOf(stream, reduce, starts, pixels.Needed(stream.Components.Length, stream.MainCod.Mct == 1)));
    }

    private static Jpeg2000Components Mode(ReaderOptions options) => options is Jpeg2000ReaderOptions j ? j.Components : Jpeg2000Components.Color;

    /// <summary>Gets the most bytes a decode at <paramref name="reduce"/> holds: every component's samples, and a tile's coefficients.</summary>
    internal static long WorkingBytes(J2kCodestream stream, int reduce, bool[]? needed = null)
    {
        long samples = 0;
        for (int c = 0; c < stream.Components.Length; c++)
        {
            if (needed is not null && !needed[c])
            {
                continue;
            }

            (int w, int h) = J2kDecoder.ComponentSize(stream, c, reduce);
            samples += (long)w * h;
        }

        long tile = Math.Min(samples, (long)Math.Min(stream.TileWidth, stream.Width) * Math.Min(stream.TileHeight, stream.Height) * stream.Components.Length);
        return (samples + tile) * 4;
    }

    private static Raster<TPixel> Decode<TPixel>(EgamiInput input, Jp2File file, J2kCodestream stream, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        J2kPixels pixels = J2kPixels.Create(file, stream.Components, Mode(options));
        bool[] needed = pixels.Needed(stream.Components.Length, stream.MainCod.Mct == 1);
        J2kImage image = Decode(input, stream, options, needed);
        J2kGrid grid = J2kGrid.Create(stream, 0, needed);
        Raster<TPixel> raster = input.CreateRaster<TPixel>(grid.Width, grid.Height);
        try
        {
            if (pixels.Model == J2kModel.Sycc && PlaneFormat(stream, pixels) is (ChromaFormat format, int depth) && format != ChromaFormat.Yuv444)
            {
                // Subsampled sYCC: chroma upsampled with the YUV kernels, as JPEG's is (design question 9).
                // Into the natural type first, so any other type is the natural pixels converted, as streaming gives them.
                if (depth == 8)
                {
                    using YuvRaster<byte> planes = Planes<byte>(image, format, depth, input.Context);
                    Natural<byte, Rgb8, TPixel>(planes, raster, input, options);
                }
                else
                {
                    using YuvRaster<ushort> planes = Planes<ushort>(image, format, depth, input.Context);
                    Natural<ushort, Rgb16, TPixel>(planes, raster, input, options);
                }
            }
            else
            {
                Fill(raster, image, grid, pixels, input.Context.MaxParallelism);
            }

            Describe(raster.Metadata, file, stream, options);
            Warn(input, raster, file, image, pixels);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
        finally
        {
            image.Release();
        }
    }

    /// <summary>Decodes the codestream at full resolution, within the limits; a truncated codestream fails a strict read.</summary>
    private static J2kImage Decode(EgamiInput input, J2kCodestream stream, ReaderOptions options, bool[]? needed)
    {
        long working = WorkingBytes(stream, 0, needed);
        if (working > input.Limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), input.Limits.MaxTotalBytes, working);
        }

        int layers = options is Jpeg2000ReaderOptions { MaxQualityLayers: int n } ? Math.Max(n, 1) : 0;
        J2kImage image = new J2kDecoder(stream, 0, layers, input.Context.MaxParallelism, input.CancellationToken) { Needed = needed }.Decode();
        if (image.Truncated && options.Recovery == RecoveryMode.Strict)
        {
            throw new CorruptDataException(input.FormatName, -1, "The JPEG 2000 codestream ends before its last packet.") { IsTruncation = true };
        }

        return image;
    }

    /// <summary>Converts YUV planes to RGB in <typeparamref name="TNatural"/>, then to <typeparamref name="TPixel"/> when that is another type.</summary>
    private static void Natural<TSample, TNatural, TPixel>(YuvRaster<TSample> planes, Raster<TPixel> raster, EgamiInput input, ReaderOptions options)
        where TSample : unmanaged
        where TNatural : unmanaged, IPixelType<TNatural>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (typeof(TPixel) == typeof(TNatural))
        {
            YuvConvert.ToRgb(planes, raster, Upsampling(options), input.CancellationToken);
            return;
        }

        using Raster<TNatural> natural = input.CreateRaster<TNatural>(raster.Width, raster.Height);
        YuvConvert.ToRgb(planes, natural, Upsampling(options), input.CancellationToken);
        for (int y = 0; y < raster.Height; y++)
        {
            PixelConvert.Convert<TNatural, TPixel>(natural.Row(y), raster.Row(y));
        }
    }

    /// <summary>Converts the decoded samples to pixels, row by row, in bands on several threads for a large image.</summary>
    private static void Fill<TPixel>(Raster<TPixel> raster, J2kImage image, J2kGrid grid, J2kPixels pixels, int parallelism)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int bytes = grid.Width * pixels.Layout.BytesPerPixel;
        bool direct = TPixel.Layout == pixels.Layout;
        void Rows(int from, int to)
        {
            byte[]? buffer = direct ? null : ArrayPool<byte>.Shared.Rent(bytes);
            try
            {
                for (int y = from; y < to; y++)
                {
                    if (direct)
                    {
                        pixels.Row(image, grid, y, 0, MemoryMarshal.AsBytes(raster.Row(y)));
                        continue;
                    }

                    Span<byte> natural = buffer.AsSpan(0, bytes);
                    pixels.Row(image, grid, y, 0, natural);
                    Convert(pixels.Natural, natural, raster.Row(y));
                }
            }
            finally
            {
                if (buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
            }
        }

        int parts = (long)grid.Width * grid.Height < 1 << 18 ? 1 : Math.Min(parallelism, grid.Height / 16);
        if (parts <= 1)
        {
            Rows(0, grid.Height);
            return;
        }

        Parallel.For(0, parts, new ParallelOptions { MaxDegreeOfParallelism = parts }, i => Rows(grid.Height * i / parts, grid.Height * (i + 1) / parts));
    }

    /// <summary>Converts a row of the natural type to <typeparamref name="TPixel"/>.</summary>
    internal static void Convert<TPixel>(J2kNatural natural, ReadOnlySpan<byte> source, Span<TPixel> target)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        switch (natural)
        {
            case J2kNatural.Gray8: PixelConvert.Convert(MemoryMarshal.Cast<byte, Gray8>(source), target); break;
            case J2kNatural.Gray16: PixelConvert.Convert(MemoryMarshal.Cast<byte, Gray16>(source), target); break;
            case J2kNatural.GrayF32: PixelConvert.Convert(MemoryMarshal.Cast<byte, GrayF32>(source), target); break;
            case J2kNatural.GrayAlpha8: PixelConvert.Convert(MemoryMarshal.Cast<byte, GrayAlpha8>(source), target); break;
            case J2kNatural.GrayAlpha16: PixelConvert.Convert(MemoryMarshal.Cast<byte, GrayAlpha16>(source), target); break;
            case J2kNatural.Rgb8: PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgb8>(source), target); break;
            case J2kNatural.Rgb16: PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgb16>(source), target); break;
            case J2kNatural.RgbF32: PixelConvert.Convert(MemoryMarshal.Cast<byte, RgbF32>(source), target); break;
            case J2kNatural.Rgba8: PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgba8>(source), target); break;
            case J2kNatural.Rgba16: PixelConvert.Convert(MemoryMarshal.Cast<byte, Rgba16>(source), target); break;
            case J2kNatural.RgbaF32: PixelConvert.Convert(MemoryMarshal.Cast<byte, RgbaF32>(source), target); break;
            case J2kNatural.Cmyk8: PixelConvert.Convert(MemoryMarshal.Cast<byte, Cmyk8>(source), target); break;
            default: PixelConvert.Convert(MemoryMarshal.Cast<byte, Cmyk16>(source), target); break;
        }
    }

    /// <summary>Fills the metadata: format details, ICC, XMP and resolution.</summary>
    internal static void Describe(RasterMetadata metadata, Jp2File file, J2kCodestream stream, ReaderOptions options)
    {
        J2kCodingStyle cod = stream.MainCod;
        Jpeg2000Details details = new()
        {
            Container = file.Container,
            Profile = stream.Profile,
            Reversible = cod.Reversible,
            DecompositionLevels = cod.Levels,
            QualityLayers = cod.Layers,
            ProgressionOrder = (Jpeg2000ProgressionOrder)cod.Progression,
            TileWidth = stream.TileWidth,
            TileHeight = stream.TileHeight,
            TileCount = stream.TileCount,
            ColorSpace = ColorSpaceOf(file),
            EnumeratedColorSpace = file.ColorMethod == 1 ? file.EnumeratedColorSpace : -1,
            HasPalette = file.Palette is not null,
        };
        foreach (J2kComponent component in stream.Components)
        {
            details.Components.Add(new Jpeg2000Component(component.Precision, component.Signed, component.Dx, component.Dy));
        }

        if (file.Channels is { } channels)
        {
            foreach ((int channel, int type, int association) in channels)
            {
                details.Channels.Add(new Jpeg2000Channel(channel, type switch
                {
                    0 => Jpeg2000ChannelType.Color,
                    1 => Jpeg2000ChannelType.Opacity,
                    2 => Jpeg2000ChannelType.PremultipliedOpacity,
                    _ => Jpeg2000ChannelType.Unspecified,
                }, association == 65535 ? -1 : association));
            }
        }

        foreach (string comment in stream.Comments)
        {
            details.Comments.Add(comment);
        }

        metadata.FormatDetails = details;
        if (!options.ReadMetadata)
        {
            return;
        }

        if (file.ColorMethod is 2 or 3 && Mode(options) == Jpeg2000Components.Color)
        {
            metadata.IccProfile = file.Icc.ToArray();
        }

        if (!file.Xmp.IsEmpty)
        {
            metadata.Xmp = file.Xmp.ToArray();
        }

        if ((file.Display ?? file.Capture) is (double vertical, double horizontal))
        {
            metadata.Resolution = new Resolution(horizontal, vertical, ResolutionUnit.PerMeter);
        }
    }

    private static Jpeg2000ColorSpace ColorSpaceOf(Jp2File file) => file.ColorMethod switch
    {
        1 => file.EnumeratedColorSpace switch
        {
            16 => Jpeg2000ColorSpace.Srgb,
            17 => Jpeg2000ColorSpace.Gray,
            18 => Jpeg2000ColorSpace.Sycc,
            12 => Jpeg2000ColorSpace.Cmyk,
            14 => Jpeg2000ColorSpace.CieLab,
            _ => Jpeg2000ColorSpace.Other,
        },
        2 or 3 => Jpeg2000ColorSpace.Icc,
        _ => Jpeg2000ColorSpace.Unspecified,
    };

    private static void Warn(EgamiInput input, Raster raster, Jp2File file, J2kImage image, J2kPixels pixels)
    {
        foreach ((string id, string message) in file.Warnings)
        {
            input.Warn(raster, id, message);
        }

        if (image.Truncated)
        {
            input.Warn(raster, "Jpeg2000.Truncated", "The codestream ends before its last packet; what it holds was decoded.");
        }

        if (image.MissingTiles > 0)
        {
            input.Warn(raster, "Jpeg2000.MissingTiles", $"{image.MissingTiles} tiles have no data and were left black.");
        }

        if (pixels.OffsetSigned)
        {
            input.Warn(raster, "Jpeg2000.SignedSamples", "Signed samples were offset by half their range to load as unsigned.");
        }

        if (pixels.Dropped > 0)
        {
            input.Warn(raster, "Jpeg2000.ChannelsDropped", $"{pixels.Dropped} channels that are neither color nor alpha were not loaded.");
        }
    }

    /// <summary>
    /// Reads the whole input, parses the boxes and the codestream, and runs <paramref name="action"/>, mapping damage to
    /// <see cref="CorruptDataException"/>.
    /// </summary>
    private static T Run<T>(EgamiInput input, ReaderOptions options, bool headerOnly, Func<Jp2File, J2kCodestream, T> action)
    {
        (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
        try
        {
            Jp2File file = Jp2File.Parse(data.AsMemory(0, length));
            J2kCodestream stream = J2kCodestream.Parse(file.Codestream, input.Limits, headerOnly);
            return action(file, stream);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            throw e is EndOfStreamException
                ? new CorruptDataException(input.FormatName, -1, e.Message) { IsTruncation = true }
                : input.Corrupt(-1, e.Message);
        }
        finally
        {
            if (pooled)
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }
    }
}
