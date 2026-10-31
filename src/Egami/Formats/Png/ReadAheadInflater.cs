using System.Buffers;
using System.Runtime.ExceptionServices;

namespace Egami.Formats.Png;

/// <summary>
/// Inflates on another thread, a few blocks ahead of the reader, so decompression overlaps reversing the filters and converting
/// the rows: zlib is about half of a large PNG's decode and cannot itself be split. What the inner stream throws is thrown to
/// the reader at the point in the data where it happened, after the bytes before it.
/// </summary>
/// <remarks>
/// The inner stream, and the input under it, belong to the worker until <see cref="Dispose"/> returns, which waits for it. The
/// blocks go round a fixed ring, so a decode allocates the same few objects however large the image.
/// </remarks>
internal sealed class ReadAheadInflater : Stream
{
    private const int BlockBytes = 256 * 1024;
    private const int Blocks = 4;

    private readonly Stream _inner;
    private readonly CancellationToken _cancellation;
    private readonly Block[] _ring = new Block[Blocks];
    private readonly SemaphoreSlim _filled = new(0, int.MaxValue);
    private readonly SemaphoreSlim _free = new(Blocks, int.MaxValue);
    private readonly Task _worker;
    private Block _current;
    private int _position;
    private int _taken;
    private bool _ended;
    private bool _disposed;
    private volatile bool _stop;

    internal ReadAheadInflater(Stream inner, CancellationToken cancellation)
    {
        _inner = inner;
        _cancellation = cancellation;
        _worker = Task.Factory.StartNew(static state => ((ReadAheadInflater)state!).Work(), this, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Gets whether reading ahead pays: more than one core allowed and at least a megabyte of image data.</summary>
    internal static bool Worth(EgamiInput input, long imageBytes)
    {
        return !input.Incremental && input.Context.MaxParallelism > 1 && Environment.ProcessorCount > 1 && imageBytes >= 1 << 20;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        while (_position == _current.Count)
        {
            // A block's failure comes after its bytes: thrown once, and the data ends there.
            ExceptionDispatchInfo? failure = _current.Failure;
            Return(ref _current);
            _position = 0;
            if (failure is not null)
            {
                _ended = true;
                failure.Throw();
            }

            if (_ended)
            {
                return 0;
            }

            _filled.Wait();
            int slot = _taken++ % Blocks;
            _current = _ring[slot];
            _ring[slot] = default;
            _free.Release();
            if (_current.Count == 0 && _current.Failure is null)
            {
                _ended = true;
                return 0;
            }
        }

        int n = Math.Min(buffer.Length, _current.Count - _position);
        _current.Data.AsSpan(_position, n).CopyTo(buffer);
        _position += n;
        return n;
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _stop = true;
            _free.Release();
            _worker.Wait();
            Return(ref _current);
            for (int i = 0; i < Blocks; i++)
            {
                Return(ref _ring[i]);
            }

            _filled.Dispose();
            _free.Dispose();
        }

        base.Dispose(disposing);
    }

    private static void Return(ref Block block)
    {
        if (block.Data is not null)
        {
            ArrayPool<byte>.Shared.Return(block.Data);
        }

        block = default;
    }

    private void Work()
    {
        for (int put = 0; ; put++)
        {
            _free.Wait();
            if (_stop)
            {
                return;
            }

            byte[] data = ArrayPool<byte>.Shared.Rent(BlockBytes);
            int count = 0;
            ExceptionDispatchInfo? failure = null;
            try
            {
                int read;
                while (count < data.Length && (read = _inner.Read(data.AsSpan(count))) > 0)
                {
                    count += read;
                    _cancellation.ThrowIfCancellationRequested();
                }
            }
            catch (Exception e)
            {
                failure = ExceptionDispatchInfo.Capture(e);
            }

            if (_stop)
            {
                // Disposed while this block filled: the reader wants no more.
                ArrayPool<byte>.Shared.Return(data);
                return;
            }

            _ring[put % Blocks] = new Block(data, count, failure);
            _filled.Release();
            if (count == 0 || failure is not null)
            {
                return;
            }
        }
    }

    /// <summary>A block of inflated bytes, and what the inner stream threw after them, if anything.</summary>
    private record struct Block(byte[]? Data, int Count, ExceptionDispatchInfo? Failure);
}
