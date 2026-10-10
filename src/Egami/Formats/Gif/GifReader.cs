using System.Buffers;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Gif;

/// <summary>
/// Reads GIF87a and GIF89a files, written from the GIF89a specification. Frames are LZW-decoded into a pooled index buffer and
/// drawn onto a shared <see cref="AnimationCanvas{TCanvas}"/>, which every kept frame is copied out of at full size.
/// </summary>
internal sealed partial class GifReader : IFormatReader, Streaming.IRowFormatReader, Streaming.IIncrementalFormatReader
{
    internal static readonly GifReader Instance = new();

    private static ReadOnlySpan<byte> XmpTrailerStart => [0x01, 0xFF, 0xFE];

    /// <summary>Returns whether the data starts with "GIF87a" or "GIF89a".</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= 6 && (header[..6].SequenceEqual("GIF87a"u8) || header[..6].SequenceEqual("GIF89a"u8));
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        Decoding<Rgba8> state = new(input, options, probe: true);
        try
        {
            state.Run();
            long canvas = (long)state.Width * state.Height * 4;
            return new RasterInfo
            {
                Format = GifFormat.Info,
                Width = state.Width,
                Height = state.Height,
                Layout = Rgba8.Layout,
                FrameCount = state.FramesSeen,
                HasMetadata = state.Metadata.Text.Count > 0 || !state.Metadata.Xmp.IsEmpty || !state.Metadata.IccProfile.IsEmpty,
                EstimatedBytes = (canvas * state.FramesSeen) + canvas,
                LoopCount = state.Metadata.LoopCount,
                Access = Streams(options) ? RasterAccess.Streamed : RasterAccess.Buffered,
                WorkingBytes = Streams(options) ? WorkingBytesFor(state.Width, state.Height) : 0,
            };
        }
        finally
        {
            state.Dispose();
        }
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Decoding<TPixel> state = new(input, options, probe: false);
        try
        {
            state.Run();
            return state.TakeRaster();
        }
        finally
        {
            state.Dispose();
        }
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        return Read<Rgba8>(input, options);
    }

    /// <summary>One read of a file: the block walk, and for a full read the frames decoded so far.</summary>
    private sealed class Decoding<TPixel> : IDisposable
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly EgamiInput _input;
        private readonly ReaderOptions _options;
        private readonly bool _probe;
        private readonly bool _strict;
        private readonly List<Frame<TPixel>> _frames = [];
        private readonly GifDetails _details = new();
        private readonly Rgba8[] _colors = new Rgba8[256];
        private byte[] _data = ArrayPool<byte>.Shared.Rent(4096);
        private byte[] _indices = [];
        private LzwDecoder? _lzw;
        private AnimationCanvas<Rgba8>? _canvas;
        private Rgb8[] _global = [];

        // Streaming (RasterDecoder): frames are composed one at a time and read from the canvas, not copied out.
        private bool _streaming;
        private bool _pendingEnd;
        private bool _stopped;
        private bool _finished;
        private int _first;
        private int _end;
        private int _served;
        private FrameMetadata? _composed;
        private PixelPlane<Rgba8>? _preview;
        private LzwDecoder? _previewLzw;
        private bool _awaiting = true;

        // The Graphic Control Extension applies to the next image only.
        private bool _hasControl;
        private FrameDisposal _disposal;
        private TimeSpan _delay;
        private int _transparent = -1;

        internal Decoding(EgamiInput input, ReaderOptions options, bool probe)
        {
            _input = input;
            _options = options;
            _probe = probe;
            _strict = options.Recovery == RecoveryMode.Strict;
            Metadata = new RasterMetadata { FormatName = GifFormat.Info.Name, FormatDetails = _details, LoopCount = 1 };
        }

        internal int Width { get; private set; }

        internal int Height { get; private set; }

        internal int FramesSeen { get; private set; }

        internal RasterMetadata Metadata { get; }

        /// <summary>Gets the canvas the current frame is composed on, once a frame has been read.</summary>
        internal AnimationCanvas<Rgba8>? Canvas => _canvas;

        /// <summary>Gets whether the walk has reached its end, so the metadata is complete.</summary>
        internal bool Finished => _finished;

        /// <summary>Starts a streaming read: reads the screen descriptor and global palette, and composes no frame yet.</summary>
        internal void BeginStreaming()
        {
            _streaming = true;
            ReadScreen();
            (_first, _end) = KeptRange();
        }

        /// <summary>
        /// Streaming: ends the frame on the canvas, walks the blocks to the next frame in the range and composes it on the canvas.
        /// Returns its metadata, or <see langword="null"/> after the last.
        /// </summary>
        internal FrameMetadata? NextFrame()
        {
            if (_pendingEnd)
            {
                _canvas!.End();
                _pendingEnd = false;
            }

            _awaiting = true;
            while (!_finished)
            {
                if (!_stopped && !BlockArrived())
                {
                    // An incremental decode reads a block once all of it has arrived.
                    throw new Streaming.NeedsInputException();
                }

                if (_stopped || !Step(_first, _end))
                {
                    _finished = true;
                    if (_served == 0)
                    {
                        throw _input.Corrupt(_input.Position, FramesSeen == 0 ? "The GIF has no image." : "No frame is in the requested range.");
                    }

                    return null;
                }

                if (_composed is FrameMetadata frame)
                {
                    _composed = null;
                    _served++;
                    _awaiting = false;
                    return frame;
                }

                if (_pendingEnd)
                {
                    // A frame before the range: drawn, because later frames build on it, but not handed out.
                    _canvas!.End();
                    _pendingEnd = false;
                }
            }

            return null;
        }

        /// <summary>
        /// Returns whether the next block has arrived whole: the trailer, an extension and its sub-blocks, or an image descriptor,
        /// its color table and its sub-blocks. Always true outside an incremental decode or once the input has ended.
        /// </summary>
        internal bool BlockArrived()
        {
            if (!_input.Incremental || _input.AtEndOfInput)
            {
                return true;
            }

            Span<byte> head = stackalloc byte[10];
            if (!_input.TryPeekAt(0, head[..1]))
            {
                return false;
            }

            switch (head[0])
            {
                case 0x21:
                    return SubBlocksArrived(2);
                case 0x2C:
                    if (!_input.TryPeekAt(0, head))
                    {
                        return false;
                    }

                    int table = (head[9] & 0x80) != 0 ? 3 * (2 << (head[9] & 7)) : 0;
                    return SubBlocksArrived(10L + table + 1);
                default:
                    return true;
            }
        }

        /// <summary>Returns whether the sub-blocks starting <paramref name="offset"/> bytes ahead have arrived, through their terminator.</summary>
        private bool SubBlocksArrived(long offset)
        {
            Span<byte> size = stackalloc byte[1];
            while (true)
            {
                if (!_input.TryPeekAt(offset, size))
                {
                    return false;
                }

                offset += 1 + size[0];
                if (size[0] == 0)
                {
                    return true;
                }

                if (_input.Available < offset)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Looks ahead, without reading, for the next image descriptor: its offset from the read position and the transparent index a
        /// graphic control extension before it gives. Returns <see langword="false"/> if none has started to arrive.
        /// </summary>
        private bool NextImage(out long at, out int transparent)
        {
            at = 0;
            transparent = _hasControl ? _transparent : -1;
            Span<byte> head = stackalloc byte[6];
            Span<byte> size = stackalloc byte[1];
            while (_input.TryPeekAt(at, head[..1]))
            {
                if (head[0] == 0x2C)
                {
                    return _input.TryPeekAt(at, head[..1]) && _input.Available >= at + 11;
                }

                if (head[0] != 0x21 || !_input.TryPeekAt(at, head[..2]))
                {
                    return false;
                }

                if (head[1] == 0xF9 && _input.TryPeekAt(at, head))
                {
                    transparent = (head[3] & 1) != 0 && _input.TryPeekAt(at + 6, head[..1]) ? head[0] : -1;
                }

                // Skip the extension's sub-blocks.
                long offset = at + 2;
                while (true)
                {
                    if (!_input.TryPeekAt(offset, size))
                    {
                        return false;
                    }

                    offset += 1 + size[0];
                    if (size[0] == 0)
                    {
                        break;
                    }
                }

                at = offset;
            }

            return false;
        }

        /// <summary>Returns the image row a stored row of an interlaced frame lands on, and how many rows it stands for until a later pass fills them.</summary>
        private static (int Row, int Rows) InterlacedBlock(int stored, int height)
        {
            int pass1 = (height + 7) / 8;
            int pass2 = (height + 3) / 8;
            int pass3 = (height + 1) / 4;
            int rows = stored < pass1 ? 8 : stored < pass1 + pass2 ? 4 : stored < pass1 + pass2 + pass3 ? 2 : 1;
            return (InterlacedRow(stored, height), rows);
        }

        private static Rgb8[] ReadTable(ReadOnlySpan<byte> bytes)
        {
            Rgb8[] palette = new Rgb8[bytes.Length / 3];
            for (int i = 0; i < palette.Length; i++)
            {
                palette[i] = new Rgb8(bytes[i * 3], bytes[(i * 3) + 1], bytes[(i * 3) + 2]);
            }

            return palette;
        }

        internal void Run()
        {
            ReadScreen();
            (int first, int end) = KeptRange();
            while (Step(first, end))
            {
            }

            if (!_probe && _frames.Count == 0)
            {
                throw _input.Corrupt(_input.Position, FramesSeen == 0 ? "The GIF has no image." : "No frame is in the requested range.");
            }
        }

        /// <summary>Reads one block. Returns false when the walk ends: the trailer, the end of the file, or the last frame wanted.</summary>
        private bool Step(int first, int end)
        {
            {
                long at = _input.Position;
                int block;
                try
                {
                    block = _input.ReadByte();
                }
                catch (CorruptDataException)
                {
                    Problem(at, "The file ends without a trailer.");
                    return false;
                }

                if (block == 0x3B)
                {
                    return false;
                }

                try
                {
                    if (block == 0x21)
                    {
                        ReadExtension();
                    }
                    else if (block == 0x2C)
                    {
                        bool complete = ReadImage(at, first, end);
                        bool last = !_probe && FramesSeen >= end;
                        if (_streaming && (!complete || last))
                        {
                            // The frame just composed is still handed out; the walk ends after it.
                            _stopped = true;
                            return _composed is not null;
                        }

                        if (!complete)
                        {
                            return false;
                        }

                        if (last)
                        {
                            // Every frame asked for is decoded; the rest of the file is not needed.
                            return false;
                        }
                    }
                    else
                    {
                        Problem(at, $"Unknown block 0x{block:X2} where a block introducer was expected.");
                        return false;
                    }
                }
                catch (CorruptDataException) when (!_strict && _input.Peek(1).IsEmpty)
                {
                    // The file ends inside a block's header: keep what came before it.
                    Problem(at, "The file ends inside a block.");
                    return false;
                }

                return true;
            }
        }

        /// <summary>Returns the raster of the frames read, which the caller then owns.</summary>
        internal Raster<TPixel> TakeRaster()
        {
            List<Frame<TPixel>> frames = [.. _frames];
            if (_options.Frames.Start.IsFromEnd || _options.Frames.End.IsFromEnd)
            {
                // A range counted from the end is only known once every frame is read.
                (int offset, int length) = AnimationCanvasRange.Clamp(_options.Frames, frames.Count);
                for (int i = 0; i < frames.Count; i++)
                {
                    if (i < offset || i >= offset + length)
                    {
                        frames[i].Plane.Dispose();
                    }
                }

                frames = frames.GetRange(offset, length);
                if (frames.Count == 0)
                {
                    throw _input.Corrupt(-1, "No frame is in the requested range.");
                }
            }

            _frames.Clear();
            return new Raster<TPixel>(frames, Metadata, _input.Context);
        }

        /// <summary>
        /// Builds a preview of the frame in progress: the canvas as the next frame starts from, with as much of that frame as has
        /// arrived drawn on it, interlaced rows repeated over the rows they stand for. The decode does not move on.
        /// </summary>
        internal PixelPlane<Rgba8> BuildPreview()
        {
            PixelPlane<Rgba8> preview = _preview ??= PixelPlane.Allocate<Rgba8>(Width, Height, _input.Memory, ClearMode.Zero);
            if (_canvas is null)
            {
                for (int y = 0; y < Height; y++)
                {
                    preview.Row(y).Clear();
                }
            }
            else
            {
                for (int y = 0; y < Height; y++)
                {
                    _canvas.Row(y).CopyTo(preview.Row(y));
                }
            }

            // A frame composed and handed out is the frame in progress; only while the next one is awaited is it drawn as it arrives.
            if (!_awaiting || _finished || !NextImage(out long at, out int transparent))
            {
                return preview;
            }

            Span<byte> descriptor = stackalloc byte[10];
            _input.TryPeekAt(at, descriptor);
            int left = descriptor[1] | (descriptor[2] << 8);
            int top = descriptor[3] | (descriptor[4] << 8);
            int width = descriptor[5] | (descriptor[6] << 8);
            int height = descriptor[7] | (descriptor[8] << 8);
            byte packed = descriptor[9];
            bool interlaced = (packed & 0x40) != 0;
            int tableBytes = (packed & 0x80) != 0 ? 3 * (2 << (packed & 7)) : 0;
            Span<byte> table = stackalloc byte[768];
            Span<byte> minCode = stackalloc byte[1];
            if (width == 0 || height == 0 || (long)width * height > int.MaxValue || !_input.TryPeekAt(at + 10, table[..tableBytes])
                || !_input.TryPeekAt(at + 10 + tableBytes, minCode) || minCode[0] is < 1 or > 8)
            {
                return preview;
            }

            // The sub-blocks that have arrived, without their lengths.
            byte[] data = ArrayPool<byte>.Shared.Rent(4096);
            byte[] indices = ArrayPool<byte>.Shared.Rent(width * height);
            try
            {
                int length = 0;
                long offset = at + 11 + tableBytes;
                Span<byte> size = stackalloc byte[1];
                while (_input.TryPeekAt(offset, size) && size[0] > 0)
                {
                    int n = (int)Math.Min(size[0], _input.Available - offset - 1);
                    if (n <= 0)
                    {
                        break;
                    }

                    if (length + n > data.Length)
                    {
                        byte[] larger = ArrayPool<byte>.Shared.Rent(data.Length * 2);
                        data.AsSpan(0, length).CopyTo(larger);
                        ArrayPool<byte>.Shared.Return(data);
                        data = larger;
                    }

                    _input.TryPeekAt(offset + 1, data.AsSpan(length, n));
                    length += n;
                    offset += 1 + size[0];
                }

                _previewLzw ??= new LzwDecoder();
                _previewLzw.Decode(minCode[0], data.AsSpan(0, length), indices.AsSpan(0, width * height), out int written);
                Rgba8[] colors = new Rgba8[256];
                Rgb8[] palette = tableBytes > 0 ? ReadTable(table[..tableBytes]) : _global;
                for (int i = 0; i < palette.Length; i++)
                {
                    colors[i] = new Rgba8(palette[i].R, palette[i].G, palette[i].B, 255);
                }

                int right = Math.Min(left + width, Width);
                for (int r = 0; (r + 1) * width <= written; r++)
                {
                    (int y, int rows) = interlaced ? InterlacedBlock(r, height) : (r, 1);
                    ReadOnlySpan<byte> row = indices.AsSpan(r * width, width);
                    for (int k = 0; k < rows && y + k < height; k++)
                    {
                        int target = top + y + k;
                        if (target >= Height || left >= Width)
                        {
                            continue;
                        }

                        // A transparent pixel shows the canvas under it, also where an earlier pass's row was repeated.
                        Span<Rgba8> line = preview.Row(target);
                        for (int x = 0; x < right - left; x++)
                        {
                            line[left + x] = row[x] != transparent ? colors[row[x]] : _canvas is null ? default : _canvas.Row(target)[left + x];
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(data);
                ArrayPool<byte>.Shared.Return(indices);
            }

            return preview;
        }

        public void Dispose()
        {
            _preview?.Dispose();
            _previewLzw?.Dispose();
            foreach (Frame<TPixel> frame in _frames)
            {
                frame.Plane.Dispose();
            }

            _frames.Clear();
            _canvas?.Dispose();
            _lzw?.Dispose();
            ArrayPool<byte>.Shared.Return(_data);
            if (_indices.Length > 0)
            {
                ArrayPool<byte>.Shared.Return(_indices);
            }
        }

        private (int First, int End) KeptRange()
        {
            Range range = _options.Frames;
            if (range.Start.IsFromEnd || range.End.IsFromEnd)
            {
                return (0, int.MaxValue);
            }

            return (range.Start.Value, range.End.Value);
        }

        private void ReadScreen()
        {
            Span<byte> header = stackalloc byte[13];
            _input.ReadExactly(header);
            if (!Matches(header))
            {
                throw _input.Corrupt(0, "The GIF signature is wrong.");
            }

            _details.Version = Encoding.ASCII.GetString(header[3..6]);
            Width = header[6] | (header[7] << 8);
            Height = header[8] | (header[9] << 8);
            if (Width == 0 || Height == 0)
            {
                throw _input.Corrupt(6, $"The logical screen size {Width}×{Height} is invalid.");
            }

            _input.Limits.CheckFrameSize(Width, Height);
            byte packed = header[10];
            _details.BackgroundIndex = header[11];
            _details.PixelAspectRatio = header[12] == 0 ? null : (header[12] + 15) / 64.0;
            if ((packed & 0x80) != 0)
            {
                _global = ReadPalette(packed & 7);
                _details.GlobalPalette = _global;
            }
        }

        private Rgb8[] ReadPalette(int sizeBits)
        {
            int count = 2 << sizeBits;
            Span<byte> bytes = stackalloc byte[256 * 3];
            _input.ReadExactly(bytes[..(count * 3)]);
            Rgb8[] palette = new Rgb8[count];
            for (int i = 0; i < count; i++)
            {
                palette[i] = new Rgb8(bytes[i * 3], bytes[(i * 3) + 1], bytes[(i * 3) + 2]);
            }

            return palette;
        }

        private void ReadExtension()
        {
            long at = _input.Position - 1;
            byte label = _input.ReadByte();
            switch (label)
            {
                case 0xF9:
                    ReadControl(at);
                    break;
                case 0xFE:
                    {
                        (int length, bool complete) = GifSubBlocks.ReadAll(_input, ref _data);
                        if (_options.ReadMetadata)
                        {
                            Metadata.Text.Add(new TextEntry("Comment", DecodeText(_data.AsSpan(0, length))));
                        }

                        CheckComplete(at, complete);
                        break;
                    }

                case 0xFF:
                    ReadApplication(at);
                    break;
                default:
                    // Plain text and unknown extensions are skipped, as browsers do.
                    CheckComplete(at, GifSubBlocks.Skip(_input));
                    break;
            }
        }

        private void ReadControl(long at)
        {
            (int length, bool complete) = GifSubBlocks.ReadAll(_input, ref _data);
            CheckComplete(at, complete);
            if (length < 4)
            {
                Problem(at, "A graphic control extension is shorter than 4 bytes.");
                return;
            }

            byte packed = _data[0];
            int method = (packed >> 2) & 7;
            _disposal = method switch
            {
                2 => FrameDisposal.ClearToBackground,
                3 => FrameDisposal.RestorePrevious,
                _ => FrameDisposal.None,
            };
            if (method > 3)
            {
                Problem(at, $"Disposal method {method} is undefined; it is read as none.");
            }

            _delay = TimeSpan.FromMilliseconds((_data[1] | (_data[2] << 8)) * 10);
            _transparent = (packed & 1) != 0 ? _data[3] : -1;
            _hasControl = true;
        }

        private void ReadApplication(long at)
        {
            int size = _input.ReadByte();
            Span<byte> identifier = stackalloc byte[255];
            _input.ReadExactly(identifier[..size]);
            string name = Encoding.ASCII.GetString(identifier[..size]);
            if (name == "XMP DataXMP")
            {
                ReadXmp(at);
                return;
            }

            (int length, bool complete) = GifSubBlocks.ReadAll(_input, ref _data);
            CheckComplete(at, complete);
            ReadOnlySpan<byte> data = _data.AsSpan(0, length);
            if (name is "NETSCAPE2.0" or "ANIMEXTS1.0")
            {
                if (length >= 3 && data[0] == 1)
                {
                    int repeats = data[1] | (data[2] << 8);
                    _details.RepetitionCount = repeats;
                    Metadata.LoopCount = repeats == 0 ? 0 : repeats + 1;
                }

                return;
            }

            if (!_options.ReadMetadata)
            {
                return;
            }

            if (name == "ICCRGBG1012")
            {
                CheckMetadataBytes(length);
                Metadata.IccProfile = data.ToArray();
            }
            else
            {
                CheckMetadataBytes(length);
                _details.OtherExtensions.Add(new GifApplicationExtension(name, data.ToArray()));
            }
        }

        /// <summary>
        /// Reads an XMP packet. XMP's GIF embedding writes the packet raw, so its bytes are read as sub-blocks: the sub-block lengths
        /// are packet bytes too, and a "magic trailer" (0x01, then 0xFF down to 0x00, then the terminator) ends it.
        /// </summary>
        private void ReadXmp(long at)
        {
            List<byte> raw = [];
            bool complete = false;
            try
            {
                while (true)
                {
                    int size = _input.ReadByte();
                    raw.Add((byte)size);
                    if (size == 0)
                    {
                        complete = true;
                        break;
                    }

                    CheckMetadataBytes(raw.Count + size);
                    byte[] chunk = new byte[size];
                    _input.ReadExactly(chunk);
                    raw.AddRange(chunk);
                }
            }
            catch (CorruptDataException)
            {
            }

            CheckComplete(at, complete);
            byte[] bytes = [.. raw];
            int start = bytes.AsSpan().LastIndexOf(XmpTrailerStart);
            if (start < 0 || bytes.Length - start != 258)
            {
                Problem(at, "The XMP block lacks its magic trailer.");
                start = Math.Max(0, bytes.Length - 1);
            }

            if (_options.ReadMetadata)
            {
                Metadata.Xmp = bytes.AsSpan(0, start).ToArray();
            }
        }

        /// <summary>Reads one image; returns false when reading must stop.</summary>
        private bool ReadImage(long at, int first, int end)
        {
            Span<byte> descriptor = stackalloc byte[9];
            _input.ReadExactly(descriptor);
            int left = descriptor[0] | (descriptor[1] << 8);
            int top = descriptor[2] | (descriptor[3] << 8);
            int width = descriptor[4] | (descriptor[5] << 8);
            int height = descriptor[6] | (descriptor[7] << 8);
            byte packed = descriptor[8];
            Rgb8[] local = (packed & 0x80) != 0 ? ReadPalette(packed & 7) : [];
            bool interlaced = (packed & 0x40) != 0;
            int index = FramesSeen++;
            _input.Limits.CheckFrameCount(FramesSeen);
            FrameDisposal disposal = _hasControl ? _disposal : FrameDisposal.None;
            TimeSpan delay = _hasControl ? _delay : TimeSpan.Zero;
            int transparent = _hasControl ? _transparent : -1;
            _hasControl = false;

            int minCodeSize;
            try
            {
                minCodeSize = _input.ReadByte();
            }
            catch (CorruptDataException)
            {
                Problem(at, "The file ends inside an image.");
                return false;
            }

            if (_probe)
            {
                bool done = GifSubBlocks.Skip(_input);
                if (!done)
                {
                    Problem(at, "The file ends inside the image data.");
                }

                return done;
            }

            if (minCodeSize is < 1 or > 8)
            {
                throw _input.Corrupt(at, $"The LZW minimum code size {minCodeSize} is invalid.");
            }

            if (minCodeSize == 1)
            {
                Problem(at, "An LZW minimum code size of 1 is outside the specification.");
            }

            // Frames before the range are still drawn, because later frames build on them, but not kept.
            (int length, bool complete) = GifSubBlocks.ReadAll(_input, ref _data);
            Rect bounds = Clip(at, left, top, width, height);
            _canvas ??= new AnimationCanvas<Rgba8>(Width, Height, _input.Memory);
            _canvas.Begin(bounds, disposal);
            int decoded = width * height > 0 ? Draw(at, _data.AsSpan(0, length), minCodeSize, left, top, width, height, interlaced,
                local.Length > 0 ? local : _global, transparent) : 0;
            if (!complete || decoded < width * height)
            {
                Problem(at, "The image data ends early.");
            }

            if (_streaming)
            {
                _pendingEnd = true;
                if (index >= first && index < end)
                {
                    _composed = new FrameMetadata
                    {
                        Delay = delay,
                        Disposal = disposal,
                        Blend = FrameBlend.Over,
                        Bounds = new Rect(left, top, width, height),
                        FormatDetails = new GifFrameDetails { LocalPalette = local, TransparentIndex = transparent >= 0 ? transparent : null, Interlaced = interlaced },
                    };
                }

                return complete;
            }

            if (index >= first && index < end)
            {
                long frameBytes = (long)Width * Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>();
                _input.Memory.Check(frameBytes);
                PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(Width, Height, _input.Memory);
                _canvas.CopyTo(plane);
                FrameMetadata metadata = new()
                {
                    Delay = delay,
                    Disposal = disposal,
                    Blend = FrameBlend.Over,
                    Bounds = new Rect(left, top, width, height),
                    FormatDetails = new GifFrameDetails { LocalPalette = local, TransparentIndex = transparent >= 0 ? transparent : null, Interlaced = interlaced },
                };
                _frames.Add(new Frame<TPixel>(plane, metadata));
            }

            _canvas.End();
            return complete;
        }

        /// <summary>Returns the part of a frame inside the logical screen; a frame sticking out is clipped (Lenient) or corrupt (Strict).</summary>
        private Rect Clip(long at, int left, int top, int width, int height)
        {
            int right = Math.Min(left + width, Width);
            int bottom = Math.Min(top + height, Height);
            if (left + width > Width || top + height > Height)
            {
                Problem(at, $"Frame {FramesSeen - 1} ({left}, {top}, {width}×{height}) sticks out of the {Width}×{Height} logical screen.");
            }

            return left >= Width || top >= Height ? default : new Rect(left, top, right - left, bottom - top);
        }

        /// <summary>Decodes a frame's indices and draws them on the canvas; returns how many pixels were decoded.</summary>
        private int Draw(long at, ReadOnlySpan<byte> data, int minCodeSize, int left, int top, int width, int height, bool interlaced, Rgb8[] palette, int transparent)
        {
            _input.Limits.CheckFrameSize(width, height);
            int pixels = width * height;
            if (_indices.Length < pixels)
            {
                if (_indices.Length > 0)
                {
                    ArrayPool<byte>.Shared.Return(_indices);
                }

                _indices = ArrayPool<byte>.Shared.Rent(pixels);
            }

            _lzw ??= new LzwDecoder();
            LzwStatus status = _lzw.Decode(minCodeSize, data, _indices.AsSpan(0, pixels), out int written);
            if (status == LzwStatus.BadCode)
            {
                throw _input.Corrupt(at, "The LZW data has a code that cannot exist.");
            }

            if (palette.Length == 0)
            {
                Problem(at, "A frame has no color table; it is drawn transparent.");
            }

            for (int i = 0; i < palette.Length; i++)
            {
                _colors[i] = new Rgba8(palette[i].R, palette[i].G, palette[i].B, 255);
            }

            Array.Clear(_colors, palette.Length, 256 - palette.Length);
            bool outOfRange = false;
            int right = Math.Min(left + width, Width);
            for (int r = 0; r * width < written; r++)
            {
                int y = top + (interlaced ? InterlacedRow(r, height) : r);
                if (y >= Height || left >= Width)
                {
                    continue;
                }

                ReadOnlySpan<byte> row = _indices.AsSpan(r * width, Math.Min(width, written - (r * width)));
                Span<Rgba8> target = _canvas!.Row(y);
                int count = Math.Min(row.Length, right - left);
                for (int x = 0; x < count; x++)
                {
                    int index = row[x];
                    if (index == transparent)
                    {
                        continue;
                    }

                    outOfRange |= index >= palette.Length;
                    target[left + x] = _colors[index];
                }
            }

            if (outOfRange && palette.Length > 0)
            {
                Problem(at, "A frame uses color indices past its color table; they are drawn transparent.");
            }

            return written;
        }

        /// <summary>Returns the image row that the <paramref name="stored"/>-th stored row of an interlaced image belongs to.</summary>
        private static int InterlacedRow(int stored, int height)
        {
            int pass1 = (height + 7) / 8;
            if (stored < pass1)
            {
                return stored * 8;
            }

            stored -= pass1;
            int pass2 = (height + 3) / 8;
            if (stored < pass2)
            {
                return 4 + (stored * 8);
            }

            stored -= pass2;
            int pass3 = (height + 1) / 4;
            return stored < pass3 ? 2 + (stored * 4) : 1 + ((stored - pass3) * 2);
        }

        private void CheckComplete(long at, bool complete)
        {
            if (!complete)
            {
                Problem(at, "The file ends inside a block.");
            }
        }

        private void CheckMetadataBytes(long bytes)
        {
            _input.Limits.CheckMetadataBytes(bytes);
        }

        /// <summary>Notes a problem as a warning (Lenient) or throws (Strict).</summary>
        private void Problem(long at, string message)
        {
            if (_strict)
            {
                throw _input.Corrupt(at, message);
            }

            Metadata.WarningList.Add(message);
        }

        private static string DecodeText(ReadOnlySpan<byte> bytes)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }
    }
}
