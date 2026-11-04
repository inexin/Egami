using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Extended.JpegLs;

/// <summary>
/// Codes samples as JPEG-LS (T.87 annex A, encoder side): the header, then the rows as they come for line and sample
/// interleave, or every row kept and one scan per component at the end for no interleave. Rows hold
/// <see cref="Components"/> samples a pixel, side by side.
/// </summary>
internal sealed class JpegLsEncoder
{
    private const int FlushBytes = 64 * 1024;

    private readonly Stream _output;
    private readonly JpegLsBitWriter _writer;
    private readonly JpegLsParameters _p;
    private readonly JpegLsState _state;
    private readonly int _width;
    private readonly int _height;
    private readonly int _bits;
    private readonly int _near;
    private readonly JpegLsInterleave _interleave;
    private readonly JpegLsColorTransform _transform;
    private readonly int _restart;
    private readonly Lane[] _lanes;
    private readonly int[] _sampleQ;
    private readonly ushort[]? _planes;
    private readonly ushort[] _row;
    private int _y;
    private int _restartsWritten;

    public JpegLsEncoder(Stream output, int width, int height, int components, int bits, int near, JpegLsInterleave interleave,
        JpegLsColorTransform transform, int restart)
    {
        _output = output;
        _width = width;
        _height = height;
        _bits = bits;
        _near = near;
        _interleave = components == 1 ? JpegLsInterleave.None : interleave;
        _transform = components >= 3 && near == 0 && _interleave != JpegLsInterleave.None ? transform : JpegLsColorTransform.None;
        _restart = restart;
        Components = components;
        _p = JpegLsParameters.Create(bits, near);
        _state = new JpegLsState(_p);
        _lanes = [.. Enumerable.Range(0, components).Select(_ => new Lane(width))];
        _sampleQ = new int[components];
        _row = new ushort[width * components];
        if (_interleave == JpegLsInterleave.None && components > 1)
        {
            _planes = new ushort[(long)width * height * components];
        }

        _writer = new JpegLsBitWriter(Math.Min(FlushBytes * 2, (width * components * 2) + 1024));
        WriteHeader();
        if (_planes is null)
        {
            WriteScanHeader(0, components);
        }
    }

    /// <summary>Gets the samples a pixel has.</summary>
    public int Components { get; }

    /// <summary>Codes the next row, <see cref="Components"/> samples a pixel, each below 2^bits.</summary>
    public void Row(ReadOnlySpan<ushort> samples)
    {
        if (_transform != JpegLsColorTransform.None)
        {
            samples.CopyTo(_row);
            JpegLsColorTransforms.Forward(_transform, _row, Components, _width, _bits);
            samples = _row;
        }

        if (_planes is not null)
        {
            samples.CopyTo(_planes.AsSpan(_y * samples.Length));
            _y++;
            return;
        }

        if (_restart > 0 && _y > 0 && _y % _restart == 0)
        {
            Restart();
        }

        if (_interleave == JpegLsInterleave.Sample)
        {
            for (int c = 0; c < Components; c++)
            {
                Lane lane = _lanes[c];
                for (int x = 0, at = c; x < _width; x++, at += Components)
                {
                    lane.Input[x] = samples[at];
                }
            }

            EncodeSampleLine();
            foreach (Lane lane in _lanes)
            {
                lane.Swap();
            }
        }
        else
        {
            for (int c = 0; c < Components; c++)
            {
                Lane lane = _lanes[c];
                for (int x = 0, at = c; x < _width; x++, at += Components)
                {
                    lane.Input[x] = samples[at];
                }

                EncodeLine(lane);
                lane.Swap();
            }
        }

        _y++;
        if (_writer.Written.Length >= FlushBytes)
        {
            _writer.MoveTo(_output);
        }
    }

    /// <summary>Codes what remains and ends the stream with EOI.</summary>
    public void Complete()
    {
        if (_planes is not null)
        {
            int pixels = _width * _height;
            for (int c = 0; c < Components; c++)
            {
                if (c > 0)
                {
                    _writer.Flush();
                }

                WriteScanHeader(c, 1);
                _state.Reset();
                Lane lane = _lanes[c];
                lane.Clear();
                for (int y = 0; y < _height; y++)
                {
                    if (_restart > 0 && y > 0 && y % _restart == 0)
                    {
                        Restart();
                    }

                    int start = y * _width * Components;
                    for (int x = 0, at = start + c; x < _width; x++, at += Components)
                    {
                        lane.Input[x] = _planes[at];
                    }

                    EncodeLine(lane);
                    lane.Swap();
                    if (_writer.Written.Length >= FlushBytes)
                    {
                        _writer.MoveTo(_output);
                    }
                }

                _restartsWritten = 0;
            }
        }

        _writer.Flush();
        _writer.Raw([0xFF, JpegLsMarker.Eoi]);
        _writer.MoveTo(_output);
    }

    private void WriteHeader()
    {
        Span<byte> segment = stackalloc byte[6 + 3 + (3 * 255) + 2];
        _writer.Raw([0xFF, JpegLsMarker.Soi]);
        if (_transform != JpegLsColorTransform.None)
        {
            _writer.Raw([0xFF, JpegLsMarker.App8, 0, 7, (byte)'m', (byte)'r', (byte)'f', (byte)'x', (byte)_transform]);
        }

        bool oversize = _width > ushort.MaxValue || _height > ushort.MaxValue;
        int length = 6 + (3 * Components);
        segment[0] = 0xFF;
        segment[1] = JpegLsMarker.Sof55;
        BinaryPrimitives.WriteUInt16BigEndian(segment[2..], (ushort)(length + 2));
        segment[4] = (byte)_bits;
        BinaryPrimitives.WriteUInt16BigEndian(segment[5..], oversize ? (ushort)0 : (ushort)_height);
        BinaryPrimitives.WriteUInt16BigEndian(segment[7..], oversize ? (ushort)0 : (ushort)_width);
        segment[9] = (byte)Components;
        for (int c = 0; c < Components; c++)
        {
            segment[10 + (3 * c)] = (byte)(c + 1);
            segment[11 + (3 * c)] = 0x11;
            segment[12 + (3 * c)] = 0;
        }

        _writer.Raw(segment[..(length + 4)]);
        if (oversize)
        {
            // LSE type 4: 32-bit dimensions for the zeros in the frame header (T.87 C.2.4.1.4).
            Span<byte> lse = [0xFF, JpegLsMarker.Lse, 0, 12, 4, 4, 0, 0, 0, 0, 0, 0, 0, 0];
            BinaryPrimitives.WriteUInt32BigEndian(lse[6..], (uint)_height);
            BinaryPrimitives.WriteUInt32BigEndian(lse[10..], (uint)_width);
            _writer.Raw(lse);
        }

        if (_restart > 0)
        {
            if (_restart <= ushort.MaxValue)
            {
                _writer.Raw([0xFF, JpegLsMarker.Dri, 0, 4, (byte)(_restart >> 8), (byte)_restart]);
            }
            else
            {
                _writer.Raw([0xFF, JpegLsMarker.Dri, 0, 6, (byte)(_restart >> 24), (byte)(_restart >> 16), (byte)(_restart >> 8), (byte)_restart]);
            }
        }
    }

    private void WriteScanHeader(int first, int count)
    {
        Span<byte> segment = stackalloc byte[2 + 2 + 1 + (2 * 255) + 3];
        int length = 6 + (2 * count);
        segment[0] = 0xFF;
        segment[1] = JpegLsMarker.Sos;
        BinaryPrimitives.WriteUInt16BigEndian(segment[2..], (ushort)length);
        segment[4] = (byte)count;
        for (int i = 0; i < count; i++)
        {
            segment[5 + (2 * i)] = (byte)(first + i + 1);
            segment[6 + (2 * i)] = 0;
        }

        segment[5 + (2 * count)] = (byte)_near;
        segment[6 + (2 * count)] = (byte)(count == 1 ? 0 : (int)_interleave);
        segment[7 + (2 * count)] = 0;
        _writer.Raw(segment[..(length + 2)]);
    }

    /// <summary>Ends a restart interval: pads the bits, writes RSTm and starts the contexts and lines afresh.</summary>
    private void Restart()
    {
        _writer.Flush();
        _writer.Raw([0xFF, (byte)(JpegLsMarker.Rst0 + (_restartsWritten & 7))]);
        _restartsWritten++;
        _state.Reset();
        foreach (Lane lane in _lanes)
        {
            lane.Clear();
        }
    }

    /// <summary>Codes one line of one component in regular and run mode (T.87 A.3 to A.7).</summary>
    private void EncodeLine(Lane lane)
    {
        int width = _width;
        int[] previousLine = lane.Previous;
        int[] currentLine = lane.Current;
        currentLine[0] = previousLine[1];
        previousLine[width + 1] = previousLine[width];
        ref int prev = ref MemoryMarshal.GetArrayDataReference(previousLine);
        ref int cur = ref MemoryMarshal.GetArrayDataReference(currentLine);
        ref int input = ref Unsafe.Subtract(ref MemoryMarshal.GetArrayDataReference(lane.Input), 1);
        JpegLsParameters p = _p;
        int near = p.Near;
        int maxVal = p.MaxVal;
        int range = p.Range;
        int limit = p.Limit;
        int qbpp = p.Qbpp;
        int reset = p.Reset;
        int step = (2 * near) + 1;
        ref sbyte quantizer = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(p.Quantizer), maxVal);
        ref JpegLsContext contexts = ref MemoryMarshal.GetArrayDataReference(_state.Contexts);
        JpegLsBitWriter writer = _writer;
        int x = 1;
        int ra = cur;
        int rb = Unsafe.Add(ref prev, 1);
        int rc = prev;
        while (x <= width)
        {
            int rd = Unsafe.Add(ref prev, x + 1);
            int q = (81 * Unsafe.Add(ref quantizer, rd - rb)) + (9 * Unsafe.Add(ref quantizer, rb - rc)) + Unsafe.Add(ref quantizer, rc - ra);
            if (q == 0)
            {
                x = EncodeRun(ref prev, ref cur, ref input, x, width, ref lane.RunIndex);
                if (x > width)
                {
                    break;
                }

                ra = Unsafe.Add(ref cur, x - 1);
                rb = Unsafe.Add(ref prev, x);
                rc = Unsafe.Add(ref prev, x - 1);
                continue;
            }

            int sign = q >> 31;
            q = (q ^ sign) - sign;
            ref JpegLsContext context = ref Unsafe.Add(ref contexts, q);
            int px = JpegLsState.Predict(ra, rb, rc) + ((context.C ^ sign) - sign);
            px = px < 0 ? 0 : px > maxVal ? maxVal : px;
            int ix = Unsafe.Add(ref input, x);
            int error = ((ix - px) ^ sign) - sign;
            int rx = ix;
            if (near > 0)
            {
                error = error > 0 ? (error + near) / step : -((near - error) / step);
                rx = px + (((error ^ sign) - sign) * step);
                rx = rx < 0 ? 0 : rx > maxVal ? maxVal : rx;
            }

            if (error < 0)
            {
                error += range;
            }

            if (error >= (range + 1) >> 1)
            {
                error -= range;
            }

            int k = JpegLsState.GolombK(context.N, context.A);
            int mapped = error >= 0 ? 2 * error : (-2 * error) - 1;
            if (k == 0 && near == 0 && 2 * context.B <= -context.N)
            {
                mapped = error >= 0 ? (2 * error) + 1 : -2 * (error + 1);
            }

            writer.Golomb(mapped, k, limit, qbpp);
            JpegLsState.Update(ref context, error, near, reset);
            Unsafe.Add(ref cur, x) = rx;
            rc = rb;
            rb = rd;
            ra = rx;
            x++;
        }
    }

    /// <summary>Codes a run from <paramref name="x"/> and the sample that ends it, if any; returns the next position.</summary>
    private int EncodeRun(ref int prev, ref int cur, ref int input, int x, int width, ref int runIndex)
    {
        int ra = Unsafe.Add(ref cur, x - 1);
        int near = _p.Near;
        int end = x;
        while (end <= width && Math.Abs(Unsafe.Add(ref input, end) - ra) <= near)
        {
            Unsafe.Add(ref cur, end) = ra;
            end++;
        }

        int count = end - x;
        WriteRunLength(count, end > width, ref runIndex);
        if (end > width)
        {
            return end;
        }

        int rb = Unsafe.Add(ref prev, end);
        Unsafe.Add(ref cur, end) = EncodeInterruption(ra, rb, Unsafe.Add(ref input, end), Math.Abs(ra - rb) <= near ? 1 : 0, runIndex);
        if (runIndex > 0)
        {
            runIndex--;
        }

        return end + 1;
    }

    /// <summary>Writes a run of <paramref name="count"/> samples (T.87 A.7.1.2).</summary>
    private void WriteRunLength(int count, bool endOfLine, ref int runIndex)
    {
        ReadOnlySpan<byte> j = JpegLsState.J;
        while (count >= 1 << j[runIndex])
        {
            _writer.Write(1, 1);
            count -= 1 << j[runIndex];
            if (runIndex < 31)
            {
                runIndex++;
            }
        }

        if (endOfLine)
        {
            if (count > 0)
            {
                _writer.Write(1, 1);
            }
        }
        else
        {
            _writer.Write((uint)count, j[runIndex] + 1);
        }
    }

    /// <summary>Codes the sample that interrupts a run (T.87 A.7.2) and returns its reconstructed value.</summary>
    private int EncodeInterruption(int ra, int rb, int ix, int type, int runIndex)
    {
        JpegLsParameters p = _p;
        int near = p.Near;
        int step = (2 * near) + 1;
        int px = type == 1 ? ra : rb;
        int error = ix - px;
        int sign = type == 0 && ra > rb ? -1 : 1;
        error *= sign;
        int rx = ix;
        if (near > 0)
        {
            error = error > 0 ? (error + near) / step : -((near - error) / step);
            rx = px + (sign * error * step);
            rx = rx < 0 ? 0 : rx > p.MaxVal ? p.MaxVal : rx;
        }

        if (error < 0)
        {
            error += p.Range;
        }

        if (error >= (p.Range + 1) >> 1)
        {
            error -= p.Range;
        }

        ref JpegLsRunContext context = ref _state.RunContexts[type];
        int k = JpegLsState.RunK(context, type);
        bool map = (k == 0 && error > 0 && 2 * context.Nn < context.N) || (error < 0 && 2 * context.Nn >= context.N) || (error < 0 && k != 0);
        int mapped = (2 * Math.Abs(error)) - type - (map ? 1 : 0);
        _writer.Golomb(mapped, k, p.Limit - JpegLsState.J[runIndex] - 1, p.Qbpp);
        JpegLsState.UpdateRun(ref context, error, mapped, type, p.Reset);
        return rx;
    }

    /// <summary>Codes one line of every component, sample-interleaved.</summary>
    private void EncodeSampleLine()
    {
        Lane[] lanes = _lanes;
        int n = lanes.Length;
        int width = _width;
        foreach (Lane lane in lanes)
        {
            lane.Current[0] = lane.Previous[1];
            lane.Previous[width + 1] = lane.Previous[width];
        }

        JpegLsParameters p = _p;
        int near = p.Near;
        int maxVal = p.MaxVal;
        int range = p.Range;
        int step = (2 * near) + 1;
        sbyte[] quantizer = p.Quantizer;
        int[] qs = _sampleQ;
        JpegLsContext[] contexts = _state.Contexts;
        ref int runIndex = ref lanes[0].RunIndex;
        int x = 1;
        while (x <= width)
        {
            bool run = true;
            for (int c = 0; c < n; c++)
            {
                int[] prev = lanes[c].Previous;
                int[] cur = lanes[c].Current;
                int ra = cur[x - 1], rb = prev[x], rc = prev[x - 1], rd = prev[x + 1];
                int q = (81 * quantizer[rd - rb + maxVal]) + (9 * quantizer[rb - rc + maxVal]) + quantizer[rc - ra + maxVal];
                qs[c] = q;
                run &= q == 0;
            }

            if (run)
            {
                x = EncodeSampleRun(x, width, ref runIndex);
                continue;
            }

            for (int c = 0; c < n; c++)
            {
                int[] prev = lanes[c].Previous;
                int[] cur = lanes[c].Current;
                int ra = cur[x - 1], rb = prev[x], rc = prev[x - 1];
                int q = qs[c];
                int sign = q >> 31;
                q = (q ^ sign) - sign;
                ref JpegLsContext context = ref contexts[q];
                int px = JpegLsState.Predict(ra, rb, rc) + ((context.C ^ sign) - sign);
                px = px < 0 ? 0 : px > maxVal ? maxVal : px;
                int ix = lanes[c].Input[x - 1];
                int error = ((ix - px) ^ sign) - sign;
                int rx = ix;
                if (near > 0)
                {
                    error = error > 0 ? (error + near) / step : -((near - error) / step);
                    rx = px + (((error ^ sign) - sign) * step);
                    rx = rx < 0 ? 0 : rx > maxVal ? maxVal : rx;
                }

                if (error < 0)
                {
                    error += range;
                }

                if (error >= (range + 1) >> 1)
                {
                    error -= range;
                }

                int k = JpegLsState.GolombK(context.N, context.A);
                int mapped = error >= 0 ? 2 * error : (-2 * error) - 1;
                if (k == 0 && near == 0 && 2 * context.B <= -context.N)
                {
                    mapped = error >= 0 ? (2 * error) + 1 : -2 * (error + 1);
                }

                _writer.Golomb(mapped, k, p.Limit, p.Qbpp);
                JpegLsState.Update(ref context, error, near, p.Reset);
                cur[x] = rx;
            }

            x++;
        }
    }

    /// <summary>Codes a run of whole pixels and the pixel that ends it, sample-interleaved; returns the next position.</summary>
    private int EncodeSampleRun(int x, int width, ref int runIndex)
    {
        Lane[] lanes = _lanes;
        int near = _p.Near;
        int end = x;
        while (end <= width)
        {
            bool same = true;
            foreach (Lane lane in lanes)
            {
                same &= Math.Abs(lane.Input[end - 1] - lane.Current[x - 1]) <= near;
            }

            if (!same)
            {
                break;
            }

            foreach (Lane lane in lanes)
            {
                lane.Current[end] = lane.Current[x - 1];
            }

            end++;
        }

        WriteRunLength(end - x, end > width, ref runIndex);
        if (end > width)
        {
            return end;
        }

        foreach (Lane lane in lanes)
        {
            int ra = lane.Current[end - 1], rb = lane.Previous[end];
            lane.Current[end] = EncodeInterruption(ra, rb, lane.Input[end - 1], 0, runIndex);
        }

        if (runIndex > 0)
        {
            runIndex--;
        }

        return end + 1;
    }

    /// <summary>The lines of one component.</summary>
    private sealed class Lane(int width)
    {
        public int RunIndex;

        /// <summary>Gets the samples of the line being coded.</summary>
        public int[] Input { get; } = new int[width];

        /// <summary>Gets the reconstructed line above, with one sample of margin on each side.</summary>
        public int[] Previous { get; private set; } = new int[width + 2];

        /// <summary>Gets the reconstructed line being coded, with one sample of margin on each side.</summary>
        public int[] Current { get; private set; } = new int[width + 2];

        public void Swap() => (Previous, Current) = (Current, Previous);

        public void Clear()
        {
            Array.Clear(Previous);
            Array.Clear(Current);
            RunIndex = 0;
        }
    }
}
