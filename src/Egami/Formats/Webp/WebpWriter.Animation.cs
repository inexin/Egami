using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Webp;

/// <content>
/// Animation: each frame is reduced to the rectangle that differs from the frame before (widened to even offsets, which ANMF
/// stores halved) and encoded on its own, lossless, lossy or both. A rectangle whose changed pixels are all opaque is blended
/// over the canvas, the unchanged pixels in it made transparent where that helps; one that brings in transparency replaces the
/// pixels. No frame is disposed of, so the canvas after each frame equals that frame.
/// </content>
internal sealed partial class WebpWriter
{
    /// <summary>The longest frame delay ANMF can hold: 24 bits of milliseconds.</summary>
    internal const int MaxDelay = 0xFFFFFF;

    /// <summary>Writes an animation a frame at a time; the RIFF and VP8X fields known only at the end are patched, or the file buffered when the output cannot seek.</summary>
    private sealed class AnimationEncoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Stream _output;
        private readonly EncodeStart<TPixel> _start;
        private readonly WebpWriterOptions _options;
        private readonly bool _lossless;
        private readonly int _width;
        private readonly int _height;
        private readonly Chunks _chunks;
        private readonly uint[] _canvas;
        private uint[] _current;
        private readonly Stream _body;
        private readonly long _origin;
        private StillEncoder<TPixel>? _still;
        private long _vp8xAt;
        private int _frames;
        private bool _alpha;
        private uint[]? _firstFrame;
        private TimeSpan _firstDelay;

        internal AnimationEncoder(Stream output, EncodeStart<TPixel> start, WebpWriterOptions options, bool lossless)
        {
            _output = output;
            _start = start;
            _options = options;
            _lossless = lossless;
            _width = start.Width;
            _height = start.Height;
            _chunks = Chunks.From(start.Metadata, options);
            _canvas = new uint[_width * _height];
            _current = new uint[_width * _height];
            _body = start.Seekable ? output : new MemoryStream();
            _origin = start.Seekable ? output.Position : 0;
        }

        public override bool Animates => true;

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            // Rows instead of frames: one image, written as a still.
            if (_frames > 0)
            {
                throw new InvalidOperationException("Frames were written; an image takes rows or frames, not both.");
            }

            _still ??= new StillEncoder<TPixel>(_output, _start, _options, _lossless);
            _still.WriteRow(row);
        }

        public override void WriteFrame(Frame<TPixel> frame)
        {
            if (_still is not null)
            {
                throw new InvalidOperationException("Rows were written; an image takes rows or frames, not both.");
            }

            for (int y = 0; y < _height; y++)
            {
                PixelConvert.Convert<TPixel, Bgra8>(frame.Row(y), MemoryMarshal.Cast<uint, Bgra8>(_current.AsSpan(y * _width, _width)));
            }

            if (_frames == 0)
            {
                // The first frame waits for a second: a lone frame is written as a still, as Save writes it.
                _firstFrame = (uint[])_current.Clone();
                _firstDelay = frame.Metadata.Delay;
                _frames++;
                return;
            }

            if (_firstFrame is uint[] first)
            {
                uint[] second = _current;
                _current = first;
                _frames = 0;
                WriteHeader();
                WriteAnmf(_firstDelay);
                first.CopyTo(_canvas, 0);
                _current = second;
                _firstFrame = null;
                _frames = 1;
            }

            WriteAnmf(frame.Metadata.Delay);
            _current.CopyTo(_canvas, 0);
            _frames++;
        }

        public override IEnumerable<int> WriteWhole(Raster<TPixel> raster)
        {
            for (int i = 0; i < raster.FrameCount; i++)
            {
                WriteFrame(raster.Frames[i]);
                yield return i;
            }
        }

        public override void Complete()
        {
            if (_still is not null)
            {
                _still.Complete();
                return;
            }

            if (_frames == 0)
            {
                throw new InvalidOperationException("No frame was written.");
            }

            if (_firstFrame is not null)
            {
                StillEncoder<TPixel>.WriteStill(_output, _start.Metadata, _options, _lossless, _firstFrame, _width, _height);
                return;
            }

            Container.WriteChunk(_body, WebpContainer.Exif, _chunks.Exif.Span);
            Container.WriteChunk(_body, WebpContainer.Xmp, _chunks.Xmp.Span);
            _chunks.WriteOthers(_body, WebpChunkPosition.AfterImageData);
            long end = _body.Position;
            long riff = end - _origin - 8;
            if (riff > uint.MaxValue)
            {
                throw new InvalidOperationException("The encoded animation is too large for a RIFF file (4 GB).");
            }

            Span<byte> field = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(field, (uint)riff);
            Patch(_origin + 4, field);
            field[0] = Flags();
            Patch(_vp8xAt + 8, field[..1]);
            if (!_start.Seekable)
            {
                ((MemoryStream)_body).WriteTo(_output);
            }
            else
            {
                _output.Position = end;
            }
        }

        protected override void Dispose(bool disposing)
        {
            _still?.Dispose();
            if (!_start.Seekable)
            {
                _body.Dispose();
            }

            base.Dispose(disposing);
        }

        private byte Flags() => (byte)((_chunks.Icc.IsEmpty ? 0 : 0x20) | (_alpha ? 0x10 : 0) | (_chunks.Exif.IsEmpty ? 0 : 0x08) | (_chunks.Xmp.IsEmpty ? 0 : 0x04) | 0x02);

        private void Patch(long at, ReadOnlySpan<byte> bytes)
        {
            if (_body is MemoryStream memory)
            {
                bytes.CopyTo(memory.GetBuffer().AsSpan((int)at));
                return;
            }

            _body.Position = at;
            _body.Write(bytes);
        }

        /// <summary>Writes RIFF and VP8X (with sizes and flags patched at the end), ICCP, the chunks kept before the image, and ANIM.</summary>
        private void WriteHeader()
        {
            Span<byte> riff = stackalloc byte[12];
            BinaryPrimitives.WriteUInt32LittleEndian(riff, WebpContainer.Riff);
            BinaryPrimitives.WriteUInt32LittleEndian(riff[4..], 0);
            BinaryPrimitives.WriteUInt32LittleEndian(riff[8..], WebpContainer.Webp);
            _body.Write(riff);
            _vp8xAt = _body.Position;
            Container.WriteVp8X(_body, 0x02, _width, _height);
            Container.WriteChunk(_body, WebpContainer.Iccp, _chunks.Icc.Span);
            _chunks.WriteOthers(_body, WebpChunkPosition.BeforeImageData);

            Span<byte> anim = stackalloc byte[6];
            Rgba8 background = _start.Metadata.FormatDetails is WebpDetails details ? details.BackgroundColor : new Rgba8(255, 255, 255, 255);
            anim[0] = background.B;
            anim[1] = background.G;
            anim[2] = background.R;
            anim[3] = background.A;
            BinaryPrimitives.WriteUInt16LittleEndian(anim[4..], (ushort)Math.Clamp(_start.Metadata.LoopCount, 0, ushort.MaxValue));
            Container.WriteChunk(_body, WebpContainer.Anim, anim);
        }

        /// <summary>Encodes the current frame's changed rectangle and writes its ANMF chunk.</summary>
        private void WriteAnmf(TimeSpan delay)
        {
            Rect bounds = _frames == 0 ? new Rect(0, 0, _width, _height) : Changed();
            int x = bounds.X & ~1, y = bounds.Y & ~1;
            int w = bounds.Width + (bounds.X - x), h = bounds.Height + (bounds.Y - y);
            uint[] rect = new uint[w * h];
            bool over = _frames > 0 && ChangedPixelsOpaque(x, y, w, h);
            bool hollow = over && (!_lossless || _options.MixedFrames) && _options.Effort >= 4;
            for (int row = 0; row < h; row++)
            {
                ReadOnlySpan<uint> now = _current.AsSpan(((y + row) * _width) + x, w);
                ReadOnlySpan<uint> before = _canvas.AsSpan(((y + row) * _width) + x, w);
                Span<uint> target = rect.AsSpan(row * w, w);
                now.CopyTo(target);
                if (over)
                {
                    for (int i = 0; i < w; i++)
                    {
                        // Blended over the canvas, an unchanged pixel must be opaque or transparent to come out unchanged.
                        if (now[i] == before[i] && (hollow || now[i] < 0xFF000000))
                        {
                            target[i] = hollow ? now[i] & 0x00FFFFFF : 0;
                        }
                    }
                }
            }

            (byte[] data, bool alpha) = EncodeFrame(rect, w, h, hollow);
            _alpha |= alpha;
            int duration = (int)Math.Clamp(Math.Round(delay.TotalMilliseconds), 0, MaxDelay);
            Span<byte> header = stackalloc byte[16];
            WriteU24(header, x / 2);
            WriteU24(header[3..], y / 2);
            WriteU24(header[6..], w - 1);
            WriteU24(header[9..], h - 1);
            WriteU24(header[12..], duration);
            header[15] = (byte)(over ? 0 : 0x02);
            Span<byte> chunk = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32LittleEndian(chunk, WebpContainer.Anmf);
            BinaryPrimitives.WriteUInt32LittleEndian(chunk[4..], (uint)(16 + data.Length));
            _body.Write(chunk);
            _body.Write(header);
            _body.Write(data);
        }

        /// <summary>
        /// Encodes one frame's rectangle as the chunks an ANMF payload holds (ALPH and VP8, or VP8L); with
        /// <see cref="WebpWriterOptions.MixedFrames"/> both ways, keeping the smaller.
        /// </summary>
        private (byte[] Data, bool Alpha) EncodeFrame(uint[] rect, int w, int h, bool hollow)
        {
            bool alpha = false;
            foreach (uint p in rect)
            {
                if (p < 0xFF000000)
                {
                    alpha = true;
                    break;
                }
            }

            byte[]? lossless = null, lossy = null;
            if (_lossless || _options.MixedFrames)
            {
                uint[] pixels = rect;
                if (hollow)
                {
                    // Lossless codes runs of one value best: the transparent pixels lose their colors.
                    pixels = [.. rect.Select(p => p < 0x01000000 ? 0u : p)];
                }

                using Vp8lBitWriter bits = new(pixels.Length);
                Vp8lEncoder.Encode(pixels, w, h, new Vp8lEncoderOptions(_options.Effort, _options.KeepTransparentColors, _options.NearLossless), bits);
                lossless = Chunk(WebpContainer.Vp8L, bits.Finish(), default);
            }

            if (!_lossless || _options.MixedFrames)
            {
                (byte[] frame, byte[]? alphaChunk) = StillEncoder<TPixel>.EncodeLossy(rect, w, h, _options, alpha);
                lossy = Chunk(WebpContainer.Vp8, frame, alphaChunk);
            }

            return (lossless is not null && (lossy is null || lossless.Length <= lossy.Length) ? lossless : lossy!, alpha);
        }

        /// <summary>Returns an ANMF payload's image chunks: an ALPH chunk if there is alpha data, then the image chunk, each padded.</summary>
        private static byte[] Chunk(uint type, ReadOnlySpan<byte> data, ReadOnlySpan<byte> alpha)
        {
            using MemoryStream stream = new();
            if (!alpha.IsEmpty)
            {
                Container.WriteChunk(stream, WebpContainer.Alph, alpha);
            }

            Container.WriteChunk(stream, type, data);
            return stream.ToArray();
        }

        /// <summary>Returns the smallest rectangle holding every pixel that differs from the canvas (one pixel when none does).</summary>
        private Rect Changed()
        {
            int left = _width, top = -1, right = -1, bottom = -1;
            for (int y = 0; y < _height; y++)
            {
                ReadOnlySpan<uint> a = _canvas.AsSpan(y * _width, _width), b = _current.AsSpan(y * _width, _width);
                int first = a.CommonPrefixLength(b);
                if (first == _width)
                {
                    continue;
                }

                int last = _width - 1;
                while (a[last] == b[last])
                {
                    last--;
                }

                left = Math.Min(left, first);
                right = Math.Max(right, last);
                top = top < 0 ? y : top;
                bottom = y;
            }

            return right < 0 ? new Rect(0, 0, 1, 1) : new Rect(left, top, right - left + 1, bottom - top + 1);
        }

        /// <summary>Returns whether every pixel of the rectangle that differs from the canvas is opaque, so blending cannot lose it.</summary>
        private bool ChangedPixelsOpaque(int x, int y, int w, int h)
        {
            for (int row = 0; row < h; row++)
            {
                ReadOnlySpan<uint> now = _current.AsSpan(((y + row) * _width) + x, w);
                ReadOnlySpan<uint> before = _canvas.AsSpan(((y + row) * _width) + x, w);
                for (int i = 0; i < w; i++)
                {
                    if (now[i] != before[i] && now[i] < 0xFF000000)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static void WriteU24(Span<byte> target, int value)
        {
            target[0] = (byte)value;
            target[1] = (byte)(value >> 8);
            target[2] = (byte)(value >> 16);
        }
    }
}
