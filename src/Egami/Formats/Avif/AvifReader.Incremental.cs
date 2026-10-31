using System.Buffers.Binary;

using Egami.Colors;
using Egami.Formats.Av1;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Avif;

/// <content>
/// Incremental decoding (AVIF extras design, question 3): a progressive (layered) still opens once its meta box has arrived, and
/// <see cref="RasterDecoder.RenderPreview"/> draws the best layer received so far at full size, as progressive JPEG draws its
/// scans; rows come out once the last byte has. Every other AVIF file decodes into a buffer first, so it waits for the end.
/// </content>
internal sealed partial class AvifReader : IIncrementalFormatReader
{
    public HeaderState HeaderStatus(EgamiInput input, ReaderOptions options)
    {
        HeaderState state = MetaEnd(input, options, out long end);
        return state != HeaderState.Arrived ? state : Progressive(input, end, out _);
    }

    /// <summary>Opens the incremental source of a progressive still whose meta box has arrived while more bytes may come, else null.</summary>
    private static ProgressiveRows? OpenProgressive(EgamiInput input, ReaderOptions options) =>
        input.Incremental && !input.AtEndOfInput && MetaEnd(input, options, out long end) == HeaderState.Arrived
            && Progressive(input, end, out AvifFile? head) == HeaderState.Arrived
            ? new ProgressiveRows(input, options, head!)
            : null;

    /// <summary>
    /// Walks the top-level boxes that have arrived up to the end of meta (ftyp, anything before meta whole, then meta): Arrived with
    /// <paramref name="end"/> when meta has, NotYet while it may still come, WaitsForEnd when the file is read whole.
    /// </summary>
    private static HeaderState MetaEnd(EgamiInput input, ReaderOptions options, out long end)
    {
        Span<byte> header = stackalloc byte[16];
        end = 0;
        while (true)
        {
            if (!input.TryPeekAt(end, header[..8]))
            {
                return HeaderState.NotYet;
            }

            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            uint type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            if (size == 1)
            {
                if (!input.TryPeekAt(end, header))
                {
                    return HeaderState.NotYet;
                }

                size = (long)Math.Min(BinaryPrimitives.ReadUInt64BigEndian(header[8..]), long.MaxValue);
            }

            if (size < 8 || options.Hdr is not null || (end == 0) != (type == IsoTypes.Ftyp))
            {
                // A box running to the end before meta, an HDR rendition, or a file that is not plain HEIF: read whole.
                return HeaderState.WaitsForEnd;
            }

            end = size > long.MaxValue - end ? long.MaxValue : end + size;
            if (input.Available < end)
            {
                // Meta far into the file (after a large mdat) leaves nothing to show early.
                return type == IsoTypes.Meta || end < 1 << 20 ? HeaderState.NotYet : HeaderState.WaitsForEnd;
            }

            if (type == IsoTypes.Meta)
            {
                return HeaderState.Arrived;
            }
        }
    }

    /// <summary>
    /// Looks at the meta box (the bytes before <paramref name="end"/>) and, when the primary image is one layered av01 item without
    /// alpha, grid, overlay or gain map, waits for its first layer, which holds the sequence header; then gives the still image,
    /// parsed from the bytes so far with the rest of its data zero. Any other file is read whole.
    /// </summary>
    private static HeaderState Progressive(EgamiInput input, long end, out AvifFile? file)
    {
        file = null;
        if (end > int.MaxValue)
        {
            return HeaderState.WaitsForEnd;
        }

        try
        {
            byte[] head = new byte[end];
            if (!input.TryPeekAt(0, head))
            {
                return HeaderState.NotYet;
            }

            HeifFile meta = HeifFile.Parse(head, input.FormatName);
            if (meta.MajorBrand == AvifFile.Avis || !meta.HasMeta || meta.PrimaryItem is not HeifItem item || item.Type != AvifFile.Av01
                || meta.Property(item, AvifProperties.A1lx) is not HeifProperty a1lx || item.ConstructionMethod != 0 || item.DataReferenceIndex != 0
                || item.Extents.Count == 0 || item.Extents.Any(e => e.Length <= 0))
            {
                return HeaderState.WaitsForEnd;
            }

            long total = item.Extents.Sum(e => e.Length), last = item.Extents.Max(e => e.Offset + e.Length);
            if (total > Array.MaxLength || last > Array.MaxLength)
            {
                return HeaderState.WaitsForEnd;
            }

            (int _, int first) = AvifCodec.LayerRanges(meta, a1lx, (int)total)[0];
            if (Arrived(item, input.Available) < first)
            {
                return HeaderState.NotYet;
            }

            byte[] bytes = new byte[last];
            if (!input.TryPeekAt(0, bytes.AsSpan(0, (int)Math.Min(last, input.Available))))
            {
                return HeaderState.NotYet;
            }

            AvifFile still = AvifFile.Open(HeifFile.Parse(bytes, input.FormatName));
            if (!still.Progressive || still.Primary != still.Cells[0] || still.Overlay is not null || still.Alpha is not null || still.ToneMap is not null)
            {
                return HeaderState.WaitsForEnd;
            }

            file = still;
            return HeaderState.Arrived;
        }
        catch (EgamiException)
        {
            return HeaderState.WaitsForEnd;
        }
    }

    /// <summary>Gets how many of an item's bytes have arrived from its start without a gap, when the file's first <paramref name="received"/> have.</summary>
    private static long Arrived(HeifItem item, long received)
    {
        long at = 0;
        foreach (HeifExtent extent in item.Extents)
        {
            long length = Math.Clamp(received - extent.Offset, 0, extent.Length);
            at += length;
            if (length < extent.Length)
            {
                break;
            }
        }

        return at;
    }

    /// <summary>
    /// A progressive still while its bytes arrive: the item's layers decode as each arrives whole, for the preview; once the input
    /// has ended the file is read as Load reads it and its rows come out.
    /// </summary>
    private sealed class ProgressiveRows : RowSource
    {
        private readonly EgamiInput _input;
        private readonly ReaderOptions _options;
        private readonly AvifFile _head;
        private readonly AvifCodec _codec;
        private readonly Av1Stream _stream;
        private readonly RasterMetadata _metadata;
        private readonly byte[] _item;
        private readonly List<(int Offset, int Length)> _layers;
        private readonly int _width;
        private readonly int _height;
        private byte[] _bytes = new byte[1 << 16];
        private int _received;
        private int _decodedLayers;
        private int _shownLayers;
        private HeifImageReader.PixelRows? _preview;
        private HeifImageReader.Prepared? _image;
        private bool _started;
        private bool _disposed;

        public ProgressiveRows(EgamiInput input, ReaderOptions options, AvifFile head)
        {
            _input = input;
            _options = options;
            _head = head;
            _codec = Codec(options);
            HeifFile heif = head.Heif;
            HeifItem item = head.Cells[0];
            long length = item.Extents.Sum(e => e.Length);
            if (item.Extents.Any(e => e.Length == 0) || length > Array.MaxLength)
            {
                throw input.Corrupt(-1, "The layered item's extents are open-ended or too large.");
            }

            input.CheckMemory(length + HeifImageReader.WorkingBytes(head));
            _item = new byte[length];
            _layers = AvifCodec.LayerRanges(heif, heif.Property(item, AvifProperties.A1lx)!, _item.Length);
            int point = heif.Property(item, AvifProperties.A1op) is HeifProperty a1op && heif.Content(a1op) is [byte op, ..] ? op : 0;
            _stream = new Av1Stream(input.FormatName)
            {
                OperatingPoint = Math.Min(point, 31),
                OutputAllLayers = true,
                Parallelism = input.Context.MaxParallelism,
                Limits = input.Limits,
            };
            (_width, _height) = HeifImageReader.OutputSize(head);
            ReadOnlyMemory<byte> icc = head.Icc;
            _metadata = new RasterMetadata
            {
                FormatName = AvifFormat.Info.Name,
                IccProfile = icc,
                CodePoints = HeifImageReader.RasterCodePoints(head.CodePoints, icc),
                FormatDetails = AvifDetails.From(head),
            };
        }

        public override int StoredWidth => _width;

        public override int StoredHeight => _height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => HeifImageReader.NaturalLayout(_head);

        public override RasterMetadata Metadata => _metadata;

        public override bool MetadataComplete => _image is not null;

        public override int? FrameCount => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => _item.Length + HeifImageReader.WorkingBytes(_head);

        public override bool CanPreview
        {
            get
            {
                if (_image is not null)
                {
                    return false;
                }

                Pull();
                DecodeArrived();
                return _preview is not null;
            }
        }

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => Layout switch
        {
            _ when Layout == Gray8.Layout => visitor.Visit<Gray8>(),
            _ when Layout == Rgb8.Layout => visitor.Visit<Rgb8>(),
            _ when Layout == Gray16.Layout => visitor.Visit<Gray16>(),
            _ => visitor.Visit<Rgb16>(),
        };

        public override FrameMetadata? NextFrame()
        {
            if (_started)
            {
                return null;
            }

            _started = true;
            return new FrameMetadata();
        }

        public override void PreviewRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            HeifImageReader.PixelRows rows = _preview ?? throw new InvalidOperationException("No layer has arrived yet.");
            HeifImageReader.ReadRow(rows, Layout, y, x, new int[destination.Length * rows.Channels], new byte[destination.Length * Layout.BytesPerPixel], destination);
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            _input.CancellationToken.ThrowIfCancellationRequested();
            HeifImageReader.Prepared image = _image ?? Finish();
            HeifImageReader.ReadRow(image.Rows, image.Natural, y, x, new int[destination.Length * image.Rows.Channels],
                new byte[destination.Length * image.Natural.BytesPerPixel], destination);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                _stream.Reset();
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

        /// <summary>Decodes the layers whose bytes have all arrived, and makes the preview of the last one shown.</summary>
        private void DecodeArrived()
        {
            int wanted = _options is AvifReaderOptions { Layer: int last } ? last + 1 : int.MaxValue;
            int copied = Copy();
            while (_decodedLayers < _layers.Count && _layers[_decodedLayers].Offset + _layers[_decodedLayers].Length <= copied && _shownLayers < wanted)
            {
                (int offset, int length) = _layers[_decodedLayers++];
                foreach ((Av1Frame frame, _) in _stream.DecodeUnit(_item, offset, length, _input.CancellationToken))
                {
                    if (_shownLayers++ >= wanted)
                    {
                        break;
                    }

                    HeifPlanes planes = _codec.Planes(frame);
                    if ((planes.Width, planes.Height) != (_head.Width, _head.Height))
                    {
                        planes = AvifCodec.Scale(planes, _head.Width, _head.Height, _input);
                    }

                    ColorCodePoints points = _head.Nclx is null ? _codec.CodePoints : _head.CodePoints;
                    _preview = Still.PlaneRows(_head, planes, points);
                }
            }
        }

        /// <summary>Copies the item's bytes that have arrived, extent by extent, and gives how many lead the item without a gap.</summary>
        private int Copy()
        {
            int at = 0;
            foreach (HeifExtent extent in _head.Cells[0].Extents)
            {
                int length = (int)Math.Clamp(_received - extent.Offset, 0, extent.Length);
                if (length > 0)
                {
                    _bytes.AsSpan((int)extent.Offset, length).CopyTo(_item.AsSpan(at));
                }

                at += length;
                if (length < extent.Length)
                {
                    break;
                }
            }

            return at;
        }

        /// <summary>Waits for the last byte, then reads the whole file as Load does.</summary>
        private HeifImageReader.Prepared Finish()
        {
            Pull();
            if (!_input.AtEndOfInput || _input.Available > 0)
            {
                throw new NeedsInputException();
            }

            AvifFile file = AvifFile.Open(HeifFile.Parse(_bytes.AsMemory(0, _received), _input.FormatName));
            HeifImageReader.Prepared image = Still.PrepareStill(_input, file, _options);
            HeifImageReader.Adopt(_metadata, image.Metadata);
            _image = image;
            _preview = null;
            return image;
        }
    }
}
