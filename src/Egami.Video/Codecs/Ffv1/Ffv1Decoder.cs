using System.Buffers.Binary;

using Egami.Pixels;

namespace Egami.Video.Codecs.Ffv1;

/// <summary>A slice that could not be decoded: where it is and why.</summary>
internal readonly record struct Ffv1SliceError(int Slice, int X, int Y, int Width, int Height, string Reason, bool CrcMismatch);

/// <summary>What a decoded FFV1 frame says about itself.</summary>
internal readonly record struct Ffv1FrameInfo(bool Keyframe, int PictureStructure, int SarNumerator, int SarDenominator);

/// <summary>
/// Decodes FFV1 frames (RFC 9043) into <see cref="YuvRaster"/> planes: versions 0, 1 and 3, both coders, every layout. Planes keep
/// their samples; depths 9, 11, 13, 14 and 15 sit in the next supported depth with the samples shifted up, and RGB comes out as
/// G, B and R planes with <see cref="YuvMatrix.Identity"/>. A version 3 frame's slices are found from their footers, checked
/// against their CRCs and decoded on their own, so a damaged slice leaves the others intact.
/// </summary>
internal sealed class Ffv1Decoder
{
    private readonly int _width;
    private readonly int _height;
    private readonly Ffv1Parameters? _config;
    private Ffv1Parameters? _parameters;
    private readonly List<(int Start, int End, int FooterEnd)> _spans = [];
    private readonly ParallelOptions _options = new();
    private readonly Action<int> _checkSlice;
    private readonly Action<int> _decodeSlice;
    private SliceContext[] _slices = [];
    private SliceJob[] _jobs = [];
    private bool[] _crcOk = [];
    private bool[] _used = [];
    private byte[]? _frame;
    private Ffv1Target? _target;
    private bool _haveKeyframe;

    /// <summary>Creates a decoder for frames of the given size.</summary>
    /// <param name="width">The frame width the container gives.</param>
    /// <param name="height">The frame height the container gives.</param>
    /// <param name="configurationRecord">The version 3 configuration record, or empty for versions 0 and 1.</param>
    /// <param name="configCrcOk">Whether the record's CRC matched (true when there is none).</param>
    public Ffv1Decoder(int width, int height, ReadOnlySpan<byte> configurationRecord, out bool configCrcOk)
    {
        if (width < 1 || height < 1)
        {
            throw new CorruptDataException("FFV1", -1, $"An FFV1 track needs its picture size from the container; it gives {width}×{height}.");
        }

        _width = width;
        _height = height;
        _checkSlice = CheckSlice;
        _decodeSlice = DecodeSlice;
        configCrcOk = true;
        if (HasConfigurationRecord(configurationRecord))
        {
            _config = Ffv1Parameters.ReadConfigurationRecord(configurationRecord, out configCrcOk);
            _parameters = _config;
        }
    }

    /// <summary>Gets the parameters of the stream, once the configuration record or a keyframe gave them.</summary>
    public Ffv1Parameters? Parameters => _parameters;

    /// <summary>Gets the chroma format a parameter set decodes to, or null when Egami has no layout for it.</summary>
    public static ChromaFormat? ChromaFormatOf(Ffv1Parameters p)
    {
        if (p.ColorSpace == 1)
        {
            return ChromaFormat.Yuv444;
        }

        if (!p.ChromaPlanes)
        {
            return ChromaFormat.Yuv400;
        }

        return (p.Log2HChromaSubsample, p.Log2VChromaSubsample) switch
        {
            (1, 1) => ChromaFormat.Yuv420,
            (1, 0) => ChromaFormat.Yuv422,
            (0, 0) => ChromaFormat.Yuv444,
            (2, 0) => ChromaFormat.Yuv411,
            (2, 2) => ChromaFormat.Yuv410,
            _ => null,
        };
    }

    /// <summary>Gets the bit depth Egami stores a sample depth in: 8, 10, 12 or 16.</summary>
    public static int StorageDepth(int bits) => bits <= 8 ? 8 : bits <= 10 ? 10 : bits <= 12 ? 12 : 16;

    /// <summary>Reads whether a frame is a keyframe without decoding it.</summary>
    public static bool IsKeyframe(ReadOnlySpan<byte> frame)
    {
        if (frame.IsEmpty)
        {
            return false;
        }

        byte[] head = frame[..Math.Min(frame.Length, 4)].ToArray();
        var rc = new Ffv1RangeDecoder(head, 0, head.Length, Ffv1StateTable.Default);
        byte state = 128;
        return rc.ReadBit(ref state) != 0;
    }

    /// <summary>Drops the states carried between frames, so decoding restarts at the next keyframe.</summary>
    public void Reset()
    {
        _haveKeyframe = false;
        if (_config is null)
        {
            _parameters = null;
        }
    }

    /// <summary>Decodes a frame.</summary>
    /// <param name="frame">The frame's bytes; the decoder reads them in place.</param>
    /// <param name="length">How many bytes of <paramref name="frame"/> the frame takes.</param>
    /// <param name="color">The color the frame's planes are tagged with when they are YCbCr.</param>
    /// <param name="context">The context to allocate the planes from.</param>
    /// <param name="errors">Receives every slice that could not be decoded; the frame is then returned with them left as zero.</param>
    /// <param name="info">What the frame says about itself.</param>
    /// <returns>The frame's planes. The caller must dispose them.</returns>
    /// <exception cref="CorruptDataException">The frame cannot be decoded at all.</exception>
    public YuvRaster Decode(byte[] frame, int length, YuvColor color, EgamiContext context, List<Ffv1SliceError> errors, out Ffv1FrameInfo info)
    {
        if (length < 1)
        {
            throw new CorruptDataException("FFV1", -1, "An FFV1 frame is empty.");
        }

        return _config is null ? DecodeVersion1(frame, length, color, context, errors, out info) : DecodeVersion3(frame, length, color, context, errors, out info);
    }

    /// <summary>Returns whether a track's codec private data holds a version 3 configuration record.</summary>
    public static bool HasConfigurationRecord(ReadOnlySpan<byte> record)
    {
        if (record.Length < 5)
        {
            return false;
        }

        // A version 0 or 1 stream has no record; FFmpeg's AVI and some Matroska writers still store a short one. Only a record
        // that says version 3 or more is one.
        byte[] head = record[..Math.Min(record.Length, 16)].ToArray();
        var rc = new Ffv1RangeDecoder(head, 0, head.Length, Ffv1StateTable.Default);
        Span<byte> state = stackalloc byte[Ffv1Parameters.ContextSize];
        state.Fill(128);
        try
        {
            return rc.ReadSymbol(state, false) >= 2;
        }
        catch (CorruptDataException)
        {
            return false;
        }
    }

    private YuvRaster DecodeVersion1(byte[] frame, int length, YuvColor color, EgamiContext context, List<Ffv1SliceError> errors, out Ffv1FrameInfo info)
    {
        var rc = new Ffv1RangeDecoder(frame, 0, length, Ffv1StateTable.Default);
        byte keyState = 128;
        bool keyframe = rc.ReadBit(ref keyState) != 0;
        if (keyframe)
        {
            _parameters = Ffv1Parameters.ReadFrameHeader(ref rc);
            _haveKeyframe = true;
        }
        else if (!_haveKeyframe || _parameters is null)
        {
            throw new Ffv1MissingKeyframeException();
        }

        Ffv1Parameters p = _parameters;
        rc.Table = p.StateTable;
        EnsureSlices(1);
        SliceContext slice = _slices[0];
        if (keyframe)
        {
            slice.Reset(p, [0, 0, 0]);
        }

        YuvRaster raster = Allocate(p, color, context);
        try
        {
            var target = new Ffv1Target(raster, p);
            slice.Geometry = new SliceGeometry(0, 0, _width, _height);
            try
            {
                DecodeContent(p, slice, ref rc, frame, 0, length, target);
            }
            catch (CorruptDataException e)
            {
                errors.Add(new Ffv1SliceError(0, 0, 0, _width, _height, e.Message, false));
            }

            info = new Ffv1FrameInfo(keyframe, 0, 0, 0);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    private YuvRaster DecodeVersion3(byte[] frame, int length, YuvColor color, EgamiContext context, List<Ffv1SliceError> errors, out Ffv1FrameInfo info)
    {
        Ffv1Parameters p = _config!;
        List<(int Start, int End, int FooterEnd)> spans = _spans;
        FindSlices(frame, length, p, spans);
        var first = new Ffv1RangeDecoder(frame, 0, spans[0].End, Ffv1StateTable.Default);
        byte keyState = 128;
        bool keyframe = first.ReadBit(ref keyState) != 0;
        if (!keyframe && !_haveKeyframe)
        {
            throw new Ffv1MissingKeyframeException();
        }

        if (keyframe)
        {
            _haveKeyframe = true;
        }

        first.Table = p.StateTable;
        int grid = p.HorizontalSlices * p.VerticalSlices;
        EnsureSlices(grid);
        YuvRaster raster = Allocate(p, color, context);
        try
        {
            int structure = 0, sarNum = 0, sarDen = 0;
            int count = spans.Count;
            if (_jobs.Length < count)
            {
                _jobs = new SliceJob[count];
                _crcOk = new bool[count];
            }

            if (_used.Length != _slices.Length)
            {
                _used = new bool[_slices.Length];
            }

            Array.Clear(_used);
            (_frame, _target) = (frame, new Ffv1Target(raster, p));
            SliceJob[] jobs = _jobs;
            int workers = Math.Min(Math.Max(1, context.MaxParallelism), count);
            _options.MaxDegreeOfParallelism = workers;

            // CRCs first, so a damaged slice's header is never trusted to place it or reset states.
            if (!p.SliceCrc)
            {
                _crcOk.AsSpan(0, count).Fill(true);
            }
            else if (workers > 1)
            {
                Parallel.For(0, count, _options, _checkSlice);
            }
            else
            {
                for (int s = 0; s < count; s++)
                {
                    CheckSlice(s);
                }
            }

            // Headers next, in order: they place each slice and reset its states on a keyframe. The slices' contents then decode
            // on their own, in parallel; each writes only the samples it owns, so the frame is the same at any thread count.
            for (int s = 0; s < count; s++)
            {
                (int start, int end, int footerEnd) = spans[s];
                if (!_crcOk[s] || (p.SliceCrc && frame[end + 3] != 0))
                {
                    SliceGeometry damaged = GuessGeometry(p, frame, start, end, s == 0);
                    jobs[s] = new SliceJob(null, default, start, end, footerEnd, CrcError(p, frame, s, end, damaged, _crcOk[s]));
                    continue;
                }

                Ffv1RangeDecoder rc = s == 0 ? first : new Ffv1RangeDecoder(frame, start, end, p.StateTable);
                try
                {
                    SliceContext slice = ReadSliceHeader(p, ref rc, keyframe, _used, out int sliceStructure, out int num, out int den);
                    if (s == 0)
                    {
                        (structure, sarNum, sarDen) = (sliceStructure, num, den);
                    }

                    jobs[s] = new SliceJob(slice, rc, start, end, footerEnd, null);
                }
                catch (CorruptDataException e)
                {
                    jobs[s] = new SliceJob(null, default, start, end, footerEnd, new Ffv1SliceError(s, 0, 0, 0, 0, $"slice {s}: {e.Message}", false));
                }
            }

            if (workers > 1)
            {
                Parallel.For(0, count, _options, _decodeSlice);
            }
            else
            {
                for (int s = 0; s < count; s++)
                {
                    DecodeSlice(s);
                }
            }

            for (int s = 0; s < count; s++)
            {
                if (jobs[s].Error is { } error)
                {
                    errors.Add(error);
                }

                jobs[s] = default;
            }

            info = new Ffv1FrameInfo(keyframe, structure, sarDen == 0 ? 0 : sarNum, sarNum == 0 ? 0 : sarDen);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
        finally
        {
            (_frame, _target) = (null, null);
        }
    }

    private void CheckSlice(int s) => _crcOk[s] = CrcOk(_frame!, _spans[s].Start, _spans[s].FooterEnd);

    private void DecodeSlice(int s)
    {
        ref SliceJob job = ref _jobs[s];
        if (job.Slice is not { } slice)
        {
            return;
        }

        try
        {
            DecodeContent(_config!, slice, ref job.Decoder, _frame!, job.Start, job.End, _target!);
        }
        catch (CorruptDataException e)
        {
            SliceGeometry g = slice.Geometry;
            job.Error = new Ffv1SliceError(s, g.X, g.Y, g.Width, g.Height, $"slice {s}: {e.Message}", false);
        }
    }

    /// <summary>Finds a version 3 frame's slices, first to last.</summary>
    internal static List<(int Start, int End, int FooterEnd)> FindSlices(byte[] frame, int length, Ffv1Parameters p)
    {
        List<(int, int, int)> spans = [];
        FindSlices(frame, length, p, spans);
        return spans;
    }

    /// <summary>Finds a version 3 frame's slices from the footers at their ends, last to first (RFC 9043 section 4.9).</summary>
    private static void FindSlices(byte[] frame, int length, Ffv1Parameters p, List<(int Start, int End, int FooterEnd)> spans)
    {
        spans.Clear();
        int footer = p.FooterSize;
        int pos = length;
        while (pos > 0)
        {
            if (pos < footer)
            {
                throw new CorruptDataException("FFV1", -1, $"An FFV1 frame has {pos} bytes before its first slice footer, too few for one.");
            }

            int size = (frame[pos - footer] << 16) | (frame[pos - footer + 1] << 8) | frame[pos - footer + 2];
            int start = pos - footer - size;
            if (start < 0)
            {
                throw new CorruptDataException("FFV1", -1, $"An FFV1 slice footer gives {size} bytes, more than the frame has before it.");
            }

            spans.Add((start, pos - footer, pos));
            pos = start;
            if (spans.Count > 4096)
            {
                throw new CorruptDataException("FFV1", -1, "An FFV1 frame has more than 4096 slices.");
            }
        }

        spans.Reverse();
    }

    private static bool CrcOk(byte[] frame, int start, int footerEnd) => Ffv1Crc.Compute(frame.AsSpan(start, footerEnd - start)) == 0;

    /// <summary>The error of a slice whose CRC does not match or whose footer reports an error, or null when it is sound.</summary>
    private static Ffv1SliceError? CrcError(Ffv1Parameters p, byte[] frame, int s, int end, SliceGeometry g, bool crcOk)
    {
        if (!p.SliceCrc)
        {
            return null;
        }

        if (!crcOk)
        {
            return new Ffv1SliceError(s, g.X, g.Y, g.Width, g.Height, $"slice {s}'s CRC does not match", true);
        }

        int status = frame[end + 3];
        return status == 0 ? null : new Ffv1SliceError(s, g.X, g.Y, g.Width, g.Height, $"slice {s}'s footer reports error status {status}", false);
    }

    /// <summary>Where a damaged slice says it is, if its header can still be read; else nowhere (width 0).</summary>
    private SliceGeometry GuessGeometry(Ffv1Parameters p, byte[] frame, int start, int end, bool first)
    {
        try
        {
            var rc = new Ffv1RangeDecoder(frame, start, end, Ffv1StateTable.Default);
            if (first)
            {
                byte keyState = 128;
                rc.ReadBit(ref keyState);
            }

            rc.Table = p.StateTable;
            Ffv1SliceHeader h = Ffv1SliceHeader.Read(p, ref rc);
            return Geometry(p, h);
        }
        catch (CorruptDataException)
        {
            return default;
        }
    }

    private SliceGeometry Geometry(Ffv1Parameters p, Ffv1SliceHeader h)
    {
        long x0 = (long)h.X * _width / p.HorizontalSlices;
        long y0 = (long)h.Y * _height / p.VerticalSlices;
        long x1 = (long)(h.X + h.Width) * _width / p.HorizontalSlices;
        long y1 = (long)(h.Y + h.Height) * _height / p.VerticalSlices;
        return new SliceGeometry((int)x0, (int)y0, (int)(x1 - x0), (int)(y1 - y0));
    }

    private SliceContext ReadSliceHeader(Ffv1Parameters p, ref Ffv1RangeDecoder rc, bool keyframe, bool[] used, out int structure, out int sarNum, out int sarDen)
    {
        Ffv1SliceHeader header = Ffv1SliceHeader.Read(p, ref rc);
        (int sx, int sy) = (header.X, header.Y);
        ReadOnlySpan<int> quant = [header.QuantY, header.QuantChroma, header.QuantExtra];
        (structure, sarNum, sarDen) = (header.PictureStructure, header.SarNumerator, header.SarDenominator);
        int index = (sy * p.HorizontalSlices) + sx;
        if (used[index])
        {
            throw new CorruptDataException("FFV1", -1, $"Two FFV1 slices in one frame start at ({sx}, {sy}).");
        }

        used[index] = true;
        SliceContext slice = _slices[index];
        slice.Geometry = Geometry(p, header);
        if (slice.Geometry.Width < 1 || slice.Geometry.Height < 1)
        {
            throw new CorruptDataException("FFV1", -1, $"FFV1 slice ({sx}, {sy}) is empty at {_width}×{_height}.");
        }

        if (keyframe)
        {
            slice.Reset(p, quant);
        }
        else if (!slice.Matches(quant, p))
        {
            throw new CorruptDataException("FFV1", -1, $"FFV1 slice ({sx}, {sy}) has no states from a keyframe to continue from.");
        }

        return slice;
    }

    private static void DecodeContent(Ffv1Parameters p, SliceContext slice, ref Ffv1RangeDecoder rc, byte[] frame, int start, int end, Ffv1Target target)
    {
        if (p.CoderType == 0)
        {
            // The range-coded header ends in Sentinel mode: in version 3 one symbol with state 129, after which the decoder has
            // read one byte past it (RFC 9043 section 3.8.1.1.1). Versions 0 and 1, and version 3 before micro-version 2, end
            // without that symbol, but the decoder has still read one byte past the header.
            if (p.Version > 3 || (p.Version == 3 && p.MicroVersion > 1))
            {
                byte sentinel = 129;
                rc.ReadBit(ref sentinel);
            }

            int bitsStart = rc.Position - 1;
            if (bitsStart < start || bitsStart > end)
            {
                throw new CorruptDataException("FFV1", -1, "An FFV1 slice header runs past the end of its slice.");
            }

            var golomb = new Ffv1GolombLineDecoder(new Ffv1BitReader(frame, bitsStart, end));
            slice.Decode(p, ref golomb, target);
        }
        else
        {
            var range = new Ffv1RangeLineDecoder(rc);
            slice.Decode(p, ref range, target);
        }
    }

    private void EnsureSlices(int count)
    {
        if (_slices.Length != count)
        {
            _slices = new SliceContext[count];
            for (int i = 0; i < count; i++)
            {
                _slices[i] = new SliceContext();
            }
        }
    }

    private YuvRaster Allocate(Ffv1Parameters p, YuvColor color, EgamiContext context)
    {
        ChromaFormat format = ChromaFormatOf(p)
            ?? throw new UnsupportedFormatException($"FFV1 chroma subsampling 1/{1 << p.Log2HChromaSubsample} by 1/{1 << p.Log2VChromaSubsample} has no Egami layout.");
        int depth = StorageDepth(p.BitsPerRawSample);
        if (p.ColorSpace == 1)
        {
            color = new YuvColor(YuvMatrix.Identity, YuvRange.Full, color.Siting);
        }

        return depth == 8
            ? YuvRaster.Create(_width, _height, format, color, p.ExtraPlane, context)
            : YuvRaster.Create(_width, _height, format, depth, color, p.ExtraPlane, context);
    }

    /// <summary>A slice to decode: its context, its range decoder after the header, its bytes, and what went wrong.</summary>
    private struct SliceJob(SliceContext? slice, Ffv1RangeDecoder decoder, int start, int end, int footerEnd, Ffv1SliceError? error)
    {
        public SliceContext? Slice = slice;
        public Ffv1RangeDecoder Decoder = decoder;
        public int Start = start;
        public int End = end;
        public int FooterEnd = footerEnd;
        public Ffv1SliceError? Error = error;
    }

    /// <summary>Where a slice sits in the frame, in luma samples.</summary>
    internal readonly record struct SliceGeometry(int X, int Y, int Width, int Height);

    /// <summary>A slice position's states and line buffers, kept between frames.</summary>
    private sealed class SliceContext
    {
        private readonly Ffv1PlaneCoding[] _coding = [new(), new(), new()];
        private int[][] _lines = [];
        private bool _ready;

        public SliceGeometry Geometry;

        public void Reset(Ffv1Parameters p, ReadOnlySpan<int> quant)
        {
            for (int i = 0; i < 3; i++)
            {
                _coding[i].Reset(p, quant[i]);
            }

            _ready = true;
        }

        public bool Matches(ReadOnlySpan<int> quant, Ffv1Parameters p) =>
            _ready && _coding[0].QuantIndex == quant[0] && _coding[1].QuantIndex == quant[1] && (!p.ExtraPlane || _coding[2].QuantIndex == quant[2]);

        public void Decode<TDecoder>(Ffv1Parameters p, ref TDecoder decoder, Ffv1Target target)
            where TDecoder : struct, IFfv1LineDecoder
        {
            SliceGeometry g = Geometry;
            int planes = 1 + (p.ChromaPlanes ? 2 : 0) + (p.ExtraPlane ? 1 : 0);
            int stride = g.Width + Ffv1Lines.Offset + 1;
            if (_lines.Length != planes * 3 || _lines[0].Length < stride)
            {
                _lines = new int[planes * 3][];
                for (int i = 0; i < _lines.Length; i++)
                {
                    _lines[i] = new int[stride];
                }
            }

            if (p.ColorSpace == 0)
            {
                DecodeYCbCr(p, ref decoder, target, planes, stride);
            }
            else
            {
                DecodeRgb(p, ref decoder, target, planes, stride);
            }
        }

        private void DecodeYCbCr<TDecoder>(Ffv1Parameters p, ref TDecoder decoder, Ffv1Target target, int planes, int stride)
            where TDecoder : struct, IFfv1LineDecoder
        {
            SliceGeometry g = Geometry;
            int bits = p.BitsPerRawSample;
            bool signed16 = bits == 16 && p.CoderType != 0;
            int hs = p.Log2HChromaSubsample;
            int vs = p.Log2VChromaSubsample;
            for (int plane = 0; plane < planes; plane++)
            {
                bool chroma = p.ChromaPlanes && plane is 1 or 2;
                int output = !p.ChromaPlanes && plane == 1 ? 3 : plane;
                int coding = output == 0 ? 0 : output == 3 ? 2 : 1;
                int x = chroma ? g.X >> hs : g.X;
                int y = chroma ? g.Y >> vs : g.Y;
                int w = chroma ? -(-g.Width >> hs) : g.Width;
                int h = chroma ? -(-g.Height >> vs) : g.Height;

                // Where subsampled slices meet mid-sample, the slice after owns the shared chroma column or row, as when slices
                // are decoded in order; so slices decoded in parallel write the same frame.
                int ownW = chroma && g.X + g.Width < target.Width ? ((g.X + g.Width) >> hs) - x : w;
                int ownH = chroma && g.Y + g.Height < target.Height ? ((g.Y + g.Height) >> vs) - y : h;
                int[] cur = _lines[0], top = _lines[1], top2 = _lines[2];
                Array.Clear(top, 0, stride);
                Array.Clear(top2, 0, stride);
                decoder.StartPlane();
                for (int row = 0; row < h; row++)
                {
                    decoder.DecodeLine(cur, top, top2, w, _coding[coding], bits, signed16);
                    if (row < ownH)
                    {
                        target.Store(output, y + row, x, cur.AsSpan(Ffv1Lines.Offset, ownW));
                    }

                    (top2, top, cur) = (top, cur, top2);
                }
            }
        }

        private void DecodeRgb<TDecoder>(Ffv1Parameters p, ref TDecoder decoder, Ffv1Target target, int planes, int stride)
            where TDecoder : struct, IFfv1LineDecoder
        {
            SliceGeometry g = Geometry;
            int bits = p.BitsPerRawSample;
            for (int i = 0; i < planes * 3; i++)
            {
                Array.Clear(_lines[i], 0, stride);
            }

            int rotation = 0;
            decoder.StartPlane();
            for (int row = 0; row < g.Height; row++)
            {
                for (int plane = 0; plane < planes; plane++)
                {
                    int r = rotation;
                    int[] cur = _lines[(plane * 3) + ((r + 2) % 3)];
                    int[] top = _lines[(plane * 3) + ((r + 1) % 3)];
                    int[] top2 = _lines[(plane * 3) + r];
                    decoder.DecodeLine(cur, top, top2, g.Width, _coding[plane == 0 ? 0 : plane == 3 ? 2 : 1], bits + 1, false);
                }

                int at = (rotation + 2) % 3;
                target.StoreRgb(
                    g.Y + row,
                    g.X,
                    _lines[at].AsSpan(Ffv1Lines.Offset, g.Width),
                    _lines[3 + at].AsSpan(Ffv1Lines.Offset, g.Width),
                    _lines[6 + at].AsSpan(Ffv1Lines.Offset, g.Width),
                    planes == 4 ? _lines[9 + at].AsSpan(Ffv1Lines.Offset, g.Width) : default);
                rotation = (rotation + 1) % 3;
            }
        }
    }
}

/// <summary>A non-keyframe came with no keyframe before it.</summary>
internal sealed class Ffv1MissingKeyframeException() : Exception("An FFV1 non-keyframe has no keyframe before it.");
