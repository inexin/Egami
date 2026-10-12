using System.Buffers.Binary;
using System.Runtime.CompilerServices;

namespace Egami.Fonts.Shaping;

/// <summary>
/// Bounds-safe big-endian reads for the layout tables. A read past the end returns 0, so a damaged offset behaves as a null
/// offset (an empty coverage, no subtable), as HarfBuzz's sanitizer makes it.
/// </summary>
internal static class OtData
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ushort U16(ReadOnlySpan<byte> d, int o) =>
        (uint)o <= (uint)(d.Length - 2) ? BinaryPrimitives.ReadUInt16BigEndian(d.Slice(o, 2)) : (ushort)0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short S16(ReadOnlySpan<byte> d, int o) => (short)U16(d, o);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint U24(ReadOnlySpan<byte> d, int o) =>
        (uint)o <= (uint)(d.Length - 3) ? ((uint)d[o] << 16) | ((uint)d[o + 1] << 8) | d[o + 2] : 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint U32(ReadOnlySpan<byte> d, int o) =>
        (uint)o <= (uint)(d.Length - 4) ? BinaryPrimitives.ReadUInt32BigEndian(d.Slice(o, 4)) : 0;

    /// <summary>Follows a 16-bit offset from <paramref name="table"/>; 0 (null) for a zero offset or one past the end.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int Off16(ReadOnlySpan<byte> d, int table, int at)
    {
        int off = U16(d, at);
        return off == 0 || table + off >= d.Length ? 0 : table + off;
    }

    /// <summary>Follows a 32-bit offset from <paramref name="table"/>; 0 (null) for a zero offset or one past the end.</summary>
    public static int Off32(ReadOnlySpan<byte> d, int table, int at)
    {
        uint off = U32(d, at);
        return off == 0 || table + (long)off >= d.Length ? 0 : table + (int)off;
    }

    /// <summary>The coverage index of a glyph, or -1.</summary>
    public static int Coverage(ReadOnlySpan<byte> d, int t, uint glyph)
    {
        if (t == 0)
        {
            return -1;
        }

        int format = U16(d, t);
        if (format == 1)
        {
            int count = U16(d, t + 2);
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                uint g = U16(d, t + 4 + (mid * 2));
                if (glyph < g)
                {
                    hi = mid - 1;
                }
                else if (glyph > g)
                {
                    lo = mid + 1;
                }
                else
                {
                    return mid;
                }
            }

            return -1;
        }

        if (format == 2)
        {
            int count = U16(d, t + 2);
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                int r = t + 4 + (mid * 6);
                uint start = U16(d, r), end = U16(d, r + 2);
                if (glyph < start)
                {
                    hi = mid - 1;
                }
                else if (glyph > end)
                {
                    lo = mid + 1;
                }
                else
                {
                    return (int)(U16(d, r + 4) + glyph - start);
                }
            }

            return -1;
        }

        return -1;
    }

    /// <summary>The class of a glyph in a class definition table; 0 for unlisted glyphs or a null table.</summary>
    public static int ClassOf(ReadOnlySpan<byte> d, int t, uint glyph)
    {
        if (t == 0)
        {
            return 0;
        }

        int format = U16(d, t);
        if (format == 1)
        {
            uint start = U16(d, t + 2);
            int count = U16(d, t + 4);
            uint i = glyph - start;
            return i < (uint)count ? U16(d, t + 6 + (int)(i * 2)) : 0;
        }

        if (format == 2)
        {
            int count = U16(d, t + 2);
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                int r = t + 4 + (mid * 6);
                uint start = U16(d, r), end = U16(d, r + 2);
                if (glyph < start)
                {
                    hi = mid - 1;
                }
                else if (glyph > end)
                {
                    lo = mid + 1;
                }
                else
                {
                    return U16(d, r + 4);
                }
            }

            return 0;
        }

        return 0;
    }

    /// <summary>Adds every glyph a coverage table lists to <paramref name="glyphs"/>.</summary>
    public static void CollectCoverage(ReadOnlySpan<byte> d, int t, HashSet<int> glyphs)
    {
        if (t == 0)
        {
            return;
        }

        int format = U16(d, t);
        int count = U16(d, t + 2);
        if (format == 1)
        {
            for (int i = 0; i < count; i++)
            {
                glyphs.Add(U16(d, t + 4 + (i * 2)));
            }
        }
        else if (format == 2)
        {
            for (int i = 0; i < count; i++)
            {
                int r = t + 4 + (i * 6);
                int start = U16(d, r), end = U16(d, r + 2);
                for (int g = start; g <= end && g - start < 65536; g++)
                {
                    glyphs.Add(g);
                }
            }
        }
    }

    /// <summary>Adds every glyph a class definition puts in <paramref name="klass"/> (class 0 is everything unlisted, so not collected).</summary>
    public static void CollectClass(ReadOnlySpan<byte> d, int t, int klass, HashSet<int> glyphs)
    {
        if (t == 0)
        {
            return;
        }

        int format = U16(d, t);
        if (format == 1)
        {
            int start = U16(d, t + 2);
            int count = U16(d, t + 4);
            for (int i = 0; i < count; i++)
            {
                if (U16(d, t + 6 + (i * 2)) == klass)
                {
                    glyphs.Add(start + i);
                }
            }
        }
        else if (format == 2)
        {
            int count = U16(d, t + 2);
            for (int i = 0; i < count; i++)
            {
                int r = t + 4 + (i * 6);
                if (U16(d, r + 4) == klass)
                {
                    for (int g = U16(d, r); g <= U16(d, r + 2); g++)
                    {
                        glyphs.Add(g);
                    }
                }
            }
        }
    }

    public static uint Tag(ReadOnlySpan<byte> d, int o) => U32(d, o);
}
