using System.Buffers;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Quantization;
using Egami.Streaming;

namespace Egami.Formats.Gif;

/// <summary>Where an animated GIF's palettes come from.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="gif"/>
/// </example>
public enum GifPaletteScope
{
    /// <summary>One global table when all frames together have few enough colors, else a table per frame.</summary>
    Auto,

    /// <summary>One global table built from every frame.</summary>
    Global,

    /// <summary>A local table for every frame.</summary>
    PerFrame,
}

/// <summary>Options for writing GIF.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="gif"/>
/// </example>
public sealed class GifWriterOptions : WriterOptions
{
    /// <summary>Gets the default options.</summary>
    public static new GifWriterOptions Default { get; } = new();

    /// <summary>Gets how palettes are built when the frames have more colors than fit. Defaults to <see cref="PaletteOptions.Default"/>.</summary>
    public PaletteOptions Palette { get; init; } = PaletteOptions.Default;

    /// <summary>Gets where an animation's palettes come from. Defaults to <see cref="GifPaletteScope.Auto"/>.</summary>
    public GifPaletteScope PaletteScope { get; init; }

    /// <summary>
    /// Gets whether each frame after the first stores only the rectangle that changed, with unchanged pixels transparent where the
    /// palette has room. Defaults to <see langword="true"/>; <see langword="false"/> writes every frame whole.
    /// </summary>
    public bool OptimizeFrames { get; init; } = true;
}

/// <summary>
/// Writes GIF89a files from the GIF89a specification. Every frame is mapped to a palette (exactly when its colors fit), and the
/// frames after the first are compared with what a viewer shows at that point: only the changed rectangle is written, unchanged
/// pixels inside it become the transparent index, and a frame whose successor must make pixels transparent again is disposed of
/// by clearing. Bounds, disposal and blend read from a file are never reused, since edits make them wrong.
/// </summary>
internal sealed class GifWriter : IFormatWriter, IRowFormatWriter
{
    internal static readonly GifWriter Instance = new();

    public void Write<TPixel>(Raster<TPixel> raster, Stream output, WriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        RowEncoding.Save(this, raster, output, options);
    }

    public RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        GifWriterOptions gif = start.Options as GifWriterOptions ?? new GifWriterOptions { StripMetadata = start.Options.StripMetadata };
        if (start.Width > ushort.MaxValue || start.Height > ushort.MaxValue)
        {
            throw new ArgumentException($"GIF images are at most 65,535 × 65,535; this one is {start.Width} × {start.Height}.");
        }

        if (gif.PaletteScope == GifPaletteScope.Global)
        {
            start.Refuse("GIF", "A global palette built from every frame", "Use PaletteScope Auto or PerFrame, which give each frame of an animation its own palette.");
        }

        return new Encoding<TPixel>(output, start, gif);
    }

    /// <summary>
    /// One encode: the planned frames, their palettes and indices, and the canvas a viewer would show. Save plans from the whole
    /// raster; a streamed encode keeps the last two frames it was given, since each frame is written once the next is known.
    /// </summary>
    private sealed class Encoding<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Raster<TPixel>? _raster;
        private readonly Stream _output;
        private readonly GifWriterOptions _options;
        private readonly RasterMetadata _metadata;
        private readonly IPixelMemorySource _memory;
        private readonly int _parallelism;
        private readonly int _width;
        private readonly int _height;
        private readonly int _pixels;
        private readonly Rgba8[] _row;
        private readonly StreamBufferWriter _buffer;
        private readonly LzwEncoder _lzw = new();
        private readonly Dictionary<int, Task<Frame>> _pending = [];
        private readonly Frame<TPixel>?[] _held = new Frame<TPixel>?[2];
        private readonly PixelPlane<TPixel>?[] _heldPlanes = new PixelPlane<TPixel>?[2];
        private readonly int[] _heldIndex = [-1, -1];
        private BuiltPalette? _global;
        private PaletteMapper? _globalMapper;
        private PixelPlane<TPixel>? _still;
        private int _stillRows;
        private int _received;
        private bool _animated;
        private bool _optimize;
        private int _reserve;
        private Frame? _current;
        private Rect _bounds;
        private int _index;
        private Rgba8[]? _canvas;
        private Rgba8[]? _after;

        internal Encoding(Stream output, EncodeStart<TPixel> start, GifWriterOptions options)
        {
            _raster = start.Whole;
            _output = output;
            _options = options;
            _metadata = start.Metadata;
            _memory = start.Context.Memory;
            _parallelism = start.Context.MaxParallelism;
            _width = start.Width;
            _height = start.Height;
            _pixels = _width * _height;
            _row = ArrayPool<Rgba8>.Shared.Rent(_width);
            _buffer = new StreamBufferWriter(output);
        }

        public override bool Animates => true;

        /// <summary>Gathers a still image's rows: GIF palettes are built from the whole frame.</summary>
        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            _still ??= PixelPlane.Allocate<TPixel>(_width, _height, _memory);
            row.CopyTo(_still.Row(_stillRows++));
        }

        /// <summary>
        /// Takes the next frame of a streamed encode. The first is held until the second shows the file is animated; from then on
        /// each frame writes the one before, whose disposal depends on it.
        /// </summary>
        public override void WriteFrame(Frame<TPixel> frame)
        {
            int index = _received++;
            Hold(index, frame);
            if (index == 1)
            {
                Begin(animated: true);
                _current = Next(0);
            }

            if (index >= 1)
            {
                Step(Next(index));
            }
        }

        public override IEnumerable<int> WriteWhole(Raster<TPixel> raster)
        {
            int frames = raster.FrameCount;
            Begin(animated: frames > 1);
            _current = Next(0);

            // Each frame is planned from the next one: a frame is cleared after showing when its successor needs transparency
            // where it was opaque.
            for (int i = 0; i < frames; i++)
            {
                Step(i + 1 < frames ? Next(i + 1) : null);
                yield return i;
            }
        }

        public override void Complete()
        {
            if (_raster is null)
            {
                if (_still is not null)
                {
                    _held[0] = new Frame<TPixel>(_still, new FrameMetadata());
                    _heldIndex[0] = 0;
                    _received = 1;
                }

                if (_received == 1)
                {
                    Begin(animated: false);
                    _current = Next(0);
                }

                Step(null);
            }

            _buffer.Flush();
            _output.WriteByte(0x3B);
        }

        protected override void Dispose(bool disposing)
        {
            foreach (Task<Frame> task in _pending.Values)
            {
                try
                {
                    task.GetAwaiter().GetResult().Dispose();
                }
                catch (Exception)
                {
                    // The encode already failed; this frame is not needed.
                }
            }

            _current?.Dispose();
            foreach (PixelPlane<TPixel>? plane in _heldPlanes)
            {
                plane?.Dispose();
            }

            _still?.Dispose();
            if (_canvas is not null)
            {
                ArrayPool<Rgba8>.Shared.Return(_canvas);
                ArrayPool<Rgba8>.Shared.Return(_after!);
            }

            ArrayPool<Rgba8>.Shared.Return(_row);
            _lzw.Dispose();
            _buffer.Dispose();
            _globalMapper?.Dispose();
            _global?.Release();
            base.Dispose(disposing);
        }

        /// <summary>Chooses the palettes and writes everything before the first frame.</summary>
        private void Begin(bool animated)
        {
            _animated = animated;
            _optimize = animated && _options.OptimizeFrames;
            PaletteOptions palette = _options.Palette;
            GifDetails? details = _metadata.FormatDetails as GifDetails;

            // A frame that keeps unchanged pixels needs a transparent slot, so quantized palettes leave one free. A streamed
            // animation cannot look at every frame first, so its frames get their own palettes.
            _reserve = _optimize ? 1 : 0;
            bool global = _options.PaletteScope switch
            {
                GifPaletteScope.Global => true,
                GifPaletteScope.PerFrame => !animated,
                _ => !animated || (_raster is not null && FitsOnePalette(palette.MaxColors)),
            };
            if (global)
            {
                _global = WithTransparentSlot(BuildPalette(0, _raster?.FrameCount ?? 1, palette, _reserve), _optimize);
                _globalMapper = new PaletteMapper(_global.Colors, _global.TransparentIndex, withAlpha: false, palette.AlphaThreshold);
            }

            WriteHeader(details);
            if (animated)
            {
                WriteLoop(details);
            }

            if (!_options.StripMetadata)
            {
                WriteMetadata(details);
            }

            _buffer.Flush();

            // The canvas is what a viewer shows before the current frame is drawn.
            _canvas = ArrayPool<Rgba8>.Shared.Rent(_pixels);
            _after = ArrayPool<Rgba8>.Shared.Rent(_pixels);
            _canvas.AsSpan(0, _pixels).Clear();
            _bounds = new(0, 0, _width, _height);
        }

        /// <summary>Writes the current frame, now that its successor (<see langword="null"/> at the end) is known.</summary>
        private void Step(Frame? next)
        {
            Rgba8[] canvas = _canvas!;
            Rgba8[] after = _after!;
            Frame current = _current!;
            Rect bounds = _bounds;
            canvas.AsSpan(0, _pixels).CopyTo(after);
            Draw(current, bounds, after);
            FrameDisposal disposal = FrameDisposal.None;
            Rect nextBounds = new(0, 0, _width, _height);
            if (next is not null && NeedsClearing(next, after))
            {
                // Clearing this frame's rectangle must be enough; when it is not, this frame covers everything. Either way the
                // next frame now draws on cleared pixels outside the area it changed, so it is mapped over the whole screen.
                disposal = FrameDisposal.ClearToBackground;
                Clear(after, bounds);
                next = Remap(next);
                if (NeedsClearing(next, after))
                {
                    bounds = new Rect(0, 0, _width, _height);
                    after.AsSpan(0, _pixels).Clear();
                    current = _current = Remap(current);
                }
            }

            if (next is not null && _optimize)
            {
                nextBounds = Changed(next, after);
            }

            WriteFrame(_output, current, bounds, disposal, _optimize && _index > 0 ? canvas : null);
            after.AsSpan(0, _pixels).CopyTo(canvas);
            current.Dispose();
            _current = next;
            _bounds = nextBounds;
            _index++;
        }

        /// <summary>Keeps a copy of a streamed frame, in the slot of the frame two before it.</summary>
        private void Hold(int index, Frame<TPixel> frame)
        {
            int slot = index & 1;
            PixelPlane<TPixel> plane = _heldPlanes[slot] ??= PixelPlane.Allocate<TPixel>(_width, _height, _memory);
            for (int y = 0; y < _height; y++)
            {
                frame.Row(y).CopyTo(plane.Row(y));
            }

            _held[slot] = new Frame<TPixel>(plane, frame.Metadata.Copy());
            _heldIndex[slot] = index;
        }

        /// <summary>Returns source frame <paramref name="index"/>: the raster's, or a held copy.</summary>
        private Frame<TPixel> Source(int index)
        {
            if (_raster is not null)
            {
                return _raster.Frames[index];
            }

            int slot = index & 1;
            return _heldIndex[slot] == index ? _held[slot]! : throw new InvalidOperationException($"GIF frame {index} is no longer held.");
        }

        /// <summary>Returns whether every frame together has at most <paramref name="limit"/> colors (transparency counting as one).</summary>
        private bool FitsOnePalette(int limit)
        {
            ExactColors exact = new(limit, binaryAlpha: true, _options.Palette.AlphaThreshold);
            for (int f = 0; f < _raster!.FrameCount && !exact.Overflowed; f++)
            {
                for (int y = 0; y < _height && !exact.Overflowed; y++)
                {
                    exact.Add(Row(f, y));
                }
            }

            bool fits = !exact.Overflowed;
            exact.Release();
            return fits;
        }

        /// <summary>Builds a frame's own palette from the pixels of <paramref name="region"/>.</summary>
        private BuiltPalette BuildPalette(int index, Rect region, PaletteOptions options, int reserve)
        {
            Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(region.Width);
            try
            {
                return PaletteBuilder.Build(region.Height, r =>
                {
                    PixelConvert.Convert<TPixel, Rgba8>(Source(index).Row(region.Y + r).Slice(region.X, region.Width), row.AsSpan(0, region.Width));
                    return row.AsMemory(0, region.Width);
                }, options, binaryAlpha: true, reserve);
            }
            finally
            {
                ArrayPool<Rgba8>.Shared.Return(row);
            }
        }

        private BuiltPalette BuildPalette(int first, int count, PaletteOptions options, int reserve)
        {
            // The builder is done with each row before asking for the next, so one buffer serves them all.
            Rgba8[] row = ArrayPool<Rgba8>.Shared.Rent(_width);
            try
            {
                return PaletteBuilder.Build(count * _height, r =>
                {
                    PixelConvert.Convert<TPixel, Rgba8>(Source(first + (r / _height)).Row(r % _height), row.AsSpan(0, _width));
                    return row.AsMemory(0, _width);
                }, options, binaryAlpha: true, reserve);
            }
            finally
            {
                ArrayPool<Rgba8>.Shared.Return(row);
            }
        }

        /// <summary>Adds a transparent entry to an exact palette that has room, so unchanged pixels can be skipped.</summary>
        private static BuiltPalette WithTransparentSlot(BuiltPalette palette, bool optimize)
        {
            if (!optimize || palette.TransparentIndex >= 0 || palette.Colors.Length >= 256)
            {
                return palette;
            }

            return palette with { Colors = [.. palette.Colors, default], TransparentIndex = palette.Colors.Length };
        }

        /// <summary>
        /// Maps frame <paramref name="index"/> to its palette. With optimized frames only the area whose source pixels differ from
        /// the frame before is mapped (and gives a frame's own palette its colors); elsewhere the viewer keeps showing what the
        /// frames before left there, which is what those pixels were mapped to then.
        /// </summary>
        private Frame Prepare(int index, PaletteOptions options, int reserve, bool optimize, bool whole = false)
        {
            Rect region = whole || !optimize || index == 0 ? new Rect(0, 0, _width, _height) : SourceChanged(index);
            Frame<TPixel> source = Source(index);
            BuiltPalette palette = _global ?? WithTransparentSlot(BuildPalette(index, region, options, reserve), optimize);
            byte[] indices = ArrayPool<byte>.Shared.Rent(_pixels);
            byte[] mapped = ArrayPool<byte>.Shared.Rent(region.Width * region.Height);
            PaletteMapper mapper = _globalMapper ?? new(palette.Colors, palette.TransparentIndex, withAlpha: false, _options.Palette.AlphaThreshold);
            try
            {
                FrameMapper.Map(region.Width, region.Height, (y, row) => PixelConvert.Convert<TPixel, Rgba8>(source.Row(region.Y + y).Slice(region.X, region.Width), row), palette, mapper, _options.Palette.Dither, mapped.AsSpan(0, region.Width * region.Height));
                for (int y = 0; y < region.Height; y++)
                {
                    mapped.AsSpan(y * region.Width, region.Width).CopyTo(indices.AsSpan(((region.Y + y) * _width) + region.X));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(mapped);
                if (_globalMapper is null)
                {
                    mapper.Dispose();
                }
            }

            return new Frame(index, palette, indices, source.Metadata.Delay, region, ownsPalette: _global is null);
        }

        /// <summary>Maps a frame again over the whole screen.</summary>
        private Frame Remap(Frame frame)
        {
            if (frame.Region == new Rect(0, 0, _width, _height))
            {
                return frame;
            }

            frame.Dispose();
            return Prepare(frame.Index, _options.Palette, _reserve, _optimize, whole: true);
        }

        /// <summary>Returns the bounding box of the source pixels that differ from the frame before (at least 1 × 1).</summary>
        private Rect SourceChanged(int index)
        {
            Frame<TPixel> previous = Source(index - 1);
            Frame<TPixel> frame = Source(index);
            int size = System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>();
            int left = _width;
            int top = -1;
            int right = -1;
            int bottom = -1;
            for (int y = 0; y < _height; y++)
            {
                ReadOnlySpan<byte> a = System.Runtime.InteropServices.MemoryMarshal.AsBytes(previous.Row(y));
                ReadOnlySpan<byte> b = System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.Row(y));
                int first = a.CommonPrefixLength(b);
                if (first == a.Length)
                {
                    continue;
                }

                int last = a.Length - 1;
                while (a[last] == b[last])
                {
                    last--;
                }

                left = Math.Min(left, first / size);
                right = Math.Max(right, last / size);
                top = top < 0 ? y : top;
                bottom = y;
            }

            return right < 0 ? new Rect(0, 0, 1, 1) : new Rect(left, top, right - left + 1, bottom - top + 1);
        }

        /// <summary>
        /// Returns frame <paramref name="index"/> mapped to its palette. With threads to spare, the frames after it are mapped on
        /// other threads meanwhile, a few ahead, since each frame's palette and dithering do not depend on the others.
        /// </summary>
        private Frame Next(int index)
        {
            PaletteOptions options = _options.Palette;
            int reserve = _reserve;
            bool optimize = _optimize;
            int ahead = _raster is null ? 1 : Math.Min(_parallelism, 8);
            if (ahead <= 1)
            {
                return Prepare(index, options, reserve, optimize);
            }

            for (int k = index; k < Math.Min(_raster!.FrameCount, index + ahead); k++)
            {
                if (!_pending.ContainsKey(k))
                {
                    int frame = k;
                    _pending[k] = Task.Run(() => Prepare(frame, options, reserve, optimize));
                }
            }

            Task<Frame> task = _pending[index];
            _pending.Remove(index);
            return task.GetAwaiter().GetResult();
        }

        private ReadOnlySpan<Rgba8> Row(int frame, int y)
        {
            PixelConvert.Convert<TPixel, Rgba8>(Source(frame).Row(y), _row.AsSpan(0, _width));
            return _row.AsSpan(0, _width);
        }

        /// <summary>The color a viewer shows for index <paramref name="index"/>: transparent for the transparent index.</summary>
        private static Rgba8 Shown(Frame frame, int index)
        {
            return index == frame.Palette.TransparentIndex ? default : frame.Palette.Colors[index];
        }

        /// <summary>Draws a frame's rectangle onto the canvas as a viewer would (transparent pixels leave it unchanged).</summary>
        private void Draw(Frame frame, Rect bounds, Rgba8[] canvas)
        {
            bounds = Intersect(bounds, frame.Region);
            for (int y = bounds.Y; y < bounds.Y + bounds.Height; y++)
            {
                for (int x = bounds.X; x < bounds.X + bounds.Width; x++)
                {
                    int index = frame.Indices[(y * _width) + x];
                    if (index != frame.Palette.TransparentIndex)
                    {
                        canvas[(y * _width) + x] = frame.Palette.Colors[index];
                    }
                }
            }
        }

        /// <summary>Returns whether <paramref name="frame"/> has a transparent pixel where the canvas is not transparent.</summary>
        private bool NeedsClearing(Frame frame, Rgba8[] canvas)
        {
            int transparent = frame.Palette.TransparentIndex;
            if (transparent < 0)
            {
                return false;
            }

            Rect region = frame.Region;
            for (int y = region.Y; y < region.Y + region.Height; y++)
            {
                for (int p = (y * _width) + region.X; p < (y * _width) + region.X + region.Width; p++)
                {
                    if (frame.Indices[p] == transparent && canvas[p].A != 0)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>Returns the bounding box of the pixels <paramref name="frame"/> shows differently from the canvas (at least 1 × 1).</summary>
        private Rect Changed(Frame frame, Rgba8[] canvas)
        {
            Rect region = frame.Region;
            int left = _width;
            int top = _height;
            int right = -1;
            int bottom = -1;
            for (int y = region.Y; y < region.Y + region.Height; y++)
            {
                for (int x = region.X; x < region.X + region.Width; x++)
                {
                    Rgba8 shown = Shown(frame, frame.Indices[(y * _width) + x]);
                    Rgba8 under = canvas[(y * _width) + x];
                    bool same = shown.A == 0 ? under.A == 0 : shown == under;
                    if (!same)
                    {
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        top = Math.Min(top, y);
                        bottom = Math.Max(bottom, y);
                    }
                }
            }

            return right < 0 ? new Rect(region.X, region.Y, 1, 1) : new Rect(left, top, right - left + 1, bottom - top + 1);
        }

        private static Rect Intersect(Rect a, Rect b)
        {
            int left = Math.Max(a.X, b.X);
            int top = Math.Max(a.Y, b.Y);
            int right = Math.Min(a.X + a.Width, b.X + b.Width);
            int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
            return right <= left || bottom <= top ? default : new Rect(left, top, right - left, bottom - top);
        }

        private void Clear(Rgba8[] canvas, Rect bounds)
        {
            for (int y = bounds.Y; y < bounds.Y + bounds.Height; y++)
            {
                canvas.AsSpan((y * _width) + bounds.X, bounds.Width).Clear();
            }
        }

        private void WriteHeader(GifDetails? details)
        {
            Span<byte> header = _buffer.GetSpan(13);
            "GIF89a"u8.CopyTo(header);
            header[6] = (byte)_width;
            header[7] = (byte)(_width >> 8);
            header[8] = (byte)_height;
            header[9] = (byte)(_height >> 8);
            int bits = _global?.Bits ?? 1;
            header[10] = (byte)(_global is null ? 0 : 0x80 | ((bits - 1) << 4) | (bits - 1));
            header[11] = _global is not null && details is not null && details.BackgroundIndex < _global.Colors.Length ? details.BackgroundIndex : (byte)0;
            header[12] = details?.PixelAspectRatio is double ratio and > 0 ? (byte)Math.Clamp(Math.Round((ratio * 64) - 15), 1, 255) : (byte)0;
            _buffer.Advance(13);
            if (_global is not null)
            {
                WriteColorTable(_global, bits);
            }
        }

        private void WriteColorTable(BuiltPalette palette, int bits)
        {
            int entries = 1 << bits;
            Span<byte> table = _buffer.GetSpan(entries * 3)[..(entries * 3)];
            table.Clear();
            for (int i = 0; i < palette.Colors.Length; i++)
            {
                table[i * 3] = palette.Colors[i].R;
                table[(i * 3) + 1] = palette.Colors[i].G;
                table[(i * 3) + 2] = palette.Colors[i].B;
            }

            _buffer.Advance(entries * 3);
        }

        /// <summary>Writes the NETSCAPE2.0 loop block: total plays become repeats after the first, 0 staying forever.</summary>
        private void WriteLoop(GifDetails? details)
        {
            int plays = _metadata.LoopCount;
            if (plays == 1 && details?.RepetitionCount is null)
            {
                // One play is what a file without the block means.
                return;
            }

            int repeats = plays == 0 ? 0 : Math.Min(plays - 1, ushort.MaxValue);
            if (details?.RepetitionCount is int raw && (raw == 0 ? 0 : raw + 1) == plays)
            {
                repeats = raw;
            }

            _buffer.Write<byte>([0x21, 0xFF, 11]);
            _buffer.Write("NETSCAPE2.0"u8);
            _buffer.Write<byte>([3, 1, (byte)repeats, (byte)(repeats >> 8), 0]);
        }

        private void WriteMetadata(GifDetails? details)
        {
            RasterMetadata metadata = _metadata;
            foreach (TextEntry entry in metadata.Text)
            {
                if (entry.Key == "Comment")
                {
                    _buffer.Write<byte>([0x21, 0xFE]);
                    WriteSubBlocks(Encoding.UTF8.GetBytes(entry.Value));
                }
            }

            if (!metadata.Xmp.IsEmpty)
            {
                // XMP's GIF embedding: the packet raw, then a "magic trailer" that makes any sub-block walk land on the terminator.
                _buffer.Write<byte>([0x21, 0xFF, 11]);
                _buffer.Write("XMP DataXMP"u8);
                _buffer.Write(metadata.Xmp.Span);
                Span<byte> trailer = _buffer.GetSpan(258)[..258];
                trailer[0] = 1;
                for (int i = 0; i < 256; i++)
                {
                    trailer[1 + i] = (byte)(255 - i);
                }

                trailer[257] = 0;
                _buffer.Advance(258);
            }

            if (!metadata.IccProfile.IsEmpty)
            {
                _buffer.Write<byte>([0x21, 0xFF, 11]);
                _buffer.Write("ICCRGBG1012"u8);
                WriteSubBlocks(metadata.IccProfile.Span);
            }

            foreach (GifApplicationExtension extension in details?.OtherExtensions ?? [])
            {
                byte[] identifier = Encoding.ASCII.GetBytes(extension.Identifier);
                if (identifier.Length != 11)
                {
                    continue;
                }

                _buffer.Write<byte>([0x21, 0xFF, 11]);
                _buffer.Write(identifier);
                WriteSubBlocks(extension.Data.Span);
            }
        }

        private void WriteSubBlocks(ReadOnlySpan<byte> data)
        {
            while (!data.IsEmpty)
            {
                int size = Math.Min(255, data.Length);
                _buffer.Write<byte>([(byte)size]);
                _buffer.Write(data[..size]);
                data = data[size..];
            }

            _buffer.Write<byte>([0]);
        }

        /// <summary>
        /// Writes one frame: its graphic control extension, descriptor, local table and LZW data. Where <paramref name="under"/> is
        /// given, pixels that already show the right color become the transparent index.
        /// </summary>
        private void WriteFrame(Stream output, Frame frame, Rect bounds, FrameDisposal disposal, Rgba8[]? under)
        {
            BuiltPalette palette = frame.Palette;
            int transparent = palette.TransparentIndex;
            byte[] rect = ArrayPool<byte>.Shared.Rent(bounds.Width * bounds.Height);
            try
            {
                bool usesTransparent = false;
                for (int y = 0; y < bounds.Height; y++)
                {
                    for (int x = 0; x < bounds.Width; x++)
                    {
                        int p = ((bounds.Y + y) * _width) + bounds.X + x;
                        int index = frame.Indices[p];
                        if (under is not null && transparent >= 0 && index != transparent && palette.Colors[index] == under[p])
                        {
                            index = transparent;
                        }

                        usesTransparent |= index == transparent;
                        rect[(y * bounds.Width) + x] = (byte)index;
                    }
                }

                int hundredths = (int)Math.Min(ushort.MaxValue, Math.Round(frame.Delay.TotalMilliseconds / 10, MidpointRounding.AwayFromZero));
                if (_animated || usesTransparent)
                {
                    int method = disposal == FrameDisposal.ClearToBackground ? 2 : 1;
                    _buffer.Write<byte>([0x21, 0xF9, 4, (byte)((method << 2) | (usesTransparent ? 1 : 0)), (byte)hundredths, (byte)(hundredths >> 8), (byte)(usesTransparent ? transparent : 0), 0]);
                }

                bool local = _global is null;
                int bits = palette.Bits;
                _buffer.Write<byte>([0x2C, (byte)bounds.X, (byte)(bounds.X >> 8), (byte)bounds.Y, (byte)(bounds.Y >> 8), (byte)bounds.Width, (byte)(bounds.Width >> 8), (byte)bounds.Height, (byte)(bounds.Height >> 8),
                    (byte)(local ? 0x80 | (bits - 1) : 0)]);
                if (local)
                {
                    WriteColorTable(palette, bits);
                }

                _lzw.Encode(Math.Max(2, (_global ?? palette).Bits), rect.AsSpan(0, bounds.Width * bounds.Height), _buffer);
                _buffer.Flush();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rect);
            }
        }

        /// <summary>A frame mapped to its palette.</summary>
        private sealed class Frame(int index, BuiltPalette palette, byte[] indices, TimeSpan delay, Rect region, bool ownsPalette) : IDisposable
        {
            internal int Index => index;

            /// <summary>Gets the area that was mapped; elsewhere the frame shows what the frames before left.</summary>
            internal Rect Region => region;

            internal BuiltPalette Palette => palette;

            internal byte[] Indices => indices;

            internal TimeSpan Delay => delay;

            public void Dispose()
            {
                ArrayPool<byte>.Shared.Return(indices);
                if (ownsPalette)
                {
                    palette.Release();
                }
            }
        }
    }
}
