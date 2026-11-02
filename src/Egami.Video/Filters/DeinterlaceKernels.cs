using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Video.Filters;

/// <summary>
/// The row kernels of <see cref="Deinterlacer"/>, for 8-bit samples in 16-bit lanes and wider samples in 32-bit lanes.
/// <para>
/// The spatial guess for a missing pixel looks along five directions through it (vertical, and one or two pixels to either
/// side) and averages the two kept-field pixels along the direction whose three-pixel neighborhoods match best: edge-based
/// line averaging. A steeper direction is only tried when the shallower one on its side won, and vertical wins ties.
/// </para>
/// <para>
/// The adaptive kernel holds that guess within a band around the temporal guess, the average of the missing line in the
/// fields just before and after. The band is as wide as the motion: how much those two fields differ, and how much the kept
/// field's neighbors moved since the frame before and until the frame after. Where nothing moves the band closes and the
/// output is the woven line; a check against the lines two above and below widens it where the picture has vertical detail
/// the temporal guess would cut.
/// </para>
/// </summary>
internal static class DeinterlaceKernels
{
    /// <summary>The columns at each end the edge search would read past the row; they take the vertical average.</summary>
    internal const int Border = 3;

    /// <summary>How many times the measured motion the comb check may widen the band to; still pictures keep their woven lines.</summary>
    private const int CombReach = 3;

    /// <summary>The lines the adaptive kernel reads for one missing line.</summary>
    /// <typeparam name="T">The sample type.</typeparam>
    internal ref struct FieldRows<T>
        where T : unmanaged
    {
        /// <summary>The kept field's line above.</summary>
        public ReadOnlySpan<T> Above;

        /// <summary>The kept field's line below.</summary>
        public ReadOnlySpan<T> Below;

        /// <summary>The missing line in the other field just before.</summary>
        public ReadOnlySpan<T> Before;

        /// <summary>The missing line in the other field just after.</summary>
        public ReadOnlySpan<T> After;

        /// <summary>The line two above in the field before.</summary>
        public ReadOnlySpan<T> BeforeUp;

        /// <summary>The line two above in the field after.</summary>
        public ReadOnlySpan<T> AfterUp;

        /// <summary>The line two below in the field before.</summary>
        public ReadOnlySpan<T> BeforeDown;

        /// <summary>The line two below in the field after.</summary>
        public ReadOnlySpan<T> AfterDown;

        /// <summary>The kept field's line above, a frame earlier.</summary>
        public ReadOnlySpan<T> EarlierAbove;

        /// <summary>The kept field's line below, a frame earlier.</summary>
        public ReadOnlySpan<T> EarlierBelow;

        /// <summary>The kept field's line above, a frame later.</summary>
        public ReadOnlySpan<T> LaterAbove;

        /// <summary>The kept field's line below, a frame later.</summary>
        public ReadOnlySpan<T> LaterBelow;
    }

    /// <summary>Makes a missing line from the kept lines above and below it.</summary>
    public static void Bob<T>(ReadOnlySpan<T> above, ReadOnlySpan<T> below, Span<T> target, bool vectors = true)
        where T : unmanaged
    {
        int width = target.Length;
        int x = 0;
        int end = Math.Min(Border, width);
        for (; x < end; x++)
        {
            target[x] = From<T>(Spatial(above, below, x));
        }

        if (vectors && width >= (2 * Border) + 1)
        {
            if (Vector256.IsHardwareAccelerated)
            {
                x = typeof(T) == typeof(byte) ? Bob256<T, short>(above, below, target, x) : Bob256<T, int>(above, below, target, x);
            }
            else if (Vector128.IsHardwareAccelerated)
            {
                x = typeof(T) == typeof(byte) ? Bob128<T, short>(above, below, target, x) : Bob128<T, int>(above, below, target, x);
            }
        }

        for (; x < width; x++)
        {
            target[x] = From<T>(Spatial(above, below, x));
        }
    }

    /// <summary>Makes a missing line from the kept lines around it and the fields before and after.</summary>
    public static void Adaptive<T>(scoped in FieldRows<T> rows, Span<T> target, bool vectors = true)
        where T : unmanaged
    {
        int width = target.Length;
        int x = 0;
        int end = Math.Min(Border, width);
        for (; x < end; x++)
        {
            target[x] = From<T>(Adaptive(rows, x));
        }

        if (vectors && width >= (2 * Border) + 1)
        {
            if (Vector256.IsHardwareAccelerated)
            {
                x = typeof(T) == typeof(byte) ? Adaptive256<T, short>(rows, target, x) : Adaptive256<T, int>(rows, target, x);
            }
            else if (Vector128.IsHardwareAccelerated)
            {
                x = typeof(T) == typeof(byte) ? Adaptive128<T, short>(rows, target, x) : Adaptive128<T, int>(rows, target, x);
            }
        }

        for (; x < width; x++)
        {
            target[x] = From<T>(Adaptive(rows, x));
        }
    }

    /// <summary>The spatial guess for one pixel; the reference the vector kernels match.</summary>
    internal static int Spatial<T>(ReadOnlySpan<T> c, ReadOnlySpan<T> e, int x)
        where T : unmanaged
    {
        int prediction = (At(c, x) + At(e, x)) >> 1;
        if (x < Border || x >= c.Length - Border)
        {
            return prediction;
        }

        int best = Score(c, e, x, 0) - 1;
        int score = Score(c, e, x, -1);
        if (score < best)
        {
            best = score;
            prediction = (At(c, x - 1) + At(e, x + 1)) >> 1;
            score = Score(c, e, x, -2);
            if (score < best)
            {
                best = score;
                prediction = (At(c, x - 2) + At(e, x + 2)) >> 1;
            }
        }

        score = Score(c, e, x, 1);
        if (score < best)
        {
            best = score;
            prediction = (At(c, x + 1) + At(e, x - 1)) >> 1;
            score = Score(c, e, x, 2);
            if (score < best)
            {
                prediction = (At(c, x + 2) + At(e, x - 2)) >> 1;
            }
        }

        return prediction;
    }

    /// <summary>The adaptive result for one pixel; the reference the vector kernels match.</summary>
    internal static int Adaptive<T>(scoped in FieldRows<T> rows, int x)
        where T : unmanaged
    {
        int c = At(rows.Above, x);
        int e = At(rows.Below, x);
        int before = At(rows.Before, x);
        int after = At(rows.After, x);
        int d = (before + after) >> 1;
        int earlier = (Math.Abs(At(rows.EarlierAbove, x) - c) + Math.Abs(At(rows.EarlierBelow, x) - e)) >> 1;
        int later = (Math.Abs(At(rows.LaterAbove, x) - c) + Math.Abs(At(rows.LaterBelow, x) - e)) >> 1;
        int band = Math.Max(Math.Abs(before - after) >> 1, Math.Max(earlier, later));
        int b = (At(rows.BeforeUp, x) + At(rows.AfterUp, x)) >> 1;
        int f = (At(rows.BeforeDown, x) + At(rows.AfterDown, x)) >> 1;
        int most = Math.Max(Math.Max(d - e, d - c), Math.Min(b - c, f - e));
        int least = Math.Min(Math.Min(d - e, d - c), Math.Max(b - c, f - e));
        band = Math.Max(band, Math.Min(Math.Max(least, -most), CombReach * band));
        int spatial = Spatial(rows.Above, rows.Below, x);
        return Math.Max(Math.Min(spatial, d + band), d - band);
    }

    private static int Score<T>(ReadOnlySpan<T> c, ReadOnlySpan<T> e, int x, int k)
        where T : unmanaged =>
        Math.Abs(At(c, x + k - 1) - At(e, x - k - 1)) + Math.Abs(At(c, x + k) - At(e, x - k)) + Math.Abs(At(c, x + k + 1) - At(e, x - k + 1));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int At<T>(ReadOnlySpan<T> row, int x)
        where T : unmanaged =>
        typeof(T) == typeof(byte) ? Unsafe.As<T, byte>(ref MemoryMarshal.GetReference(row[x..])) : Unsafe.As<T, ushort>(ref MemoryMarshal.GetReference(row[x..]));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T From<T>(int value)
        where T : unmanaged
    {
        if (typeof(T) == typeof(byte))
        {
            byte b = (byte)value;
            return Unsafe.As<byte, T>(ref b);
        }

        ushort s = (ushort)value;
        return Unsafe.As<ushort, T>(ref s);
    }

    // The vector kernels: TS is the sample type, TL the lane type (short for bytes, int for wider samples). Each step reads half
    // a vector of samples and widens it to a full vector of lanes, so 8-bit rows go 16 pixels a step with Vector256.

    private static int Bob256<TS, TL>(ReadOnlySpan<TS> above, ReadOnlySpan<TS> below, Span<TS> target, int x)
        where TS : unmanaged
        where TL : unmanaged, INumber<TL>
    {
        ref TS c = ref MemoryMarshal.GetReference(above);
        ref TS e = ref MemoryMarshal.GetReference(below);
        ref TS t = ref MemoryMarshal.GetReference(target);
        int last = target.Length - Border - Vector256<TL>.Count;
        for (; x <= last; x += Vector256<TL>.Count)
        {
            Store256<TS, TL>(Spatial256<TS, TL>(ref c, ref e, x), ref Unsafe.Add(ref t, x));
        }

        return x;
    }

    private static int Bob128<TS, TL>(ReadOnlySpan<TS> above, ReadOnlySpan<TS> below, Span<TS> target, int x)
        where TS : unmanaged
        where TL : unmanaged, INumber<TL>
    {
        ref TS c = ref MemoryMarshal.GetReference(above);
        ref TS e = ref MemoryMarshal.GetReference(below);
        ref TS t = ref MemoryMarshal.GetReference(target);
        int last = target.Length - Border - Vector128<TL>.Count;
        for (; x <= last; x += Vector128<TL>.Count)
        {
            Store128<TS, TL>(Spatial128<TS, TL>(ref c, ref e, x), ref Unsafe.Add(ref t, x));
        }

        return x;
    }

    private static int Adaptive256<TS, TL>(scoped in FieldRows<TS> rows, Span<TS> target, int x)
        where TS : unmanaged
        where TL : unmanaged, INumber<TL>
    {
        ref TS c = ref MemoryMarshal.GetReference(rows.Above);
        ref TS e = ref MemoryMarshal.GetReference(rows.Below);
        ref TS before = ref MemoryMarshal.GetReference(rows.Before);
        ref TS after = ref MemoryMarshal.GetReference(rows.After);
        ref TS beforeUp = ref MemoryMarshal.GetReference(rows.BeforeUp);
        ref TS afterUp = ref MemoryMarshal.GetReference(rows.AfterUp);
        ref TS beforeDown = ref MemoryMarshal.GetReference(rows.BeforeDown);
        ref TS afterDown = ref MemoryMarshal.GetReference(rows.AfterDown);
        ref TS earlierAbove = ref MemoryMarshal.GetReference(rows.EarlierAbove);
        ref TS earlierBelow = ref MemoryMarshal.GetReference(rows.EarlierBelow);
        ref TS laterAbove = ref MemoryMarshal.GetReference(rows.LaterAbove);
        ref TS laterBelow = ref MemoryMarshal.GetReference(rows.LaterBelow);
        ref TS t = ref MemoryMarshal.GetReference(target);
        int last = target.Length - Border - Vector256<TL>.Count;
        for (; x <= last; x += Vector256<TL>.Count)
        {
            Vector256<TL> spatial = Spatial256<TS, TL>(ref c, ref e, x);
            Vector256<TL> c0 = Load256<TS, TL>(ref c, x);
            Vector256<TL> e0 = Load256<TS, TL>(ref e, x);
            Vector256<TL> b0 = Load256<TS, TL>(ref before, x);
            Vector256<TL> a0 = Load256<TS, TL>(ref after, x);
            Vector256<TL> d = (b0 + a0) >> 1;
            Vector256<TL> band = Vector256.Abs(b0 - a0) >> 1;
            band = Vector256.Max(band, (Vector256.Abs(Load256<TS, TL>(ref earlierAbove, x) - c0) + Vector256.Abs(Load256<TS, TL>(ref earlierBelow, x) - e0)) >> 1);
            band = Vector256.Max(band, (Vector256.Abs(Load256<TS, TL>(ref laterAbove, x) - c0) + Vector256.Abs(Load256<TS, TL>(ref laterBelow, x) - e0)) >> 1);
            Vector256<TL> bc = ((Load256<TS, TL>(ref beforeUp, x) + Load256<TS, TL>(ref afterUp, x)) >> 1) - c0;
            Vector256<TL> fe = ((Load256<TS, TL>(ref beforeDown, x) + Load256<TS, TL>(ref afterDown, x)) >> 1) - e0;
            Vector256<TL> de = d - e0;
            Vector256<TL> dc = d - c0;
            Vector256<TL> most = Vector256.Max(Vector256.Max(de, dc), Vector256.Min(bc, fe));
            Vector256<TL> least = Vector256.Min(Vector256.Min(de, dc), Vector256.Max(bc, fe));
            band = Vector256.Max(band, Vector256.Min(Vector256.Max(least, -most), band + band + band));
            Store256<TS, TL>(Vector256.Max(Vector256.Min(spatial, d + band), d - band), ref Unsafe.Add(ref t, x));
        }

        return x;
    }

    private static int Adaptive128<TS, TL>(scoped in FieldRows<TS> rows, Span<TS> target, int x)
        where TS : unmanaged
        where TL : unmanaged, INumber<TL>
    {
        ref TS c = ref MemoryMarshal.GetReference(rows.Above);
        ref TS e = ref MemoryMarshal.GetReference(rows.Below);
        ref TS before = ref MemoryMarshal.GetReference(rows.Before);
        ref TS after = ref MemoryMarshal.GetReference(rows.After);
        ref TS beforeUp = ref MemoryMarshal.GetReference(rows.BeforeUp);
        ref TS afterUp = ref MemoryMarshal.GetReference(rows.AfterUp);
        ref TS beforeDown = ref MemoryMarshal.GetReference(rows.BeforeDown);
        ref TS afterDown = ref MemoryMarshal.GetReference(rows.AfterDown);
        ref TS earlierAbove = ref MemoryMarshal.GetReference(rows.EarlierAbove);
        ref TS earlierBelow = ref MemoryMarshal.GetReference(rows.EarlierBelow);
        ref TS laterAbove = ref MemoryMarshal.GetReference(rows.LaterAbove);
        ref TS laterBelow = ref MemoryMarshal.GetReference(rows.LaterBelow);
        ref TS t = ref MemoryMarshal.GetReference(target);
        int last = target.Length - Border - Vector128<TL>.Count;
        for (; x <= last; x += Vector128<TL>.Count)
        {
            Vector128<TL> spatial = Spatial128<TS, TL>(ref c, ref e, x);
            Vector128<TL> c0 = Load128<TS, TL>(ref c, x);
            Vector128<TL> e0 = Load128<TS, TL>(ref e, x);
            Vector128<TL> b0 = Load128<TS, TL>(ref before, x);
            Vector128<TL> a0 = Load128<TS, TL>(ref after, x);
            Vector128<TL> d = (b0 + a0) >> 1;
            Vector128<TL> band = Vector128.Abs(b0 - a0) >> 1;
            band = Vector128.Max(band, (Vector128.Abs(Load128<TS, TL>(ref earlierAbove, x) - c0) + Vector128.Abs(Load128<TS, TL>(ref earlierBelow, x) - e0)) >> 1);
            band = Vector128.Max(band, (Vector128.Abs(Load128<TS, TL>(ref laterAbove, x) - c0) + Vector128.Abs(Load128<TS, TL>(ref laterBelow, x) - e0)) >> 1);
            Vector128<TL> bc = ((Load128<TS, TL>(ref beforeUp, x) + Load128<TS, TL>(ref afterUp, x)) >> 1) - c0;
            Vector128<TL> fe = ((Load128<TS, TL>(ref beforeDown, x) + Load128<TS, TL>(ref afterDown, x)) >> 1) - e0;
            Vector128<TL> de = d - e0;
            Vector128<TL> dc = d - c0;
            Vector128<TL> most = Vector128.Max(Vector128.Max(de, dc), Vector128.Min(bc, fe));
            Vector128<TL> least = Vector128.Min(Vector128.Min(de, dc), Vector128.Max(bc, fe));
            band = Vector128.Max(band, Vector128.Min(Vector128.Max(least, -most), band + band + band));
            Store128<TS, TL>(Vector128.Max(Vector128.Min(spatial, d + band), d - band), ref Unsafe.Add(ref t, x));
        }

        return x;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<TL> Spatial256<TS, TL>(ref TS c, ref TS e, int x)
        where TS : unmanaged
        where TL : unmanaged, INumber<TL>
    {
        Vector256<TL> cm3 = Load256<TS, TL>(ref c, x - 3);
        Vector256<TL> cm2 = Load256<TS, TL>(ref c, x - 2);
        Vector256<TL> cm1 = Load256<TS, TL>(ref c, x - 1);
        Vector256<TL> c0 = Load256<TS, TL>(ref c, x);
        Vector256<TL> cp1 = Load256<TS, TL>(ref c, x + 1);
        Vector256<TL> cp2 = Load256<TS, TL>(ref c, x + 2);
        Vector256<TL> cp3 = Load256<TS, TL>(ref c, x + 3);
        Vector256<TL> em3 = Load256<TS, TL>(ref e, x - 3);
        Vector256<TL> em2 = Load256<TS, TL>(ref e, x - 2);
        Vector256<TL> em1 = Load256<TS, TL>(ref e, x - 1);
        Vector256<TL> e0 = Load256<TS, TL>(ref e, x);
        Vector256<TL> ep1 = Load256<TS, TL>(ref e, x + 1);
        Vector256<TL> ep2 = Load256<TS, TL>(ref e, x + 2);
        Vector256<TL> ep3 = Load256<TS, TL>(ref e, x + 3);

        Vector256<TL> best = Vector256.Abs(cm1 - em1) + Vector256.Abs(c0 - e0) + Vector256.Abs(cp1 - ep1) - Vector256<TL>.One;
        Vector256<TL> prediction = (c0 + e0) >> 1;

        Vector256<TL> score = Vector256.Abs(cm2 - e0) + Vector256.Abs(cm1 - ep1) + Vector256.Abs(c0 - ep2);
        Vector256<TL> won = Vector256.LessThan(score, best);
        best = Vector256.ConditionalSelect(won, score, best);
        prediction = Vector256.ConditionalSelect(won, (cm1 + ep1) >> 1, prediction);
        score = Vector256.Abs(cm3 - ep1) + Vector256.Abs(cm2 - ep2) + Vector256.Abs(cm1 - ep3);
        Vector256<TL> steeper = won & Vector256.LessThan(score, best);
        best = Vector256.ConditionalSelect(steeper, score, best);
        prediction = Vector256.ConditionalSelect(steeper, (cm2 + ep2) >> 1, prediction);

        score = Vector256.Abs(c0 - em2) + Vector256.Abs(cp1 - em1) + Vector256.Abs(cp2 - e0);
        won = Vector256.LessThan(score, best);
        best = Vector256.ConditionalSelect(won, score, best);
        prediction = Vector256.ConditionalSelect(won, (cp1 + em1) >> 1, prediction);
        score = Vector256.Abs(cp1 - em3) + Vector256.Abs(cp2 - em2) + Vector256.Abs(cp3 - em1);
        steeper = won & Vector256.LessThan(score, best);
        return Vector256.ConditionalSelect(steeper, (cp2 + em2) >> 1, prediction);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<TL> Spatial128<TS, TL>(ref TS c, ref TS e, int x)
        where TS : unmanaged
        where TL : unmanaged, INumber<TL>
    {
        Vector128<TL> cm3 = Load128<TS, TL>(ref c, x - 3);
        Vector128<TL> cm2 = Load128<TS, TL>(ref c, x - 2);
        Vector128<TL> cm1 = Load128<TS, TL>(ref c, x - 1);
        Vector128<TL> c0 = Load128<TS, TL>(ref c, x);
        Vector128<TL> cp1 = Load128<TS, TL>(ref c, x + 1);
        Vector128<TL> cp2 = Load128<TS, TL>(ref c, x + 2);
        Vector128<TL> cp3 = Load128<TS, TL>(ref c, x + 3);
        Vector128<TL> em3 = Load128<TS, TL>(ref e, x - 3);
        Vector128<TL> em2 = Load128<TS, TL>(ref e, x - 2);
        Vector128<TL> em1 = Load128<TS, TL>(ref e, x - 1);
        Vector128<TL> e0 = Load128<TS, TL>(ref e, x);
        Vector128<TL> ep1 = Load128<TS, TL>(ref e, x + 1);
        Vector128<TL> ep2 = Load128<TS, TL>(ref e, x + 2);
        Vector128<TL> ep3 = Load128<TS, TL>(ref e, x + 3);

        Vector128<TL> best = Vector128.Abs(cm1 - em1) + Vector128.Abs(c0 - e0) + Vector128.Abs(cp1 - ep1) - Vector128<TL>.One;
        Vector128<TL> prediction = (c0 + e0) >> 1;

        Vector128<TL> score = Vector128.Abs(cm2 - e0) + Vector128.Abs(cm1 - ep1) + Vector128.Abs(c0 - ep2);
        Vector128<TL> won = Vector128.LessThan(score, best);
        best = Vector128.ConditionalSelect(won, score, best);
        prediction = Vector128.ConditionalSelect(won, (cm1 + ep1) >> 1, prediction);
        score = Vector128.Abs(cm3 - ep1) + Vector128.Abs(cm2 - ep2) + Vector128.Abs(cm1 - ep3);
        Vector128<TL> steeper = won & Vector128.LessThan(score, best);
        best = Vector128.ConditionalSelect(steeper, score, best);
        prediction = Vector128.ConditionalSelect(steeper, (cm2 + ep2) >> 1, prediction);

        score = Vector128.Abs(c0 - em2) + Vector128.Abs(cp1 - em1) + Vector128.Abs(cp2 - e0);
        won = Vector128.LessThan(score, best);
        best = Vector128.ConditionalSelect(won, score, best);
        prediction = Vector128.ConditionalSelect(won, (cp1 + em1) >> 1, prediction);
        score = Vector128.Abs(cp1 - em3) + Vector128.Abs(cp2 - em2) + Vector128.Abs(cp3 - em1);
        steeper = won & Vector128.LessThan(score, best);
        return Vector128.ConditionalSelect(steeper, (cp2 + em2) >> 1, prediction);
    }

    /// <summary>Reads half a Vector256 of samples at <paramref name="x"/> and widens them to lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<TL> Load256<TS, TL>(ref TS row, int x)
        where TS : unmanaged
        where TL : unmanaged
    {
        if (typeof(TS) == typeof(byte))
        {
            Vector128<byte> samples = Vector128.LoadUnsafe(ref Unsafe.As<TS, byte>(ref row), (nuint)x);
            return Vector256.WidenLower(samples.ToVector256Unsafe()).As<ushort, TL>();
        }

        Vector128<ushort> wide = Vector128.LoadUnsafe(ref Unsafe.As<TS, ushort>(ref row), (nuint)x);
        return Vector256.WidenLower(wide.ToVector256Unsafe()).As<uint, TL>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store256<TS, TL>(Vector256<TL> value, ref TS target)
        where TS : unmanaged
        where TL : unmanaged
    {
        if (typeof(TS) == typeof(byte))
        {
            Vector256<ushort> lanes = value.As<TL, ushort>();
            Vector256.Narrow(lanes, lanes).GetLower().StoreUnsafe(ref Unsafe.As<TS, byte>(ref target));
            return;
        }

        Vector256<uint> wide = value.As<TL, uint>();
        Vector256.Narrow(wide, wide).GetLower().StoreUnsafe(ref Unsafe.As<TS, ushort>(ref target));
    }

    /// <summary>Reads 8 bytes of samples at <paramref name="x"/> and widens them to a Vector128 of lanes.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<TL> Load128<TS, TL>(ref TS row, int x)
        where TS : unmanaged
        where TL : unmanaged
    {
        ulong bits = Unsafe.ReadUnaligned<ulong>(ref Unsafe.As<TS, byte>(ref Unsafe.Add(ref row, x)));
        Vector128<ulong> samples = Vector128.CreateScalarUnsafe(bits);
        return typeof(TS) == typeof(byte)
            ? Vector128.WidenLower(samples.AsByte()).As<ushort, TL>()
            : Vector128.WidenLower(samples.AsUInt16()).As<uint, TL>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Store128<TS, TL>(Vector128<TL> value, ref TS target)
        where TS : unmanaged
        where TL : unmanaged
    {
        ulong bits = typeof(TS) == typeof(byte)
            ? Vector128.Narrow(value.As<TL, ushort>(), value.As<TL, ushort>()).AsUInt64().ToScalar()
            : Vector128.Narrow(value.As<TL, uint>(), value.As<TL, uint>()).AsUInt64().ToScalar();
        Unsafe.WriteUnaligned(ref Unsafe.As<TS, byte>(ref target), bits);
    }
}
