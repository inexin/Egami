using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Operations.Compose;

/// <summary>
/// Blend kernels for every <see cref="BlendMode"/> on premultiplied rows. 8-bit rows (RGBA or BGRA, alpha in the top byte) run
/// the Porter–Duff operators, Plus, Modulate and the product-and-sum blend modes in exact integers, equal bit for bit to
/// <see cref="BlendReference.Blend8"/>; the modes that divide or take roots run in single precision, within a level of it, with
/// alpha still exact. Float rows run every mode in single precision. One generic body serves every vector width and the scalar
/// path, so all of them give the same bits.
/// </summary>
internal static class BlendKernels
{
    private const uint DivideBy255 = 0x8081;

    /// <summary>Gets the widest vector the hardware runs, or 0.</summary>
    internal static int HardwareBits => Pixels.PixelKernels.HardwareBits;

    /// <summary>Gets whether a mode runs in exact integers on 8-bit rows.</summary>
    internal static bool IsInteger(BlendMode mode)
    {
        return mode is not (BlendMode.ColorDodge or BlendMode.ColorBurn or BlendMode.SoftLight) && !BlendReference.IsNonSeparable(mode);
    }

    /// <summary>
    /// Blends premultiplied 8-bit <paramref name="source"/> pixels onto <paramref name="destination"/> in place. A one-pixel source
    /// is a solid color blended onto every pixel. With <paramref name="coverage"/> (one byte per pixel, or empty for full), each
    /// result is mixed with the original destination by it, so coverage 0 leaves a pixel unchanged.
    /// </summary>
    /// <param name="mode">The mode; <see cref="BlendMode.Destination"/> changes nothing.</param>
    /// <param name="source">The source row, or one pixel.</param>
    /// <param name="destination">The destination row.</param>
    /// <param name="coverage">Per-pixel coverage, or empty.</param>
    /// <param name="opacity255">Scales the source before the mode; 255 is full.</param>
    /// <param name="bgr">Whether the low three bytes are blue, green, red, which only the non-separable modes need to know.</param>
    /// <param name="vectorBits">The widest vector to use, or −1 for the hardware's; below 128 runs the scalar path.</param>
    internal static void Blend8(
        BlendMode mode, ReadOnlySpan<uint> source, Span<uint> destination, ReadOnlySpan<byte> coverage, int opacity255, bool bgr, int vectorBits = -1)
    {
        if (mode == BlendMode.Destination || destination.IsEmpty)
        {
            return;
        }

        if (vectorBits < 0)
        {
            vectorBits = HardwareBits;
        }

        Context c = new(mode, opacity255, bgr, source.Length == 1, !coverage.IsEmpty);
        ref uint s = ref MemoryMarshal.GetReference(source);
        ref uint d = ref MemoryMarshal.GetReference(destination);
        ref byte k = ref MemoryMarshal.GetReference(coverage);
        int n = destination.Length;
        int i = 0;
        if (c.Integer)
        {
            Integer(in c, source, destination, coverage, vectorBits);
            return;
        }

        if (vectorBits >= 512 && Vector512.IsHardwareAccelerated)
        {
            i = Run<U512, F512>(in c, ref s, ref d, ref k, i, n);
        }

        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            i = Run<U256, F256>(in c, ref s, ref d, ref k, i, n);
        }

        if (vectorBits >= 128 && Vector128.IsHardwareAccelerated)
        {
            i = Run<U128, F128>(in c, ref s, ref d, ref k, i, n);
        }

        Run<U1, F1>(in c, ref s, ref d, ref k, i, n);
    }

    /// <summary>
    /// Blends premultiplied float RGBA <paramref name="source"/> pixels onto <paramref name="destination"/> in place, every mode in
    /// single precision. A four-float source is a solid color; coverage works as in <see cref="Blend8"/>.
    /// </summary>
    internal static void BlendFloat(BlendMode mode, ReadOnlySpan<float> source, Span<float> destination, ReadOnlySpan<byte> coverage, float opacity)
    {
        if (mode == BlendMode.Destination)
        {
            return;
        }

        bool solid = source.Length == 4;
        for (int p = 0, n = destination.Length / 4; p < n; p++)
        {
            int si = solid ? 0 : p * 4, di = p * 4;
            F1 sr = new(source[si] * opacity), sg = new(source[si + 1] * opacity), sb = new(source[si + 2] * opacity), sa = new(source[si + 3] * opacity);
            F1 dr = new(destination[di]), dg = new(destination[di + 1]), db = new(destination[di + 2]), da = new(destination[di + 3]);
            Normalized<F1, U1>(mode, 0.3f, 0.59f, 0.11f, ref sr, ref sg, ref sb, ref sa, dr, dg, db, da);
            if (!coverage.IsEmpty && coverage[p] != 255)
            {
                float t = coverage[p] / 255f;
                sr = new(dr.V + ((sr.V - dr.V) * t));
                sg = new(dg.V + ((sg.V - dg.V) * t));
                sb = new(db.V + ((sb.V - db.V) * t));
                sa = new(da.V + ((sa.V - da.V) * t));
            }

            destination[di] = sr.V;
            destination[di + 1] = sg.V;
            destination[di + 2] = sb.V;
            destination[di + 3] = sa.V;
        }
    }

    /// <summary>Gets a Porter–Duff operator's coefficients as Fa = ka0 + ka1·αd and Fb = kb0 + kb1·αs, with alpha in 0..1.</summary>
    private static void Coefficients(BlendMode mode, out float ka0, out float ka1, out float kb0, out float kb1)
    {
        (ka0, ka1, kb0, kb1) = mode switch
        {
            BlendMode.Clear => (0f, 0f, 0f, 0f),
            BlendMode.Source => (1f, 0f, 0f, 0f),
            BlendMode.Destination => (0f, 0f, 1f, 0f),
            BlendMode.SourceOver => (1f, 0f, 1f, -1f),
            BlendMode.DestinationOver => (1f, -1f, 1f, 0f),
            BlendMode.SourceIn => (0f, 1f, 0f, 0f),
            BlendMode.DestinationIn => (0f, 0f, 0f, 1f),
            BlendMode.SourceOut => (1f, -1f, 0f, 0f),
            BlendMode.DestinationOut => (0f, 0f, 1f, -1f),
            BlendMode.SourceAtop => (0f, 1f, 1f, -1f),
            BlendMode.DestinationAtop => (1f, -1f, 0f, 1f),
            BlendMode.Xor => (1f, -1f, 1f, -1f),
            _ => (0f, 0f, 0f, 0f),
        };
    }

    private static int Run<TU, TF>(in Context c, ref uint s, ref uint d, ref byte k, int i, int n)
        where TU : struct, IULanes<TU>
        where TF : struct, IFLanes<TF, TU>
    {
        TU full = TU.Splat(255);
        for (; i + TU.Count <= n; i += TU.Count)
        {
            TU sv = c.Solid ? TU.Splat(s) : TU.Load(ref s, (nuint)i);
            TU dv = TU.Load(ref d, (nuint)i);
            if (c.Opacity255 != 255)
            {
                sv = Scale(sv, TU.Splat((uint)c.Opacity255));
            }

            TU result = Float<TU, TF>(in c, sv, dv);
            if (c.Covered)
            {
                TU cover = TU.LoadBytes(ref k, (nuint)i);
                result = Mix(result, dv, cover, full);
            }

            TU.Store(result, ref d, (nuint)i);
        }

        return i;
    }

    /// <summary>Scales every channel by <paramref name="factor"/> / 255, rounding to nearest.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TU Scale<TU>(TU p, TU factor)
        where TU : struct, IULanes<TU>
    {
        TU bytes = TU.Splat(0xFF);
        return Div255((p & bytes) * factor) | (Div255(((p >> 8) & bytes) * factor) << 8)
            | (Div255(((p >> 16) & bytes) * factor) << 16) | (Div255((p >> 24) * factor) << 24);
    }

    /// <summary>Mixes <paramref name="result"/> into <paramref name="original"/> by coverage: round((r·k + o·(255 − k)) / 255) per channel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TU Mix<TU>(TU result, TU original, TU cover, TU full)
        where TU : struct, IULanes<TU>
    {
        TU bytes = TU.Splat(0xFF);
        TU keep = full - cover;
        TU c0 = Div255(((result & bytes) * cover) + ((original & bytes) * keep));
        TU c1 = Div255((((result >> 8) & bytes) * cover) + (((original >> 8) & bytes) * keep));
        TU c2 = Div255((((result >> 16) & bytes) * cover) + (((original >> 16) & bytes) * keep));
        TU c3 = Div255(((result >> 24) * cover) + ((original >> 24) * keep));
        return c0 | (c1 << 8) | (c2 << 16) | (c3 << 24);
    }

    /// <summary>Divides by 255, rounding to nearest, for lanes up to 65,152.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TU Div255<TU>(TU x)
        where TU : struct, IULanes<TU>
    {
        return ((x + TU.Splat(127)) * TU.Splat(DivideBy255)) >> 23;
    }

    /// <summary>Runs an integer mode in 16-bit channel lanes: the widest vectors first, then pairs, then a last odd pixel padded to a pair.</summary>
    private static void Integer(in Context c, ReadOnlySpan<uint> source, Span<uint> destination, ReadOnlySpan<byte> coverage, int vectorBits)
    {
        ref uint s = ref MemoryMarshal.GetReference(source);
        ref uint d = ref MemoryMarshal.GetReference(destination);
        ref byte k = ref MemoryMarshal.GetReference(coverage);
        int n = destination.Length;
        int i = 0;
        if (vectorBits >= 512 && Vector512.IsHardwareAccelerated)
        {
            i = IntegerMode<C512>(in c, ref s, ref d, ref k, i, n);
        }

        if (vectorBits >= 256 && Vector256.IsHardwareAccelerated)
        {
            i = IntegerMode<C256>(in c, ref s, ref d, ref k, i, n);
        }

        i = IntegerMode<C128>(in c, ref s, ref d, ref k, i, n);
        if (i < n)
        {
            // The last pixel of an odd row runs as a pair whose second pixel is a copy, so every pixel takes the same path.
            Span<uint> pairS = stackalloc uint[2];
            Span<uint> pairD = stackalloc uint[2];
            Span<byte> pairK = stackalloc byte[2];
            pairS[0] = pairS[1] = c.Solid ? source[0] : source[i];
            pairD[0] = pairD[1] = destination[i];
            pairK[0] = pairK[1] = c.Covered ? coverage[i] : (byte)255;
            IntegerMode<C128>(in c, ref MemoryMarshal.GetReference(pairS), ref MemoryMarshal.GetReference(pairD), ref MemoryMarshal.GetReference(pairK), 0, 2);
            destination[i] = pairD[0];
        }
    }

    private static int IntegerMode<TC>(in Context c, ref uint s, ref uint d, ref byte k, int i, int n)
        where TC : struct, IChannelLanes<TC>
    {
        return c.Mode switch
        {
            BlendMode.Multiply => Separable<TC, Multiply>(in c, ref s, ref d, ref k, i, n),
            BlendMode.Screen => Separable<TC, Screen>(in c, ref s, ref d, ref k, i, n),
            BlendMode.Overlay => Separable<TC, Overlay>(in c, ref s, ref d, ref k, i, n),
            BlendMode.HardLight => Separable<TC, HardLight>(in c, ref s, ref d, ref k, i, n),
            BlendMode.Darken => Separable<TC, Darken>(in c, ref s, ref d, ref k, i, n),
            BlendMode.Lighten => Separable<TC, Lighten>(in c, ref s, ref d, ref k, i, n),
            BlendMode.Difference => Separable<TC, Difference>(in c, ref s, ref d, ref k, i, n),
            BlendMode.Exclusion => Separable<TC, Exclusion>(in c, ref s, ref d, ref k, i, n),
            BlendMode.Add => Separable<TC, Add>(in c, ref s, ref d, ref k, i, n),
            BlendMode.Subtract => Separable<TC, Subtract>(in c, ref s, ref d, ref k, i, n),
            _ => Composite<TC>(in c, ref s, ref d, ref k, i, n),
        };
    }

    /// <summary>Divides 16-bit lanes up to 65,025 by 255, rounding to nearest: t = x + 128, then (t + t / 256) / 256.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TC Div255C<TC>(TC x)
        where TC : struct, IChannelLanes<TC>
    {
        TC t = x + TC.Splat(128);
        return (t + (t >> 8)) >> 8;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static TC LoadSource<TC>(in Context c, ref uint s, int i, TC solid)
        where TC : struct, IChannelLanes<TC>
    {
        TC sv = c.Solid ? solid : TC.Load(ref s, (nuint)i);
        return c.Opacity255 == 255 ? sv : Div255C(sv * TC.Splat((ushort)c.Opacity255));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreResult<TC>(in Context c, TC result, TC dv, ref uint d, ref byte k, int i)
        where TC : struct, IChannelLanes<TC>
    {
        if (c.Covered)
        {
            TC cover = TC.LoadCoverage(ref k, (nuint)i);
            result = Div255C((result * cover) + (dv * (TC.Splat(255) - cover)));
        }

        TC.Store(result, ref d, (nuint)i);
    }

    /// <summary>
    /// The Porter–Duff operators, Plus and Modulate: every channel, alpha included, is round((cs·Fa + cb·Fb) / 255) with
    /// Fa = ka0 + ka1·αd and Fb = kb0 + kb1·αs (−1 wraps to 65535, so 255 − α comes out right), or the clamped sum, or the product.
    /// </summary>
    private static int Composite<TC>(in Context c, ref uint s, ref uint d, ref byte k, int i, int n)
        where TC : struct, IChannelLanes<TC>
    {
        TC solid = c.Solid ? Solid<TC>(s) : default;
        TC ka0 = TC.Splat((ushort)c.Ka0), ka1 = TC.Splat((ushort)c.Ka1), kb0 = TC.Splat((ushort)c.Kb0), kb1 = TC.Splat((ushort)c.Kb1);
        TC full = TC.Splat(255);
        for (; i + TC.Pixels <= n; i += TC.Pixels)
        {
            TC sv = LoadSource(in c, ref s, i, solid);
            TC dv = TC.Load(ref d, (nuint)i);
            TC result;
            if (c.Mode == BlendMode.Plus)
            {
                result = TC.Min(sv + dv, full);
            }
            else if (c.Mode == BlendMode.Modulate)
            {
                result = Div255C(sv * dv);
            }
            else
            {
                TC sa = TC.BroadcastAlpha(sv), da = TC.BroadcastAlpha(dv);
                TC fa = ka0 + (ka1 * da), fb = kb0 + (kb1 * sa);
                result = Div255C((TC.Min(sv, sa) * fa) + (TC.Min(dv, da) * fb));
            }

            StoreResult(in c, result, dv, ref d, ref k, i);
        }

        return i;
    }

    /// <summary>
    /// A product-and-sum blend mode: round((cs(255 − αd) + cb(255 − αs) + M) / 255) per color lane, with M = αs·αd·B(Cb, Cs)·255
    /// in premultiplied integers, and round((255αs + 255αd − αs·αd) / 255) in the alpha lane. Colors above their alpha are read
    /// as the alpha, so every true result fits 16 bits.
    /// </summary>
    private static int Separable<TC, TMode>(in Context c, ref uint s, ref uint d, ref byte k, int i, int n)
        where TC : struct, IChannelLanes<TC>
        where TMode : struct, IBlend16
    {
        TC solid = c.Solid ? Solid<TC>(s) : default;
        TC full = TC.Splat(255), alphaLanes = TC.AlphaLanes;
        for (; i + TC.Pixels <= n; i += TC.Pixels)
        {
            TC sv = LoadSource(in c, ref s, i, solid);
            TC dv = TC.Load(ref d, (nuint)i);
            TC sa = TC.BroadcastAlpha(sv), da = TC.BroadcastAlpha(dv);
            TC cs = TC.Min(sv, sa), cb = TC.Min(dv, da);
            TC m = TC.Select(alphaLanes, sa * da, TMode.M(cs, cb, sa, da));
            TC result = Div255C((cs * (full - da)) + (cb * (full - sa)) + m);
            StoreResult(in c, result, dv, ref d, ref k, i);
        }

        return i;
    }

    private static TC Solid<TC>(uint color)
        where TC : struct, IChannelLanes<TC>
    {
        Span<uint> pixels = stackalloc uint[16];
        pixels.Fill(color);
        return TC.Load(ref MemoryMarshal.GetReference(pixels), 0);
    }

    private static TU Float<TU, TF>(in Context c, TU s, TU d)
        where TU : struct, IULanes<TU>
        where TF : struct, IFLanes<TF, TU>
    {
        TU bytes = TU.Splat(0xFF);
        TU sa = s >> 24, da = d >> 24;
        TF scale = TF.Splat(255f);
        TF r = TF.From(s & bytes) / scale, g = TF.From((s >> 8) & bytes) / scale, b = TF.From((s >> 16) & bytes) / scale, a = TF.From(sa) / scale;
        TF dr = TF.From(d & bytes) / scale, dg = TF.From((d >> 8) & bytes) / scale, db = TF.From((d >> 16) & bytes) / scale, dA = TF.From(da) / scale;
        Normalized<TF, TU>(c.Mode, c.LumR, c.LumG, c.LumB, ref r, ref g, ref b, ref a, dr, dg, db, dA);

        // Alpha is exact in integers; color is rounded from the float result and kept within alpha, as premultiplied pixels must be.
        TU alpha = sa + da - Div255(sa * da);
        TU c0 = TU.Min(TF.Round(TF.Max(r * scale, TF.Splat(0))), alpha);
        TU c1 = TU.Min(TF.Round(TF.Max(g * scale, TF.Splat(0))), alpha);
        TU c2 = TU.Min(TF.Round(TF.Max(b * scale, TF.Splat(0))), alpha);
        return c0 | (c1 << 8) | (c2 << 16) | (alpha << 24);
    }

    /// <summary>
    /// Blends normalized premultiplied lanes: the source in <paramref name="r"/> to <paramref name="a"/> (replaced by the result)
    /// onto the destination. The Porter–Duff operators use their coefficients; the blend modes blend straight colors, then
    /// composite source-over. <paramref name="lumR"/> to <paramref name="lumB"/> weigh the channels in the order they are passed.
    /// </summary>
    private static void Normalized<TF, TU>(
        BlendMode mode, float lumR, float lumG, float lumB,
        ref TF r, ref TF g, ref TF b, ref TF a, TF dr, TF dg, TF db, TF da)
        where TF : struct, IFLanes<TF, TU>
        where TU : struct, IULanes<TU>
    {
        TF one = TF.Splat(1), zero = TF.Splat(0);
        switch (mode)
        {
            case BlendMode.Plus:
                r = TF.Min(one, r + dr);
                g = TF.Min(one, g + dg);
                b = TF.Min(one, b + db);
                a = TF.Min(one, a + da);
                return;
            case BlendMode.Modulate:
                r *= dr;
                g *= dg;
                b *= db;
                a *= da;
                return;
        }

        if (!BlendReference.IsBlend(mode))
        {
            Coefficients(mode, out float ka0, out float ka1, out float kb0, out float kb1);
            TF fa = TF.Splat(ka0) + (TF.Splat(ka1) * da);
            TF fb = TF.Splat(kb0) + (TF.Splat(kb1) * a);
            r = (r * fa) + (dr * fb);
            g = (g * fa) + (dg * fb);
            b = (b * fa) + (db * fb);
            a = (a * fa) + (da * fb);
            return;
        }

        TF sa = a;
        TF sNone = TF.Equal(sa, zero), dNone = TF.Equal(da, zero);
        TF csR = TF.Select(sNone, zero, r / sa), csG = TF.Select(sNone, zero, g / sa), csB = TF.Select(sNone, zero, b / sa);
        TF cbR = TF.Select(dNone, zero, dr / da), cbG = TF.Select(dNone, zero, dg / da), cbB = TF.Select(dNone, zero, db / da);
        TF mR, mG, mB;
        if (BlendReference.IsNonSeparable(mode))
        {
            NonSeparable<TF, TU>(mode, lumR, lumG, lumB, cbR, cbG, cbB, csR, csG, csB, out mR, out mG, out mB);
        }
        else
        {
            mR = Separable<TF, TU>(mode, cbR, csR);
            mG = Separable<TF, TU>(mode, cbG, csG);
            mB = Separable<TF, TU>(mode, cbB, csB);
        }

        TF both = sa * da, keepD = one - sa, keepS = one - da;
        r = (r * keepS) + (dr * keepD) + (both * mR);
        g = (g * keepS) + (dg * keepD) + (both * mG);
        b = (b * keepS) + (db * keepD) + (both * mB);
        a = sa + da - both;
    }

    private static TF Separable<TF, TU>(BlendMode mode, TF cb, TF cs)
        where TF : struct, IFLanes<TF, TU>
        where TU : struct, IULanes<TU>
    {
        TF one = TF.Splat(1), zero = TF.Splat(0), half = TF.Splat(0.5f), two = TF.Splat(2);
        switch (mode)
        {
            case BlendMode.Multiply:
                return cb * cs;
            case BlendMode.Screen:
                return cb + cs - (cb * cs);
            case BlendMode.Overlay:
                return Separable<TF, TU>(BlendMode.HardLight, cs, cb);
            case BlendMode.Darken:
                return TF.Min(cb, cs);
            case BlendMode.Lighten:
                return TF.Max(cb, cs);
            case BlendMode.ColorDodge:
            {
                TF q = TF.Min(one, cb / TF.Max(one - cs, TF.Splat(1e-30f)));
                return TF.Select(TF.Equal(cb, zero), zero, TF.Select(TF.LessOrEqual(one, cs), one, q));
            }

            case BlendMode.ColorBurn:
            {
                TF q = one - TF.Min(one, (one - cb) / TF.Max(cs, TF.Splat(1e-30f)));
                return TF.Select(TF.LessOrEqual(one, cb), one, TF.Select(TF.LessOrEqual(cs, zero), zero, q));
            }

            case BlendMode.HardLight:
            {
                TF low = cb * two * cs;
                TF s2 = (two * cs) - one;
                TF high = cb + s2 - (cb * s2);
                return TF.Select(TF.LessOrEqual(cs, half), low, high);
            }

            case BlendMode.SoftLight:
            {
                TF low = cb - ((one - (two * cs)) * cb * (one - cb));
                TF dark = ((((TF.Splat(16) * cb) - TF.Splat(12)) * cb) + TF.Splat(4)) * cb;
                TF dd = TF.Select(TF.LessOrEqual(cb, TF.Splat(0.25f)), dark, TF.Sqrt(cb));
                TF high = cb + (((two * cs) - one) * (dd - cb));
                return TF.Select(TF.LessOrEqual(cs, half), low, high);
            }

            case BlendMode.Difference:
                return TF.Max(cb - cs, cs - cb);
            case BlendMode.Exclusion:
                return cb + cs - (two * cb * cs);
            case BlendMode.Add:
                return TF.Min(one, cb + cs);
            case BlendMode.Subtract:
                return TF.Max(zero, cb - cs);
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    private static void NonSeparable<TF, TU>(
        BlendMode mode, float lumR, float lumG, float lumB, TF cbR, TF cbG, TF cbB, TF csR, TF csG, TF csB, out TF r, out TF g, out TF b)
        where TF : struct, IFLanes<TF, TU>
        where TU : struct, IULanes<TU>
    {
        TF wr = TF.Splat(lumR), wg = TF.Splat(lumG), wb = TF.Splat(lumB);
        switch (mode)
        {
            case BlendMode.Hue:
                SetSat<TF, TU>(csR, csG, csB, Sat(cbR, cbG, cbB), out r, out g, out b);
                SetLum<TF, TU>(ref r, ref g, ref b, Lum(cbR, cbG, cbB), wr, wg, wb);
                break;
            case BlendMode.Saturation:
                SetSat<TF, TU>(cbR, cbG, cbB, Sat(csR, csG, csB), out r, out g, out b);
                SetLum<TF, TU>(ref r, ref g, ref b, Lum(cbR, cbG, cbB), wr, wg, wb);
                break;
            case BlendMode.Color:
                (r, g, b) = (csR, csG, csB);
                SetLum<TF, TU>(ref r, ref g, ref b, Lum(cbR, cbG, cbB), wr, wg, wb);
                break;
            default:
                (r, g, b) = (cbR, cbG, cbB);
                SetLum<TF, TU>(ref r, ref g, ref b, Lum(csR, csG, csB), wr, wg, wb);
                break;
        }

        TF Lum(TF x, TF y, TF z) => (wr * x) + (wg * y) + (wb * z);

        static TF Sat(TF x, TF y, TF z) => TF.Max(x, TF.Max(y, z)) - TF.Min(x, TF.Min(y, z));
    }

    private static void SetSat<TF, TU>(TF r, TF g, TF b, TF s, out TF outR, out TF outG, out TF outB)
        where TF : struct, IFLanes<TF, TU>
        where TU : struct, IULanes<TU>
    {
        TF zero = TF.Splat(0);
        TF max = TF.Max(r, TF.Max(g, b)), min = TF.Min(r, TF.Min(g, b));
        TF flat = TF.LessOrEqual(max, min);
        TF range = TF.Max(max - min, TF.Splat(1e-30f));
        outR = Channel(r);
        outG = Channel(g);
        outB = Channel(b);

        TF Channel(TF v)
        {
            TF mid = (v - min) * s / range;
            TF value = TF.Select(TF.Equal(v, max), s, TF.Select(TF.Equal(v, min), zero, mid));
            return TF.Select(flat, zero, value);
        }
    }

    private static void SetLum<TF, TU>(ref TF r, ref TF g, ref TF b, TF l, TF wr, TF wg, TF wb)
        where TF : struct, IFLanes<TF, TU>
        where TU : struct, IULanes<TU>
    {
        TF d = l - ((wr * r) + (wg * g) + (wb * b));
        r += d;
        g += d;
        b += d;

        // ClipColor: pull the color toward its luminosity until it lies in 0..1.
        TF one = TF.Splat(1), zero = TF.Splat(0), tiny = TF.Splat(1e-30f);
        TF lum = (wr * r) + (wg * g) + (wb * b);
        TF n = TF.Min(r, TF.Min(g, b)), x = TF.Max(r, TF.Max(g, b));
        TF under = TF.Less(n, zero);
        TF downScale = lum / TF.Max(lum - n, tiny);
        r = TF.Select(under, lum + ((r - lum) * downScale), r);
        g = TF.Select(under, lum + ((g - lum) * downScale), g);
        b = TF.Select(under, lum + ((b - lum) * downScale), b);
        TF over = TF.Less(one, x);
        TF upScale = (one - lum) / TF.Max(x - lum, tiny);
        r = TF.Select(over, lum + ((r - lum) * upScale), r);
        g = TF.Select(over, lum + ((g - lum) * upScale), g);
        b = TF.Select(over, lum + ((b - lum) * upScale), b);
    }

    /// <summary>The M term of a product-and-sum blend mode, from colors clamped to their alpha.</summary>
    private interface IBlend16
    {
        static abstract TC M<TC>(TC cs, TC cb, TC sa, TC da)
            where TC : struct, IChannelLanes<TC>;
    }

    private readonly struct Multiply : IBlend16
    {
        public static TC M<TC>(TC cs, TC cb, TC sa, TC da)
            where TC : struct, IChannelLanes<TC> => cs * cb;
    }

    private readonly struct Screen : IBlend16
    {
        // cs·αd + cb·αs − cs·cb, which may wrap on the way; the whole numerator does not.
        public static TC M<TC>(TC cs, TC cb, TC sa, TC da)
            where TC : struct, IChannelLanes<TC> => (cs * da) + (cb * sa) - (cs * cb);
    }

    private readonly struct Darken : IBlend16
    {
        public static TC M<TC>(TC cs, TC cb, TC sa, TC da)
            where TC : struct, IChannelLanes<TC> => TC.Min(cs * da, cb * sa);
    }

    private readonly struct Lighten : IBlend16
    {
        public static TC M<TC>(TC cs, TC cb, TC sa, TC da)
            where TC : struct, IChannelLanes<TC> => TC.Max(cs * da, cb * sa);
    }

    private readonly struct Difference : IBlend16
    {
        // cs·αd + cb·αs − 2·min(cs·αd, cb·αs) = |cs·αd − cb·αs|.
        public static TC M<TC>(TC cs, TC cb, TC sa, TC da)
            where TC : struct, IChannelLanes<TC>
        {
            TC a = cs * da, b = cb * sa;
            return TC.Max(a, b) - TC.Min(a, b);
        }
    }

    private readonly struct Exclusion : IBlend16
    {
        public static TC M<TC>(TC cs, TC cb, TC sa, TC da)
            where TC : struct, IChannelLanes<TC> => (cs * da) + (cb * sa) - ((cs * cb) << 1);
    }

    private readonly struct HardLight : IBlend16
    {
        // Multiply 2·cs·cb where 2cs ≤ αs, else screen αs·αd − 2(αd − cb)(αs − cs); each fits where it is chosen.
        public static TC M<TC>(TC cs, TC cb, TC sa, TC da)
            where TC : struct, IChannelLanes<TC>
            => TC.Select(TC.LessOrEqual(cs << 1, sa), (cs * cb) << 1, (sa * da) - (((da - cb) * (sa - cs)) << 1));
    }

    private readonly struct Overlay : IBlend16
    {
        // Hard light with the layers swapped: the test is on the destination.
        public static TC M<TC>(TC cs, TC cb, TC sa, TC da)
            where TC : struct, IChannelLanes<TC>
            => TC.Select(TC.LessOrEqual(cb << 1, da), (cs * cb) << 1, (sa * da) - (((da - cb) * (sa - cs)) << 1));
    }

    private readonly struct Add : IBlend16
    {
        // min(αs·αd, cs·αd + cb·αs): the sum reaches αs·αd exactly when cs·αd ≥ αs·αd − cb·αs, tested without the sum overflowing.
        public static TC M<TC>(TC cs, TC cb, TC sa, TC da)
            where TC : struct, IChannelLanes<TC>
        {
            TC both = sa * da, a = cs * da, b = cb * sa;
            return TC.Select(TC.LessOrEqual(both - b, a), both, a + b);
        }
    }

    private readonly struct Subtract : IBlend16
    {
        public static TC M<TC>(TC cs, TC cb, TC sa, TC da)
            where TC : struct, IChannelLanes<TC>
        {
            TC a = cb * sa;
            return a - TC.Min(a, cs * da);
        }
    }

    /// <summary>The loop-invariant settings of one call.</summary>
    private readonly struct Context
    {
        internal readonly BlendMode Mode;
        internal readonly int Opacity255;
        internal readonly bool Solid;
        internal readonly bool Covered;
        internal readonly bool Integer;
        internal readonly uint Ka0, Ka1, Kb0, Kb1;
        internal readonly float LumR, LumG, LumB;

        internal Context(BlendMode mode, int opacity255, bool bgr, bool solid, bool covered)
        {
            Mode = mode;
            Opacity255 = opacity255;
            Solid = solid;
            Covered = covered;
            Integer = IsInteger(mode);
            Coefficients(mode, out float ka0, out float ka1, out float kb0, out float kb1);
            Ka0 = (uint)(ka0 * 255);
            Ka1 = unchecked((uint)(int)ka1);
            Kb0 = (uint)(kb0 * 255);
            Kb1 = unchecked((uint)(int)kb1);
            (LumR, LumG, LumB) = bgr ? (0.11f, 0.59f, 0.3f) : (0.3f, 0.59f, 0.11f);
        }
    }
}
