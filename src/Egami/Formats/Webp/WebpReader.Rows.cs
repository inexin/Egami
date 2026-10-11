using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Webp;

/// <content>
/// Streaming: lossless rows come out top-down as the decoder finishes them, holding the window of coded pixels; an incremental
/// decode goes back to the start of the row it ran dry in, with the bit position and the color cache as they were.
/// </content>
internal sealed partial class WebpReader : IRowSourceReader, IIncrementalFormatReader
{
    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        WebpParser parser = WebpParser.Open(input, options);
        if (parser.ImageChunk == WebpContainer.Vp8)
        {
            return new LossyRows(input, parser, options);
        }

        if (parser.Animated)
        {
            Range frames = options.Frames;
            if (frames.Start.IsFromEnd || (frames.End.IsFromEnd && frames.End.Value > 0))
            {
                // Frames counted from the end need the frame count, which only reading every frame gives.
                try
                {
                    return BufferedRowSource.From(ReadAnimation<Rgba8>(input, options, parser));
                }
                finally
                {
                    input.ReturnUnreadBytes();
                    input.Dispose();
                }
            }

            return new AnimationRows(input, parser, options);
        }

        return new LosslessRows(input, parser, options);
    }

    public HeaderState HeaderStatus(EgamiInput input, ReaderOptions options)
    {
        // The RIFF header, then every chunk before the image data whole, then the image chunk's header and the VP8L header.
        Span<byte> header = stackalloc byte[8];
        long at = 12;
        if (input.Available < at)
        {
            return HeaderState.NotYet;
        }

        while (true)
        {
            if (!input.TryPeekAt(at, header))
            {
                return HeaderState.NotYet;
            }

            uint type = BinaryPrimitives.ReadUInt32LittleEndian(header);
            long size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (type == WebpContainer.Vp8L)
            {
                return input.Available >= at + 8 + 5 ? HeaderState.Arrived : HeaderState.NotYet;
            }

            if (type == WebpContainer.Vp8)
            {
                // The frame header, the first partition (every macroblock's modes) and the token partition sizes.
                Span<byte> tag = stackalloc byte[3];
                if (!input.TryPeekAt(at + 8, tag))
                {
                    return HeaderState.NotYet;
                }

                int needed = (int)Math.Min(size, 10 + ((tag[0] | (tag[1] << 8) | (tag[2] << 16)) >> 5) + 21);
                return input.Available >= at + 8 + needed ? HeaderState.Arrived : HeaderState.NotYet;
            }

            if (type == WebpContainer.Anmf)
            {
                return HeaderState.WaitsForEnd;
            }

            at += 8 + size + (size & 1);
            if (input.Available < at)
            {
                return HeaderState.NotYet;
            }
        }
    }

    /// <summary>
    /// A lossy still, decoded a macroblock row at a time. The payload is held as it arrives (VP8 reads its modes and its tokens from
    /// two places at once); rows are converted to RGB one behind the decoder, since the upsampler needs the next chroma row.
    /// </summary>
    private sealed class LossyRows : RowSource
    {
        private const int LumaRing = 64;
        private const int ChromaRing = 32;

        private readonly EgamiInput _input;
        private readonly WebpParser _parser;
        private readonly bool _strict;
        private readonly byte[] _payload;
        private readonly int _payloadLength;
        private readonly Vp8Decoder _decoder;
        private readonly WebpAlpha? _alpha;
        private readonly WebpYuvToRgb _converter;
        private readonly IMemoryOwner<byte> _ring;
        private readonly Rgba8[] _rgba;
        private readonly int _width;
        private readonly int _chromaWidth;
        private int _filled;
        private int _lumaReady;
        private int _chromaReady;
        private int _alphaReady;
        private int _frame = -1;
        private EgamiDiagnostic? _warning;
        private bool _ended;
        private bool _disposed;

        public LossyRows(EgamiInput input, WebpParser parser, ReaderOptions options)
        {
            _input = input;
            _parser = parser;
            _strict = options.Recovery == RecoveryMode.Strict;
            if (parser.ImageSize > int.MaxValue)
            {
                throw new LimitExceededException("MaxTotalBytes", int.MaxValue, parser.ImageSize);
            }

            _payloadLength = (int)parser.ImageSize;
            _width = parser.Width;
            _chromaWidth = (_width + 1) >> 1;
            input.CheckMemory(_payloadLength);
            _payload = ArrayPool<byte>.Shared.Rent(Math.Max(_payloadLength, 16));
            _payload.AsSpan(0, _payloadLength).Clear();
            _ring = input.RentScratch((_width * (LumaRing + (parser.Alpha is null ? 0 : LumaRing))) + (2 * _chromaWidth * ChromaRing));
            _rgba = ArrayPool<Rgba8>.Shared.Rent(_width);
            try
            {
                Fill();
                if (_filled < Math.Min(_payloadLength, 10))
                {
                    throw new CorruptDataException(WebpFormat.Info.Name, input.Position, "The VP8 frame header is cut short.") { IsTruncation = true };
                }

                byte[] payload = Payload;
                _decoder = Vp8Decoder.Create(payload, 0, _payloadLength);
                if (_decoder.Width != parser.Width || _decoder.Height != parser.Height)
                {
                    throw new CorruptDataException(WebpFormat.Info.Name, parser.ImageStart, "The VP8 frame size differs from the one its header gave.");
                }

                _decoder.Arrived = _filled < _payloadLength && input.Incremental && !input.AtEndOfInput ? _filled : int.MaxValue;
                _converter = new WebpYuvToRgb(_width, parser.Height, options is WebpReaderOptions { FastUpsampling: true });
                if (parser.Alpha is byte[] alpha)
                {
                    _alpha = new WebpAlpha(input, alpha, _width, parser.Height, _strict);
                }
            }
            catch
            {
                _decoder?.Dispose();
                ArrayPool<byte>.Shared.Return(_payload);
                _ring.Dispose();
                ArrayPool<Rgba8>.Shared.Return(_rgba);
                throw;
            }

            Publish();
        }

        public override int StoredWidth => _parser.Width;

        public override int StoredHeight => _parser.Height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => _parser.HasAlpha ? Rgba8.Layout : Rgb8.Layout;

        public override RasterMetadata Metadata { get; } = new() { FormatName = WebpFormat.Info.Name };

        public override bool MetadataComplete => _ended;

        public override int? FrameCount => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => WebpReader.WorkingBytes(_parser);

        private byte[] Payload => _payload;

        private Span<byte> Ring => _ring.Memory.Span;

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor)
        {
            return _parser.HasAlpha ? visitor.Visit<Rgba8>() : visitor.Visit<Rgb8>();
        }

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
            (int above, int current, _) = _converter.ChromaRows(y);
            while (_lumaReady <= y || _chromaReady <= current)
            {
                _input.CancellationToken.ThrowIfCancellationRequested();
                Fill();
                _decoder.Arrived = _filled < _payloadLength && _input.Incremental && !_input.AtEndOfInput ? _filled : int.MaxValue;
                _decoder.DecodeRow(Keep);
            }

            while (_alpha is not null && _alphaReady <= y)
            {
                _alpha.DecodeRow(AlphaRow(_alphaReady));
                _alphaReady++;
            }

            Span<byte> ring = Ring;
            bool alpha = _parser.HasAlpha;
            YuvConvert.Order order = alpha ? YuvConvert.Order.Of<Rgba8>() : YuvConvert.Order.Of<Rgb8>();
            Span<byte> target = System.Runtime.InteropServices.MemoryMarshal.AsBytes(_rgba.AsSpan(0, _width));
            _converter.Convert(y, LumaRow(y), ChromaRow(0, above), ChromaRow(0, current), ChromaRow(1, above), ChromaRow(1, current), _alpha is null ? default : AlphaRow(y), target, order);
            if (alpha)
            {
                PixelConvert.Convert<Rgba8, TPixel>(_rgba.AsSpan(x, destination.Length), destination);
            }
            else
            {
                PixelConvert.Convert<Rgb8, TPixel>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Rgb8>(target[..(_width * 3)]).Slice(x, destination.Length), destination);
            }
        }

        public override void EndFrame()
        {
            if (_ended)
            {
                return;
            }

            // The rest of the payload is skipped, then the chunks after it are read.
            while (_filled < _payloadLength)
            {
                int n = _input.Peek((int)Math.Min(_payloadLength - _filled, 64 * 1024)).Length;
                if (n == 0)
                {
                    if (_input.Incremental && !_input.AtEndOfInput)
                    {
                        throw new NeedsInputException();
                    }

                    break;
                }

                _input.Skip(n);
                _filled += n;
            }

            _parser.ReadToEnd(Math.Min(_filled, _payloadLength));
            if (_decoder.PastEnd && _filled >= _payloadLength && _warning is null)
            {
                Problem("Webp.LossyDataEnded", "The lossy image data ends early; the rest of the image is decoded from zeros.");
            }

            _warning ??= _alpha?.Warning;
            _ended = true;
            Publish();
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _decoder.Dispose();
                _alpha?.Dispose();
                _converter.Dispose();
                ArrayPool<byte>.Shared.Return(_payload);
                _ring.Dispose();
                ArrayPool<Rgba8>.Shared.Return(_rgba);
                _input.ReturnUnreadBytes();
                _input.Dispose();
            }
        }

        private Span<byte> LumaRow(int y) => Ring.Slice((y % LumaRing) * _width, _width);

        private Span<byte> ChromaRow(int plane, int y) => Ring.Slice((_width * LumaRing) + (((plane * ChromaRing) + (y % ChromaRing)) * _chromaWidth), _chromaWidth);

        private Span<byte> AlphaRow(int y) => Ring.Slice((_width * LumaRing) + (2 * _chromaWidth * ChromaRing) + ((y % LumaRing) * _width), _width);

        /// <summary>Copies the rows the decoder made final into the rings.</summary>
        private void Keep(Vp8Decoder d, int from, int to, int chromaFrom, int chromaTo)
        {
            for (int y = from; y < to; y++)
            {
                d.RowY(y).CopyTo(LumaRow(y));
            }

            for (int y = chromaFrom; y < chromaTo; y++)
            {
                d.RowU(y).CopyTo(ChromaRow(0, y));
                d.RowV(y).CopyTo(ChromaRow(1, y));
            }

            _lumaReady = to;
            _chromaReady = chromaTo;
        }

        /// <summary>Takes the payload bytes that have arrived; a file that ends early leaves zeros, noted at the end.</summary>
        private void Fill()
        {
            while (_filled < _payloadLength)
            {
                int n = _input.Read(Payload.AsSpan(_filled, _payloadLength - _filled));
                if (n == 0)
                {
                    if (!_input.Incremental || _input.AtEndOfInput)
                    {
                        if (_warning is null)
                        {
                            Problem("Webp.LossyDataEnded", "The lossy image data ends early; the rest of the image is decoded from zeros.");
                        }

                        _filled = _payloadLength;
                    }

                    return;
                }

                _filled += n;
            }
        }

        private void Problem(string id, string problem)
        {
            if (_strict)
            {
                throw new CorruptDataException(WebpFormat.Info.Name, _input.Position, problem) { IsTruncation = true };
            }

            _warning = EgamiDiagnostic.Create(DiagnosticLevel.Warning, id, problem, _input.Position);
        }

        private void Publish()
        {
            RasterMetadata metadata = new();
            CopyMetadata(_parser.Metadata, metadata);
            if (_warning is EgamiDiagnostic warning)
            {
                metadata.WarningList.Add(warning);
            }

            Metadata.ReplaceWith(metadata);
        }
    }

    /// <summary>A lossless still, decoded a row at a time.</summary>
    private sealed class LosslessRows : RowSource
    {
        private readonly EgamiInput _input;
        private readonly WebpParser _parser;
        private readonly Vp8lBitReader _reader;
        private readonly Vp8lDecoder _decoder;
        private readonly uint[] _row;
        private int _frame = -1;
        private bool _ended;
        private bool _disposed;

        public LosslessRows(EgamiInput input, WebpParser parser, ReaderOptions options)
        {
            _input = input;
            _parser = parser;
            _reader = new Vp8lBitReader(input, parser.ImageSize);
            try
            {
                _decoder = new Vp8lDecoder(input, _reader, options.Recovery == RecoveryMode.Strict);
            }
            catch
            {
                _reader.Dispose();
                throw;
            }

            _row = ArrayPool<uint>.Shared.Rent(parser.Width);
            Publish();
        }

        public override int StoredWidth => _parser.Width;

        public override int StoredHeight => _parser.Height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => _parser.HasAlpha ? Rgba8.Layout : Rgb8.Layout;

        public override RasterMetadata Metadata { get; } = new() { FormatName = WebpFormat.Info.Name };

        public override bool MetadataComplete => _ended;

        public override int? FrameCount => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => WebpReader.WorkingBytes(_parser);

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor)
        {
            return _parser.HasAlpha ? visitor.Visit<Rgba8>() : visitor.Visit<Rgb8>();
        }

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
            Span<uint> row = _row.AsSpan(0, StoredWidth);
            while (_decoder.NextRow <= y)
            {
                _input.CancellationToken.ThrowIfCancellationRequested();
                _decoder.DecodeRow(row);
            }

            PixelConvert.Convert<Bgra8, TPixel>(MemoryMarshal.Cast<uint, Bgra8>(row).Slice(x, destination.Length), destination);
        }

        public override void EndFrame()
        {
            if (_ended)
            {
                return;
            }

            // Rows below the last one asked for are not decoded: their data is skipped, then the chunks after it are read.
            _reader.SkipRest();
            _parser.ReadToEnd(_parser.ImageSize);
            _ended = true;
            Publish();
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _decoder.Dispose();
                _reader.Dispose();
                ArrayPool<uint>.Shared.Return(_row);
                _input.ReturnUnreadBytes();
                _input.Dispose();
            }
        }

        private void Publish()
        {
            RasterMetadata metadata = new();
            CopyMetadata(_parser.Metadata, metadata);
            if (_decoder.Warning is EgamiDiagnostic warning)
            {
                metadata.WarningList.Add(warning);
            }

            Metadata.ReplaceWith(metadata);
        }
    }

    /// <summary>
    /// An animation read a frame at a time: each frame is composed on the canvas and its rows read from there, so the memory held
    /// is the canvas and one decoded frame, whatever the frame count.
    /// </summary>
    private sealed class AnimationRows : RowSource
    {
        private readonly EgamiInput _input;
        private readonly WebpParser _parser;
        private readonly WebpAnimation _animation;
        private readonly int _first;
        private readonly int _end;
        private bool _disposed;

        public AnimationRows(EgamiInput input, WebpParser parser, ReaderOptions options)
        {
            _input = input;
            _parser = parser;
            _animation = new WebpAnimation(input, options, parser);
            _first = options.Frames.Start.Value;
            _end = options.Frames.End.IsFromEnd ? int.MaxValue : options.Frames.End.Value;
            CopyMetadata(parser.Metadata, Metadata);
        }

        public override int StoredWidth => _parser.CanvasWidth;

        public override int StoredHeight => _parser.CanvasHeight;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => _parser.HasAlpha ? Rgba8.Layout : Rgb8.Layout;

        public override RasterMetadata Metadata { get; } = new() { FormatName = WebpFormat.Info.Name };

        public override bool MetadataComplete => _animation.Finished;

        public override int? FrameCount => null;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => WebpAnimation.WorkingBytes(_parser.CanvasWidth, _parser.CanvasHeight);

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor)
        {
            return _parser.HasAlpha ? visitor.Visit<Rgba8>() : visitor.Visit<Rgb8>();
        }

        public override FrameMetadata? NextFrame()
        {
            while (true)
            {
                if (_animation.FramesSeen >= _end)
                {
                    _animation.SkipRest();
                    Publish();
                    return null;
                }

                if (_animation.Next() is not FrameMetadata frame)
                {
                    if (_animation.FramesSeen == 0)
                    {
                        throw _input.Corrupt(-1, "The animation has no frame.");
                    }

                    Publish();
                    return null;
                }

                if (_animation.FramesSeen - 1 >= _first)
                {
                    return frame;
                }
            }
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            PixelConvert.Convert<Rgba8, TPixel>(_animation.Canvas.Row(y).Slice(x, destination.Length), destination);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _animation.Dispose();
                _input.ReturnUnreadBytes();
                _input.Dispose();
            }
        }

        private void Publish()
        {
            RasterMetadata metadata = new();
            CopyMetadata(_parser.Metadata, metadata);
            Metadata.ReplaceWith(metadata);
        }
    }
}
