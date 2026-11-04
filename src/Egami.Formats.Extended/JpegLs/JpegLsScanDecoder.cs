using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Extended.JpegLs;

/// <summary>Receives the lines a scan decodes.</summary>
internal abstract class JpegLsLineSink
{
    /// <summary>Takes line <paramref name="y"/> of frame component <paramref name="component"/>.</summary>
    public abstract void Line(int component, int y, ReadOnlySpan<int> samples);
}

/// <summary>
/// Decodes one scan (T.87 annex A, decoder side, as annex F describes it): its components' lines in interleave order, group by
/// group, with the context modelling, the regular and run modes, and restart intervals. A line group is one line of each of the
/// scan's components, or V lines of each when a line-interleaved scan has subsampled components.
/// </summary>
internal sealed class JpegLsScanDecoder
{
    private readonly byte[] _data;
    private readonly int _length;
    private readonly JpegLsHeader _header;
    private readonly Lane[] _lanes;
    private readonly JpegLsState _state;
    private readonly JpegLsParameters _p;
    private readonly int _interleave;
    private readonly int _groups;
    private readonly int _restart;
    private readonly Action<string, string> _warn;
    private readonly int[] _sampleQ;
    private JpegLsBitReader _reader;
    private int _segmentEnd;
    private int _group;
    private int _restartsSeen;
    private bool _skipToRestart;

    /// <summary>Reads the SOS segment whose length bytes are at <paramref name="sos"/> and gets ready to decode the scan.</summary>
    public JpegLsScanDecoder(byte[] data, int length, int sos, JpegLsHeader header, Action<string, string> warn)
    {
        _data = data;
        _length = length;
        _header = header;
        _warn = warn;
        int size = (data[sos] << 8) | data[sos + 1];
        int count = sos + 2 < length ? data[sos + 2] : 0;
        if (count < 1 || size < 6 + (2 * count) || sos + size > length)
        {
            throw new JpegLsDataException("The scan header is malformed.");
        }

        ReadOnlySpan<byte> body = data.AsSpan(sos + 3, size - 3);
        _lanes = new Lane[count];
        for (int i = 0; i < count; i++)
        {
            int id = body[2 * i];
            JpegLsComponent component = header.Components.FirstOrDefault(c => c.Id == id)
                ?? throw new JpegLsDataException($"The scan names component {id}, which the frame does not have.");
            component.Table = body[(2 * i) + 1];
            _lanes[i] = new Lane(component);
        }

        int near = body[2 * count];
        _interleave = body[(2 * count) + 1];
        PointTransform = body[(2 * count) + 2] & 15;
        if (_interleave > 2)
        {
            throw new JpegLsDataException($"The interleave mode {_interleave} is not defined.");
        }

        if (count == 1)
        {
            _interleave = 0;
        }
        else if (_interleave == 0)
        {
            throw new JpegLsDataException($"A scan of {count} components cannot be non-interleaved.");
        }

        if (_interleave == 2 && _lanes.Any(l => l.Component.H != header.HMax || l.Component.V != header.VMax))
        {
            throw new JpegLsDataException("A sample-interleaved scan has subsampled components.");
        }

        int bits = header.Bits - PointTransform;
        if (bits < 2)
        {
            throw new JpegLsDataException($"The point transform {PointTransform} leaves fewer than 2 bits.");
        }

        int maxVal = header.MaxVal > 0 ? Math.Min(header.MaxVal, (1 << header.Bits) - 1) >> PointTransform : 0;
        int fullMax = maxVal > 0 ? maxVal : (1 << bits) - 1;
        if (near > Math.Min(255, fullMax / 2))
        {
            throw new JpegLsDataException($"NEAR is {near}; with MAXVAL {fullMax} it can be at most {Math.Min(255, fullMax / 2)}.");
        }

        _p = JpegLsParameters.Create(bits, near, maxVal, header.T1, header.T2, header.T3, header.Reset);
        if (_p.T1 < near + 1 || _p.T2 < _p.T1 || _p.T3 < _p.T2 || _p.T3 > _p.MaxVal || _p.Reset < 3)
        {
            throw new JpegLsDataException($"The preset thresholds {_p.T1}, {_p.T2}, {_p.T3} and RESET {_p.Reset} are not valid.");
        }

        _state = new JpegLsState(_p);
        _sampleQ = new int[count];
        _restart = header.RestartInterval;
        Near = near;
        Interleave = _interleave;
        _groups = _interleave == 1 ? (header.Height + header.VMax - 1) / header.VMax : _lanes[0].Component.Height;
        foreach (Lane lane in _lanes)
        {
            lane.Lines = _interleave == 1 ? lane.Component.V : 1;
        }

        int start = sos + size;
        _segmentEnd = FindMarker(start);
        _reader = new JpegLsBitReader(data, start, _segmentEnd);
    }

    /// <summary>Gets NEAR.</summary>
    public int Near { get; }

    /// <summary>Gets the interleave mode: 0 none, 1 line, 2 sample.</summary>
    public int Interleave { get; }

    /// <summary>Gets the point transform, the low bits left out of every sample.</summary>
    public int PointTransform { get; }

    /// <summary>Gets the parameters the scan codes with.</summary>
    public JpegLsParameters Parameters => _p;

    /// <summary>Gets the frame components of the scan, in its order.</summary>
    public IEnumerable<JpegLsComponent> Components => _lanes.Select(l => l.Component);

    /// <summary>Gets the number of line groups in the scan.</summary>
    public int Groups => _groups;

    /// <summary>Gets whether every group has been decoded.</summary>
    public bool Done => _group >= _groups;

    /// <summary>
    /// Gets the position just after the scan's coded data once every group is decoded: the marker that follows it.
    /// </summary>
    public int End => _segmentEnd;

    /// <summary>
    /// Decodes the next line group into <paramref name="sink"/>. Damaged data inside a restart interval is skipped to the next
    /// restart marker, its lines given as zeros, when <paramref name="strict"/> is off; otherwise it throws.
    /// </summary>
    public void DecodeGroup(JpegLsLineSink sink, bool strict)
    {
        if (_restart > 0 && _group > 0 && _group % _restart == 0)
        {
            NextInterval(strict);
        }

        if (!_skipToRestart)
        {
            try
            {
                DecodeLines(sink);
                if (_reader.Padding > 8)
                {
                    throw new JpegLsDataException("The coded data ends before the scan does.");
                }

                _group++;
                return;
            }
            catch (JpegLsDataException) when (!strict && _restart > 0 && (_group / _restart) + 1 < (_groups + _restart - 1) / _restart)
            {
                // Recover at the next restart marker.
                _warn("IntervalDamaged", $"A restart interval of the scan is damaged; its lines from group {_group} on are left black.");
                _skipToRestart = true;
            }
        }

        foreach (Lane lane in _lanes)
        {
            for (int i = 0; i < lane.Lines && lane.Y < lane.Component.Height; i++)
            {
                sink.Line(lane.Component.Index, lane.Y++, new int[lane.Component.Width]);
            }
        }

        _group++;
    }

    /// <summary>Returns the index of the next marker at or after <paramref name="at"/>: 0xFF followed by a byte of 0x80 or more.</summary>
    private int FindMarker(int at)
    {
        ReadOnlySpan<byte> data = _data.AsSpan(0, _length);
        while (at < _length)
        {
            int ff = data[at..].IndexOf((byte)0xFF);
            if (ff < 0)
            {
                return _length;
            }

            at += ff;
            if (at + 1 >= _length || data[at + 1] >= 0x80)
            {
                return at;
            }

            at++;
        }

        return _length;
    }

    /// <summary>Moves to the coded data after the next restart marker and resets the contexts and the previous lines.</summary>
    private void NextInterval(bool strict)
    {
        int expected = JpegLsMarker.Rst0 + (_restartsSeen & 7);
        int at = _segmentEnd;
        while (at + 1 < _length && _data[at + 1] == 0xFF)
        {
            at++;
        }

        if (_skipToRestart || at + 1 >= _length || _data[at + 1] != expected)
        {
            if (strict && !_skipToRestart)
            {
                throw new JpegLsDataException($"Restart marker RST{expected - JpegLsMarker.Rst0} is missing.");
            }

            // Find the marker this interval should start with.
            int search = _skipToRestart ? _reader.Position : at;
            at = -1;
            while (search + 1 < _length)
            {
                int m = FindMarker(search);
                if (m + 1 >= _length)
                {
                    break;
                }

                if (_data[m + 1] == expected)
                {
                    at = m;
                    break;
                }

                if (_data[m + 1] is < JpegLsMarker.Rst0 or > JpegLsMarker.Rst0 + 7)
                {
                    break;
                }

                search = m + 2;
            }

            if (at < 0)
            {
                throw new JpegLsDataException($"Restart marker RST{expected - JpegLsMarker.Rst0} is missing.");
            }

            if (!_skipToRestart)
            {
                _warn("RestartMissing", $"Restart marker RST{expected - JpegLsMarker.Rst0} is not where the interval ends; decoding went on after it.");
            }
        }

        _restartsSeen++;
        _skipToRestart = false;
        int start = at + 2;
        _segmentEnd = FindMarker(start);
        _reader = new JpegLsBitReader(_data, start, _segmentEnd);
        _state.Reset();
        foreach (Lane lane in _lanes)
        {
            lane.Clear();
        }
    }

    private void DecodeLines(JpegLsLineSink sink)
    {
        if (_interleave == 2)
        {
            DecodeSampleLine();
            int y = _lanes[0].Y++;
            foreach (Lane lane in _lanes)
            {
                lane.Swap();
                sink.Line(lane.Component.Index, y, lane.Previous.AsSpan(1, lane.Component.Width));
            }

            return;
        }

        foreach (Lane lane in _lanes)
        {
            for (int i = 0; i < lane.Lines && lane.Y < lane.Component.Height; i++)
            {
                DecodeLine(lane);
                lane.Swap();
                sink.Line(lane.Component.Index, lane.Y++, lane.Previous.AsSpan(1, lane.Component.Width));
            }
        }
    }

    /// <summary>Decodes one line of one component, in regular and run mode (T.87 A.3 to A.7).</summary>
    private void DecodeLine(Lane lane)
    {
        int width = lane.Component.Width;
        int[] previousLine = lane.Previous;
        int[] currentLine = lane.Current;
        currentLine[0] = previousLine[1];
        previousLine[width + 1] = previousLine[width];
        ref int prev = ref MemoryMarshal.GetArrayDataReference(previousLine);
        ref int cur = ref MemoryMarshal.GetArrayDataReference(currentLine);
        JpegLsParameters p = _p;
        int near = p.Near;
        int maxVal = p.MaxVal;
        int limit = p.Limit;
        int qbpp = p.Qbpp;
        int reset = p.Reset;
        int step = (2 * near) + 1;
        int wrap = p.Range * step;
        ref sbyte quantizer = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(p.Quantizer), maxVal);
        ref JpegLsContext contexts = ref MemoryMarshal.GetArrayDataReference(_state.Contexts);
        JpegLsBitReader reader = _reader;
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
                x = DecodeRun(ref reader, ref prev, ref cur, x, width, ref lane.RunIndex);
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
            int px = JpegLsState.Predict(ra, rb, rc) + (((context.C ^ sign) - sign));
            px = px < 0 ? 0 : px > maxVal ? maxVal : px;
            int k = JpegLsState.GolombK(context.N, context.A);
            int mapped = reader.Golomb(k, limit, qbpp);
            int error = (mapped >> 1) ^ -(mapped & 1);
            if (k == 0 && near == 0 && 2 * context.B <= -context.N)
            {
                error = ~error;
            }

            JpegLsState.Update(ref context, error, near, reset);
            int rx = px + (((error ^ sign) - sign) * step);
            if (rx < -near)
            {
                rx += wrap;
            }
            else if (rx > maxVal + near)
            {
                rx -= wrap;
            }

            rx = rx < 0 ? 0 : rx > maxVal ? maxVal : rx;
            Unsafe.Add(ref cur, x) = rx;
            rc = rb;
            rb = rd;
            ra = rx;
            x++;
        }

        _reader = reader;
    }

    /// <summary>Decodes a run from <paramref name="x"/> and the sample that ends it, if any; returns the next position.</summary>
    private int DecodeRun(ref JpegLsBitReader reader, ref int prev, ref int cur, int x, int width, ref int runIndex)
    {
        int ra = Unsafe.Add(ref cur, x - 1);
        ReadOnlySpan<byte> j = JpegLsState.J;
        while (true)
        {
            if (reader.ReadBit() == 1)
            {
                int count = 1 << j[runIndex];
                int left = width - x + 1;
                if (count <= left)
                {
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref cur, x), count).Fill(ra);
                    x += count;
                    if (runIndex < 31)
                    {
                        runIndex++;
                    }

                    if (x > width)
                    {
                        return x;
                    }
                }
                else
                {
                    MemoryMarshal.CreateSpan(ref Unsafe.Add(ref cur, x), left).Fill(ra);
                    return width + 1;
                }
            }
            else
            {
                int count = reader.Read(j[runIndex]);
                if (count >= width - x + 1)
                {
                    throw new JpegLsDataException("A run passes the end of its line.");
                }

                MemoryMarshal.CreateSpan(ref Unsafe.Add(ref cur, x), count).Fill(ra);
                x += count;
                int rb = Unsafe.Add(ref prev, x);
                Unsafe.Add(ref cur, x) = DecodeInterruption(ref reader, ra, rb, Math.Abs(ra - rb) <= _p.Near ? 1 : 0, runIndex);
                if (runIndex > 0)
                {
                    runIndex--;
                }

                return x + 1;
            }
        }
    }

    /// <summary>Decodes the sample that interrupts a run (T.87 A.7.2), of interruption type <paramref name="type"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int DecodeInterruption(ref JpegLsBitReader reader, int ra, int rb, int type, int runIndex)
    {
        JpegLsParameters p = _p;
        ref JpegLsRunContext context = ref _state.RunContexts[type];
        int k = JpegLsState.RunK(context, type);
        int mapped = reader.Golomb(k, p.Limit - JpegLsState.J[runIndex] - 1, p.Qbpp);
        int t = mapped + type;
        int map = t & 1;
        int magnitude = (t + map) >> 1;
        bool flip = k == 0 && 2 * context.Nn < context.N;
        int error = (map == 1) != flip ? -magnitude : magnitude;
        JpegLsState.UpdateRun(ref context, error, mapped, type, p.Reset);
        int px = type == 1 ? ra : rb;
        if (type == 0 && ra > rb)
        {
            error = -error;
        }

        int step = (2 * p.Near) + 1;
        int rx = px + (error * step);
        if (rx < -p.Near)
        {
            rx += p.Range * step;
        }
        else if (rx > p.MaxVal + p.Near)
        {
            rx -= p.Range * step;
        }

        return rx < 0 ? 0 : rx > p.MaxVal ? p.MaxVal : rx;
    }

    /// <summary>Decodes one line of every component, sample-interleaved (T.87 A.8 for interleave mode 2).</summary>
    private void DecodeSampleLine()
    {
        Lane[] lanes = _lanes;
        int n = lanes.Length;
        int width = lanes[0].Component.Width;
        foreach (Lane lane in lanes)
        {
            lane.Current[0] = lane.Previous[1];
            lane.Previous[width + 1] = lane.Previous[width];
        }

        JpegLsParameters p = _p;
        int near = p.Near;
        int maxVal = p.MaxVal;
        int step = (2 * near) + 1;
        int wrap = p.Range * step;
        sbyte[] quantizer = p.Quantizer;
        int[] qs = _sampleQ;
        JpegLsContext[] contexts = _state.Contexts;
        JpegLsBitReader reader = _reader;
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
                x = DecodeSampleRun(ref reader, x, width, ref runIndex);
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
                int k = JpegLsState.GolombK(context.N, context.A);
                int mapped = reader.Golomb(k, p.Limit, p.Qbpp);
                int error = (mapped >> 1) ^ -(mapped & 1);
                if (k == 0 && near == 0 && 2 * context.B <= -context.N)
                {
                    error = ~error;
                }

                JpegLsState.Update(ref context, error, near, p.Reset);
                int rx = px + (((error ^ sign) - sign) * step);
                if (rx < -near)
                {
                    rx += wrap;
                }
                else if (rx > maxVal + near)
                {
                    rx -= wrap;
                }

                cur[x] = rx < 0 ? 0 : rx > maxVal ? maxVal : rx;
            }

            x++;
        }

        _reader = reader;
    }

    /// <summary>Decodes a run of whole pixels and the pixel that ends it, sample-interleaved; returns the next position.</summary>
    private int DecodeSampleRun(ref JpegLsBitReader reader, int x, int width, ref int runIndex)
    {
        Lane[] lanes = _lanes;
        ReadOnlySpan<byte> j = JpegLsState.J;
        while (true)
        {
            if (reader.ReadBit() == 1)
            {
                int count = 1 << j[runIndex];
                int left = width - x + 1;
                bool full = count <= left;
                count = Math.Min(count, left);
                foreach (Lane lane in lanes)
                {
                    lane.Current.AsSpan(x, count).Fill(lane.Current[x - 1]);
                }

                x += count;
                if (!full)
                {
                    return x;
                }

                if (runIndex < 31)
                {
                    runIndex++;
                }

                if (x > width)
                {
                    return x;
                }
            }
            else
            {
                int count = reader.Read(j[runIndex]);
                if (count >= width - x + 1)
                {
                    throw new JpegLsDataException("A run passes the end of its line.");
                }

                foreach (Lane lane in lanes)
                {
                    lane.Current.AsSpan(x, count).Fill(lane.Current[x - 1]);
                }

                x += count;
                foreach (Lane lane in lanes)
                {
                    int ra = lane.Current[x - 1], rb = lane.Previous[x];
                    lane.Current[x] = DecodeInterruption(ref reader, ra, rb, SampleRunType(ra, rb), runIndex);
                }

                if (runIndex > 0)
                {
                    runIndex--;
                }

                return x + 1;
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int SampleRunType(int ra, int rb) => 0;

    /// <summary>The lines of one component of the scan.</summary>
    private sealed class Lane(JpegLsComponent component)
    {
        public int RunIndex;

        public JpegLsComponent Component { get; } = component;

        /// <summary>Gets the line above, with one sample of margin on each side.</summary>
        public int[] Previous { get; private set; } = new int[component.Width + 2];

        /// <summary>Gets the line being decoded, with one sample of margin on each side.</summary>
        public int[] Current { get; private set; } = new int[component.Width + 2];

        /// <summary>Gets or sets how many lines of this component a group holds.</summary>
        public int Lines { get; set; } = 1;

        /// <summary>Gets or sets the next line of the component.</summary>
        public int Y { get; set; }

        public void Swap() => (Previous, Current) = (Current, Previous);

        public void Clear()
        {
            Array.Clear(Previous);
            Array.Clear(Current);
            RunIndex = 0;
        }
    }
}
