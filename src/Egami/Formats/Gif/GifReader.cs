using System.Buffers;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Gif;

/// <summary>
/// Reads GIF87a and GIF89a files, written from the GIF89a specification. Frames are LZW-decoded into a pooled index buffer and
/// drawn onto a shared <see cref="AnimationCanvas{TCanvas}"/>, which every kept frame is copied out of at full size.
/// </summary>
internal sealed class GifReader : IFormatReader
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

        internal void Run()
        {
            ReadScreen();
            (int first, int end) = KeptRange();
            while (true)
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
                    break;
                }

                if (block == 0x3B)
                {
                    break;
                }

                try
                {
                    if (block == 0x21)
                    {
                        ReadExtension();
                    }
                    else if (block == 0x2C)
                    {
                        if (!ReadImage(at, first, end))
                        {
                            break;
                        }

                        if (!_probe && FramesSeen >= end)
                        {
                            // Every frame asked for is decoded; the rest of the file is not needed.
                            break;
                        }
                    }
                    else
                    {
                        Problem(at, $"Unknown block 0x{block:X2} where a block introducer was expected.");
                        break;
                    }
                }
                catch (CorruptDataException) when (!_strict && _input.Peek(1).IsEmpty)
                {
                    // The file ends inside a block's header: keep what came before it.
                    Problem(at, "The file ends inside a block.");
                    break;
                }
            }

            if (!_probe && _frames.Count == 0)
            {
                throw _input.Corrupt(_input.Position, FramesSeen == 0 ? "The GIF has no image." : "No frame is in the requested range.");
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

        public void Dispose()
        {
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
            _details.PixelAspectRatio = header[12];
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
                    _details.RepeatCount = repeats;
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

            if (index >= first && index < end)
            {
                long frameBytes = (long)Width * Height * TPixel.Layout.BytesPerPixel;
                _input.Memory.Check(frameBytes);
                PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(Width, Height, _input.Memory);
                _canvas.CopyTo(plane);
                FrameMetadata metadata = new()
                {
                    Delay = delay,
                    Disposal = disposal,
                    Blend = FrameBlend.Over,
                    Bounds = new Rect(left, top, width, height),
                    FormatDetails = new GifFrameDetails { LocalPalette = local, TransparentIndex = transparent >= 0 ? transparent : null, IsInterlaced = interlaced },
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

            Metadata.Warnings.Add(message);
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
