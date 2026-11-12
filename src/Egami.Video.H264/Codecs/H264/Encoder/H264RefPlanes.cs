using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// A reference picture as motion search and the encoder's prediction read it: its full samples and the three half-sample
/// planes of 8.4.2.2.1 (b across, h down, j between), from which every quarter sample is one plane or the rounded mean of two,
/// exactly as the decoder interpolates; with the explicit weights of the slices that predict from it.
/// </summary>
internal sealed class H264RefPlanes
{
    /// <summary>luma_log2_weight_denom and chroma_log2_weight_denom of the encoder's slices.</summary>
    internal const int Denominator = 6;

    /// <summary>How far past the picture edges, in samples, motion search lets a block's top-left corner go.</summary>
    internal const int Reach = 24;

    // How far past the edges the half-sample planes are made: a block at the reach and its quarter-sample neighbours fit.
    private const int Margin = Reach + 4;

    private readonly int _stride;
    private readonly int _origin;
    private readonly int _width;
    private readonly int _height;
    private readonly short[] _mid;

    // f, b, h and j, then none: the planes of Sources.
    private readonly byte[]?[] _planes;

    public H264RefPlanes(int widthMbs, int heightMbs)
    {
        Picture = new H264Picture(widthMbs, heightMbs, 1, narrow: true);
        _stride = Picture.Stride;
        _origin = Picture.Origin;
        _width = Picture.Width;
        _height = Picture.Height;
        int size = Picture.Y<byte>().Length;
        Half = new byte[size];
        Down = new byte[size];
        Center = new byte[size];
        _mid = new short[size];
        _planes = [Picture.Y<byte>(), Half, Down, Center, null];
    }

    /// <summary>Gets the reconstructed picture, its borders extended.</summary>
    public H264Picture Picture { get; }

    /// <summary>Gets the half samples between each sample and the one to its right (b).</summary>
    public byte[] Half { get; }

    /// <summary>Gets the half samples between each sample and the one below it (h).</summary>
    public byte[] Down { get; }

    /// <summary>Gets the half samples at the middle of each sample's square (j).</summary>
    public byte[] Center { get; }

    /// <summary>
    /// Gets or sets the explicit luma weight and offset, over 2^<see cref="Denominator"/>; the default (64, 0) leaves the samples
    /// as they are.
    /// </summary>
    public (int Weight, int Offset) LumaWeight { get; set; } = (1 << Denominator, 0);

    /// <summary>Gets or sets the explicit weight and offset of Cb.</summary>
    public (int Weight, int Offset) CbWeight { get; set; } = (1 << Denominator, 0);

    /// <summary>Gets or sets the explicit weight and offset of Cr.</summary>
    public (int Weight, int Offset) CrWeight { get; set; } = (1 << Denominator, 0);

    /// <summary>Gets or sets whether the slices predicting from this picture weight it explicitly.</summary>
    public bool Weighted { get; set; }

    /// <summary>Gets or sets the picture's frame_num.</summary>
    public int FrameNum { get; set; }

    /// <summary>Gets or sets the picture's order count.</summary>
    public int Poc { get; set; }

    /// <summary>
    /// Gets or sets whether the picture is a B-frame other pictures predict from (a pyramid's middle), which the next P or I
    /// picture marks unused.
    /// </summary>
    public bool Disposable { get; set; }

    /// <summary>Extends the picture's borders and interpolates its half-sample planes.</summary>
    public void Build()
    {
        Picture.ExtendEdges();
        byte[] f = Picture.Y<byte>();
        int s = _stride;
        int x0 = -Margin, x1 = _width + Margin, y0 = -Margin, y1 = _height + Margin;

        // b and the unrounded sums b1 that j filters down, for every row j reads (two above to three below).
        for (int y = y0 - 2; y < y1 + 3; y++)
        {
            int row = _origin + (y * s);
            int x = x0;
            if (Vector256.IsHardwareAccelerated)
            {
                for (; x + 32 <= x1; x += 32)
                {
                    HalfRow(f, row + x);
                }
            }

            for (; x < x1; x++)
            {
                int at = row + x;
                int b1 = f[at - 2] - (5 * f[at - 1]) + (20 * f[at]) + (20 * f[at + 1]) - (5 * f[at + 2]) + f[at + 3];
                _mid[at] = (short)b1;
                Half[at] = Clip((b1 + 16) >> 5);
            }
        }

        for (int y = y0; y < y1; y++)
        {
            int row = _origin + (y * s);
            int x = x0;
            if (Vector256.IsHardwareAccelerated)
            {
                for (; x + 32 <= x1; x += 32)
                {
                    DownRow(f, row + x);
                    CenterRow(row + x);
                    CenterRow(row + x + 16);
                }
            }

            for (; x < x1; x++)
            {
                int at = row + x;
                int h1 = f[at - (2 * s)] - (5 * f[at - s]) + (20 * f[at]) + (20 * f[at + s]) - (5 * f[at + (2 * s)]) + f[at + (3 * s)];
                Down[at] = Clip((h1 + 16) >> 5);
                int j1 = _mid[at - (2 * s)] - (5 * _mid[at - s]) + (20 * _mid[at]) + (20 * _mid[at + s]) - (5 * _mid[at + (2 * s)]) + _mid[at + (3 * s)];
                Center[at] = Clip((j1 + 512) >> 10);
            }
        }
    }

    private static byte Clip(int v) => (byte)Math.Clamp(v, 0, 255);

    /// <summary>The six-tap sum (a + f) − 5 (b + e) + 20 (c + d) of six rows or columns of 16 samples.</summary>
    private static Vector256<short> Tap(Vector256<short> a, Vector256<short> b, Vector256<short> c, Vector256<short> d, Vector256<short> e, Vector256<short> g) =>
        (a + g) - ((b + e) * 5) + ((c + d) * 20);

    private static Vector256<short> Widen(byte[] f, int at) => Vector256.WidenLower(Vector256.Create(Vector128.LoadUnsafe(ref MemoryMarshal.GetArrayDataReference(f), (nuint)at), Vector128<byte>.Zero)).AsInt16();

    /// <summary>Clips (v + 16) >> 5 of two halves to bytes.</summary>
    private static Vector256<byte> Round5(Vector256<short> lo, Vector256<short> hi)
    {
        Vector256<short> zero = Vector256<short>.Zero, top = Vector256.Create((short)255), sixteen = Vector256.Create((short)16);
        lo = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(lo + sixteen, 5), zero), top);
        hi = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(hi + sixteen, 5), zero), top);
        return Vector256.Narrow(lo.AsUInt16(), hi.AsUInt16());
    }

    /// <summary>32 samples of b and its unrounded sums from at, as the scalar loop computes them.</summary>
    private void HalfRow(byte[] f, int at)
    {
        Vector256<short> lo = Tap(Widen(f, at - 2), Widen(f, at - 1), Widen(f, at), Widen(f, at + 1), Widen(f, at + 2), Widen(f, at + 3));
        Vector256<short> hi = Tap(Widen(f, at + 14), Widen(f, at + 15), Widen(f, at + 16), Widen(f, at + 17), Widen(f, at + 18), Widen(f, at + 19));
        lo.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_mid), (nuint)at);
        hi.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(_mid), (nuint)(at + 16));
        Round5(lo, hi).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(Half), (nuint)at);
    }

    /// <summary>32 samples of h from at.</summary>
    private void DownRow(byte[] f, int at)
    {
        int s = _stride;
        Vector256<short> lo = Tap(Widen(f, at - (2 * s)), Widen(f, at - s), Widen(f, at), Widen(f, at + s), Widen(f, at + (2 * s)), Widen(f, at + (3 * s)));
        at += 16;
        Vector256<short> hi = Tap(Widen(f, at - (2 * s)), Widen(f, at - s), Widen(f, at), Widen(f, at + s), Widen(f, at + (2 * s)), Widen(f, at + (3 * s)));
        Round5(lo, hi).StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(Down), (nuint)(at - 16));
    }

    /// <summary>16 samples of j from at: the six-tap of the unrounded sums, in 32 bits, (v + 512) >> 10 clipped.</summary>
    private void CenterRow(int at)
    {
        int s = _stride;
        ref short mid = ref MemoryMarshal.GetArrayDataReference(_mid);
        Vector256<int> lo = Vector256<int>.Zero, hi = Vector256<int>.Zero;
        ReadOnlySpan<int> taps = [1, -5, 20, 20, -5, 1];
        for (int t = 0; t < 6; t++)
        {
            (Vector256<int> l, Vector256<int> h) = Vector256.Widen(Vector256.LoadUnsafe(ref mid, (nuint)(at + ((t - 2) * s))));
            Vector256<int> k = Vector256.Create(taps[t]);
            lo += l * k;
            hi += h * k;
        }

        Vector256<int> zero = Vector256<int>.Zero, top = Vector256.Create(255), round = Vector256.Create(512);
        lo = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(lo + round, 10), zero), top);
        hi = Vector256.Min(Vector256.Max(Vector256.ShiftRightArithmetic(hi + round, 10), zero), top);
        Vector256<ushort> words = Vector256.Narrow(lo.AsUInt32(), hi.AsUInt32());
        Vector128<byte> bytes = Vector128.Narrow(words.GetLower(), words.GetUpper());
        bytes.StoreUnsafe(ref MemoryMarshal.GetArrayDataReference(Center), (nuint)at);
    }

    /// <summary>
    /// The one or two planes and offsets whose rounded mean is the quarter sample (fx, fy) at full sample offset at
    /// (8.4.2.2.1): b is <see cref="Half"/>, h <see cref="Down"/>, j <see cref="Center"/>.
    /// </summary>
    public (byte[] A, int AtA, byte[]? B, int AtB) Sources(int at, int fx, int fy)
    {
        // A table look-up for what is, by phase: (0, 0) f; (2, 0) b; (0, 2) h; (2, 2) j; (1, 0) f and b; (3, 0) b and f one
        // right; (0, 1) f and h; (0, 3) h and f one down; (2, 1) b and j; (2, 3) j and b one down; (1, 2) h and j; (3, 2) j and
        // h one right; (1, 1) b and h; (3, 1) b and h one right; (1, 3) h and b one down; (3, 3) h one right and b one down.
        ref byte e = ref Unsafe.Add(ref MemoryMarshal.GetReference(Phases), ((fy * 4) + fx) * 3);
        ref byte[]? planes = ref MemoryMarshal.GetArrayDataReference(_planes);
        int a = e, b = Unsafe.Add(ref e, 1), shift = Unsafe.Add(ref e, 2);
        int s = _stride;
        return (Unsafe.Add(ref planes, a)!, at + (shift & 1), Unsafe.Add(ref planes, b), at + ((shift >> 1) & 1) + ((shift >> 2) * s));
    }

    // Each phase fy · 4 + fx: plane A and plane B (indexes of _planes; 4 for none), then bit 0 A one sample right, bit 1 B one
    // sample right, bit 2 B one row down.
    private static ReadOnlySpan<byte> Phases =>
    [
        0, 4, 0,  0, 1, 0,  1, 4, 0,  1, 0, 2,
        0, 2, 0,  1, 2, 0,  1, 3, 0,  1, 2, 2,
        2, 4, 0,  2, 3, 0,  3, 4, 0,  3, 2, 2,
        2, 0, 4,  2, 1, 4,  3, 1, 4,  2, 1, 5,
    ];

    /// <summary>The planes and offsets of the luma block at (x, y) with the quarter-sample vector mv (see <see cref="Sources"/>).</summary>
    public (byte[] A, int AtA, byte[]? B, int AtB) Locate(int x, int y, int mv)
    {
        int qx = (x * 4) + H264SliceDecoder.MvX(mv), qy = (y * 4) + H264SliceDecoder.MvY(mv);
        return Sources(_origin + ((qy >> 2) * _stride) + (qx >> 2), qx & 3, qy & 3);
    }

    /// <summary>
    /// Predicts a w by h luma block whose top-left sample is (x, y) with the quarter-sample vector mv into dst (row length
    /// dstStride), weighted when <paramref name="weighted"/>.
    /// </summary>
    public void Luma(int x, int y, int w, int h, int mv, byte[] dst, int dstAt, int dstStride, bool weighted)
    {
        int qx = (x * 4) + H264SliceDecoder.MvX(mv), qy = (y * 4) + H264SliceDecoder.MvY(mv);
        int at = _origin + ((qy >> 2) * _stride) + (qx >> 2);
        (byte[] a, int atA, byte[]? b, int atB) = Sources(at, qx & 3, qy & 3);
        if (b is null)
        {
            for (int j = 0; j < h; j++)
            {
                a.AsSpan(atA + (j * _stride), w).CopyTo(dst.AsSpan(dstAt + (j * dstStride), w));
            }
        }
        else
        {
            H264EncodeSimd.Average(a, atA, _stride, b, atB, _stride, dst, dstAt, dstStride, w, h);
        }

        if (weighted && Weighted)
        {
            Weigh(dst, dstAt, dstStride, w, h, LumaWeight, Denominator);
        }
    }

    /// <summary>
    /// Predicts the 4:2:0 chroma of a w by h chroma-sample block at (x, y) (8.4.2.2.2): Cb into cb and Cr into cr, row length
    /// dstStride.
    /// </summary>
    public void Chroma(int x, int y, int w, int h, int mv, byte[] cb, byte[] cr, int dstAt, int dstStride, bool weighted)
    {
        int mvx = H264SliceDecoder.MvX(mv), mvy = H264SliceDecoder.MvY(mv);
        int xf = mvx & 7, yf = mvy & 7;
        int at = Picture.ChromaOrigin + ((y + (mvy >> 3)) * Picture.ChromaStride) + x + (mvx >> 3);
        int s = Picture.ChromaStride;
        int wa = (8 - xf) * (8 - yf), wb = xf * (8 - yf), wc = (8 - xf) * yf, wd = xf * yf;
        for (int c = 0; c < 2; c++)
        {
            byte[] src = c == 0 ? Picture.Cb<byte>() : Picture.Cr<byte>();
            byte[] dst = c == 0 ? cb : cr;
            for (int j = 0; j < h; j++)
            {
                int r = at + (j * s), d = dstAt + (j * dstStride);
                for (int i = 0; i < w; i++)
                {
                    int p = r + i;
                    dst[d + i] = (byte)(((wa * src[p]) + (wb * src[p + 1]) + (wc * src[p + s]) + (wd * src[p + s + 1]) + 32) >> 6);
                }
            }

            if (weighted && Weighted)
            {
                Weigh(dst, dstAt, dstStride, w, h, c == 0 ? CbWeight : CrWeight, Denominator);
            }
        }
    }

    /// <summary>Explicit weighted prediction of one list (8-270).</summary>
    private static void Weigh(byte[] p, int at, int stride, int w, int h, (int Weight, int Offset) weight, int logWD)
    {
        int round = logWD >= 1 ? 1 << (logWD - 1) : 0;
        for (int j = 0; j < h; j++)
        {
            int r = at + (j * stride);
            for (int i = 0; i < w; i++)
            {
                int v = logWD >= 1 ? (((p[r + i] * weight.Weight) + round) >> logWD) + weight.Offset : (p[r + i] * weight.Weight) + weight.Offset;
                p[r + i] = Clip(v);
            }
        }
    }
}
