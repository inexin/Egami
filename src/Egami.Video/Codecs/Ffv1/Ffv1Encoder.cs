using Egami.Pixels;

namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// Encodes <see cref="YuvRaster"/> planes as FFV1 version 3 frames (RFC 9043): a slice grid coded in parallel, each slice with its
/// header and its footer (size, error status and CRC), the configuration record given up front. YCbCr planes are coded as they
/// are; a 4:4:4 raster tagged <see cref="YuvMatrix.Identity"/> holds G, B and R and is coded as RGB through the reversible color
/// transform.
/// </summary>
internal sealed class Ffv1Encoder
{
    private readonly int _width;
    private readonly int _height;
    private readonly Ffv1Parameters _p;
    private readonly int _shift;
    private readonly SliceEncoder[] _slices;
    private readonly Action<int> _encodeSlice;
    private readonly ParallelOptions _options = new();
    private YuvRaster? _source;
    private bool _keyframe;

    public Ffv1Encoder(int width, int height, Ffv1Parameters parameters, int storageDepth)
    {
        _width = width;
        _height = height;
        _p = parameters;
        _shift = storageDepth - parameters.BitsPerRawSample;
        ConfigurationRecord = parameters.WriteConfigurationRecord();
        _slices = new SliceEncoder[parameters.HorizontalSlices * parameters.VerticalSlices];
        for (int sy = 0; sy < parameters.VerticalSlices; sy++)
        {
            for (int sx = 0; sx < parameters.HorizontalSlices; sx++)
            {
                long x0 = (long)sx * width / parameters.HorizontalSlices;
                long y0 = (long)sy * height / parameters.VerticalSlices;
                long x1 = (long)(sx + 1) * width / parameters.HorizontalSlices;
                long y1 = (long)(sy + 1) * height / parameters.VerticalSlices;
                _slices[(sy * parameters.HorizontalSlices) + sx] = new SliceEncoder(this, sx, sy, (int)x0, (int)y0, (int)(x1 - x0), (int)(y1 - y0));
            }
        }

        _encodeSlice = s => _slices[s].Encode(s == 0);
    }

    /// <summary>Gets the configuration record, the track's CodecPrivate.</summary>
    public byte[] ConfigurationRecord { get; }

    /// <summary>Gets the parameters the frames are coded with.</summary>
    public Ffv1Parameters Parameters => _p;

    /// <summary>Encodes a frame into <paramref name="output"/>, returning its length.</summary>
    /// <param name="source">The planes, at the encoder's size, layout and storage depth.</param>
    /// <param name="keyframe">Whether the frame resets the coder's states.</param>
    /// <param name="maxParallelism">The most slices coded at once.</param>
    /// <param name="output">Receives the frame; grown as needed.</param>
    public int Encode(YuvRaster source, bool keyframe, int maxParallelism, ref byte[] output)
    {
        (_source, _keyframe) = (source, keyframe);
        try
        {
            int workers = Math.Min(Math.Max(1, maxParallelism), _slices.Length);
            if (workers > 1)
            {
                _options.MaxDegreeOfParallelism = workers;
                Parallel.For(0, _slices.Length, _options, _encodeSlice);
            }
            else
            {
                for (int s = 0; s < _slices.Length; s++)
                {
                    _encodeSlice(s);
                }
            }
        }
        finally
        {
            _source = null;
        }

        int total = 0;
        foreach (SliceEncoder slice in _slices)
        {
            total += slice.Output.Length;
        }

        if (output.Length < total)
        {
            output = new byte[total];
        }

        int at = 0;
        foreach (SliceEncoder slice in _slices)
        {
            ReadOnlySpan<byte> bytes = slice.Output.Written;
            bytes.CopyTo(output.AsSpan(at));
            at += bytes.Length;
        }

        return total;
    }

    /// <summary>A slice position: its coder states, carried from frame to frame, and the buffers it codes with.</summary>
    private sealed class SliceEncoder
    {
        private readonly Ffv1Encoder _owner;
        private readonly int _sx;
        private readonly int _sy;
        private readonly int _x;
        private readonly int _y;
        private readonly int _width;
        private readonly int _height;
        private readonly Ffv1PlaneCoding[] _coding = [new(), new(), new()];
        private readonly int[][] _lines;
        private readonly int[] _contexts;
        private readonly int[] _diffs;
        private readonly int[] _zeros;

        public SliceEncoder(Ffv1Encoder owner, int sx, int sy, int x, int y, int width, int height)
        {
            (_owner, _sx, _sy, _x, _y, _width, _height) = (owner, sx, sy, x, y, width, height);
            int stride = width + Ffv1Lines.Offset + 1;
            _lines = new int[12][];
            for (int i = 0; i < _lines.Length; i++)
            {
                _lines[i] = new int[stride];
            }

            _contexts = new int[width + 1];
            _diffs = new int[width + 1];
            _zeros = new int[width + 1];
        }

        public Ffv1RangeEncoder Output { get; } = new();

        public void Encode(bool first)
        {
            Ffv1Parameters p = _owner._p;
            Ffv1RangeEncoder rc = Output;
            rc.Reset();
            if (first)
            {
                byte keyState = 128;
                rc.WriteBit(ref keyState, _owner._keyframe ? 1 : 0);
            }

            rc.Table = p.StateTable;
            Span<byte> state = stackalloc byte[Ffv1Parameters.ContextSize];
            state.Fill(128);
            rc.WriteSymbol(state, _sx, false);
            rc.WriteSymbol(state, _sy, false);
            rc.WriteSymbol(state, 0, false);
            rc.WriteSymbol(state, 0, false);
            for (int i = 0; i < p.QuantIndexCount; i++)
            {
                rc.WriteSymbol(state, 0, false);
            }

            // Field order unknown; aspect ratio unknown, written 0:1 as FFmpeg expects (a 0 numerator is what RFC 9043 asks for).
            rc.WriteSymbol(state, 0, false);
            rc.WriteSymbol(state, 0, false);
            rc.WriteSymbol(state, 1, false);
            if (_owner._keyframe)
            {
                for (int i = 0; i < 3; i++)
                {
                    _coding[i].Reset(p, 0);
                }
            }

            if (p.CoderType == 0)
            {
                rc.FinishSentinel();
                var golomb = new GolombCoder(new Ffv1BitWriter(rc), _contexts, _diffs, _zeros);
                if (p.ColorSpace == 0)
                {
                    EncodeYCbCr(p, ref golomb);
                }
                else
                {
                    EncodeRgb(p, ref golomb);
                }

                golomb.Writer.Finish();
            }
            else
            {
                var range = new RangeCoder(rc);
                if (p.ColorSpace == 0)
                {
                    EncodeYCbCr(p, ref range);
                }
                else
                {
                    EncodeRgb(p, ref range);
                }

                // Sentinel mode here too, so a decoder that reads the closing symbol finds it ends where the footer begins.
                rc.FinishSentinel();
            }

            // Footer: the slice's size, its error status and the CRC parity that makes the whole slice's remainder zero.
            int size = rc.Length;
            Span<byte> footer = [(byte)(size >> 16), (byte)(size >> 8), (byte)size, 0];
            rc.Append(p.SliceCrc ? footer : footer[..3]);
            if (p.SliceCrc)
            {
                uint crc = Ffv1Crc.Compute(rc.Written);
                Span<byte> parity = [(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
                rc.Append(parity);
            }
        }

        private void EncodeYCbCr<TCoder>(Ffv1Parameters p, ref TCoder code)
            where TCoder : struct, ILineCoder
        {
            int planes = 1 + (p.ChromaPlanes ? 2 : 0) + (p.ExtraPlane ? 1 : 0);
            int bits = p.BitsPerRawSample;
            bool signed16 = bits == 16 && p.CoderType != 0;
            int hs = p.Log2HChromaSubsample;
            int vs = p.Log2VChromaSubsample;
            for (int plane = 0; plane < planes; plane++)
            {
                bool chroma = p.ChromaPlanes && plane is 1 or 2;
                int source = !p.ChromaPlanes && plane == 1 ? 3 : plane;
                int coding = source == 0 ? 0 : source == 3 ? 2 : 1;
                int x = chroma ? _x >> hs : _x;
                int y = chroma ? _y >> vs : _y;
                int w = chroma ? -(-_width >> hs) : _width;
                int h = chroma ? -(-_height >> vs) : _height;
                int[] cur = _lines[0], top = _lines[1], top2 = _lines[2];
                Array.Clear(top);
                Array.Clear(top2);
                for (int row = 0; row < h; row++)
                {
                    Load(source, y + row, x, cur, w);
                    code.Code(cur, top, top2, w, _coding[coding], bits, signed16, row == 0);
                    (top2, top, cur) = (top, cur, top2);
                }
            }
        }

        private void EncodeRgb<TCoder>(Ffv1Parameters p, ref TCoder code)
            where TCoder : struct, ILineCoder
        {
            int planes = p.ExtraPlane ? 4 : 3;
            int bits = p.BitsPerRawSample;
            bool bgr = bits is >= 9 and <= 15 && !p.ExtraPlane;
            int offset = 1 << bits;
            foreach (int[] line in _lines)
            {
                Array.Clear(line);
            }

            int rotation = 0;
            for (int row = 0; row < _height; row++)
            {
                int at = (rotation + 2) % 3;
                int[] yLine = _lines[at], cbLine = _lines[3 + at], crLine = _lines[6 + at], aLine = _lines[9 + at];
                Load(0, _y + row, _x, yLine, _width);
                Load(1, _y + row, _x, cbLine, _width);
                Load(2, _y + row, _x, crLine, _width);
                if (planes == 4)
                {
                    Load(3, _y + row, _x, aLine, _width);
                }

                // G, B, R into the reversible color transform (RFC 9043 section 3.7.2), offset to stay non-negative.
                for (int i = Ffv1Lines.Offset; i < Ffv1Lines.Offset + _width; i++)
                {
                    int g = yLine[i], b = cbLine[i], r = crLine[i];
                    (int first, int u, int v) = bgr ? (b, g - b, r - b) : (g, b - g, r - g);
                    yLine[i] = first + ((u + v) >> 2);
                    cbLine[i] = u + offset;
                    crLine[i] = v + offset;
                }

                for (int plane = 0; plane < planes; plane++)
                {
                    int r = rotation;
                    int[] cur = _lines[(plane * 3) + ((r + 2) % 3)];
                    int[] top = _lines[(plane * 3) + ((r + 1) % 3)];
                    int[] top2 = _lines[(plane * 3) + r];
                    code.Code(cur, top, top2, _width, _coding[plane == 0 ? 0 : plane == 3 ? 2 : 1], bits + 1, false, false);
                }

                rotation = (rotation + 1) % 3;
            }
        }

        /// <summary>Copies a row of a source plane (0 Y or G, 1 Cb or B, 2 Cr or R, 3 alpha) into a line buffer, at the coded depth.</summary>
        private void Load(int plane, int y, int x, int[] line, int width)
        {
            YuvRaster source = _owner._source!;
            int shift = _owner._shift;
            Span<int> target = line.AsSpan(Ffv1Lines.Offset, width);
            if (source is YuvRaster<byte> bytes)
            {
                YuvPlane<byte> p = plane switch { 0 => bytes.Y, 1 => bytes.U!, 2 => bytes.V!, _ => bytes.A! };
                ReadOnlySpan<byte> row = p.Row(Math.Min(y, p.Height - 1));
                int inside = Math.Clamp(p.Width - x, 0, width);
                ReadOnlySpan<byte> part = row.Slice(Math.Min(x, p.Width), inside);
                for (int i = 0; i < part.Length; i++)
                {
                    target[i] = part[i];
                }

                // A slice can reach a sample past a subsampled plane's edge; it repeats the last one, which no decoder shows.
                target[inside..].Fill(inside > 0 ? part[^1] : row[^1]);
            }
            else
            {
                var words = (YuvRaster<ushort>)source;
                YuvPlane<ushort> p = plane switch { 0 => words.Y, 1 => words.U!, 2 => words.V!, _ => words.A! };
                ReadOnlySpan<ushort> row = p.Row(Math.Min(y, p.Height - 1));
                int inside = Math.Clamp(p.Width - x, 0, width);
                ReadOnlySpan<ushort> part = row.Slice(Math.Min(x, p.Width), inside);
                for (int i = 0; i < part.Length; i++)
                {
                    target[i] = part[i] >> shift;
                }

                target[inside..].Fill((inside > 0 ? part[^1] : row[^1]) >> shift);
            }
        }
    }

    /// <summary>Codes one line; <c>newPlane</c> marks a YCbCr plane's first line, where Golomb-Rice resets its run index.</summary>
    private interface ILineCoder
    {
        void Code(int[] cur, int[] top, int[] top2, int width, Ffv1PlaneCoding coding, int bits, bool signed16, bool newPlane);
    }

    private readonly struct RangeCoder(Ffv1RangeEncoder rc) : ILineCoder
    {
        public void Code(int[] cur, int[] top, int[] top2, int width, Ffv1PlaneCoding coding, int bits, bool signed16, bool newPlane) =>
            Ffv1LineEncoder.EncodeRange(rc, cur, top, top2, width, coding, bits, signed16);
    }

    private struct GolombCoder(Ffv1BitWriter writer, int[] contexts, int[] diffs, int[] zeros) : ILineCoder
    {
        public Ffv1BitWriter Writer = writer;
        private int _runIndex;

        public void Code(int[] cur, int[] top, int[] top2, int width, Ffv1PlaneCoding coding, int bits, bool signed16, bool newPlane)
        {
            if (newPlane)
            {
                _runIndex = 0;
            }

            Ffv1LineEncoder.EncodeGolomb(ref Writer, ref _runIndex, cur, top, top2, width, coding, bits, signed16, contexts, diffs, zeros);
        }
    }
}
