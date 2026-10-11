using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Qoi;

/// <content>Streaming: QOI rows decode in order, the 64-entry index and the run carried from one row to the next.</content>
internal sealed partial class QoiReader : IRowSourceReader, IIncrementalFormatReader
{
    public HeaderState HeaderStatus(EgamiInput input, ReaderOptions options) => input.Available >= HeaderLength ? HeaderState.Arrived : HeaderState.NotYet;

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

    /// <summary>The read window between rows: small enough that keeping it filled never moves much of the input buffer.</summary>
    private const int Window = 4096;

    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input);
        input.Limits.CheckFrameSize(header.Width, header.Height);
        return new QoiRows(input, header, options.Recovery == RecoveryMode.Strict);
    }

    /// <summary>Returns what a streaming read holds: one row of packed pixels.</summary>
    private static long WorkingBytes(Header header) => (long)header.Width * 4;

    /// <summary>One QOI image, decoded a row at a time.</summary>
    private sealed class QoiRows : RowSource
    {
        private readonly EgamiInput _input;
        private readonly Header _header;
        private readonly bool _strict;
        private readonly uint[] _index = new uint[64];
        private readonly IMemoryOwner<byte> _row;
        private uint _px = 0xFF00_0000;
        private int _run;
        private int _next;
        private int _x;
        private int _failedAt = -1;
        private bool _ended;
        private bool _disposed;

        public QoiRows(EgamiInput input, Header header, bool strict)
        {
            _input = input;
            _header = header;
            _strict = strict;
            Metadata.FormatName = QoiFormat.Info.Name;
            Metadata.FormatDetails = new QoiDetails { Channels = header.Channels, Linear = header.Linear };
            Metadata.ColorSpace = header.Linear ? ColorSpaceHint.Linear : ColorSpaceHint.Srgb;
            _row = input.RentScratch((int)WorkingBytes(header));
        }

        public override int StoredWidth => (int)_header.Width;

        public override int StoredHeight => (int)_header.Height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => _header.Channels == 3 ? Rgb8.Layout : Rgba8.Layout;

        public override RasterMetadata Metadata { get; } = new();

        public override bool MetadataComplete => _ended;

        public override int? FrameCount => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => QoiReader.WorkingBytes(_header);

        private Span<uint> Row => MemoryMarshal.Cast<byte, uint>(_row.Memory.Span)[..StoredWidth];

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor)
        {
            return _header.Channels == 3 ? visitor.Visit<Rgb8>() : visitor.Visit<Rgba8>();
        }

        public override FrameMetadata? NextFrame()
        {
            if (_next == 0 && _failedAt < 0 && !_ended)
            {
                return new FrameMetadata();
            }

            EndFrame();
            return null;
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            while (_next <= y && _failedAt < 0)
            {
                DecodeRow();
            }

            if (_failedAt >= 0 && y > _failedAt)
            {
                destination.Clear();
                return;
            }

            PixelConvert.Convert<Rgba8, TPixel>(MemoryMarshal.Cast<uint, Rgba8>(Row).Slice(x, destination.Length), destination);
        }

        public override void EndFrame()
        {
            if (_ended)
            {
                return;
            }

            if (_next < StoredHeight || _failedAt >= 0)
            {
                _ended = true;
                // Rows after the last one asked for are not decoded, and a truncated file has no end marker to check.
                return;
            }

            Wait(Arrived(_input, EndMarker.Length));
            ReadOnlySpan<byte> end = _input.Peek(EndMarker.Length);
            if (end.SequenceEqual(EndMarker))
            {
                _input.Skip(EndMarker.Length);
                _ended = true;
                return;
            }

            _ended = true;
            string problem = end.Length == 0 || EndMarker.StartsWith(end)
                ? "The file ends without the QOI end marker."
                : "More data follows the last pixel where the QOI end marker should be; it was ignored.";
            if (_strict)
            {
                throw _input.Corrupt(_input.Position, problem);
            }

            Metadata.WarningList.Add(end.Length == 0 || EndMarker.StartsWith(end) ? "Qoi.EndMarkerMissing" : "Qoi.DataAfterEnd", problem, _input.Position);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _row.Dispose();
                _input.ReturnUnreadBytes();
                _input.Dispose();
            }
        }

        /// <summary>Decodes the next row into the row buffer, chunk by chunk, as the whole-image decoder does.</summary>
        private void DecodeRow()
        {
            _input.CancellationToken.ThrowIfCancellationRequested();
            Span<uint> row = Row;
            Span<uint> index = _index;
            int width = row.Length;

            // A row an incremental decode stopped in carries on from the pixel it reached.
            int x = _x;
            ReadOnlySpan<byte> data = _input.Peek(Window);
            int pos = 0;
            while (x < width)
            {
                if (_run > 0)
                {
                    int n = Math.Min(_run, width - x);
                    row.Slice(x, n).Fill(_px);
                    x += n;
                    _run -= n;
                    continue;
                }

                if (data.Length - pos < 5)
                {
                    _input.Skip(pos);
                    data = _input.Peek(Window);
                    pos = 0;
                    if (data.Length < 5 && (data.Length == 0 || (data[0] == OpRgb && data.Length < 4) || (data[0] == OpRgba && data.Length < 5)
                        || ((data[0] & 0xC0) == OpLuma && data.Length < 2)))
                    {
                        if (!Arrived(_input, 5))
                        {
                            _x = x;
                            throw new NeedsInputException();
                        }

                        Truncated(row, x);
                        _x = 0;
                        return;
                    }
                }

                byte c = data[pos++];
                if (c == OpRgb)
                {
                    _px = (_px & 0xFF00_0000) | data[pos] | ((uint)data[pos + 1] << 8) | ((uint)data[pos + 2] << 16);
                    pos += 3;
                }
                else if (c == OpRgba)
                {
                    _px = BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]);
                    pos += 4;
                }
                else
                {
                    switch (c & 0xC0)
                    {
                        case OpIndex:
                            _px = index[c];
                            break;
                        case OpDiff:
                            _px = Add(_px, ((c >> 4) & 3) - 2, ((c >> 2) & 3) - 2, (c & 3) - 2);
                            break;
                        case OpLuma:
                            int dg = (c & 0x3F) - 32;
                            byte next = data[pos++];
                            _px = Add(_px, dg - 8 + (next >> 4), dg, dg - 8 + (next & 0x0F));
                            break;
                        default:
                            _run = (c & 0x3F) + 1;
                            break;
                    }
                }

                index[Hash(_px)] = _px;
                if (_run == 0)
                {
                    row[x++] = _px;
                }
            }

            _input.Skip(pos);
            _x = 0;
            _next++;
        }

        /// <summary>Keeps the part of the row decoded and marks the rest of the image transparent black (Lenient), or throws.</summary>
        private void Truncated(Span<uint> row, int x)
        {
            if (_strict)
            {
                throw new CorruptDataException(QoiFormat.Info.Name, _input.Position, "The data ends unexpectedly.") { IsTruncation = true };
            }

            row[x..].Clear();
            _failedAt = _next;
            _next++;
            Metadata.WarningList.Add("Qoi.RowsMissing", $"The file ends after {_failedAt} of {StoredHeight} rows; the rest is left transparent black.");
        }
    }
}
