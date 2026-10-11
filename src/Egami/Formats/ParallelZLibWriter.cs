using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Egami.Formats;

/// <summary>
/// Writes one zlib stream (RFC 1950) whose data is compressed in bands on several threads. Each band is raw Deflate (RFC 1951)
/// ended by a sync flush, which closes the band's last block and pads it to a byte boundary with an empty stored block, so the
/// bands join into one Deflate stream; an empty final block ends it, and the Adler-32 of the whole is combined from each band's.
/// Matches cannot reach back into the band before, which costs a little size. The caller cuts the bands at fixed sizes, so the
/// bytes depend only on the data and the settings, never on how many threads ran.
/// </summary>
internal sealed class ParallelZLibWriter : IDisposable
{
    /// <summary>The uncompressed size a band aims for: 1 MiB (Codec Depth Design, question 10).</summary>
    internal const int BandSize = 1 << 20;

    private readonly Stream _output;
    private readonly ZLibCompressionOptions _options;
    private readonly int _inFlight;
    private readonly Queue<Task<Band>> _pending = new();
    private uint _adler = 1;
    private bool _finished;

    /// <summary>Starts the stream, writing the zlib header that the runtime's own ZLibStream writes for these options.</summary>
    /// <param name="output">Where the stream goes; left open.</param>
    /// <param name="options">The level and strategy.</param>
    /// <param name="maxParallelism">How many bands may compress at once; 1 compresses each on the calling thread as it comes.</param>
    internal ParallelZLibWriter(Stream output, ZLibCompressionOptions options, int maxParallelism)
    {
        _output = output;
        _options = options;
        _inFlight = maxParallelism <= 1 ? 0 : maxParallelism;
        output.Write(Header(options));
    }

    /// <summary>
    /// Adds the next band. <paramref name="fill"/> writes the band's uncompressed bytes into the array it is given, at least
    /// <paramref name="capacity"/> long, and returns how many; it runs on another thread when threads are allowed, so it may only
    /// use data the caller leaves alone, and it owns (and returns) any buffers it captures.
    /// </summary>
    public void Add(int capacity, Func<byte[], int> fill)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (_inFlight == 0)
        {
            Write(Compress(capacity, fill, _options));
            return;
        }

        while (_pending.Count >= _inFlight)
        {
            Write(_pending.Dequeue().GetAwaiter().GetResult());
        }

        ZLibCompressionOptions options = _options;
        _pending.Enqueue(Task.Run(() => Compress(capacity, fill, options)));
    }

    /// <summary>Writes the bands still compressing, the final block and the Adler-32 trailer.</summary>
    public void Finish()
    {
        while (_pending.Count > 0)
        {
            Write(_pending.Dequeue().GetAwaiter().GetResult());
        }

        _finished = true;

        // An empty final block with fixed Huffman codes: BFINAL 1, BTYPE 01, then the 7-bit end-of-block code 0, as zlib ends a
        // stream after a sync flush.
        Span<byte> end = [0x03, 0x00, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt32BigEndian(end[2..], _adler);
        _output.Write(end);
    }

    /// <summary>Waits for any band still compressing, so no worker outlives the buffers it uses, and drops the results.</summary>
    public void Dispose()
    {
        _finished = true;
        while (_pending.Count > 0)
        {
            Task<Band> task = _pending.Dequeue();
            try
            {
                task.GetAwaiter().GetResult().Dispose();
            }
            catch (Exception)
            {
                // The encode already failed; the first exception is the one that matters.
            }
        }
    }

    /// <summary>Returns the two header bytes ZLibStream writes, so a parallel stream starts as a single-thread one does.</summary>
    internal static byte[] Header(ZLibCompressionOptions options)
    {
        // The header does not depend on the data, but a stream with nothing written writes nothing.
        using MemoryStream one = new();
        using (ZLibStream zlib = new(one, options, leaveOpen: true))
        {
            zlib.Write([0]);
        }

        return one.ToArray()[..2];
    }

    private static Band Compress(int capacity, Func<byte[], int> fill, ZLibCompressionOptions options)
    {
        byte[] data = ArrayPool<byte>.Shared.Rent(capacity);
        try
        {
            int length = fill(data);
            PooledBufferStream compressed = new();
            if (length == 0)
            {
                return new Band(compressed, 0, 1, 0);
            }

            int written;
            using (DeflateStream deflate = new(compressed, options, leaveOpen: true))
            {
                deflate.Write(data, 0, length);

                // DeflateStream.Flush is a zlib sync flush: it ends on a byte boundary without setting BFINAL. Disposing then
                // adds a final block, which is not part of the band.
                deflate.Flush();
                written = (int)compressed.Length;
            }

            return new Band(compressed, written, Adler32.Update(1, data.AsSpan(0, length)), length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(data);
        }
    }

    private void Write(Band band)
    {
        using (band)
        {
            _output.Write(band.Data.Written.Span[..band.Written]);
            _adler = Adler32.Combine(_adler, band.Adler, band.Length);
        }
    }

    /// <summary>One compressed band: its Deflate bytes, and the Adler-32 and length of what went in.</summary>
    private sealed record Band(PooledBufferStream Data, int Written, uint Adler, int Length) : IDisposable
    {
        public void Dispose() => Data.Dispose();
    }
}
