using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Pnm;

/// <content>
/// Streaming: PBM, PGM, PPM and PAM rows, binary or plain text, decode in order, one row held. PFM stores its rows bottom-up, so it
/// is decoded into a buffer first.
/// </content>
internal sealed partial class PnmReader : IRowFormatReader, IIncrementalFormatReader
{
    public HeaderState HeaderStatus(EgamiInput input, ReaderOptions options)
    {
        // The header is text of no fixed length: it has arrived when it parses from the bytes so far. Plain text images and PFM
        // wait for the end.
        if (input.Available < 3)
        {
            return HeaderState.NotYet;
        }

        if (Pfm.Is(input))
        {
            return HeaderState.WaitsForEnd;
        }

        try
        {
            using MemoryStream peeked = new(input.Peek(64 * 1024).ToArray(), writable: false);
            EgamiInput probe = new(peeked, input.Context, input.CancellationToken);
            try
            {
                return ReadHeader(probe).Plain ? HeaderState.WaitsForEnd : HeaderState.Arrived;
            }
            finally
            {
                probe.Dispose();
            }
        }
        catch (CorruptDataException)
        {
            return HeaderState.NotYet;
        }
    }

    /// <summary>Returns the bytes one stored row of a binary PNM takes.</summary>
    private static long RowBytes(Header header)
    {
        long samples = (long)header.Width * header.Depth;
        return header.Wide ? samples * 2 : header.Bitmap ? (header.Width + 7) / 8 : samples;
    }

    /// <summary>Waits for more bytes unless <paramref name="ready"/>: an incremental decode stops here and comes back later.</summary>
    private static void Wait(bool ready)
    {
        if (!ready)
        {
            throw new NeedsInputException();
        }
    }

    /// <summary>Returns whether <paramref name="bytes"/> bytes from the read position have arrived, or the input has ended.</summary>
    private static bool Arrived(EgamiInput input, long bytes)
    {
        return !input.Incremental || input.AtEndOfInput || input.Available >= bytes;
    }

    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        if (Pfm.Is(input))
        {
            return BufferedRowSource.Read(PnmFormat.Info, input, options);
        }

        Header header = ReadHeader(input);
        input.Limits.CheckFrameSize(header.Width, header.Height);
        return header.Natural switch
        {
            Natural.Gray8 => new PnmRows<Gray8>(input, header, options),
            Natural.Gray16 => new PnmRows<Gray16>(input, header, options),
            Natural.GrayAlpha8 => new PnmRows<GrayAlpha8>(input, header, options),
            Natural.Rgb8 => new PnmRows<Rgb8>(input, header, options),
            Natural.Rgba8 => new PnmRows<Rgba8>(input, header, options),
            _ => new PnmRows<Rgba16>(input, header, options),
        };
    }

    /// <summary>Returns what a streaming read holds: one natural row. The text and sample buffers come from the shared pool.</summary>
    private static long WorkingBytes(Header header) => (long)header.Width * LayoutOf(header.Natural).BytesPerPixel;

    /// <summary>One PNM image, decoded a row at a time in its natural pixel type.</summary>
    private sealed class PnmRows<TNatural> : RowSource
        where TNatural : unmanaged, IPixelType<TNatural>
    {
        private readonly EgamiInput _input;
        private readonly Header _header;
        private readonly bool _strict;
        private readonly Decoder<TNatural> _decoder;
        private readonly IMemoryOwner<byte> _row;
        private int _frame = -1;
        private int _next;
        private int _failedAt = -1;
        private bool _ended;
        private bool _disposed;

        public PnmRows(EgamiInput input, Header header, ReaderOptions options)
        {
            _input = input;
            _header = header;
            _strict = options.Recovery == RecoveryMode.Strict;
            Metadata.FormatName = PnmFormat.Info.Name;
            Metadata.FormatDetails = new PnmDetails
            {
                Kind = header.Magic switch { 1 or 4 => PnmKind.Bitmap, 2 or 5 => PnmKind.Graymap, 3 or 6 => PnmKind.Pixmap, _ => PnmKind.Pam },
                PlainText = header.Magic <= 3,
                MaxValue = header.MaxValue,
                TupleType = header.TupleType,
            };
            if (options.ReadMetadata)
            {
                foreach (string comment in header.Comments)
                {
                    Metadata.Text.Add(new TextEntry("Comment", comment));
                }
            }

            foreach (string warning in header.Warnings)
            {
                Metadata.WarningList.Add(warning);
            }

            _row = input.RentScratch((int)PnmReader.WorkingBytes(header));
            _decoder = new Decoder<TNatural>(input, header, _strict);
        }

        public override int StoredWidth => _header.Width;

        public override int StoredHeight => _header.Height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => TNatural.Layout;

        public override RasterMetadata Metadata { get; } = new();

        public override bool MetadataComplete => _ended;

        public override int? FrameCount => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => PnmReader.WorkingBytes(_header);

        private Span<TNatural> Row => MemoryMarshal.Cast<byte, TNatural>(_row.Memory.Span)[.._header.Width];

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => visitor.Visit<TNatural>();

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
            while (_next <= y && _failedAt < 0)
            {
                try
                {
                    Wait(_header.Plain || Arrived(_input, RowBytes(_header)));
                    _decoder.DecodeRow(Row);
                    _next++;
                }
                catch (CorruptDataException e) when (!_strict)
                {
                    _failedAt = _next;
                    Metadata.WarningList.Add(_decoder.Damaged(e, _next));
                }
            }

            if (_failedAt >= 0 && y >= _failedAt)
            {
                destination.Clear();
                return;
            }

            PixelConvert.Convert<TNatural, TPixel>(Row.Slice(x, destination.Length), destination);
        }

        public override void EndFrame()
        {
            if (_ended)
            {
                return;
            }

            if (_next == _header.Height && _failedAt < 0)
            {
                // The check for data after the image looks at up to 4 KB of it.
                Wait(Arrived(_input, 4096));
                _decoder.Finish(Metadata.WarningList);
            }

            _ended = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _decoder.Dispose();
                _row.Dispose();
                _input.ReturnUnreadBytes();
                _input.Dispose();
            }
        }
    }
}
