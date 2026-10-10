using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Jpeg;

/// <content>
/// Streaming: a sequential JPEG is entropy-decoded one MCU row at a time as rows are asked for; a progressive or multi-scan one
/// keeps every coefficient until its last scan, then transforms MCU rows as they are asked for. Either way only the region's
/// blocks go through the IDCT, upsampling and color conversion, and the output pixels are the ones Load gives.
/// </content>
internal sealed partial class JpegReader : IRowFormatReader, IIncrementalFormatReader
{
    public bool HeaderArrived(EgamiInput input, ReaderOptions options)
    {
        // SOI, then marker segments by their lengths, through the first SOS, which is where Open stops.
        Span<byte> bytes = stackalloc byte[4];
        long offset = 2;
        while (true)
        {
            if (!input.TryPeekAt(offset, bytes[..2]))
            {
                return false;
            }

            if (bytes[0] != 0xFF || bytes[1] == 0xFF)
            {
                // Stray or fill bytes before a marker: the header reader skips them.
                offset++;
                continue;
            }

            int marker = bytes[1];
            offset += 2;
            if (marker is (>= 0xD0 and <= 0xD7) or 0x01 or 0x00)
            {
                continue;
            }

            if (marker == JpegMarker.Eoi)
            {
                return true;
            }

            if (!input.TryPeekAt(offset, bytes[..2]))
            {
                return false;
            }

            offset += (bytes[0] << 8) | bytes[1];
            if (input.Available < offset)
            {
                return false;
            }

            if (marker == JpegMarker.Sos)
            {
                return true;
            }
        }
    }

    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        JpegHeader header = JpegSegmentReader.ReadHeader(input, options);
        ReadPlan plan = ReadPlan.Create(header.Width, header.Height, options.Region, options.MinimumSize, MaxNativeScale);
        header.Details.ScaleDenominator = plan.NativeScale;
        return new JpegRows(header, input, options, plan);
    }

    /// <summary>Returns the bytes a row source holds for this header at this scale.</summary>
    private static long WorkingBytes(JpegHeader header, ReaderOptions options, int scale)
    {
        using JpegDecoder<Rgb8> decoder = new(header, options, [], fastUpsampling: false, scale);
        return decoder.WorkingBytes(Buffered(header));
    }

    /// <summary>Returns whether the file keeps every coefficient until its last scan: progressive, or a scan per component.</summary>
    private static bool Buffered(JpegHeader header)
    {
        return header.Progressive || (header.FirstScan is JpegScan first && first.Components.Length != header.Components.Length);
    }

    /// <summary>One JPEG image, decoded as its rows are asked for.</summary>
    private sealed class JpegRows : RowSource
    {
        private readonly JpegHeader _header;
        private readonly EgamiInput _input;
        private readonly Rect _region;
        private JpegDecoder<Rgb8>? _decoder;
        private bool _begun;
        private int _frame = -1;
        private bool _disposed;

        public JpegRows(JpegHeader header, EgamiInput input, ReaderOptions options, ReadPlan plan)
        {
            _header = header;
            _input = input;
            _region = plan.Region;
            NativeScale = plan.NativeScale;
            CopyMetadata(header, Metadata);
            bool fast = options is JpegReaderOptions { FastUpsampling: true };
            _decoder = new JpegDecoder<Rgb8>(header, options, Metadata.WarningList, fast, NativeScale);
            WorkingBytes = _decoder.WorkingBytes(Buffered(header));
        }

        public override int StoredWidth => _header.Width;

        public override int StoredHeight => _header.Height;

        public override int NativeScale { get; }

        public override int MaxNativeScale => 8;

        public override PixelLayoutInfo Layout => JpegSegmentReader.Layout(_header);

        public override RasterMetadata Metadata { get; } = new();

        public override int? FrameCount => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes { get; }

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor)
        {
            return _header.Components.Length == 1 ? visitor.Visit<Gray8>() : visitor.Visit<Rgb8>();
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

        public override bool CanPreview => Buffered(_header) && _decoder is { } decoder && (!_begun || decoder.CanPreview);

        public override void PreviewRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            JpegDecoder<Rgb8> decoder = _decoder ?? throw new InvalidOperationException("The frame has ended.");
            try
            {
                Begin(decoder);
            }
            catch (NeedsInputException)
            {
                // The scans that have arrived are what the preview shows.
            }

            decoder.PreviewRow(y, x, destination);
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            JpegDecoder<Rgb8> decoder = _decoder ?? throw new InvalidOperationException("The frame has ended.");
            Begin(decoder);
            decoder.ReadRow(y, x, destination);
        }

        private void Begin(JpegDecoder<Rgb8> decoder)
        {
            if (!_begun)
            {
                // Set first: in an incremental decode Begin can stop at a scan that has not arrived, and the rows carry on from there.
                _begun = true;
                decoder.Begin(_input, _region);
            }
        }

        public override void EndFrame()
        {
            // JPEG keeps nothing after its image data, so the rest of the file is not read; the buffers go back now.
            _decoder?.Dispose();
            _decoder = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _decoder?.Dispose();
                _decoder = null;
                _input.ReturnUnreadBytes();
                _input.Dispose();
            }
        }
    }
}
