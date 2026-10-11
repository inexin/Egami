using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Jpeg2000;

/// <content>
/// Streaming: a row of tiles is decoded at a time and only its samples are held, at the resolution the plan asks for (dropping
/// whole wavelet levels is JPEG 2000's native reduction), and a region decodes only the tiles and code-blocks it needs.
/// </content>
internal sealed partial class Jpeg2000Reader : IRowSourceReader
{
    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
        try
        {
            Jp2File file = Jp2File.Parse(data.AsMemory(0, length));
            J2kCodestream stream = J2kCodestream.Parse(file.Codestream, input.Limits);
            if (stream.Truncated && options.Recovery == RecoveryMode.Strict)
            {
                throw new CorruptDataException(input.FormatName, -1, "The JPEG 2000 codestream ends before its last packet.") { IsTruncation = true };
            }

            return new Jpeg2000Rows(input, data, pooled, file, stream, options);
        }
        catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
        {
            Release(data, pooled, input);
            throw e is EndOfStreamException
                ? new CorruptDataException(input.FormatName, -1, e.Message) { IsTruncation = true }
                : input.Corrupt(-1, e.Message);
        }
        catch
        {
            Release(data, pooled, input);
            throw;
        }
    }

    private static void Release(byte[] data, bool pooled, EgamiInput input)
    {
        if (pooled)
        {
            ArrayPool<byte>.Shared.Return(data);
        }

        input.Dispose();
    }

    /// <summary>
    /// Returns the largest reduction a decode can make by dropping wavelet levels: 2 to the fewest levels any component has,
    /// limited so the reduced image is exactly the stored size divided and rounded up (the image offset a multiple of it).
    /// </summary>
    internal static int MaxNativeScale(J2kCodestream stream)
    {
        int levels = stream.MainCod.Levels;
        foreach (J2kCodingStyle? coc in stream.MainCoc)
        {
            if (coc is not null)
            {
                levels = Math.Min(levels, coc.Levels);
            }
        }

        int scale = 1;
        for (int r = 1; r <= Math.Min(levels, 16); r++)
        {
            int s = 1 << r;
            if (stream.OffsetX % s != 0 || stream.OffsetY % s != 0)
            {
                break;
            }

            scale = s;
        }

        return scale;
    }

    /// <summary>
    /// Gets whether rows can stream a row of tiles at a time: every component's rows of a tile row are its own, which holds
    /// when no component is subsampled vertically or every tile row starts on a multiple of each component's step.
    /// </summary>
    private static bool Banded(J2kCodestream stream)
    {
        foreach (J2kComponent component in stream.Components)
        {
            if (component.Dy != 1 && (stream.TileHeight % component.Dy != 0 || stream.TileOffsetY % component.Dy != 0 || stream.OffsetY % component.Dy != 0))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>A JPEG 2000 image streamed a row of tiles at a time, its rows converted on demand.</summary>
    private sealed class Jpeg2000Rows : RowSource
    {
        private readonly EgamiInput _input;
        private readonly bool _pooled;
        private readonly J2kCodestream _stream;
        private readonly J2kPixels _pixels;
        private readonly J2kGrid _grid;
        private readonly J2kDecoder _decoder;
        private readonly J2kWindow? _window;
        private readonly bool _strict;
        private readonly int _reduce;
        private readonly int[] _bandStarts;
        private readonly bool _yuv;
        private readonly Jp2File _file;
        private readonly ReaderOptions _options;
        private byte[]? _data;
        private byte[]? _row;
        private J2kImage? _image;
        private Raster<Rgb8>? _whole8;
        private Raster<Rgb16>? _whole16;
        private int _band = -1;
        private bool _started;
        private bool _ended;
        private bool _truncated;
        private int _missing;

        public Jpeg2000Rows(EgamiInput input, byte[] data, bool pooled, Jp2File file, J2kCodestream stream, ReaderOptions options)
        {
            _input = input;
            _data = data;
            _pooled = pooled;
            _file = file;
            _stream = stream;
            _options = options;
            _strict = options.Recovery == RecoveryMode.Strict;
            _pixels = J2kPixels.Create(file, stream.Components, Mode(options));
            StoredWidth = stream.Width - stream.OffsetX;
            StoredHeight = stream.Height - stream.OffsetY;
            MaxNativeScale = Jpeg2000Reader.MaxNativeScale(stream);
            int scale = options.NativeScale is { } choose
                ? choose(StoredWidth, StoredHeight)
                : ReadPlan.Create(StoredWidth, StoredHeight, options.Region, options.MinimumSize, MaxNativeScale).NativeScale;
            NativeScale = Math.Clamp(scale, 1, MaxNativeScale);
            _reduce = System.Numerics.BitOperations.Log2((uint)NativeScale);
            input.Limits.CheckFrameSize(Math.Max(1, StoredWidth), Math.Max(1, StoredHeight));
            bool[] needed = _pixels.Needed(stream.Components.Length, stream.MainCod.Mct == 1);
            _grid = J2kGrid.Create(stream, _reduce, needed);
            int layers = options is Jpeg2000ReaderOptions { MaxQualityLayers: int n } ? Math.Max(n, 1) : 0;
            _decoder = new J2kDecoder(stream, _reduce, layers, input.Context.MaxParallelism, input.CancellationToken) { Needed = needed };
            if (options.Region is Rect region)
            {
                // The region, in reduced pixels, moved onto the reduced reference grid.
                int x0 = J2kTier2.CeilShift(stream.OffsetX, _reduce);
                int y0 = J2kTier2.CeilShift(stream.OffsetY, _reduce);
                _window = new J2kWindow(
                    x0 + (region.X / NativeScale),
                    y0 + (region.Y / NativeScale),
                    x0 + ((region.X + region.Width + NativeScale - 1) / NativeScale),
                    y0 + ((region.Y + region.Height + NativeScale - 1) / NativeScale));
            }

            _yuv = _pixels.Model == J2kModel.Sycc && PlaneFormat(stream, _pixels) is (ChromaFormat format, _) && format != ChromaFormat.Yuv444;
            _bandStarts = Bands(stream, _reduce, !_yuv && Banded(stream));
            long working = WorkingBytesOf(stream, _reduce, _bandStarts, _yuv, needed);
            if (working > input.Limits.MaxTotalBytes)
            {
                throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), input.Limits.MaxTotalBytes, working);
            }

            WorkingBytes = working;
            _row = ArrayPool<byte>.Shared.Rent(Math.Max(1, _grid.Width * _pixels.Layout.BytesPerPixel));
            Metadata.FormatName = Jpeg2000Format.Info.Name;
            Describe(Metadata, file, stream, options);
        }

        public override int StoredWidth { get; }

        public override int StoredHeight { get; }

        public override int NativeScale { get; }

        public override int MaxNativeScale { get; }

        public override PixelLayoutInfo Layout => _pixels.Layout;

        public override RasterMetadata Metadata { get; } = new();

        public override int? FrameCount => 1;

        public override RasterAccess Access => _bandStarts.Length > 2 ? RasterAccess.Streamed : RasterAccess.Buffered;

        public override long WorkingBytes { get; }

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => _pixels.Natural switch
        {
            J2kNatural.Gray8 => visitor.Visit<Gray8>(),
            J2kNatural.Gray16 => visitor.Visit<Gray16>(),
            J2kNatural.GrayF32 => visitor.Visit<GrayF32>(),
            J2kNatural.GrayAlpha8 => visitor.Visit<GrayAlpha8>(),
            J2kNatural.GrayAlpha16 => visitor.Visit<GrayAlpha16>(),
            J2kNatural.Rgb8 => visitor.Visit<Rgb8>(),
            J2kNatural.Rgb16 => visitor.Visit<Rgb16>(),
            J2kNatural.RgbF32 => visitor.Visit<RgbF32>(),
            J2kNatural.Rgba8 => visitor.Visit<Rgba8>(),
            J2kNatural.Rgba16 => visitor.Visit<Rgba16>(),
            J2kNatural.RgbaF32 => visitor.Visit<RgbaF32>(),
            J2kNatural.Cmyk8 => visitor.Visit<Cmyk8>(),
            _ => visitor.Visit<Cmyk16>(),
        };

        public override FrameMetadata? NextFrame()
        {
            if (!_started)
            {
                _started = true;
                return new FrameMetadata();
            }

            EndFrame();
            return null;
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            try
            {
                if (_yuv)
                {
                    if (_whole8 is null && _whole16 is null)
                    {
                        Whole();
                    }

                    if (_whole8 is not null)
                    {
                        PixelConvert.Convert<Rgb8, TPixel>(_whole8.Row(y).Slice(x, destination.Length), destination);
                    }
                    else
                    {
                        PixelConvert.Convert<Rgb16, TPixel>(_whole16!.Row(y).Slice(x, destination.Length), destination);
                    }

                    return;
                }

                int band = _band;
                while (band + 1 < _bandStarts.Length - 1 && y >= _bandStarts[band + 1])
                {
                    band++;
                }

                if (band != _band || _image is null)
                {
                    _image = null;
                    _band = band;
                    _image = Decode(band);
                }

                int bytes = destination.Length * _pixels.Layout.BytesPerPixel;
                Span<byte> natural = _row.AsSpan(0, bytes);
                _pixels.Row(_image, _grid, y, x, natural);
                Convert(_pixels.Natural, natural, destination);
            }
            catch (Exception e) when (e is InvalidDataException or EndOfStreamException)
            {
                throw _input.Corrupt(-1, e.Message);
            }
        }

        public override void EndFrame()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            Report();
        }

        protected override void Dispose(bool disposing)
        {
            if (_data is not null)
            {
                if (_pooled)
                {
                    ArrayPool<byte>.Shared.Return(_data);
                }

                _data = null;
                ArrayPool<byte>.Shared.Return(_row!);
                _row = null;
                _whole8?.Dispose();
                _whole8 = null;
                _whole16?.Dispose();
                _whole16 = null;
                _input.Dispose();
            }
        }

        /// <summary>Decodes band <paramref name="band"/>: one row of tiles, or the whole image.</summary>
        private J2kImage Decode(int band)
        {
            (int y0, int y1) = BandRows(_stream, band, _bandStarts.Length - 1);
            J2kImage image = _decoder.Decode(y0, y1, _window);
            _truncated |= image.Truncated;
            _missing += image.MissingTiles;
            if (image.Truncated && _strict)
            {
                throw new CorruptDataException(_input.FormatName, -1, "The JPEG 2000 codestream ends before its last packet.") { IsTruncation = true };
            }

            return image;
        }

        /// <summary>Decodes the whole image the way Load does, for subsampled sYCC, whose chroma is interpolated across rows.</summary>
        private void Whole()
        {
            J2kImage image = _decoder.Decode(_stream.OffsetY, _stream.Height, _window);
            _truncated |= image.Truncated;
            _missing += image.MissingTiles;
            (ChromaFormat format, int depth) = PlaneFormat(_stream, _pixels)!.Value;
            if (depth == 8)
            {
                using YuvRaster<byte> planes = Planes<byte>(image, format, depth, _input.Context);
                _whole8 = _input.CreateRaster<Rgb8>(_grid.Width, _grid.Height);
                YuvConvert.ToRgb(planes, _whole8, Upsampling(_options), _input.CancellationToken);
            }
            else
            {
                using YuvRaster<ushort> planes = Planes<ushort>(image, format, depth, _input.Context);
                _whole16 = _input.CreateRaster<Rgb16>(_grid.Width, _grid.Height);
                YuvConvert.ToRgb(planes, _whole16, Upsampling(_options), _input.CancellationToken);
            }
        }

        private void Report()
        {
            if (_truncated)
            {
                Metadata.WarningList.Add("Jpeg2000.Truncated", "The codestream ends before its last packet; what it holds was decoded.");
            }

            if (_missing > 0)
            {
                Metadata.WarningList.Add("Jpeg2000.MissingTiles", $"{_missing} tiles have no data and were left black.");
            }

            if (_pixels.OffsetSigned)
            {
                Metadata.WarningList.Add("Jpeg2000.SignedSamples", "Signed samples were offset by half their range to load as unsigned.");
            }

            if (_pixels.Dropped > 0)
            {
                Metadata.WarningList.Add("Jpeg2000.ChannelsDropped", $"{_pixels.Dropped} channels that are neither color nor alpha were not loaded.");
            }

            foreach ((string id, string message) in _file.Warnings)
            {
                Metadata.WarningList.Add(id, message);
            }
        }
    }

    /// <summary>
    /// Returns the first output row of each band and, last, the output height: a band per row of tiles when
    /// <paramref name="banded"/>, else one band.
    /// </summary>
    private static int[] Bands(J2kCodestream stream, int reduce, bool banded)
    {
        int origin = J2kTier2.CeilShift(stream.OffsetY, reduce);
        int height = J2kTier2.CeilShift(stream.Height, reduce) - origin;
        if (!banded)
        {
            return [0, height];
        }

        List<int> starts = [];
        for (int ty = 0; ty < stream.TilesY; ty++)
        {
            (_, int y0, _, _) = stream.TileRect(ty * stream.TilesX);
            int start = J2kTier2.CeilShift(y0, reduce) - origin;
            if (starts.Count == 0 || start > starts[^1])
            {
                starts.Add(start);
            }
        }

        starts.Add(height);
        return [.. starts];
    }

    /// <summary>Returns the reference grid rows of band <paramref name="band"/> of <paramref name="count"/>.</summary>
    private static (int Y0, int Y1) BandRows(J2kCodestream stream, int band, int count)
    {
        if (count == 1)
        {
            return (stream.OffsetY, stream.Height);
        }

        (_, int y0, _, int y1) = stream.TileRect(band * stream.TilesX);
        return (y0, y1);
    }

    /// <summary>Returns what streaming holds: one band's samples of every component and one tile's coefficients.</summary>
    private static long WorkingBytesOf(J2kCodestream stream, int reduce, int[] bands, bool whole, bool[] needed)
    {
        int tallest = 0;
        for (int b = 0; b + 1 < bands.Length; b++)
        {
            tallest = Math.Max(tallest, bands[b + 1] - bands[b]);
        }

        long samples = 0;
        for (int c = 0; c < stream.Components.Length; c++)
        {
            if (!needed[c])
            {
                continue;
            }

            (int w, int h) = J2kDecoder.ComponentSize(stream, c, reduce);
            samples += (long)w * Math.Min(h, tallest + 1);
        }

        long tileWidth = J2kTier2.CeilShift(Math.Min(stream.TileWidth, stream.Width), reduce);
        long tileHeight = J2kTier2.CeilShift(Math.Min(stream.TileHeight, stream.Height), reduce);
        long tile = Math.Min(samples, tileWidth * tileHeight * stream.Components.Length);
        long held = (samples + tile) * 4;
        if (whole)
        {
            held += (long)J2kTier2.CeilShift(stream.Width, reduce) * J2kTier2.CeilShift(stream.Height, reduce) * 6;
        }

        return held;
    }
}
