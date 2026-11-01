using Egami.Formats;
using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Tests.Streaming;

/// <summary>
/// <see cref="IFormatConverter"/>: Transcode hands the input to the output writer's converter when there are no operations and
/// the reader options ask for nothing, and decodes as usual when it declines or there is work to do.
/// </summary>
public class FormatConverterTests
{
    /// <summary>A writer that writes "converted" for PNG inputs and the pixel count otherwise.</summary>
    private sealed class Converter : IFormatWriter, IFormatConverter
    {
        public int Converted { get; private set; }

        public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            output.Write(BitConverter.GetBytes(raster.Width * raster.Height));
        }

        public bool TryConvert(Stream input, FormatInfo inputFormat, Stream output, WriterOptions options, EgamiContext context)
        {
            if (inputFormat != PngFormat.Info)
            {
                return false;
            }

            Converted++;
            output.Write("converted"u8);
            return true;
        }
    }

    private static (FormatInfo Format, Converter Writer) Target()
    {
        Converter writer = new();
        return (new FormatInfo { Name = "Converted", Extensions = ["cvt"], SignatureLength = 0, Matches = _ => false, Writer = writer }, writer);
    }

    private static byte[] Png()
    {
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(4, 3);
        using MemoryStream png = new();
        raster.Save(png, PngFormat.Info);
        return png.ToArray();
    }

    private static byte[] Transcode(byte[] input, FormatInfo format, Action<Egami.Operations.RasterOps>? ops = null, ReaderOptions? read = null)
    {
        using MemoryStream source = new(input);
        using MemoryStream output = new();
        Raster.Transcode(source, output, format, ops, read);
        return output.ToArray();
    }

    [Fact]
    public void ANoOperationTranscodeIsConverted()
    {
        (FormatInfo format, Converter writer) = Target();
        Assert.Equal("converted"u8.ToArray(), Transcode(Png(), format));
        Assert.Equal(1, writer.Converted);
    }

    [Fact]
    public void OperationsOrReaderOptionsDecodeAsUsual()
    {
        (FormatInfo format, Converter writer) = Target();
        Assert.Equal(BitConverter.GetBytes(2 * 2), Transcode(Png(), format, ops => ops.Resize(2, 2)));
        Assert.Equal(BitConverter.GetBytes(2 * 1), Transcode(Png(), format, read: new ReaderOptions { Region = new Rect(0, 0, 2, 1) }));
        Assert.Equal(0, writer.Converted);
    }

    [Fact]
    public void ADeclinedConversionDecodesAsUsual()
    {
        (FormatInfo format, Converter writer) = Target();
        using Raster<Rgb8> raster = Raster.Create<Rgb8>(5, 2);
        using MemoryStream bmp = new();
        raster.Save(bmp, Egami.Formats.Bmp.BmpFormat.Info);
        Assert.Equal(BitConverter.GetBytes(10), Transcode(bmp.ToArray(), format));
        Assert.Equal(0, writer.Converted);
    }
}
