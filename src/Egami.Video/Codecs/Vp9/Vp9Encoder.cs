using System.Numerics;

namespace Egami.Video.Codecs.Vp9;

/// <summary>What one VP9 frame codes: its kind, references, quantizer and the choices the header carries.</summary>
internal sealed record Vp9FramePlan
{
    /// <summary>Gets whether the frame is a keyframe.</summary>
    public bool Key { get; init; }

    /// <summary>Gets whether the frame is shown (hidden frames are alternate references).</summary>
    public bool Show { get; init; } = true;

    /// <summary>Gets whether the frame keeps no state from earlier frames (error_resilient_mode).</summary>
    public bool ErrorResilient { get; init; }

    /// <summary>Gets the base quantizer index, 0 to 255.</summary>
    public int Quantizer { get; init; } = 100;

    /// <summary>Gets the reference slots the frame replaces (ignored for keyframes, which replace all eight).</summary>
    public int RefreshFlags { get; init; } = 1;

    /// <summary>Gets the slots LAST, GOLDEN and ALTREF come from.</summary>
    public int LastSlot { get; init; }

    public int GoldenSlot { get; init; } = 1;

    public int AltRefSlot { get; init; } = 2;

    /// <summary>Gets whether ALTREF is a frame still to be shown (its sign bias is 1, which allows compound prediction).</summary>
    public bool AltRefAhead { get; init; }

    /// <summary>Gets the saved probability context the frame codes with and refreshes.</summary>
    public int FrameContext { get; init; }

    /// <summary>Gets the loop filter level, or -1 to pick one.</summary>
    public int LoopFilterLevel { get; init; } = -1;

    /// <summary>Gets the tile columns as a power of two (clamped to what the width allows).</summary>
    public int TileColumnsLog2 { get; init; }

    /// <summary>Gets how hard the encoder searches, 0 to 10.</summary>
    public int Effort { get; init; } = 4;

    /// <summary>Gets the adaptive quantization mode: 0 off, 1 by block variance (libvpx's aq-mode 1).</summary>
    public int AqMode { get; init; }

    /// <summary>Gets whether frames keep their symbol statistics for the next frame (refresh_frame_context with backward adaptation).</summary>
    public bool RefreshContext { get; init; } = true;

    /// <summary>Gets whether this is the shown frame that mostly copies a hidden alternate reference (the overlay).</summary>
    public bool Overlay { get; init; }
}

/// <summary>
/// The VP9 encoder core for one sample type: byte for 8-bit streams, ushort for 10 and 12 bits. It searches each superblock's
/// partition, modes, motion vectors and transforms by rate and distortion, quantizes and codes the tokens, writes the frame, and
/// then decodes what it wrote with <see cref="Vp9Decoder{T}"/>, so its references, probability contexts and motion vectors are
/// exactly what every decoder holds. The encoder parts (forward transforms, bool encoder, costs, the bitstream writer) follow
/// libvpx 1.14.1's vp9/encoder (BSD-3; see THIRD-PARTY-NOTICES).
/// </summary>
internal sealed partial class Vp9Encoder<T> : IDisposable
    where T : unmanaged, IBinaryInteger<T>
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _bitDepth;
    private readonly int _subX;
    private readonly int _subY;
    private readonly int _profile;
    private readonly int _colorSpace;
    private readonly int _colorRange;
    private readonly int _miRows;
    private readonly int _miCols;
    private readonly int _sbCols;
    private readonly int _sbRows;
    private readonly IPixelMemorySource _memory;
    private readonly Vp9Decoder<T> _decoder;

    // The source picture, each plane sized to whole superblocks with its edges repeated past the frame.
    private readonly T[][] _source = new T[3][];
    private readonly int[] _sourceStride = new int[3];

    // The frame being coded, before the loop filter: what intra prediction within the frame reads.
    private readonly Vp9Frame<T> _recon;

    // Mode info for the whole frame, as the decoder lays it out.
    private readonly Vp9ModeInfo[] _mi;
    private readonly int[] _miGrid;
    private readonly byte[] _segmentMap;
    private readonly byte[][] _aboveContext = [[], [], []];
    private readonly byte[] _abovePartition;
    private readonly Vp9FrameContext _fc = new();
    private readonly Vp9FrameContext _basis = new();
    private readonly Vp9Counts _counts = new();
    private readonly Vp9RdCosts _costs = new();
    private readonly Tile?[] _tiles = new Tile?[256];
    private readonly Vp9MvRef[] _previousMvs;
    private readonly PaddedReference?[] _references = new PaddedReference?[3];
    private readonly short[] _yDequant = new short[8 * 2];
    private readonly short[] _uvDequant = new short[8 * 2];
    private readonly int[] _segmentQuantizer = new int[8];
    private readonly int[] _refSignBias = new int[4];
    private readonly int[] _slotVersions = new int[8];
    private byte[] _output = new byte[1 << 16];
    private Vp9FramePlan _plan = new();
    private int _txMode;
    private int _interpFilter;
    private int _referenceMode;
    private int _compFixedRef;
    private readonly int[] _compVarRef = new int[2];
    private bool _usePrevFrameMvs;
    private bool _allowHighPrecisionMv;
    private bool _frameIsIntra;
    private bool _lossless;
    private bool _segmentationEnabled;
    private int _tileColsLog2;
    private int _rdMult;
    private int _frames;

    public Vp9Encoder(int width, int height, int bitDepth, int subsamplingX, int subsamplingY, int colorSpace, int colorRange, IPixelMemorySource memory)
    {
        _width = width;
        _height = height;
        _bitDepth = bitDepth;
        _subX = subsamplingX;
        _subY = subsamplingY;
        _colorSpace = colorSpace;
        _colorRange = colorRange;
        _memory = memory;
        bool chroma444Or422 = subsamplingX == 0 || subsamplingY == 0;
        _profile = (bitDepth > 8 ? 2 : 0) + (chroma444Or422 || colorSpace == 7 ? 1 : 0);
        _miRows = (height + 7) >> 3;
        _miCols = (width + 7) >> 3;
        _sbCols = (_miCols + 7) >> 3;
        _sbRows = (_miRows + 7) >> 3;
        int alignedWidth = _sbCols * 64, alignedHeight = _sbRows * 64;
        for (int p = 0; p < 3; p++)
        {
            int w = p == 0 ? alignedWidth : alignedWidth >> subsamplingX, h = p == 0 ? alignedHeight : alignedHeight >> subsamplingY;
            _source[p] = new T[w * h];
            _sourceStride[p] = w;
        }

        _recon = new Vp9Frame<T>(width, height, subsamplingX, subsamplingY, bitDepth, memory);
        _decoder = new Vp9Decoder<T>(memory);
        int mis = _miRows * _miCols;
        _mi = new Vp9ModeInfo[mis];
        _miGrid = new int[mis];
        _segmentMap = new byte[mis];
        _previousMvs = new Vp9MvRef[mis];
        int alignedCols = (_miCols + 7) & ~7;
        _abovePartition = new byte[alignedCols];
        for (int p = 0; p < 3; p++)
        {
            _aboveContext[p] = new byte[(2 * alignedCols) + 16];
        }
    }

    public int Width => _width;

    public int Height => _height;

    public int BitDepth => _bitDepth;

    public int Profile => _profile;

    public int SubsamplingX => _subX;

    public int SubsamplingY => _subY;

    /// <summary>Gets the most threads a frame's tile columns are searched on; the output is the same at any count.</summary>
    public int MaxParallelism { get; init; } = 1;

    /// <summary>Gets the decoder that holds the references, for tests.</summary>
    internal Vp9Decoder<T> Decoder => _decoder;

    /// <summary>Gets the frame as coded before the loop filter, for tests.</summary>
    internal Vp9Frame<T> Reconstruction => _recon;

    /// <summary>Copies one row of a source plane in; rows and columns past the frame repeat its edge.</summary>
    public void SetSourceRow(int plane, int y, ReadOnlySpan<T> row)
    {
        int stride = _sourceStride[plane];
        int width = plane == 0 ? _width : (_width + _subX) >> _subX;
        Span<T> dst = _source[plane].AsSpan(y * stride, stride);
        row[..width].CopyTo(dst);
        dst[width..].Fill(row[width - 1]);
    }

    /// <summary>Repeats the last source rows down to whole superblocks, after every row was set.</summary>
    public void FinishSource()
    {
        for (int p = 0; p < 3; p++)
        {
            int stride = _sourceStride[p];
            int height = p == 0 ? _height : (_height + _subY) >> _subY;
            int rows = _source[p].Length / stride;
            ReadOnlySpan<T> last = _source[p].AsSpan((height - 1) * stride, stride);
            for (int y = height; y < rows; y++)
            {
                last.CopyTo(_source[p].AsSpan(y * stride, stride));
            }
        }
    }

    /// <summary>
    /// Codes the source as one frame and returns its bytes (valid until the next call). The frame is decoded at once into the
    /// reference slots it refreshes.
    /// </summary>
    public ReadOnlyMemory<byte> Encode(Vp9FramePlan plan)
    {
        _plan = plan;
        SetupFrame(plan);
        int length = WriteFrame(plan);
        _decoder.DecodeFrame(_output.AsSpan(0, length), out _);
        _frames++;
        int refresh = plan.Key ? 0xff : plan.RefreshFlags;
        for (int i = 0; i < 8; i++)
        {
            if ((refresh & (1 << i)) != 0)
            {
                _slotVersions[i] = _frames;
            }
        }

        return _output.AsMemory(0, length);
    }

    /// <summary>A frame that only shows a reference slot (show_existing_frame).</summary>
    public static byte[] ShowExisting(int profile, int slot)
    {
        Span<byte> data = stackalloc byte[2];
        Vp9BitWriter w = new(data);
        w.Literal(2, 2);
        w.Bit(profile & 1);
        w.Bit(profile >> 1);
        if (profile == 3)
        {
            w.Bit(0);
        }

        w.Bit(1);
        w.Literal(slot, 3);
        return data[..w.BytesWritten].ToArray();
    }

    private void SetupFrame(Vp9FramePlan plan)
    {
        _frameIsIntra = plan.Key;
        _lossless = plan.Quantizer == 0;
        _tileColsLog2 = Vp9TileColumns(plan.TileColumnsLog2);
        _txMode = _lossless ? 0 : Vp9Tables.TxModeSelect;
        _allowHighPrecisionMv = plan.Quantizer < 160;
        _interpFilter = Vp9Tables.Switchable;

        // The probabilities the frame starts from: what the decoder holds for its context, or the defaults.
        if (plan.Key || plan.ErrorResilient)
        {
            _basis.SetDefaults();
        }
        else
        {
            _basis.CopyFrom(_decoder.Context(plan.FrameContext));
        }

        _fc.CopyFrom(_basis);
        Array.Clear(_refSignBias);
        if (!plan.Key)
        {
            _refSignBias[Vp9Tables.AltrefFrame] = plan.AltRefAhead ? 1 : 0;
        }

        _referenceMode = Vp9Tables.SingleReference;
        bool compoundAllowed = !plan.Key && _refSignBias[Vp9Tables.AltrefFrame] != _refSignBias[Vp9Tables.LastFrame] && plan.Effort >= 3;
        if (compoundAllowed)
        {
            _referenceMode = Vp9Tables.ReferenceModeSelect;
            _compFixedRef = Vp9Tables.AltrefFrame;
            _compVarRef[0] = Vp9Tables.LastFrame;
            _compVarRef[1] = Vp9Tables.GoldenFrame;
        }

        _usePrevFrameMvs = !plan.Key && !plan.ErrorResilient && _decoder.UsesPreviousMvs(_width, _height);
        if (_usePrevFrameMvs)
        {
            _decoder.PreviousMvs.AsSpan(0, _previousMvs.Length).CopyTo(_previousMvs);
        }

        if (!plan.Key)
        {
            int[] slots = [plan.LastSlot, plan.GoldenSlot, plan.AltRefSlot];
            for (int i = 0; i < 3; i++)
            {
                Vp9Frame<T> frame = _decoder.Slot(slots[i]) ?? throw new InvalidOperationException($"Reference slot {slots[i]} is empty.");
                _references[i] ??= new PaddedReference(_width, _height, _subX, _subY);
                _references[i]!.Load(frame, slots[i], _slotVersions[slots[i]]);
            }
        }

        SetupSegmentation(plan);
        SetupQuantizer(plan);
        _rdMult = RdMultiplier(plan);
        _sadPerBit = Math.Max(1, (int)Math.Sqrt(_rdMult / 2048.0));
        _filterLevel = plan.LoopFilterLevel >= 0 ? plan.LoopFilterLevel : FilterLevelFromQuantizer(plan);
        _costs.Fill(_fc.P, _frameIsIntra, _bitDepth);
        Array.Clear(_segmentMap);
    }

    /// <summary>The tile columns the frame uses: the plan's, within what its width allows (vp9_get_tile_n_bits).</summary>
    private int Vp9TileColumns(int wanted)
    {
        Vp9FrameHeader.TileLog2Bounds(_miCols, out int min, out int max);
        return Math.Clamp(wanted, min, Math.Max(min, max));
    }

    private int RdMultiplier(Vp9FramePlan plan)
    {
        int q = DcQuant(plan.Quantizer, 0);
        double factor = plan.Key ? 4.35 : (plan.RefreshFlags & ~1) != 0 && !plan.Overlay ? 4.25 : 4.15;
        long mult = (long)((double)q * q * (factor + (0.001 * plan.Quantizer)));
        if (_bitDepth == 10)
        {
            mult = (mult + 8) >> 4;
        }
        else if (_bitDepth == 12)
        {
            mult = (mult + 128) >> 8;
        }

        return (int)Math.Max(1, mult);
    }

    /// <summary>libvpx's loop filter level guess from the quantizer (LPF_PICK_FROM_Q).</summary>
    private int FilterLevelFromQuantizer(Vp9FramePlan plan)
    {
        if (_lossless)
        {
            return 0;
        }

        int q = AcQuant(plan.Quantizer, 0);
        int guess = _bitDepth switch
        {
            8 => (int)(((q * 20723L) + 1015158 + (1 << 17)) >> 18),
            10 => (int)(((q * 20723L) + 4060632 + (1 << 19)) >> 20),
            _ => (int)(((q * 20723L) + 16242526 + (1 << 21)) >> 22),
        };
        if (plan.Key)
        {
            guess -= 4;
        }

        return Math.Clamp(guess, 0, Vp9Tables.MaxLoopFilter);
    }

    private short DcQuant(int q, int delta)
    {
        int i = Math.Clamp(q + delta, 0, 255);
        return _bitDepth switch { 8 => Vp9Tables.DcQLookup8[i], 10 => Vp9Tables.DcQLookup10[i], _ => Vp9Tables.DcQLookup12[i] };
    }

    private short AcQuant(int q, int delta)
    {
        int i = Math.Clamp(q + delta, 0, 255);
        return _bitDepth switch { 8 => Vp9Tables.AcQLookup8[i], 10 => Vp9Tables.AcQLookup10[i], _ => Vp9Tables.AcQLookup12[i] };
    }

    private void SetupQuantizer(Vp9FramePlan plan)
    {
        int segments = _segmentationEnabled ? 8 : 1;
        for (int i = 0; i < segments; i++)
        {
            int q = _segmentationEnabled ? _segmentQuantizer[i] : plan.Quantizer;
            _yDequant[i * 2] = DcQuant(q, 0);
            _yDequant[(i * 2) + 1] = AcQuant(q, 0);
            _uvDequant[i * 2] = DcQuant(q, 0);
            _uvDequant[(i * 2) + 1] = AcQuant(q, 0);
        }
    }

    public void Dispose()
    {
        _recon.Dispose();
        _decoder.Dispose();
    }
}
