using System.Runtime.CompilerServices;

using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>
/// The process-wide cache of glyph coverage masks the raster canvas draws small text from: one mask per typeface (each variation
/// position its own), glyph, device em size, axis scales, subpixel phase, edging, hinting and synthesis, for glyphs up to 256
/// device pixels per em under a scale-and-translate transform. Masks hold one byte per pixel, or three for LCD edging. Bigger,
/// rotated or skewed glyphs fill as paths and are not cached. Masks are rendered by the same rasterizer as paths and drawn at the
/// same rounded positions either way, so the cache changes speed, never pixels. Shared by every canvas on every thread; least recently used masks
/// go first when the budget is spent.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="cache"/>
/// </example>
public static class GlyphCache
{
    private const int StripeCount = 8;
    private const int EntryOverhead = 64;
    private static readonly Stripe[] s_stripes = CreateStripes();
    private static long s_byteLimit = 4L * 1024 * 1024;

    /// <summary>Gets or sets the most bytes the masks may hold, 4 MiB by default; 0 turns caching off. Lowering it evicts at once.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public static long ByteLimit
    {
        get => Volatile.Read(ref s_byteLimit);
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            Volatile.Write(ref s_byteLimit, value);
            foreach (Stripe stripe in s_stripes)
            {
                stripe.Trim(value / StripeCount);
            }
        }
    }

    /// <summary>Gets the bytes the cached masks hold now.</summary>
    internal static long BytesUsed => s_stripes.Sum(s => s.Bytes);

    /// <summary>Drops every cached mask.</summary>
    public static void Clear()
    {
        foreach (Stripe stripe in s_stripes)
        {
            stripe.Trim(0);
        }
    }

    /// <summary>Returns the cached mask for <paramref name="key"/>, rendering and adding it on a miss.</summary>
    internal static GlyphMask GetOrAdd(in GlyphKey key, Func<GlyphMask> render)
    {
        long limit = ByteLimit;
        if (limit == 0)
        {
            return render();
        }

        Stripe stripe = s_stripes[(key.GetHashCode() & int.MaxValue) % StripeCount];
        if (stripe.TryGet(key, out GlyphMask? mask))
        {
            return mask;
        }

        mask = render();
        stripe.Add(key, mask, limit / StripeCount);
        return mask;
    }

    private static Stripe[] CreateStripes()
    {
        var stripes = new Stripe[StripeCount];
        for (int i = 0; i < StripeCount; i++)
        {
            stripes[i] = new Stripe();
        }

        return stripes;
    }

    private sealed class Stripe
    {
        private readonly Lock _lock = new();
        private readonly Dictionary<GlyphKey, LinkedListNode<(GlyphKey Key, GlyphMask Mask)>> _map = [];
        private readonly LinkedList<(GlyphKey Key, GlyphMask Mask)> _order = new();

        public long Bytes { get; private set; }

        public bool TryGet(in GlyphKey key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out GlyphMask? mask)
        {
            lock (_lock)
            {
                if (_map.TryGetValue(key, out LinkedListNode<(GlyphKey, GlyphMask)>? node))
                {
                    _order.Remove(node);
                    _order.AddFirst(node);
                    mask = node.Value.Item2;
                    return true;
                }
            }

            mask = null;
            return false;
        }

        public void Add(in GlyphKey key, GlyphMask mask, long limit)
        {
            long size = mask.ByteSize + EntryOverhead;
            if (size > limit)
            {
                return;
            }

            lock (_lock)
            {
                if (_map.ContainsKey(key))
                {
                    return;
                }

                _map[key] = _order.AddFirst((key, mask));
                Bytes += size;
                TrimLocked(limit);
            }
        }

        public void Trim(long limit)
        {
            lock (_lock)
            {
                TrimLocked(limit);
            }
        }

        private void TrimLocked(long limit)
        {
            while (Bytes > limit && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Key);
                Bytes -= last.Value.Mask.ByteSize + EntryOverhead;
            }
        }
    }
}

/// <summary>What a cached glyph mask depends on. The typeface compares by reference.</summary>
internal readonly record struct GlyphKey(Typeface Face, ushort Glyph, float EmX, float EmY, GlyphStyle Style, int Phase)
{
    public bool Equals(GlyphKey other)
        => ReferenceEquals(Face, other.Face) && Glyph == other.Glyph && EmX == other.EmX && EmY == other.EmY && Style == other.Style && Phase == other.Phase;

    public override int GetHashCode() => HashCode.Combine(RuntimeHelpers.GetHashCode(Face), Glyph, EmX, EmY, Style, Phase);
}

/// <summary>
/// A glyph's coverage over whole device pixels, relative to its origin snapped to a pixel corner: one byte per pixel, or three
/// (red, green, blue) for LCD edging.
/// </summary>
internal sealed class GlyphMask(Rect bounds, byte[] coverage, int channels)
{
    public static readonly GlyphMask Empty = new(default, [], 1);

    public Rect Bounds { get; } = bounds;

    public byte[] Coverage { get; } = coverage;

    public int Channels { get; } = channels;

    /// <summary>Gets a color glyph's premultiplied pixels over <see cref="Bounds"/>, or <see langword="null"/> for a coverage mask.</summary>
    public PremulRgba8[]? Pixels { get; init; }

    /// <summary>Gets the bytes the mask holds, for the cache's budget.</summary>
    public long ByteSize => Coverage.Length + ((Pixels?.Length ?? 0) * 4L);
}
