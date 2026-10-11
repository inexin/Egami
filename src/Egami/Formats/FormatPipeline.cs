using System.Buffers;

using Egami.Pixels;

namespace Egami.Formats;

/// <summary>Detection, reading and writing shared by every <see cref="Raster"/> I/O entry point.</summary>
internal static class FormatPipeline
{
    internal static Raster Load(Stream stream, EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
    {
        if (options.ConvertColorTo is Colors.ColorProfile target)
        {
            return ColorLoad.Natural(Load(stream, context, options with { ConvertColorTo = null }, cancellationToken), target);
        }

        if (Reshapes(options))
        {
            using RasterDecoder decoder = RasterDecoder.Open(stream, context, options, owned: null);
            return Named(decoder.ReadAll(), decoder.Info.Format);
        }

        return Run(stream, context, (format, input) =>
        {
            try
            {
                return Loaded(input, format.Reader!.Read(input, options), format, options);
            }
            catch (LimitExceededException e) when (LimitHit(input, e))
            {
                throw;
            }
        }, cancellationToken);
    }

    internal static Raster<TPixel> Load<TPixel>(Stream stream, EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (options.ConvertColorTo is Colors.ColorProfile target)
        {
            return ColorLoad.Typed<TPixel>(Load(stream, context, options with { ConvertColorTo = null }, cancellationToken), target);
        }

        if (Reshapes(options))
        {
            using RasterDecoder decoder = RasterDecoder.Open(stream, context, options, owned: null);
            return Named(decoder.ReadAll<TPixel>(), decoder.Info.Format);
        }

        return Run(stream, context, (format, input) =>
        {
            try
            {
                return Loaded(input, format.Reader!.Read<TPixel>(input, options), format, options);
            }
            catch (LimitExceededException e) when (LimitHit(input, e))
            {
                throw;
            }
        }, cancellationToken);
    }

    internal static RasterInfo Probe(Stream stream, EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
    {
        return Run(stream, context, (format, input) => ProbeCore(format, input, options), cancellationToken);
    }

    /// <summary>
    /// Opens a source of rows: a streaming one when the format offers it (it then owns the input), otherwise the whole image
    /// decoded into a buffer.
    /// </summary>
    internal static (Streaming.RowSource Source, FormatInfo Format) OpenSource(Stream stream, EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EgamiInput input = new(stream, context, cancellationToken);
        FormatInfo format;
        try
        {
            format = Detect(input, context);
            input.SetFormat(format);
        }
        catch (LimitExceededException e) when (LimitHit(input, e))
        {
            throw;
        }
        catch
        {
            input.Dispose();
            throw;
        }

        return (OpenSource(input, format, options), format);
    }

    /// <summary>
    /// Opens a source of rows over an input whose format is known, taking the input over: a streaming source owns it from then on,
    /// and a buffered one has read it whole and disposed of it.
    /// </summary>
    internal static Streaming.RowSource OpenSource(EgamiInput input, FormatInfo format, ReaderOptions options)
    {
        bool owned = false;
        try
        {
            if (format.Reader is Streaming.IRowSourceReader rows)
            {
                Streaming.RowSource source = rows.Open(input, options);
                if (source is not Streaming.BufferedRowSource buffered)
                {
                    owned = true;
                    return source;
                }

                buffered.Raster.Metadata.FormatName ??= format.Name;
                input.ReturnUnreadBytes();
                return source;
            }

            if (format.Reader is IRowFormatReader plugin)
            {
                Streaming.RowSource source = new Streaming.PluginRowSource(plugin.Open(input, options), input);
                owned = true;
                return source;
            }

            Streaming.BufferedRowSource read = Streaming.BufferedRowSource.Read(format, input, options);
            read.Raster.Metadata.FormatName ??= format.Name;
            input.ReturnUnreadBytes();
            return read;
        }
        finally
        {
            if (!owned)
            {
                input.Dispose();
            }
        }
    }

    /// <summary>Names a raster a reader returned, has its Warnings sent as the load ends, and notes a frame range it was read with.</summary>
    private static TRaster Loaded<TRaster>(EgamiInput input, TRaster raster, FormatInfo format, ReaderOptions options)
        where TRaster : Raster
    {
        if (!options.Frames.Equals(Range.All))
        {
            input.Emit(DiagnosticLevel.Info, "Decode.FramesSelected", $"ReaderOptions.Frames is {options.Frames}: {raster.FrameCount} frames were read and the rest skipped.");
        }

        return input.Watch(Named(raster, format));
    }

    /// <summary>Notes a safety limit as it fails a load, while the exception is on its way out; never catches it.</summary>
    internal static bool LimitHit(EgamiInput input, LimitExceededException e)
    {
        input.Emit(DiagnosticLevel.Debug, "Limits.Exceeded", $"{e.LimitName} is {e.Allowed:N0}; the file needs {e.Found:N0}. The load fails with LimitExceededException.");
        return false;
    }

    /// <summary>Gets whether the options crop or reduce, which the session path does; otherwise Load runs as in 1.0.</summary>
    private static bool Reshapes(ReaderOptions options)
    {
        return options.Region is not null || options.MinimumSize is not null;
    }

    /// <summary>Reads the header, then adds what a decoder with the same options would do: its access and working bytes.</summary>
    private static RasterInfo ProbeCore(FormatInfo format, EgamiInput input, ReaderOptions options)
    {
        IFormatReader reader = format.Reader!;
        int maxScale = reader is Streaming.INativeScaling native ? native.MaxNativeScale : 1;
        int width = 0;
        int height = 0;
        ReaderOptions read = options;
        if (maxScale > 1 && options.Region is not null)
        {
            read = options with
            {
                NativeScale = (w, h) =>
                {
                    (width, height) = (w, h);
                    return Streaming.ReadPlan.Create(w, h, options.Region, options.MinimumSize, maxScale).NativeScale;
                },
            };
        }

        RasterInfo info = reader.ReadInfo(input, read);
        if (info.ImageCount is null && reader is not IMultiImageReader && format.Reader is not null && IsBuiltIn(format))
        {
            info = info with { ImageCount = 1 };
        }

        if (reader is Streaming.IRowSourceReader or IRowFormatReader && info.Access == RasterAccess.Streamed)
        {
            // A streaming reader reports its own working set.
            Streaming.ReadPlan streamed = Streaming.ReadPlan.Create(info.Width, info.Height, options.Region, options.MinimumSize, maxScale);
            long reduce = streamed.Factor == 1 ? 0 : (streamed.Output.Width * 4L * sizeof(double)) + (streamed.Region.Width * 16L);
            return info with { WorkingBytes = info.WorkingBytes + reduce };
        }

        if (width == 0)
        {
            (width, height) = (info.Width, info.Height);
        }

        Streaming.ReadPlan plan = Streaming.ReadPlan.Create(width, height, options.Region, options.MinimumSize, maxScale);
        long stage = plan.Factor == 1 ? 0 : (plan.Output.Width * 4L * sizeof(double)) + (plan.Region.Width * 16L);
        long buffer = (long)plan.ScaledSize.Width * plan.ScaledSize.Height * info.Layout.BytesPerPixel * Math.Max(1, info.FrameCount ?? 1);
        return info with { Access = RasterAccess.Buffered, WorkingBytes = buffer + stage };
    }

    /// <summary>Gets whether a format is one of Egami's own, which all say how many images they hold.</summary>
    private static bool IsBuiltIn(FormatInfo format) => format.Reader?.GetType().Assembly == typeof(FormatPipeline).Assembly;

    /// <summary>
    /// Loads every image of a file. A format that holds several (<see cref="IMultiImageReader"/>) names an options value for each,
    /// and each is loaded as Load would load it, from the start of the data; any other format gives its one image. A stream that
    /// cannot seek is read whole first. The images' bytes together count against <see cref="SafetyLimits.MaxTotalBytes"/> and
    /// their number against <see cref="SafetyLimits.MaxFrames"/>.
    /// </summary>
    internal static RasterList LoadAll(Stream stream, EgamiContext context, ReaderOptions options, Func<Stream, ReaderOptions, Raster> load, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
        {
            (byte[] buffer, int length) = ReadAll(stream, context, cancellationToken);
            DiagnosticReporter? diagnostics = null;
            context.Emit(ref diagnostics, DiagnosticLevel.Debug, "Streaming.ReadWhole", $"The stream cannot seek, so its {length:N0} bytes were read into memory to find every image.");
            try
            {
                using MemoryStream memory = new(buffer, 0, length, writable: false, publiclyVisible: true);
                return LoadAll(memory, context, options, load, cancellationToken);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        long start = stream.Position;
        IReadOnlyList<ReaderOptions> images = Run(stream, context, (format, input) => format.Reader is IMultiImageReader multi ? multi.Images(input, options) : [options], cancellationToken);
        context.Limits.CheckFrameCount(images.Count);
        List<Raster?> rasters = new(images.Count);
        long total = 0;
        long end = start;
        try
        {
            foreach (ReaderOptions image in images)
            {
                cancellationToken.ThrowIfCancellationRequested();
                stream.Position = start;
                Raster raster = load(stream, image);
                rasters.Add(raster);
                end = Math.Max(end, stream.Position);
                total += (long)raster.Width * raster.Height * raster.Layout.BytesPerPixel * raster.FrameCount;
                if (total > context.Limits.MaxTotalBytes)
                {
                    throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), context.Limits.MaxTotalBytes, total);
                }
            }

            stream.Position = end;
            return new RasterList(rasters);
        }
        catch
        {
            foreach (Raster? raster in rasters)
            {
                raster?.Dispose();
            }

            throw;
        }
    }

    /// <summary>Reads a whole stream into a pooled buffer, counting the bytes against <see cref="SafetyLimits.MaxTotalBytes"/>.</summary>
    private static (byte[] Buffer, int Length) ReadAll(Stream stream, EgamiContext context, CancellationToken cancellationToken)
    {
        long max = Math.Min(context.Limits.MaxTotalBytes, Array.MaxLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int length = 0;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
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

                int n = stream.Read(buffer, length, (int)Math.Min(buffer.Length, max) - length);
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
    /// Writes several rasters to one file: a format that holds several (<see cref="IMultiImageWriter"/>) takes them all, and any
    /// format takes one, as Save writes it.
    /// </summary>
    internal static void SaveAll(IReadOnlyList<Raster> rasters, Stream output, FormatInfo format, WriterOptions options)
    {
        IFormatWriter writer = format.Writer ?? throw new UnsupportedFormatException($"The {format.Name} format has no writer.");
        if (rasters.Count == 0)
        {
            throw new ArgumentException("SaveAll needs at least one raster.", nameof(rasters));
        }

        foreach (Raster raster in rasters)
        {
            ArgumentNullException.ThrowIfNull(raster, nameof(rasters));
        }

        if (writer is IMultiImageWriter multi)
        {
            multi.WriteAll(rasters, output, options);
            return;
        }

        if (rasters.Count > 1)
        {
            throw new ArgumentException($"The {format.Name} format holds one image; {rasters.Count} rasters were given. Save each to its own file, or use a format that holds several, such as TIFF.", nameof(rasters));
        }

        rasters[0].Save(output, format, options);
    }

    internal static void Save(Stream output, FormatInfo format, WriterOptions options, Action<IFormatWriter, Stream, WriterOptions> write)
    {
        IFormatWriter writer = format.Writer ?? throw new UnsupportedFormatException($"The {format.Name} format has no writer.");
        write(writer, output, options);
    }

    /// <summary>
    /// Encodes asynchronously through the format's row encoder, as Save would, writing the bytes out between bands so the file is
    /// never held whole. Formats from plugins without one are encoded into a pooled buffer first, as in 1.0.
    /// </summary>
    internal static Task SaveAsync(Raster raster, Stream stream, FormatInfo format, WriterOptions options, CancellationToken cancellationToken)
    {
        return raster.Accept(new SaveAsyncVisitor(stream, format, options, cancellationToken));
    }

    private static async Task SaveAsync<TPixel>(Raster<TPixel> raster, Stream stream, FormatInfo format, WriterOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        raster.ThrowIfDisposed();
        IFormatWriter writer = format.Writer ?? throw new UnsupportedFormatException($"The {format.Name} format has no writer.");
        if (writer is not IRowFormatWriter rows)
        {
            using PooledBufferStream buffer = new();
            writer.Write(raster, buffer, options);
            await stream.WriteAsync(buffer.Written, cancellationToken).ConfigureAwait(false);
            return;
        }

        using Streaming.EncodeOutput output = new(stream) { Hold = true };
        using (RowEncoder<TPixel> encoder = rows.Start(output, Streaming.RowEncoding.Whole(raster, options)))
        {
            foreach (int _ in encoder.WriteWhole(raster))
            {
                if (output.Held >= 64 * 1024)
                {
                    await output.WriteOutAsync(cancellationToken).ConfigureAwait(false);
                }
            }

            encoder.Complete();
        }

        await output.WriteOutAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed class SaveAsyncVisitor(Stream stream, FormatInfo format, WriterOptions options, CancellationToken cancellationToken) : IRasterVisitor<Task>
    {
        public Task Visit<TPixel>(Raster<TPixel> raster)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            return SaveAsync(raster, stream, format, options, cancellationToken);
        }
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

    /// <summary>
    /// Lets the file extension refine the options where one format has several variants: .pbm, .pgm, .ppm and .pam choose the
    /// Netpbm kind (and .pfm the float map) unless the options already name one.
    /// </summary>
    internal static WriterOptions? OptionsForPath(FormatInfo format, string path, WriterOptions? options)
    {
        Pnm.PnmWriterOptions? pnm = options as Pnm.PnmWriterOptions;
        if (format != Pnm.PnmFormat.Info || pnm?.Kind is not (null or Pnm.PnmKind.Auto) || (options is not null && pnm is null && options.GetType() != typeof(WriterOptions)))
        {
            return options;
        }

        Pnm.PnmKind kind = Path.GetExtension(path).ToUpperInvariant() switch
        {
            ".PBM" => Pnm.PnmKind.Bitmap,
            ".PGM" => Pnm.PnmKind.Graymap,
            ".PPM" => Pnm.PnmKind.Pixmap,
            ".PAM" => Pnm.PnmKind.Pam,
            ".PFM" => Pnm.PnmKind.FloatMap,
            _ => Pnm.PnmKind.Auto,
        };
        return kind == Pnm.PnmKind.Auto
            ? options
            : new Pnm.PnmWriterOptions { Kind = kind, PlainText = pnm?.PlainText ?? false, StripMetadata = options?.StripMetadata ?? false };
    }

    /// <summary>
    /// Loads asynchronously. A stream that cannot seek (a network or pipe) is read on the incremental path, each chunk decoded
    /// before the next is read, so decoding overlaps the wait for bytes and the whole file is never held. A seekable stream (a
    /// file or memory) has its bytes at hand: it is read whole and decoded as 1.0 did, which is faster.
    /// </summary>
    internal static async Task<Raster<TPixel>> LoadAsync<TPixel>(Stream stream, EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (options.ConvertColorTo is Colors.ColorProfile target)
        {
            return ColorLoad.Typed<TPixel>(await LoadAsync(stream, context, options with { ConvertColorTo = null }, cancellationToken).ConfigureAwait(false), target);
        }

        if (stream.CanSeek)
        {
            return await RunBufferedAsync(stream, context, s => Load<TPixel>(s, context, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        using Streaming.IncrementalLoad load = new(stream, context, options, cancellationToken);
        Raster<TPixel> raster = await load.ReadAsync<TPixel>().ConfigureAwait(false);
        return load.Decoder.HeaderRead ? Named(raster, load.Decoder.Info.Format) : raster;
    }

    /// <summary>Loads asynchronously in the natural pixel type. See <see cref="LoadAsync{TPixel}"/>.</summary>
    internal static async Task<Raster> LoadAsync(Stream stream, EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
    {
        if (options.ConvertColorTo is Colors.ColorProfile target)
        {
            return ColorLoad.Natural(await LoadAsync(stream, context, options with { ConvertColorTo = null }, cancellationToken).ConfigureAwait(false), target);
        }

        if (stream.CanSeek)
        {
            return await RunBufferedAsync(stream, context, s => Load(s, context, options, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        using Streaming.IncrementalLoad load = new(stream, context, options, cancellationToken);
        await load.ReadHeaderAsync().ConfigureAwait(false);
        if (load.Decoder.WaitsForEnd && !load.Decoder.HeaderRead)
        {
            return await load.LoadWholeAsync(file => Load(file, context, options, cancellationToken)).ConfigureAwait(false);
        }

        Raster raster = await load.Decoder.WithNaturalType(new Streaming.NaturalLoad(load)).ConfigureAwait(false);
        return Named(raster, load.Decoder.Info.Format);
    }

    /// <summary>
    /// Reads a whole stream into a pooled buffer asynchronously, after the bytes of <paramref name="prefix"/> if given, counting
    /// them against <see cref="SafetyLimits.MaxTotalBytes"/>.
    /// </summary>
    internal static async Task<(byte[] Buffer, int Length)> ReadAllAsync(
        Stream stream, EgamiContext context, CancellationToken cancellationToken, Stream? prefix = null)
    {
        long max = Math.Min(context.Limits.MaxTotalBytes, Array.MaxLength);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        int length = 0;
        try
        {
            if (prefix is not null)
            {
                if (prefix.Length > max)
                {
                    throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), context.Limits.MaxTotalBytes, prefix.Length);
                }

                if (prefix.Length > buffer.Length)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = ArrayPool<byte>.Shared.Rent((int)prefix.Length);
                }

                prefix.ReadExactly(buffer, 0, (int)prefix.Length);
                length = (int)prefix.Length;
            }

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

    internal static T Run<T>(Stream stream, EgamiContext context, Func<FormatInfo, EgamiInput, T> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        EgamiInput input = new(stream, context, cancellationToken);
        T result;
        try
        {
            FormatInfo format = Detect(input, context);
            input.SetFormat(format);
            result = read(format, input);
            input.ReturnUnreadBytes();
        }
        finally
        {
            input.Dispose();
        }

        // Sends the load's Warnings, which can throw when the handler turns them into errors.
        return input.Deliver(result);
    }

    internal static FormatInfo Detect(EgamiInput input, EgamiContext context)
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
                if (format.Reader is null)
                {
                    throw new UnsupportedFormatException($"The data is {format.Name}, but that format has no reader.");
                }

                input.Emit(DiagnosticLevel.Info, "Detect.Format", $"The data is {format.Name}, found by its signature.");
                return format;
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
