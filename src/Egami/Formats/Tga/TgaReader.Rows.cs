using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Tga;

/// <content>
/// Streaming: top-down files, raw or run-length, decode their rows in order; bottom-up raw files on a seekable stream read each row
/// where it is stored. Bottom-up run-length files, bottom-up files on a stream that cannot seek, and files whose alpha meaning is
/// only known from a footer after the pixels are decoded into a buffer first.
/// </content>
internal sealed partial class TgaReader : IRowFormatReader, IIncrementalFormatReader
{
    public HeaderState HeaderStatus(EgamiInput input, ReaderOptions options)
    {
        ReadOnlySpan<byte> bytes = input.Peek(HeaderLength);
        if (bytes.Length < HeaderLength)
        {
            return HeaderState.NotYet;
        }

        if (!Streams(input, options))
        {
            return HeaderState.WaitsForEnd;
        }

        int map = BinaryPrimitives.ReadUInt16LittleEndian(bytes[5..]) * (bytes[1] == 1 ? (bytes[7] + 7) / 8 : 0);
        return input.Available >= HeaderLength + bytes[0] + map ? HeaderState.Arrived : HeaderState.NotYet;
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
        if (!Streams(input, options))
        {
            return BufferedRowSource.Read(TgaFormat.Info, input, options);
        }

        Header header = ReadHeader(input, options, probe: false);
        input.Limits.CheckFrameSize(header.Width, header.Height);
        long pixels = input.Position;
        PixelLayoutInfo natural = header.Natural;
        return natural == Gray8.Layout ? new TgaRows<Gray8>(input, header, options, pixels)
            : natural == GrayAlpha8.Layout ? new TgaRows<GrayAlpha8>(input, header, options, pixels)
            : natural == Bgra8.Layout ? new TgaRows<Bgra8>(input, header, options, pixels)
            : new TgaRows<Bgr8>(input, header, options, pixels);
    }

    /// <summary>
    /// Returns whether the file streams, from its first 18 bytes: rows in order (top-down), or rows read where they are stored
    /// (bottom-up raw on a seekable stream with all its pixels), and alpha that does not wait for a footer after the pixels.
    /// </summary>
    private static bool Streams(EgamiInput input, ReaderOptions options)
    {
        ReadOnlySpan<byte> bytes = input.Peek(HeaderLength);
        if (bytes.Length < HeaderLength || !Matches(bytes))
        {
            return false;
        }

        Header header = new()
        {
            Type = bytes[2],
            MapLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[5..]),
            MapEntry = bytes[1] == 1 ? bytes[7] : 0,
            Width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]),
            Height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]),
            Depth = bytes[16],
            AttributeBits = bytes[17] & 0x0F,
            TopDown = (bytes[17] & 0x20) != 0,
        };
        if (header.Type is 32 or 33 || header.Width == 0 || header.Height == 0)
        {
            return false;
        }

        bool seekable = input.Length is not null;
        if (!seekable && header.HasAlphaBits)
        {
            return false;
        }

        if (header.TopDown)
        {
            return true;
        }

        long offset = HeaderLength + bytes[0] + ((long)header.MapLength * ((header.MapEntry + 7) / 8));
        return !header.Rle && seekable && offset + ((long)header.Width * header.Height * header.BytesPerPixel) <= input.Length;
    }

    /// <summary>Returns what a streaming read holds: one stored row and one natural row.</summary>
    private static long WorkingBytes(Header header)
    {
        return (long)header.Width * (header.BytesPerPixel + header.Natural.BytesPerPixel);
    }

    /// <summary>One TGA image, decoded a row at a time in its natural pixel type.</summary>
    private sealed class TgaRows<TNatural> : RowSource
        where TNatural : unmanaged, IPixelType<TNatural>
    {
        private readonly EgamiInput _input;
        private readonly Header _header;
        private readonly ReaderOptions _options;
        private readonly long _pixels;
        private readonly bool _strict;
        private readonly Decoder<TNatural> _decoder;
        private readonly IMemoryOwner<byte> _row;
        private int _frame = -1;
        private int _next;
        private int _current = -1;
        private int _failedAt = -1;
        private bool _ended;
        private bool _disposed;

        public TgaRows(EgamiInput input, Header header, ReaderOptions options, long pixels)
        {
            _input = input;
            _header = header;
            _options = options;
            _pixels = pixels;
            _strict = options.Recovery == RecoveryMode.Strict;
            Metadata.FormatName = TgaFormat.Info.Name;
            _row = input.RentScratch(header.Width * header.Natural.BytesPerPixel);
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

        public override long WorkingBytes => TgaReader.WorkingBytes(_header);

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
            if (_header.TopDown)
            {
                while (_next <= y && _failedAt < 0)
                {
                    try
                    {
                        Wait(_decoder.RowArrived());
                        Span<byte> stored = _decoder.Stored;
                        _decoder.ReadStored(stored);
                        _decoder.Convert(stored, Row);
                        _next++;
                    }
                    catch (CorruptDataException e) when (!_strict)
                    {
                        _failedAt = _next;
                        _decoder.Damaged(e, _next);
                    }
                }

                if (_failedAt >= 0 && y >= _failedAt)
                {
                    destination.Clear();
                    return;
                }
            }
            else if (_current != y)
            {
                // Bottom-up: the row is stored at its place from the end; Streams checked that every row is there.
                Span<byte> stored = _decoder.Stored;
                long fileRow = _header.Height - 1 - y;
                if (!_input.TryReadAt(_pixels + (fileRow * stored.Length), stored))
                {
                    throw _input.Corrupt(_pixels + (fileRow * stored.Length), "The data ends unexpectedly.");
                }

                _decoder.Convert(stored, Row);
                _current = y;
                _next = Math.Max(_next, y + 1);
            }

            PixelConvert.Convert<TNatural, TPixel>(Row.Slice(x, destination.Length), destination);
        }

        public override void EndFrame()
        {
            if (_ended)
            {
                return;
            }

            bool allRows = _next == _header.Height && _failedAt < 0;
            Wait(!allRows || _decoder.RestArrived());
            Wait(!(_header.FooterAfterPixels && _next == _header.Height) || _input.AtEndOfInput);
            _ended = true;
            if (allRows)
            {
                _decoder.FinishRows();
            }

            _decoder.FinishBytes();
            if (_header.FooterAfterPixels && _next == _header.Height)
            {
                ReadTrailingFooter(_input, _header);
            }

            ApplyMetadata(_header, Metadata, _options);
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
