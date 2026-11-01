using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// The entry points for JPEG streams inside other formats: TIFF compression 7 and DNG tiles, where the tables may live in a
/// separate JPEGTables stream and lossless samples are wanted raw, without color conversion. Internal until a reader uses them.
/// </summary>
internal static class JpegEmbedded
{
    /// <summary>
    /// Decodes a lossless JPEG stream (SOF3 or SOF11, 1 to 4 components, all sampled 1×1) to its samples, interleaved by
    /// component, at the stream's own precision and with its point transform applied. <paramref name="tables"/> is the shared
    /// table stream, or empty.
    /// </summary>
    internal static ushort[] DecodeLosslessSamples(
        ReadOnlyMemory<byte> tables, ReadOnlyMemory<byte> data, EgamiContext context, out JpegDetails details, CancellationToken cancellationToken = default)
    {
        return DecodeLosslessSamples(tables, data, context, out details, out _, out _, out _, cancellationToken);
    }

    /// <summary>
    /// Decodes a lossless JPEG stream to its samples as the overload without the frame's size does, also giving the frame's width
    /// and component count, and whether the decode warned (the data was damaged or ended early; what decoded is kept).
    /// </summary>
    internal static ushort[] DecodeLosslessSamples(
        ReadOnlyMemory<byte> tables, ReadOnlyMemory<byte> data, EgamiContext context, out JpegDetails details, out int width, out int components,
        out bool damaged, CancellationToken cancellationToken = default)
    {
        JpegHeader header = ReadHeader(tables, data, context, cancellationToken, out EgamiInput input);
        if (!header.Lossless)
        {
            throw new UnsupportedFormatException("The JPEG stream is not lossless; its samples are not raw.");
        }

        if (header.Components.Length > 1 && header.Components.Any(c => c.H != 1 || c.V != 1))
        {
            throw new UnsupportedFormatException("A lossless JPEG stream with subsampled components has no interleaved raw samples.");
        }

        context.Limits.CheckFrameSize(header.Width, header.Height);
        long count = (long)header.Width * header.Height * header.Components.Length;
        input.CheckMemory(count * sizeof(ushort));
        ushort[] samples = new ushort[count];
        using JpegLosslessDecoder decoder = new(header, ReaderOptions.Default, header.Metadata.WarningList, fastUpsampling: true);
        decoder.Decode(input);
        decoder.CopySamples(samples);
        details = header.Details;
        width = header.Width;
        components = header.Components.Length;
        damaged = header.Metadata.WarningList.Count > 0;
        return samples;
    }

    /// <summary>Decodes a JPEG stream with shared tables to pixels, as a load of the whole file would.</summary>
    internal static Raster<TPixel> Decode<TPixel>(ReadOnlyMemory<byte> tables, ReadOnlyMemory<byte> data, EgamiContext context, CancellationToken cancellationToken = default)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        JpegHeader header = ReadHeader(tables, data, context, cancellationToken, out EgamiInput input);
        return JpegReader.Decode<TPixel>(input, header, ReaderOptions.Default);
    }

    private static JpegHeader ReadHeader(ReadOnlyMemory<byte> tables, ReadOnlyMemory<byte> data, EgamiContext context, CancellationToken cancellationToken, out EgamiInput input)
    {
        JpegHeader header = new() { Raw = true };
        if (!tables.IsEmpty)
        {
            JpegSegmentReader.ReadTables(new EgamiInput(Stream(tables), context, cancellationToken), header);
        }

        input = new EgamiInput(Stream(data), context, cancellationToken);
        return JpegSegmentReader.ReadHeader(input, ReaderOptions.Default, header);
    }

    /// <summary>Returns a read-only stream over the bytes, without a copy when they are an array's.</summary>
    private static MemoryStream Stream(ReadOnlyMemory<byte> bytes)
    {
        return MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false)
            : new MemoryStream(bytes.ToArray(), writable: false);
    }
}
