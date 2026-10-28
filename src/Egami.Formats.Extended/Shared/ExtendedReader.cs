using Egami.Pixels;

namespace Egami.Formats.Extended;

/// <summary>
/// A reader of a format in this package: <see cref="Open"/> reads the header and returns the open image, and the whole-image
/// reads read every row of it, so a load and a streaming decode take the same path.
/// </summary>
internal abstract class ExtendedReader : IFormatReader, IRowFormatReader
{
    public abstract RasterInfo ReadInfo(EgamiInput input, ReaderOptions options);

    public abstract RowReader Open(EgamiInput input, ReaderOptions options);

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using RowReader rows = Open(input, options);
        return ((IWholeRead)rows).ReadAll<TPixel>(input);
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        using RowReader rows = Open(input, options);
        return ((IWholeRead)rows).ReadNatural(input);
    }

    /// <summary>Returns what the header says, for a single still image.</summary>
    protected static RasterInfo Describe(FormatInfo format, int width, int height, PixelLayoutInfo layout, RasterAccess access, long workingBytes)
    {
        return new RasterInfo
        {
            Format = format,
            Width = width,
            Height = height,
            Layout = layout,
            FrameCount = 1,
            EstimatedBytes = (long)width * height * layout.BytesPerPixel,
            Access = access,
            WorkingBytes = workingBytes,
        };
    }
}

/// <summary>
/// A writer of a format in this package. Every save goes through <see cref="Start"/>: <see cref="Write"/> saves through Egami,
/// which calls it, so Save, SaveAsync, RasterEncoder and Transcode write the same bytes.
/// </summary>
internal abstract class ExtendedWriter : IFormatWriter, IRowFormatWriter
{
    /// <summary>Gets the format this writer writes.</summary>
    protected abstract FormatInfo Format { get; }

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(raster);
        raster.Save(output, Format, options);
    }

    public abstract RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>;

    /// <summary>Returns whether rows of <typeparamref name="TPixel"/> hold only gray, so a gray layout can be written.</summary>
    protected static bool IsGray<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return TPixel.Layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
    }
}
