using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Jxl;

/// <content>
/// Incremental decoding (JPEG XL extras PR D): a JPEG XL file opens once its image header has arrived, and
/// <see cref="RasterDecoder.RenderPreview"/> draws what has arrived of a still image as libjxl's progressive flush does (see
/// <see cref="JxlImageDecoder.DecodePartial"/>). Rows come out once the last byte has, decoded as Load decodes the file, so they
/// are the same pixels.
/// </content>
internal sealed partial class JxlReader : IIncrementalFormatReader
{
    public HeaderState HeaderStatus(EgamiInput input, ReaderOptions options)
    {
        if (options.Hdr is not null)
        {
            // A gain map rendition needs the boxes after the codestream: read whole.
            return HeaderState.WaitsForEnd;
        }

        return Header(input) is null ? HeaderState.NotYet : HeaderState.ArrivedRowsAtEnd;
    }

    /// <summary>Gets the image header from the bytes that have arrived, or <see langword="null"/> while it has not.</summary>
    private static JxlImageHeader? Header(EgamiInput input)
    {
        int available = (int)Math.Min(input.Available, 1 << 20);
        byte[] bytes = new byte[available];
        if (!input.TryPeekAt(0, bytes))
        {
            return null;
        }

        byte[]? codestream = JxlPartial.Codestream(bytes, bytes.Length);
        if (codestream is null)
        {
            return null;
        }

        try
        {
            return JxlImageHeader.Read(new JxlBitReader(codestream, 0, codestream.Length), input.Limits);
        }
        catch (Exception e) when (e is EgamiException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// A JPEG XL file while its bytes arrive: previews from what has arrived, then, once the input has ended, the file read as
    /// Load reads it.
    /// </summary>
    private sealed class IncrementalRows : RowSource
    {
        private readonly EgamiInput _input;
        private readonly ReaderOptions _options;
        private readonly JxlImageHeader _header;
        private readonly RasterMetadata _metadata = new() { FormatName = JxlFormat.Info.Name };
        private byte[] _bytes = new byte[1 << 16];
        private int _received;
        private int _previewed = -1;
        private Raster? _preview;
        private BufferedRowSource? _previewRows;
        private BufferedRowSource? _final;
        private bool _started;
        private bool _disposed;

        public IncrementalRows(EgamiInput input, ReaderOptions options, JxlImageHeader header)
        {
            _input = input;
            _options = options;
            _header = header;
        }

        public override int StoredWidth => _header.Width;

        public override int StoredHeight => _header.Height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => _final?.Layout ?? NaturalLayout(_header, _options is JxlReaderOptions { LinearOutput: true });

        public override RasterMetadata Metadata => _final?.Metadata ?? _metadata;

        public override bool MetadataComplete => _final is not null;

        public override int? FrameCount => _final?.FrameCount ?? (_header.Animation is null ? 1 : null);

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => _received + ((long)_header.Width * _header.Height * Layout.BytesPerPixel * 2);

        public override bool CanPreview
        {
            get
            {
                if (_final is not null || _header.Animation is not null)
                {
                    return false;
                }

                Pull();
                if (_received != _previewed)
                {
                    _previewed = _received;
                    Raster? preview = JxlImageDecoder.DecodePartial(_bytes, _received, _input, _options);
                    if (preview is not null)
                    {
                        _preview?.Dispose();
                        _preview = preview;
                        _previewRows = BufferedRowSource.From(preview);
                        _previewRows.NextFrame();
                    }
                }

                return _previewRows is not null;
            }
        }

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor)
        {
            if (_final is not null)
            {
                return _final.WithNaturalType(visitor);
            }

            PixelLayoutInfo layout = Layout;
            return layout switch
            {
                _ when layout == Gray8.Layout => visitor.Visit<Gray8>(),
                _ when layout == GrayAlpha8.Layout => visitor.Visit<GrayAlpha8>(),
                _ when layout == Rgb8.Layout => visitor.Visit<Rgb8>(),
                _ when layout == Rgba8.Layout => visitor.Visit<Rgba8>(),
                _ when layout == Gray16.Layout => visitor.Visit<Gray16>(),
                _ when layout == GrayAlpha16.Layout => visitor.Visit<GrayAlpha16>(),
                _ when layout == Rgb16.Layout => visitor.Visit<Rgb16>(),
                _ when layout == Rgba16.Layout => visitor.Visit<Rgba16>(),
                _ when layout == RgbaF16.Layout => visitor.Visit<RgbaF16>(),
                _ when layout == GrayF32.Layout => visitor.Visit<GrayF32>(),
                _ when layout == RgbF32.Layout => visitor.Visit<RgbF32>(),
                _ when layout == Cmyk8.Layout => visitor.Visit<Cmyk8>(),
                _ when layout == Cmyk16.Layout => visitor.Visit<Cmyk16>(),
                _ => visitor.Visit<RgbaF32>(),
            };
        }

        public override FrameMetadata? NextFrame()
        {
            if (_header.Animation is not null || _started)
            {
                // Animations, and the end of a still, come from the whole file.
                return Finish().NextFrame();
            }

            _started = true;
            return new FrameMetadata();
        }

        public override void PreviewRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            BufferedRowSource rows = _previewRows ?? throw new InvalidOperationException("Nothing has arrived to show yet.");
            rows.ReadRow(y, x, destination);
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            _input.CancellationToken.ThrowIfCancellationRequested();
            if (_final is null)
            {
                Finish().NextFrame();
            }

            _final!.ReadRow(y, x, destination);
        }

        public override void EndFrame() => _final?.EndFrame();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _preview?.Dispose();
                _final?.Dispose();
                _input.Dispose();
            }
        }

        /// <summary>Takes every byte that has arrived.</summary>
        private void Pull()
        {
            int available = (int)Math.Min(_input.Available, Array.MaxLength - _received);
            if (available <= 0)
            {
                return;
            }

            _input.CheckMemory((long)_received + available + WorkingBytes);
            if (_bytes.Length - _received < available)
            {
                Array.Resize(ref _bytes, (int)Math.Min(Array.MaxLength, Math.Max(_bytes.Length * 2L, (long)_received + available)));
            }

            _received += _input.Read(_bytes.AsSpan(_received, available));
        }

        /// <summary>Waits for the last byte, then decodes the file as Load does.</summary>
        private BufferedRowSource Finish()
        {
            if (_final is not null)
            {
                return _final;
            }

            Pull();
            if (!_input.AtEndOfInput || _input.Available > 0)
            {
                throw new NeedsInputException();
            }

            using MemoryStream stream = new(_bytes, 0, _received, writable: false);
            EgamiInput whole = new(stream, _input.Context, _input.CancellationToken);
            try
            {
                Raster raster = JxlImageDecoder.Decode(whole, _options);
                raster.Metadata.FormatName ??= JxlFormat.Info.Name;
                _final = BufferedRowSource.From(raster);
            }
            finally
            {
                whole.Dispose();
            }

            _preview?.Dispose();
            _preview = null;
            _previewRows = null;
            return _final;
        }
    }
}
