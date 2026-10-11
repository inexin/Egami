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
        if (parser.ImageChunk != WebpContainer.Vp8L)
        {
            throw new NotSupportedException("Only lossless WebP streams so far.");
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

            if (type is WebpContainer.Vp8 or WebpContainer.Anmf)
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
            if (_decoder.Warning is string warning)
            {
                metadata.WarningList.Add(warning);
            }

            Metadata.ReplaceWith(metadata);
        }
    }
}
