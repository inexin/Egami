using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <content>
/// Animation: each frame is one more frame of the codestream, cut to the rectangle that differs from the frame before. A
/// lossless rectangle whose changed pixels are all opaque is blended over the canvas, its unchanged pixels made transparent;
/// any other rectangle, and every lossy one, replaces the pixels, so lossy errors cannot pile up. Each frame but the last is
/// saved in reference slot 1, which the next one blends onto. Durations are in ticks of a millisecond.
/// </content>
internal sealed partial class JxlWriter
{
    /// <summary>The animation's ticks per second: durations are in milliseconds.</summary>
    internal const int TicksPerSecond = 1000;

    /// <summary>The reference slot each shown frame is saved in for the next to blend onto (slot 3 holds patches).</summary>
    private const int CanvasSlot = 1;

    /// <summary>Gets a frame's duration in ticks: its delay in milliseconds, at least one so that every frame is shown.</summary>
    internal static uint Ticks(TimeSpan delay) => (uint)Math.Clamp(Math.Round(delay.TotalMilliseconds), 1, uint.MaxValue);

    /// <summary>Writes a multi-frame raster from Save as an animation, a frame at a time.</summary>
    private sealed class AnimationEncoder<TPixel>(Stream output, EncodeStart<TPixel> start, JxlWriterOptions options, bool lossless) : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly MemoryStream _codestream = new();
        private readonly int _width = start.Width;
        private readonly int _height = start.Height;
        private TPixel[] _canvas = [];
        private TPixel[] _next = [];
        private FrameCoder<TPixel>? _coder;
        private bool _alpha;
        private int _frames;

        public override bool Animates => true;

        public override void WriteRow(ReadOnlySpan<TPixel> row) => throw new InvalidOperationException("A JPEG XL animation is written from a whole raster.");

        public override void WriteFrame(Frame<TPixel> frame) => throw new InvalidOperationException("A JPEG XL animation is written from a whole raster.");

        public override IEnumerable<int> WriteWhole(Raster<TPixel> raster)
        {
            ArgumentNullException.ThrowIfNull(raster);
            _alpha = TPixel.Layout.HasAlpha && AnyTranslucent(raster);
            _canvas = new TPixel[_width * _height];
            _next = new TPixel[_width * _height];
            for (int i = 0; i < raster.FrameCount; i++)
            {
                Frame<TPixel> frame = raster.Frames[i];
                for (int y = 0; y < _height; y++)
                {
                    frame.Row(y).CopyTo(_next.AsSpan(y * _width, _width));
                }

                Write(frame.Metadata.Delay, i == raster.FrameCount - 1);
                (_canvas, _next) = (_next, _canvas);
                _frames++;
                yield return i;
            }
        }

        public override void Complete()
        {
            if (_frames == 0)
            {
                throw new InvalidOperationException("No frame was written.");
            }

            RasterMetadata metadata = start.Metadata;
            bool strip = options.StripMetadata;
            byte[]? exif = strip || metadata.Exif is null ? null : metadata.Exif.ToBytes().ToArray();
            byte[]? xmp = strip || metadata.Xmp.IsEmpty ? null : metadata.Xmp.ToArray();
            byte[]? gainMap = strip || metadata.GainMap is null ? null : GainMapBundle(metadata.GainMap, options.Effort, start.Context.MaxParallelism);
            WriteFile(output, _codestream.ToArray(), exif, xmp, gainMap: gainMap);
        }

        protected override void Dispose(bool disposing)
        {
            _codestream.Dispose();
            base.Dispose(disposing);
        }

        /// <summary>Returns whether any pixel of any frame is not opaque, so the file needs alpha.</summary>
        private static bool AnyTranslucent(Raster<TPixel> raster)
        {
            Rgba16[] row = new Rgba16[raster.Width];
            for (int i = 0; i < raster.FrameCount; i++)
            {
                for (int y = 0; y < raster.Height; y++)
                {
                    PixelConvert.Convert<TPixel, Rgba16>(raster.Frames[i].Row(y), row);
                    foreach (Rgba16 p in row)
                    {
                        if (p.A != ushort.MaxValue)
                        {
                            return true;
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>Codes the next frame (in <see cref="_next"/>) against the canvas before it.</summary>
        private void Write(TimeSpan delay, bool last)
        {
            (int x, int y, int w, int h) = _frames == 0 ? (0, 0, _width, _height) : Changed();
            bool crop = w != _width || h != _height;
            JxlFrameInfo info = new()
            {
                Crop = crop,
                X0 = x,
                Y0 = y,
                Duration = Ticks(delay),
                IsLast = last,
                SaveAsReference = last ? 0 : CanvasSlot,
                Blending = new JxlBlending(JxlBlendMode.Replace, 0, false, CanvasSlot),
            };

            if (_coder is null)
            {
                RasterMetadata metadata = start.Metadata;
                JxlAnimation animation = new(TicksPerSecond, 1, (uint)Math.Max(0, metadata.LoopCount), false);
                _coder = new FrameCoder<TPixel>(metadata, options, start.Context.MaxParallelism, _width, _height, _alpha, lossless, animation);
            }

            _codestream.Write(_coder.Code(_next, _width, x, y, w, h, info, _frames > 0 ? planes => Hollow(planes, x, y, w, h) : null));
        }

        /// <summary>
        /// Makes the unchanged pixels of a lossless rectangle transparent so that it blends, when every changed pixel in it is
        /// opaque; otherwise leaves it to replace.
        /// </summary>
        private bool Hollow(Planes planes, int x, int y, int w, int h)
        {
            if (!_alpha || planes.Alpha is not int[] alpha)
            {
                return false;
            }

            bool[] changed = new bool[w * h];
            int opaque = planes.Opaque;
            for (int r = 0; r < h; r++)
            {
                ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(_next.AsSpan(((y + r) * _width) + x, w));
                ReadOnlySpan<byte> b = MemoryMarshal.AsBytes(_canvas.AsSpan(((y + r) * _width) + x, w));
                int size = a.Length / w;
                for (int i = 0; i < w; i++)
                {
                    bool differs = !a.Slice(i * size, size).SequenceEqual(b.Slice(i * size, size));
                    if (differs && alpha[(r * w) + i] != opaque)
                    {
                        return false;
                    }

                    changed[(r * w) + i] = differs;
                }
            }

            // Blended over the canvas, an unchanged pixel made fully transparent comes out unchanged, and codes cheaply.
            for (int i = 0; i < changed.Length; i++)
            {
                if (!changed[i])
                {
                    alpha[i] = 0;
                    foreach (int[] plane in planes.Color)
                    {
                        plane[i] = 0;
                    }
                }
            }

            return true;
        }

        /// <summary>Returns the smallest rectangle holding every pixel that differs from the canvas (one pixel when none does).</summary>
        private (int X, int Y, int W, int H) Changed()
        {
            int left = _width, top = -1, right = -1, bottom = -1;
            for (int y = 0; y < _height; y++)
            {
                ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(_canvas.AsSpan(y * _width, _width));
                ReadOnlySpan<byte> b = MemoryMarshal.AsBytes(_next.AsSpan(y * _width, _width));
                int size = a.Length / _width;
                int first = a.CommonPrefixLength(b) / size;
                if (first == _width)
                {
                    continue;
                }

                int last = _width - 1;
                while (a.Slice(last * size, size).SequenceEqual(b.Slice(last * size, size)))
                {
                    last--;
                }

                left = Math.Min(left, first);
                right = Math.Max(right, last);
                top = top < 0 ? y : top;
                bottom = y;
            }

            return right < 0 ? (0, 0, 1, 1) : (left, top, right - left + 1, bottom - top + 1);
        }
    }

    /// <summary>
    /// Codes the frames of one codestream, the headers before the first: each a rectangle of <typeparamref name="TPixel"/>
    /// pixels, lossless or lossy, placed on the canvas as its <see cref="JxlFrameInfo"/> says.
    /// </summary>
    private sealed class FrameCoder<TPixel>(RasterMetadata metadata, JxlWriterOptions options, int parallelism, int width, int height, bool alpha, bool lossless, JxlAnimation? animation)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly int _orientation = metadata.Exif?.Orientation is ImageOrientation o && (int)o is >= 1 and <= 8 ? (int)o : 1;
        private bool _started;

        /// <summary>
        /// Codes the <paramref name="w"/> by <paramref name="h"/> rectangle at (<paramref name="x"/>, <paramref name="y"/>) of
        /// <paramref name="pixels"/>, rows <paramref name="stride"/> apart. A lossless frame's planes go through
        /// <paramref name="blend"/> first, which may change them and returns whether the frame then blends instead of replacing.
        /// </summary>
        internal byte[] Code(TPixel[] pixels, int stride, int x, int y, int w, int h, JxlFrameInfo info, Func<Planes, bool>? blend = null)
        {
            byte[] bytes = lossless ? Lossless(pixels, stride, x, y, w, h, info, blend) : Lossy(pixels, stride, x, y, w, h, info);
            _started = true;
            return bytes;
        }

        private byte[] Lossless(TPixel[] pixels, int stride, int x, int y, int w, int h, JxlFrameInfo info, Func<Planes, bool>? blend)
        {
            Planes planes = new(TPixel.Layout, w, h);
            IntermediateRow row = new(w);
            for (int r = 0; r < h; r++)
            {
                ReadOnlySpan<TPixel> now = pixels.AsSpan(((y + r) * stride) + x, w);
                switch (planes.Kind)
                {
                    case PixelChannelType.Unorm8:
                        PixelConvert.Convert(now, row.Rgba8);
                        planes.Add8(row.Rgba8, r);
                        break;
                    case PixelChannelType.Float32:
                        PixelConvert.Convert(now, row.RgbaF32);
                        planes.AddF32(row.RgbaF32, r);
                        break;
                    case PixelChannelType.Float16:
                        PixelConvert.Convert(now, row.RgbaF16);
                        planes.AddF16(row.RgbaF16, r);
                        break;
                    default:
                        PixelConvert.Convert(now, row.Rgba16);
                        planes.Add16(row.Rgba16, r);
                        break;
                }
            }

            if (blend?.Invoke(planes) == true)
            {
                info = info with { Blending = info.Blending with { Mode = JxlBlendMode.Blend } };
            }

            (JxlColorEncoding color, byte[]? icc) = ColorOf(metadata, options, planes.Color.Length == 1);
            // libjxl's fast lossless path takes whole still images only; other frames get effort 2 instead, as with cjxl -e 1.
            JxlEncodeInput input = planes.ToInput(Math.Max(2, options.Effort), alpha) with
            {
                Parallelism = parallelism,
                ColorEncoding = color,
                Icc = icc,
                Orientation = _orientation,
                Animation = animation,
            };

            JxlBitWriter bits = new();
            if (!_started)
            {
                JxlLosslessEncoder.WriteHeaders(bits, input with { Width = width, Height = height });
            }

            return JxlLosslessEncoder.EncodeFrame(bits, input, info, width, height);
        }

        private byte[] Lossy(TPixel[] pixels, int stride, int x, int y, int w, int h, JxlFrameInfo info)
        {
            ColorProfile profile = metadata.GetColorProfile();
            JxlLinearRows rows = new(w, h, profile, TPixel.Layout);
            int alphaBits = TPixel.Layout.ChannelType == PixelChannelType.Unorm8 ? 8 : 16;
            int[]? alphaPlane = alpha ? new int[w * h] : null;
            Cmyk16[]? inks = rows.HasBlack ? new Cmyk16[w] : null;
            Rgba16[] rgba = new Rgba16[w];
            for (int r = 0; r < h; r++)
            {
                ReadOnlySpan<TPixel> now = pixels.AsSpan(((y + r) * stride) + x, w);
                if (inks is not null)
                {
                    PixelConvert.Convert(now, inks.AsSpan());
                    rows.Add<Cmyk16>(inks, r);
                }
                else
                {
                    rows.Add(now, r);
                }

                if (alphaPlane is not null)
                {
                    PixelConvert.Convert(now, rgba.AsSpan());
                    for (int i = 0; i < w; i++)
                    {
                        alphaPlane[(r * w) + i] = alphaBits == 8 ? (rgba[i].A + 128) / 257 : rgba[i].A;
                    }
                }
            }

            JxlLinearImage image = rows.Image;
            (JxlColorEncoding color, byte[]? icc) = ColorOf(metadata, options, image.IsGray, lossy: true);
            JxlEncodeInput header = new()
            {
                Width = width,
                Height = height,
                Color = image.IsGray ? [[]] : [[], [], []],
                ColorEncoding = color,
                Icc = icc,
                Orientation = _orientation,
                BitDepth = TPixel.Layout.ChannelType switch
                {
                    PixelChannelType.Unorm8 => new JxlBitDepth(8, 0),
                    PixelChannelType.Float16 => new JxlBitDepth(16, 5),
                    PixelChannelType.Float32 => new JxlBitDepth(32, 8),
                    _ => new JxlBitDepth(16, 0),
                },
                Alpha = alphaPlane,
                AlphaBitDepth = new JxlBitDepth(alphaBits, 0),
                HasBlack = image.Black is not null,
                IntensityTarget = rows.IntensityTarget,
                Animation = animation,
            };

            JxlBitWriter bits = new();
            if (!_started)
            {
                JxlVarDctEncoder.WriteHeaders(bits, header);
            }

            return JxlVarDctEncoder.EncodeFrame(bits, image, header, LossySettings(options, parallelism), info, rows.IntensityTarget);
        }
    }
}
