using System.Buffers.Binary;

using Egami.Formats;
using Egami.Pixels;

namespace Egami.Tests.Formats;

/// <summary>
/// A minimal format built only on the public API, the way a custom format would be: "EGTF", width and height as big-endian
/// 32-bit integers, a gray flag byte, then raw Rgba8 or Gray8 pixels.
/// </summary>
internal sealed class TestFormat : IFormatReader, IFormatWriter
{
    public static readonly FormatInfo Info = Create();

    public static EgamiContext Context { get; } = EgamiContext.Default.WithFormat(Info);

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        (int width, int height, bool gray) = ReadHeader(input);
        PixelLayoutInfo layout = gray ? Gray8.Layout : Rgba8.Layout;
        return new RasterInfo
        {
            Format = Info,
            Width = width,
            Height = height,
            Layout = layout,
            FrameCount = 1,
            EstimatedBytes = (long)width * height * layout.BytesPerPixel,
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        (int width, int height, bool gray) = ReadHeader(input);
        return gray ? Decode<Gray8, TPixel>(input, width, height) : Decode<Rgba8, TPixel>(input, width, height);
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        (int width, int height, bool gray) = ReadHeader(input);
        return gray ? Decode<Gray8, Gray8>(input, width, height) : Decode<Rgba8, Rgba8>(input, width, height);
    }

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        bool gray = TPixel.Layout.ChannelOrder == PixelChannelOrder.Gray;
        Span<byte> header = stackalloc byte[13];
        "EGTF"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], (uint)raster.Width);
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)raster.Height);
        header[12] = gray ? (byte)1 : (byte)0;
        output.Write(header);

        if (gray)
        {
            WriteRows<TPixel, Gray8>(raster, output);
        }
        else
        {
            WriteRows<TPixel, Rgba8>(raster, output);
        }
    }

    private static void WriteRows<TPixel, TFile>(Raster<TPixel> raster, Stream output)
        where TPixel : unmanaged, IPixelType<TPixel>
        where TFile : unmanaged, IPixelType<TFile>
    {
        TFile[] row = new TFile[raster.Width];
        for (int y = 0; y < raster.Height; y++)
        {
            PixelConvert.Convert<TPixel, TFile>(raster.Row(y), row);
            output.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(row.AsSpan()));
        }
    }

    private static Raster<TPixel> Decode<TFile, TPixel>(EgamiInput input, int width, int height)
        where TFile : unmanaged, IPixelType<TFile>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = input.CreateRaster<TPixel>(width, height);
        try
        {
            TFile[] row = new TFile[width];
            for (int y = 0; y < height; y++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                input.ReadExactly(System.Runtime.InteropServices.MemoryMarshal.AsBytes(row.AsSpan()));
                PixelConvert.Convert<TFile, TPixel>(row, raster.Row(y));
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    private static (int Width, int Height, bool Gray) ReadHeader(EgamiInput input)
    {
        input.Skip(4);
        uint width = input.ReadUInt32BigEndian();
        uint height = input.ReadUInt32BigEndian();
        byte gray = input.ReadByte();
        if (width > int.MaxValue || height > int.MaxValue)
        {
            throw input.Corrupt(4, "The size does not fit in 31 bits.");
        }

        return ((int)width, (int)height, gray != 0);
    }

    private static FormatInfo Create()
    {
        TestFormat format = new();
        return new FormatInfo
        {
            Name = "EGTF",
            MimeTypes = ["image/x-egami-test"],
            Extensions = ["egtf", "egt"],
            SignatureLength = 4,
            Matches = header => header.StartsWith("EGTF"u8),
            Reader = format,
            Writer = format,
        };
    }
}
