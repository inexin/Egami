using System.Buffers;

using Egami.Pixels;

namespace Egami.Formats;

/// <summary>Detection, reading and writing shared by every <see cref="Raster"/> I/O entry point.</summary>
internal static class FormatPipeline
{
    internal static Raster Load(Stream stream, EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
    {
        return Run(stream, context, (format, input) => Named(format.Reader!.Read(input, options), format), cancellationToken);
    }

    internal static Raster<TPixel> Load<TPixel>(Stream stream, EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Run(stream, context, (format, input) => Named(format.Reader!.Read<TPixel>(input, options), format), cancellationToken);
    }

    internal static RasterInfo Probe(Stream stream, EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
    {
        return Run(stream, context, (format, input) => format.Reader!.ReadInfo(input, options), cancellationToken);
    }

    internal static void Save(Stream output, FormatInfo format, WriterOptions options, Action<IFormatWriter, Stream, WriterOptions> write)
    {
        IFormatWriter writer = format.Writer ?? throw new UnsupportedFormatException($"The {format.Name} format has no writer.");
        write(writer, output, options);
    }

    internal static FormatInfo FormatForName(EgamiContext context, string nameOrExtension)
    {
        return context.FindFormat(nameOrExtension)
            ?? throw new UnsupportedFormatException($"No registered format is named or uses the extension '{nameOrExtension}'.");
    }

    internal static FormatInfo FormatForPath(EgamiContext context, string path)
    {
        string extension = Path.GetExtension(path);
        if (extension.Length < 2)
        {
            throw new UnsupportedFormatException($"The path '{path}' has no file extension, so the format cannot be chosen. Pass a format instead.");
        }

        return FormatForName(context, extension);
    }

    /// <summary>Reads a whole stream into a pooled buffer asynchronously, counting it against <see cref="SafetyLimits.MaxTotalBytes"/>.</summary>
    internal static async Task<(byte[] Buffer, int Length)> ReadAllAsync(Stream stream, EgamiContext context, CancellationToken cancellationToken)
    {
        long max = Math.Min(context.Limits.MaxTotalBytes, Array.MaxLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int length = 0;
        try
        {
            while (true)
            {
                if (length == buffer.Length)
                {
                    if (length >= max)
                    {
                        throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), context.Limits.MaxTotalBytes, length + 1L);
                    }

                    byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(max, (long)buffer.Length * 2));
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }

                int n = await stream.ReadAsync(buffer.AsMemory(length, (int)Math.Min(buffer.Length, max) - length), cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    return (buffer, length);
                }

                length += n;
            }
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
    }

    /// <summary>
    /// Reads only as much of the stream as the header needs: 64 KB first, doubling while the header runs past what has been read.
    /// A seekable stream ends just past the bytes the header used.
    /// </summary>
    internal static async Task<RasterInfo> ProbeAsync(Stream stream, EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
    {
        long start = stream.CanSeek ? stream.Position : -1;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int length = 0;
        bool ended = false;
        try
        {
            while (true)
            {
                while (!ended && length < buffer.Length)
                {
                    int n = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                    ended = n == 0;
                    length += n;
                }

                using MemoryStream memory = new(buffer, 0, length, writable: false);
                try
                {
                    RasterInfo info = Probe(memory, context, options, cancellationToken);
                    if (start >= 0)
                    {
                        stream.Position = start + memory.Position;
                    }

                    return info;
                }
                catch (CorruptDataException e) when (e.IsTruncation && !ended)
                {
                    if (buffer.Length >= Math.Min(context.Limits.MaxTotalBytes, Array.MaxLength / 2))
                    {
                        throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), context.Limits.MaxTotalBytes, buffer.Length * 2L);
                    }

                    byte[] larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    buffer.AsSpan(0, length).CopyTo(larger);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Runs <paramref name="read"/> on a stream that was read ahead into memory, then puts a seekable stream just past the image.</summary>
    internal static async Task<T> RunBufferedAsync<T>(Stream stream, EgamiContext context, Func<Stream, T> read, CancellationToken cancellationToken)
    {
        long start = stream.CanSeek ? stream.Position : -1;
        (byte[] buffer, int length) = await ReadAllAsync(stream, context, cancellationToken).ConfigureAwait(false);
        try
        {
            using MemoryStream memory = new(buffer, 0, length, writable: false);
            T result = read(memory);
            if (start >= 0)
            {
                stream.Position = start + memory.Position;
            }

            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static T Run<T>(Stream stream, EgamiContext context, Func<FormatInfo, EgamiInput, T> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using EgamiInput input = new(stream, context, cancellationToken);
        FormatInfo format = Detect(input, context);
        input.SetFormat(format);
        T result = read(format, input);
        input.ReturnUnreadBytes();
        return result;
    }

    private static FormatInfo Detect(EgamiInput input, EgamiContext context)
    {
        if (context.Formats.Count == 0)
        {
            throw new UnsupportedFormatException("No formats are registered in the context.");
        }

        int signatureLength = 0;
        foreach (FormatInfo format in context.Formats)
        {
            signatureLength = Math.Max(signatureLength, format.SignatureLength);
        }

        ReadOnlySpan<byte> header = input.Peek(signatureLength);
        foreach (FormatInfo format in context.Formats)
        {
            if (header.Length >= format.SignatureLength && format.Matches(header))
            {
                return format.Reader is null ? throw new UnsupportedFormatException($"The data is {format.Name}, but that format has no reader.") : format;
            }
        }

        throw new UnsupportedFormatException(
            $"The data does not match any registered format ({string.Join(", ", context.Formats.Select(f => f.Name))}).");
    }

    private static TRaster Named<TRaster>(TRaster raster, FormatInfo format)
        where TRaster : Raster
    {
        raster.Metadata.FormatName ??= format.Name;
        return raster;
    }
}
