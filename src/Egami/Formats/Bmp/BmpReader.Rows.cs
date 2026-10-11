using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Bmp;

/// <content>
/// Streaming: uncompressed top-down files decode their rows in order; bottom-up ones on a seekable stream read each row where it
/// is stored. Run-length and embedded PNG or JPEG files, bottom-up files on a stream that cannot seek, and 32-bit files whose alpha
/// counts only if some pixel uses it (unless the stream can seek to check first) are decoded into a buffer first.
/// </content>
internal sealed partial class BmpReader : IRowSourceReader, IIncrementalFormatReader
{
    public HeaderState HeaderStatus(EgamiInput input, ReaderOptions options)
    {
        if (!HeaderParses(input, options))
        {
            return HeaderState.NotYet;
        }

        if (!Streams(input, options))
        {
            return HeaderState.WaitsForEnd;
        }

        // The headers, the color table and any profile before the pixels: everything Open reads, up to the pixel data.
        ReadOnlySpan<byte> file = input.Peek(14);
        return input.Available >= BinaryPrimitives.ReadUInt32LittleEndian(file[10..]) ? HeaderState.Arrived : HeaderState.NotYet;
    }

    /// <summary>Returns whether the headers parse from the first 4 KB, read in a probe of their own.</summary>
    private static bool HeaderParses(EgamiInput input, ReaderOptions options)
    {
        try
        {
            using MemoryStream peeked = new(input.Peek(4096).ToArray(), writable: false);
            EgamiInput probe = new(peeked, input.Context, input.CancellationToken);
            try
            {
                ReadHeader(probe, options);
                return true;
            }
            finally
            {
                probe.Dispose();
            }
        }
        catch (CorruptDataException)
        {
            return false;
        }
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
            return BufferedRowSource.Read(BmpFormat.Info, input, options);
        }

        Header header = ReadHeader(input, options);
        input.Limits.CheckFrameSize(header.Width, header.Height);
        SkipToPixels(input, header);
        return new BmpRows(input, header, options);
    }

    /// <summary>Returns whether the file streams, from its headers read out of the first bytes without consuming them.</summary>
    private static bool Streams(EgamiInput input, ReaderOptions options)
    {
        Header header;
        try
        {
            using MemoryStream peeked = new(input.Peek(4096).ToArray(), writable: false);
            EgamiInput probe = new(peeked, input.Context, input.CancellationToken);
            try
            {
                header = ReadHeader(probe, options);
            }
            finally
            {
                probe.Dispose();
            }
        }
        catch (CorruptDataException)
        {
            return false;
        }

        if (header.Embedded || header.Compression is BmpCompression.Rle4 or BmpCompression.Rle8 or BmpCompression.Rle24 || header.Width < 1 || header.Height < 1)
        {
            return false;
        }

        bool complete = input.Length is long length && header.DataOffset + (Stride(header) * header.Height) <= length;
        if (header.Bits == 32 && header.AlphaUnlessAllZero && !complete)
        {
            // Whether the alpha byte counts is known only after every pixel; without seeking ahead to check, read it all first.
            return false;
        }

        return header.TopDown || complete;
    }

    /// <summary>Returns the bytes of one stored row, padded to 4.</summary>
    private static long Stride(Header header) => (((long)header.Width * header.Bits) + 31) / 32 * 4;

    /// <summary>Returns what a streaming read holds: one stored row and one row of 8-bit BGRA.</summary>
    private static long WorkingBytes(Header header) => Stride(header) + ((long)header.Width * 4);

    /// <summary>One uncompressed BMP image, decoded a row at a time.</summary>
    private sealed class BmpRows : RowSource
    {
        private readonly EgamiInput _input;
        private readonly Header _header;
        private readonly ReaderOptions _options;
        private readonly long _pixels;
        private readonly bool _strict;
        private readonly int _stride;
        private readonly int _used;
        private readonly IMemoryOwner<byte> _stored;
        private readonly IMemoryOwner<byte> _row;
        private readonly bool _standard32;
        private readonly uint[] _table = new uint[256];
        private readonly bool _opaque;
        private bool _pastPalette;
        private int _frame = -1;
        private int _next;
        private int _current = -1;
        private int _failedAt = -1;
        private bool _ended;
        private bool _disposed;

        public BmpRows(EgamiInput input, Header header, ReaderOptions options)
        {
            _input = input;
            _header = header;
            _options = options;
            _pixels = input.Position;
            _strict = options.Recovery == RecoveryMode.Strict;
            _stride = checked((int)Stride(header));
            _used = checked((int)((((long)header.Width * header.Bits) + 7) / 8));
            _standard32 = header.Bits == 32 && header.Masks is (0x00FF_0000, 0x0000_FF00, 0x0000_00FF, 0xFF00_0000 or 0);
            Metadata.FormatName = BmpFormat.Info.Name;
            for (int i = 0; i < 256; i++)
            {
                Rgb8 c = i < header.Palette.Length ? header.Palette[i] : default;
                _table[i] = c.B | ((uint)c.G << 8) | ((uint)c.R << 16) | 0xFF00_0000;
            }

            _stored = input.RentScratch(_stride);
            _row = input.RentScratch(header.Width * 4);
            if (header.Bits == 32 && header.AlphaUnlessAllZero)
            {
                _opaque = KnownOpaque(input, header, _pixels) ?? false;
            }
        }

        public override int StoredWidth => _header.Width;

        public override int StoredHeight => _header.Height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => _header.Natural;

        public override RasterMetadata Metadata { get; } = new();

        public override bool MetadataComplete => _ended;

        public override int? FrameCount => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => BmpReader.WorkingBytes(_header);

        private Span<Bgra8> Row => MemoryMarshal.Cast<byte, Bgra8>(_row.Memory.Span)[.._header.Width];

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor)
        {
            return _header.Natural.HasAlpha ? visitor.Visit<Bgra8>() : visitor.Visit<Bgr8>();
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
            Span<byte> stored = _stored.Memory.Span[.._stride];
            if (_header.TopDown)
            {
                while (_next <= y && _failedAt < 0)
                {
                    try
                    {
                        _input.CancellationToken.ThrowIfCancellationRequested();
                        Wait(Arrived(_input, _stride));
                        _input.ReadExactly(stored);
                        Decode(stored);
                        _next++;
                    }
                    catch (CorruptDataException e) when (!_strict)
                    {
                        _failedAt = _next;
                        Metadata.WarningList.Add("Bmp.RowsMissing", $"{e.Message} The {_header.Height - _next} rows not decoded are left black.", e);
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
                long at = _pixels + ((long)(_header.Height - 1 - y) * _stride);
                if (!_input.TryReadAt(at, stored))
                {
                    throw _input.Corrupt(at, "The data ends unexpectedly.");
                }

                Decode(stored);
                _current = y;
                _next = Math.Max(_next, y + 1);
            }

            if (_header.Bits == 24)
            {
                PixelConvert.Convert<Bgr8, TPixel>(MemoryMarshal.Cast<byte, Bgr8>(stored[.._used]).Slice(x, destination.Length), destination);
                return;
            }

            PixelConvert.Convert<Bgra8, TPixel>(Row.Slice(x, destination.Length), destination);
        }

        public override void EndFrame()
        {
            if (_ended)
            {
                return;
            }

            if (_options.ReadMetadata && _header.ProfileSize > 0 && _header.Profile is null && _header.ProfileOffset >= _input.Position)
            {
                // A profile after the pixels is read once all of it has arrived.
                Wait(Arrived(_input, _header.ProfileOffset - _input.Position + _header.ProfileSize));
            }

            _ended = true;
            if (_pastPalette && _failedAt < 0)
            {
                Metadata.WarningList.Add("Bmp.IndexOutOfRange", $"Some pixels use indices past the {_header.Palette.Length}-color palette; they are drawn black.");
            }

            ApplyMetadata(_input, _header, Metadata, _options);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _stored.Dispose();
                _row.Dispose();
                _input.ReturnUnreadBytes();
                _input.Dispose();
            }
        }

        /// <summary>Unpacks one stored row into 8-bit BGRA, as the whole-image decoder does (24-bit rows are read as stored).</summary>
        private void Decode(ReadOnlySpan<byte> bytes)
        {
            Span<Bgra8> pixels = Row;
            int width = _header.Width;
            Channel red = new(_header.Masks.Red), green = new(_header.Masks.Green), blue = new(_header.Masks.Blue), alpha = new(_header.Masks.Alpha);
            switch (_header.Bits)
            {
                case 24:
                    return;
                case 32 when _standard32:
                    MemoryMarshal.Cast<byte, Bgra8>(bytes[.._used]).CopyTo(pixels);
                    if (_header.Masks.Alpha == 0 || _opaque)
                    {
                        foreach (ref Bgra8 p in pixels)
                        {
                            p.A = 255;
                        }
                    }

                    return;
                case 32:
                    for (int x = 0; x < width; x++)
                    {
                        uint p = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 * x)..]);
                        pixels[x] = new Bgra8(blue.Read(p, 0), green.Read(p, 0), red.Read(p, 0), _opaque ? (byte)255 : alpha.Read(p, 255));
                    }

                    return;
                case 16:
                    for (int x = 0; x < width; x++)
                    {
                        uint p = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(2 * x)..]);
                        pixels[x] = new Bgra8(blue.Read(p, 0), green.Read(p, 0), red.Read(p, 0), alpha.Read(p, 255));
                    }

                    return;
                default:
                    _pastPalette |= Indices(bytes, MemoryMarshal.Cast<Bgra8, uint>(pixels));
                    return;
            }
        }

        /// <summary>Looks up 1, 2, 4 or 8-bit indices in the palette; returns whether any was past its end.</summary>
        private bool Indices(ReadOnlySpan<byte> bytes, Span<uint> output)
        {
            int bits = _header.Bits;
            int count = _header.Palette.Length;
            if (bits == 8)
            {
                for (int x = 0; x < output.Length; x++)
                {
                    output[x] = _table[bytes[x]];
                }

                return count < 256 && bytes[..output.Length].IndexOfAnyInRange((byte)count, (byte)255) >= 0;
            }

            bool past = false;
            int perByte = 8 / bits;
            int mask = (1 << bits) - 1;
            int x0 = 0;
            for (int i = 0; x0 < output.Length; i++)
            {
                int b = bytes[i];
                int n = Math.Min(perByte, output.Length - x0);
                for (int k = 0; k < n; k++)
                {
                    int index = (b >> (8 - (bits * (k + 1)))) & mask;
                    past |= index >= count;
                    output[x0 + k] = _table[index];
                }

                x0 += n;
            }

            return past;
        }
    }
}
