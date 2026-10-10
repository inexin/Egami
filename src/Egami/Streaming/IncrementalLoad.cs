using Egami.Formats;
using Egami.Pixels;

namespace Egami.Streaming;

/// <summary>
/// An asynchronous load on the incremental path: the stream is read 256 KB at a time and each chunk is decoded before the next is
/// read, so only the bytes since the decode's last step are held, not the whole file. Formats that cannot decode while bytes
/// arrive (plugins, and files decoded into a buffer first) gather the bytes and are read at the end, as 1.0 did.
/// </summary>
internal sealed class IncrementalLoad : IDisposable
{
    private readonly Stream _stream;
    private readonly CancellationToken _cancellationToken;
    private readonly long _start;

    public IncrementalLoad(Stream stream, EgamiContext context, ReaderOptions options, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _cancellationToken = cancellationToken;
        _start = stream.CanSeek ? stream.Position : -1;
        Decoder = RasterDecoder.OpenIncrementalCore(context, options, cancellationToken);
        Decoder.WholeIfRowsAtEnd = true;
        Decoder.Attach(stream);
    }

    /// <summary>Gets the incremental decoder the stream feeds.</summary>
    public RasterDecoder Decoder { get; }

    /// <summary>Reads the next chunk of the stream into the decoder; at the end of the stream, tells it so and returns false.</summary>
    public ValueTask<bool> MoreAsync()
    {
        return Decoder.MoreAsync(_cancellationToken);
    }

    /// <summary>
    /// Reads until the header has arrived, or until the format turns out to wait for the end: the caller then decodes the file
    /// whole with <see cref="LoadWholeAsync"/>.
    /// </summary>
    public async ValueTask ReadHeaderAsync()
    {
        while (!Decoder.HeaderRead && !Decoder.WaitsForEnd && await MoreAsync().ConfigureAwait(false))
        {
        }
    }

    /// <summary>Decodes every frame into one raster as the stream arrives, as Load returns it.</summary>
    public async Task<Raster<TPixel>> ReadAsync<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        await ReadHeaderAsync().ConfigureAwait(false);
        Raster<TPixel> raster;
        if (Decoder.WaitsForEnd && !Decoder.HeaderRead)
        {
            // Decoded whole at the end, as 1.0 did, straight into the pixel type.
            return await LoadWholeAsync(file => FormatPipeline.Load<TPixel>(file, Decoder.Context, Decoder.Options, _cancellationToken))
                .ConfigureAwait(false);
        }

        if (Decoder.Info.Access == RasterAccess.Buffered)
        {
            while (await MoreAsync().ConfigureAwait(false))
            {
            }

            raster = Decoder.ReadAll<TPixel>();
        }
        else
        {
            RasterDecoder.LoadFrames<TPixel> frames = new();
            try
            {
                while (!Decoder.Pump(frames))
                {
                    if (!await MoreAsync().ConfigureAwait(false) && !Decoder.Pump(frames))
                    {
                        throw new InvalidOperationException("The decode did not finish at the end of the data.");
                    }
                }

                raster = Decoder.Finish(frames);
            }
            catch
            {
                frames.Dispose();
                throw;
            }
        }

        ReturnUnreadBytes();
        return raster;
    }

    /// <summary>
    /// Decodes a file whose format waits for the end as 1.0 did: the bytes received so far and the rest of the stream are read into
    /// one buffer, which <paramref name="read"/> decodes.
    /// </summary>
    public async Task<T> LoadWholeAsync<T>(Func<Stream, T> read)
    {
        byte[] buffer;
        int length;
        using (Stream received = Decoder.OpenReceived())
        {
            (buffer, length) = await FormatPipeline.ReadAllAsync(_stream, Decoder.Context, _cancellationToken, received).ConfigureAwait(false);
        }

        try
        {
            using MemoryStream file = new(buffer, 0, length, writable: false);
            T result = read(file);
            if (_start >= 0)
            {
                _stream.Position = _start + file.Position;
            }

            return result;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Dispose()
    {
        Decoder.Dispose();
    }

    /// <summary>Puts a seekable stream just past the image, as a synchronous load leaves it.</summary>
    private void ReturnUnreadBytes()
    {
        if (_start >= 0)
        {
            _stream.Position = _start + Decoder.ConsumedBytes;
        }
    }
}

/// <summary>Loads in the natural pixel type once the header says what it is.</summary>
internal sealed class NaturalLoad(IncrementalLoad load) : INaturalTypeVisitor<Task<Raster>>
{
    public async Task<Raster> Visit<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return await load.ReadAsync<TPixel>().ConfigureAwait(false);
    }
}
