using System.Buffers;
using System.IO.Compression;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Png;

/// <content>
/// Streaming: rows of a non-interlaced PNG as they inflate, an Adam7 PNG from its passes kept at the file's bit depth, and APNG
/// frames composed one at a time on the reused canvas. The pixels are the ones Load gives.
/// </content>
internal sealed partial class PngReader
{
    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        if (!Streams(options))
        {
            // Frames counted from the end need the frame count, which only decoding every frame gives.
            return BufferedRowSource.Read(PngFormat.Info, input, options);
        }

        State state = ReadUpToImageData(input, options);
        if (state.Animated)
        {
            return state.Header.BitDepth == 16 ? new AnimationRows<Rgba16>(state) : new AnimationRows<Rgba8>(state);
        }

        return state.Unpacker.Natural switch
        {
            PngNaturalType.Gray8 => new ImageRows<Gray8>(state, static (u, s, d) => u.Unpack(s, d)),
            PngNaturalType.GrayAlpha8 => new ImageRows<GrayAlpha8>(state, static (u, s, d) => u.Unpack(s, d)),
            PngNaturalType.Gray16 => new ImageRows<Gray16>(state, static (_, s, d) => PngUnpacker.Unpack(s, d)),
            PngNaturalType.Rgb8 => new ImageRows<Rgb8>(state, static (u, s, d) => u.Unpack(s, d)),
            PngNaturalType.Rgba8 => new ImageRows<Rgba8>(state, static (u, s, d) => u.Unpack(s, d)),
            _ => new ImageRows<Rgba16>(state, static (u, s, d) => u.Unpack(s, d)),
        };
    }

    /// <summary>Returns whether <see cref="Open"/> streams with these options, rather than reading every frame first.</summary>
    private static bool Streams(ReaderOptions options)
    {
        Range frames = options.Frames;
        return !frames.Start.IsFromEnd && !(frames.End.IsFromEnd && frames.End.Value > 0);
    }

    /// <summary>Returns the bytes a row source holds for this file and options, all of it from the load's memory budget.</summary>
    private static long WorkingBytes(State state)
    {
        PngHeader header = state.Header;
        if (state.Animated)
        {
            // The canvas, the restore copy and one frame's pixels, each at most the canvas size.
            return 3L * header.Width * header.Height * (header.BitDepth == 16 ? 8 : 4);
        }

        long lines = 2 * (header.RowBytes(header.Width) + 1);
        long natural = (long)header.Width * PngUnpacker.Layout(state.Unpacker.Natural).BytesPerPixel;
        if (!header.Interlaced)
        {
            return lines + (state.Unpacker.ScanlineIsNatural ? 0 : natural);
        }

        Rect region = RegionOf(state);
        long passes = 0;
        for (int pass = 0; pass < 7; pass++)
        {
            passes += StoredRows(header, region, pass).Count * header.RowBytes(Adam7.Columns(pass, header.Width));
        }

        return lines + natural + passes;
    }

    /// <summary>Returns the rows the region needs, in stored pixels.</summary>
    private static Rect RegionOf(State state)
    {
        return ReadPlan.Create(state.Header.Width, state.Header.Height, state.Options.Region, null, 1).Region;
    }

    /// <summary>Returns the rows of an Adam7 pass that land inside the region: the first one's index and how many.</summary>
    private static (int First, int Count) StoredRows(PngHeader header, Rect region, int pass)
    {
        int columns = Adam7.Columns(pass, header.Width);
        int rows = Adam7.Rows(pass, header.Height);
        if (columns == 0 || rows == 0)
        {
            return (0, 0);
        }

        int start = Adam7.YStart[pass];
        int step = Adam7.YStep[pass];
        int first = Math.Max(0, (region.Y - start + step - 1) / step);
        int end = Math.Min(rows, Math.Max(0, (region.Bottom - start + step - 1) / step));
        return (first, Math.Max(0, end - first));
    }

    /// <summary>Reads one filter byte and scanline, checksums them and reverses the filter, as the whole-image decoder does.</summary>
    /// <returns><see langword="false"/> if the data ended first in lenient mode.</returns>
    private static bool ReadScanline(State state, InflatedReader inflated, Span<byte> line, ReadOnlySpan<byte> previous, int index, ref uint adler)
    {
        state.Input.CancellationToken.ThrowIfCancellationRequested();
        Span<byte> filter = line[..1];
        Span<byte> row = line[1..];
        int read = inflated.Read(filter);
        read += read == 1 ? inflated.Read(row) : 0;
        if (read < 1 + row.Length)
        {
            string? problem = inflated.Problem;
            state.Chunks.Recoverable(state.Input.Position, (problem ?? "The image data ends early.") + $" Decoded {index} of the scanlines; the rest are transparent black.");
            return false;
        }

        adler = Adler32.Update(Adler32.Update(adler, filter), row);
        if (filter[0] > PngFilter.Paeth)
        {
            throw state.Input.Corrupt(state.Input.Position, $"Scanline {index} has filter type {filter[0]}; only 0 to 4 exist.");
        }

        PngFilterSimd.Unfilter(filter[0], row, previous, state.Header.FilterStride);
        return true;
    }

    /// <summary>What every PNG row source shares: the state, the metadata so far, and ownership of the input.</summary>
    private abstract class PngRows(State state) : RowSource
    {
        private bool _complete;
        private bool _disposed;

        public override int StoredWidth => State.Header.Width;

        public override int StoredHeight => State.Header.Height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => PngReader.WorkingBytes(State);

        public override RasterMetadata Metadata { get; } = new() { FormatName = PngFormat.Info.Name };

        public override bool MetadataComplete => _complete;

        protected State State { get; } = state;

        /// <summary>Publishes what has been read so far, the same way Load builds its metadata.</summary>
        protected void Publish(bool complete, int? loopCount = null)
        {
            RasterMetadata metadata = new() { FormatName = PngFormat.Info.Name };
            if (loopCount is int plays)
            {
                metadata.LoopCount = plays;
            }

            CopyMetadata(State, metadata);
            Metadata.ReplaceWith(metadata);
            _complete = complete;
        }

        protected sealed override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                Release();
                State.Input.ReturnUnreadBytes();
                State.Input.Dispose();
            }
        }

        /// <summary>Returns the source's own buffers.</summary>
        protected abstract void Release();
    }

    /// <summary>A static PNG: rows in order as they inflate, or for Adam7, every pass first with the region's rows kept as stored.</summary>
    private sealed class ImageRows<TNatural> : PngRows
        where TNatural : unmanaged, IPixelType<TNatural>
    {
        private readonly UnpackRow<TNatural> _unpack;
        private readonly IdatStream _idat;
        private readonly int _rowBytes;
        private readonly Rect _region;
        private readonly int[] _firstStored = new int[7];
        private readonly int[] _decoded = new int[7];
        private readonly RowStore?[] _passes = new RowStore?[7];
        private DeflateStream? _deflate;
        private InflatedReader? _inflated;
        private IMemoryOwner<byte>? _lines;
        private IMemoryOwner<byte>? _natural;
        private uint _adler = 1;
        private int _frame = -1;
        private int _next;
        private int _current;
        private bool _started;
        private bool _failed;
        private bool _ended;

        public ImageRows(State state, UnpackRow<TNatural> unpack)
            : base(state)
        {
            _unpack = unpack;
            state.SeenImageData = true;
            state.Position = PngChunkPosition.AfterImageData;
            _idat = new IdatStream(state.Chunks, state.FirstIdatLength);
            _rowBytes = (int)state.Header.RowBytes(state.Header.Width);
            _region = RegionOf(state);
            Publish(complete: false);
        }

        public override PixelLayoutInfo Layout => TNatural.Layout;

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => visitor.Visit<TNatural>();

        public override int? FrameCount => 1;

        private int LinesBytes => 2 * (_rowBytes + 1);

        private int NaturalBytes => State.Header.Interlaced || !State.Unpacker.ScanlineIsNatural ? State.Header.Width * Marshal.SizeOf<TNatural>() : 0;

        public override FrameMetadata? NextFrame()
        {
            if (_frame < 0)
            {
                _frame = 0;
                return new FrameMetadata();
            }

            EndFrame();
            _frame = 1;
            return null;
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            Start();
            if (State.Header.Interlaced)
            {
                InterlacedRow(y, x, destination);
                return;
            }

            while (_next <= y && !_failed)
            {
                Advance();
            }

            if (y >= _next)
            {
                // The data ended before this row: transparent black, as Load leaves it.
                destination.Clear();
                return;
            }

            ReadOnlySpan<byte> scanline = Line(_current)[1..];
            if (x == 0 && destination.Length == State.Header.Width && State.Unpacker.TryUnpackPalette(scanline, destination))
            {
                // A whole palette row goes straight to the target type, as Load decodes it.
                return;
            }

            PixelConvert.Convert<TNatural, TPixel>(Unpack(scanline, State.Header.Width).Slice(x, destination.Length), destination);
        }

        public override void EndFrame()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            Start();
            int rows = State.Header.Height;
            bool allRows = State.Header.Interlaced ? !_failed : _next == rows;
            bool checkAdler = allRows && _inflated is not null && IsEndOfImageData(State, _inflated);
            _inflated?.Dispose();
            _inflated = null;
            _deflate?.Dispose();
            _deflate = null;
            try
            {
                if (allRows || _failed)
                {
                    _idat.Drain();
                }
                else
                {
                    // The rows below the last one asked for are not needed: skip their data without inflating it.
                    _idat.SkipRest();
                }
            }
            catch (CorruptDataException e) when (e.IsTruncation)
            {
                // Reported below.
            }

            EgamiInput input = State.Input;
            if (_idat.Truncated)
            {
                State.Chunks.Recoverable(input.Position, "The file ends inside the image data.");
            }
            else if (checkAdler && _idat.Tail != _adler)
            {
                State.Chunks.Recoverable(input.Position, $"The image data's Adler-32 checksum is 0x{_idat.Tail:X8} but the data gives 0x{_adler:X8}.");
            }

            State.Unpacker.ThrowIfBadIndices(State.Chunks, input.Position);
            State.NextChunk = _idat.Ended && !_idat.Truncated && _idat.NextType != 0 ? (_idat.NextLength, _idat.NextType) : null;
            try
            {
                ReadTrailingChunks(State);
            }
            catch (CorruptDataException e) when (State.Options.Recovery == RecoveryMode.Lenient && e.IsTruncation)
            {
                State.Metadata.WarningList.Add("The file ends inside a chunk after the image data. That chunk was ignored.");
            }

            Publish(complete: true);
            Release();
        }

        protected override void Release()
        {
            _inflated?.Dispose();
            _inflated = null;
            _deflate?.Dispose();
            _deflate = null;
            _lines?.Dispose();
            _lines = null;
            _natural?.Dispose();
            _natural = null;
            for (int pass = 0; pass < 7; pass++)
            {
                _passes[pass]?.Dispose();
                _passes[pass] = null;
            }
        }

        private void Start()
        {
            if (_started)
            {
                return;
            }

            _started = true;
            EgamiInput input = State.Input;
            _lines = input.RentScratch(LinesBytes, ClearMode.Zero);
            if (NaturalBytes > 0)
            {
                _natural = input.RentScratch(NaturalBytes);
            }

            if (!ReadZlibHeader(State, _idat))
            {
                _failed = true;
                return;
            }

            _deflate = new DeflateStream(_idat, CompressionMode.Decompress, leaveOpen: true);
            _inflated = new InflatedReader(_deflate, _rowBytes + 1);
            if (State.Header.Interlaced)
            {
                DecodePasses();
            }
        }

        private Span<byte> Line(int index) => _lines!.Memory.Span.Slice(index * (_rowBytes + 1), _rowBytes + 1);

        /// <summary>Decodes the next scanline into the line the previous one is not in.</summary>
        private void Advance()
        {
            int target = 1 - _current;
            if (!ReadScanline(State, _inflated!, Line(target), Line(_current)[1..], _next, ref _adler))
            {
                _failed = true;
                return;
            }

            _current = target;
            _next++;
        }

        /// <summary>Returns a scanline as natural pixels: the scanline itself when its bytes already are, else unpacked.</summary>
        private ReadOnlySpan<TNatural> Unpack(ReadOnlySpan<byte> scanline, int columns)
        {
            if (State.Unpacker.ScanlineIsNatural)
            {
                return MemoryMarshal.Cast<byte, TNatural>(scanline)[..columns];
            }

            Span<TNatural> natural = MemoryMarshal.Cast<byte, TNatural>(_natural!.Memory.Span)[..columns];
            if (!State.Unpacker.TryUnpackPalette(scanline, natural))
            {
                _unpack(State.Unpacker, scanline, natural);
            }

            return natural;
        }

        /// <summary>Decodes all seven passes, keeping the region's rows of each at the file's bit depth.</summary>
        private void DecodePasses()
        {
            int width = State.Header.Width;
            int line = 0;
            for (int pass = 0; pass < 7; pass++)
            {
                int columns = Adam7.Columns(pass, width);
                int rows = Adam7.Rows(pass, State.Header.Height);
                if (columns == 0 || rows == 0)
                {
                    continue;
                }

                int rowBytes = (int)State.Header.RowBytes(columns);
                (int first, int count) = StoredRows(State.Header, _region, pass);
                _firstStored[pass] = first;
                RowStore? store = count > 0 ? new RowStore(State.Input, rowBytes, count) : null;
                _passes[pass] = store;
                Line(_current).Clear();
                for (int r = 0; r < rows; r++)
                {
                    int target = 1 - _current;
                    Span<byte> current = Line(target)[..(rowBytes + 1)];
                    if (!ReadScanline(State, _inflated!, current, Line(_current).Slice(1, rowBytes), line++, ref _adler))
                    {
                        _failed = true;
                        return;
                    }

                    _current = target;
                    _decoded[pass] = r + 1;
                    if (store is not null && r >= first && r < first + count)
                    {
                        current[1..].CopyTo(store.Row(r - first));
                    }
                }
            }
        }

        /// <summary>Builds row <paramref name="y"/> from the passes that cover it, leaving pixels no decoded pass reached transparent black.</summary>
        private void InterlacedRow<TPixel>(int y, int x, Span<TPixel> destination)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            destination.Clear();
            int width = State.Header.Width;
            TPixel[] pixels = ArrayPool<TPixel>.Shared.Rent(width);
            try
            {
                for (int pass = 0; pass < 7; pass++)
                {
                    int dy = y - Adam7.YStart[pass];
                    int columns = Adam7.Columns(pass, width);
                    if (columns == 0 || dy < 0 || dy % Adam7.YStep[pass] != 0)
                    {
                        continue;
                    }

                    int r = dy / Adam7.YStep[pass];
                    if (r >= _decoded[pass] || _passes[pass] is not RowStore store)
                    {
                        continue;
                    }

                    ReadOnlySpan<byte> scanline = store.Row(r - _firstStored[pass]);
                    Span<TPixel> converted = pixels.AsSpan(0, columns);
                    PixelConvert.Convert(Unpack(scanline, columns), converted);
                    int xStart = Adam7.XStart[pass];
                    int xStep = Adam7.XStep[pass];
                    int first = Math.Max(0, (x - xStart + xStep - 1) / xStep);
                    for (int i = first; i < columns; i++)
                    {
                        int column = xStart + (i * xStep) - x;
                        if (column >= destination.Length)
                        {
                            break;
                        }

                        destination[column] = converted[i];
                    }
                }
            }
            finally
            {
                ArrayPool<TPixel>.Shared.Return(pixels);
            }
        }
    }

    /// <summary>An APNG: each frame composed on the canvas when asked for, its rows read from the canvas.</summary>
    private sealed class AnimationRows<TCanvas> : PngRows
        where TCanvas : unmanaged, IPixelType<TCanvas>
    {
        private readonly AnimationCanvas<TCanvas> _canvas;
        private readonly int _first;
        private readonly int _end;
        private (uint Length, uint Type)? _next;
        private int _index;
        private int _served;
        private bool _begun;
        private bool _stopped;
        private bool _drawn;
        private bool _finished;

        public AnimationRows(State state)
            : base(state)
        {
            Range range = state.Options.Frames;
            _first = range.Start.Value;
            _end = range.End.IsFromEnd ? int.MaxValue : range.End.Value;
            _canvas = new AnimationCanvas<TCanvas>(state.Header.Width, state.Header.Height, state.Input.Memory);
            Publish(complete: false, state.Plays);
        }

        public override PixelLayoutInfo Layout => TCanvas.Layout;

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => visitor.Visit<TCanvas>();

        public override int? FrameCount => Math.Max(0, Math.Min(State.AnimationFrames, _end) - _first);

        public override FrameMetadata? NextFrame()
        {
            if (_drawn)
            {
                _canvas.End();
                _drawn = false;
            }

            while (!_finished)
            {
                if (ComposeNext() is not FrameControl control)
                {
                    Finish();
                    return null;
                }

                int index = _index - 1;
                if (index >= _first && index < _end)
                {
                    _drawn = true;
                    _served++;
                    return FrameMetadataOf(control);
                }

                _canvas.End();
            }

            return null;
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            PixelConvert.Convert<TCanvas, TPixel>(_canvas.Row(y).Slice(x, destination.Length), destination);
        }

        protected override void Release()
        {
            _canvas.Dispose();
        }

        /// <summary>Composes the next frame on the canvas, as the whole-animation reader's loop does, or returns <see langword="null"/> at the end.</summary>
        private FrameControl? ComposeNext()
        {
            State state = State;
            EgamiInput input = state.Input;
            PngChunkReader chunks = state.Chunks;
            try
            {
                if (!_begun)
                {
                    _begun = true;
                    state.SeenImageData = true;
                    state.Position = PngChunkPosition.AfterImageData;
                    IdatStream idat = new(chunks, state.FirstIdatLength);
                    if (state.FirstControl is FrameControl first)
                    {
                        if (first.Bounds != new Rect(0, 0, state.Header.Width, state.Header.Height))
                        {
                            throw input.Corrupt(chunks.ChunkOffset, "The first frame is the default image but its fcTL does not cover the whole image.");
                        }

                        bool? complete = ComposeFrame(state, _canvas, first, idat, _index++);
                        _stopped = complete != true;
                        _next = NextOf(idat);
                        if (complete is not null)
                        {
                            return first;
                        }
                    }
                    else
                    {
                        idat.Drain();
                        _next = NextOf(idat);
                    }
                }

                while (!_stopped && _index < _end)
                {
                    if (_next is null)
                    {
                        chunks.Recoverable(input.Position, "The file ends before IEND.");
                        break;
                    }

                    (uint length, uint type) = _next.Value;
                    if (type == PngChunkType.Iend)
                    {
                        ReadChunk(state, type, length);
                        break;
                    }

                    if (type == PngChunkType.Fctl)
                    {
                        FrameControl? frame = ReadFrameControl(state, length);
                        if (frame is null || !chunks.TryReadHeader(out length, out type))
                        {
                            break;
                        }

                        if (type != PngChunkType.Fdat)
                        {
                            chunks.Recoverable(chunks.ChunkOffset, "An fcTL chunk is not followed by fdAT; the animation ends there.");
                            break;
                        }

                        if (_index >= state.AnimationFrames)
                        {
                            chunks.Recoverable(chunks.ChunkOffset, $"There are more frames than the {state.AnimationFrames} acTL gives; the rest were ignored.");
                            break;
                        }

                        IdatStream fdat = new(chunks, length, PngChunkType.Fdat, state.CheckSequence);
                        bool? complete = ComposeFrame(state, _canvas, frame.Value, fdat, _index++);
                        _stopped = complete != true;
                        _next = NextOf(fdat);
                        if (complete is not null)
                        {
                            return frame.Value;
                        }

                        continue;
                    }

                    if (type is PngChunkType.Idat or PngChunkType.Fdat)
                    {
                        chunks.Recoverable(chunks.ChunkOffset, $"A {PngChunkType.Name(type)} chunk is out of place. It was ignored.");
                        chunks.Skip(type, length);
                    }
                    else
                    {
                        ReadChunk(state, type, length);
                    }

                    _next = chunks.TryReadHeader(out length, out type) ? (length, type) : null;
                }

                if (!_stopped && _index < state.AnimationFrames && _index < _end)
                {
                    chunks.Recoverable(input.Position, $"acTL gives {state.AnimationFrames} frames but the file has {_index}.");
                }
            }
            catch (CorruptDataException e) when (state.Options.Recovery == RecoveryMode.Lenient && e.IsTruncation && _served > 0)
            {
                state.Metadata.WarningList.Add("The file ends inside the animation; the frames before that are kept.");
            }

            return null;
        }

        private void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            if (_served == 0)
            {
                throw State.Input.Corrupt(State.Input.Position, "No frame of the animation is in the requested range or could be read.");
            }

            Publish(complete: true, State.Plays);
        }

        private static (uint Length, uint Type)? NextOf(IdatStream data)
        {
            return data.Ended && !data.Truncated && data.NextType != 0 ? (data.NextLength, data.NextType) : null;
        }
    }

    /// <summary>Rows of bytes in blocks of up to 16 MB from the load's memory budget, so a large image needs no single huge buffer.</summary>
    private sealed class RowStore : IDisposable
    {
        private const int BlockBytes = 16 * 1024 * 1024;
        private readonly int _rowBytes;
        private readonly int _rowsPerBlock;
        private readonly IMemoryOwner<byte>[] _blocks;

        public RowStore(EgamiInput input, int rowBytes, int rows)
        {
            _rowBytes = Math.Max(1, rowBytes);
            _rowsPerBlock = Math.Max(1, BlockBytes / _rowBytes);
            _blocks = new IMemoryOwner<byte>[(rows + _rowsPerBlock - 1) / _rowsPerBlock];
            try
            {
                for (int i = 0; i < _blocks.Length; i++)
                {
                    int count = Math.Min(_rowsPerBlock, rows - (i * _rowsPerBlock));
                    _blocks[i] = input.RentScratch(count * rowBytes, ClearMode.Zero);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public Span<byte> Row(int index)
        {
            return _blocks[index / _rowsPerBlock].Memory.Span.Slice(index % _rowsPerBlock * _rowBytes, _rowBytes);
        }

        public void Dispose()
        {
            foreach (IMemoryOwner<byte>? block in _blocks)
            {
                block?.Dispose();
            }
        }
    }
}
