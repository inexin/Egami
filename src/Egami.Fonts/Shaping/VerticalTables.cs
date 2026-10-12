// Translated from HarfBuzz's hb-ot-hmtx-table.hh, hb-ot-vorg-table.hh and hb-ot-var-hvar-table.hh (MIT); see THIRD-PARTY-NOTICES.md.
using System.Runtime.CompilerServices;

using Egami.Fonts.Variations;

using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Shaping;

/// <summary>
/// A face's vertical metrics as HarfBuzz's ot font functions read them: vhea and vmtx advances and top bearings, VORG origins
/// and the VVAR deltas for both, read once per default face.
/// </summary>
internal sealed class VerticalTables
{
    private static readonly ConditionalWeakTable<Typeface, VerticalTables> s_tables = new();

    private readonly ReadOnlyMemory<byte> _vmtx;
    private readonly int _longMetrics;
    private readonly int _bearings;
    private readonly int _advances;
    private readonly int _glyphs;
    private readonly int _defaultAdvance;
    private readonly ReadOnlyMemory<byte> _vorg;
    private readonly DeltaSetIndexMap? _advanceMap;
    private readonly DeltaSetIndexMap? _originMap;

    private VerticalTables(Typeface face)
    {
        // As hmtxvmtx::accelerator_t: the long metrics vhea counts, the bearings maxp counts, then any extra advances.
        _defaultAdvance = face.UnitsPerEm;
        face.TryGetTable(new FontTag("vmtx"), out _vmtx);
        int len = _vmtx.Length & ~1;
        int longMetrics = 0;
        if (face.TryGetTable(new FontTag("vhea"), out ReadOnlyMemory<byte> vhea) && vhea.Length >= 36 && U16(vhea.Span, 0) != 0)
        {
            longMetrics = U16(vhea.Span, 34);
        }

        if (longMetrics * 4 > len)
        {
            longMetrics = len / 4;
        }

        len -= longMetrics * 4;
        int bearings = Math.Max(face.GlyphCount, longMetrics);
        if ((bearings - longMetrics) * 2 > len)
        {
            bearings = longMetrics + (len / 2);
        }

        len -= (bearings - longMetrics) * 2;
        if (longMetrics == 0)
        {
            bearings = 0;
        }

        _longMetrics = longMetrics;
        _bearings = bearings;
        _advances = bearings + (len / 2);
        _glyphs = Math.Max(face.GlyphCount, _advances);

        if (face.TryGetTable(new FontTag("VORG"), out ReadOnlyMemory<byte> vorg) && vorg.Length >= 8 && U16(vorg.Span, 0) == 1 &&
            8 + (4 * U16(vorg.Span, 6)) <= vorg.Length)
        {
            _vorg = vorg;
        }

        if (face.VariationData is not null && face.TryGetTable(new FontTag("VVAR"), out ReadOnlyMemory<byte> vvar) && vvar.Length >= 24 && U16(vvar.Span, 0) == 1)
        {
            try
            {
                ReadOnlySpan<byte> d = vvar.Span;
                long fileOffset = face.TableOffset(new FontTag("VVAR"));
                Store = ItemVariationStore.Read(vvar, (int)U32(d, 4), fileOffset);
                int advanceMap = (int)U32(d, 8), originMap = (int)U32(d, 20);
                _advanceMap = advanceMap != 0 ? DeltaSetIndexMap.Read(d, advanceMap, fileOffset) : null;
                _originMap = originMap != 0 ? DeltaSetIndexMap.Read(d, originMap, fileOffset) : null;
            }
            catch (CorruptDataException)
            {
                // As HarfBuzz, a table that fails its check is ignored.
                Store = null;
                _advanceMap = null;
                _originMap = null;
            }
        }
    }

    /// <summary>Gets whether the face has vertical metrics (vmtx with at least one long metric).</summary>
    public bool HasVmtx => _bearings != 0;

    /// <summary>Gets whether the face has a VORG table.</summary>
    public bool HasVorg => !_vorg.IsEmpty;

    /// <summary>Gets VVAR's variation store, or null when the face has no (sound) VVAR.</summary>
    public ItemVariationStore? Store { get; }

    public static VerticalTables For(Typeface face) => s_tables.GetValue(face.Root, f => new VerticalTables(f));

    /// <summary>A glyph's vmtx advance height, as get_advance_without_var_unscaled.</summary>
    public int GetAdvance(uint glyph)
    {
        ReadOnlySpan<byte> d = _vmtx.Span;
        if (glyph < (uint)_bearings)
        {
            return U16(d, 4 * (int)Math.Min(glyph, (uint)_longMetrics - 1));
        }

        if (_advances == 0)
        {
            return _defaultAdvance;
        }

        if (glyph >= (uint)_glyphs)
        {
            return 0;
        }

        if (_bearings == _advances)
        {
            return GetAdvance((uint)_bearings - 1);
        }

        int advances = (4 * _longMetrics) + (2 * (_bearings - _longMetrics));
        return U16(d, advances + (2 * (int)Math.Min(glyph - (uint)_bearings, (uint)(_advances - _bearings - 1))));
    }

    /// <summary>A glyph's top side bearing, as get_leading_bearing_without_var_unscaled; 0 past the bearings.</summary>
    public int GetTopBearing(uint glyph)
    {
        ReadOnlySpan<byte> d = _vmtx.Span;
        if (glyph < (uint)_longMetrics)
        {
            return S16(d, (4 * (int)glyph) + 2);
        }

        return glyph < (uint)_bearings ? S16(d, (4 * _longMetrics) + (2 * ((int)glyph - _longMetrics))) : 0;
    }

    /// <summary>VVAR's advance delta for a glyph; without an advance map the glyph id indexes the store directly.</summary>
    public float AdvanceDelta(uint glyph, float[] scalars)
    {
        uint index = _advanceMap?.Get((int)glyph) ?? glyph;
        return Store!.GetDelta(index, scalars);
    }

    /// <summary>VVAR's vertical origin delta for a glyph; 0 without an origin map.</summary>
    public float OriginDelta(uint glyph, float[]? scalars)
    {
        if (_originMap is null || Store is null || scalars is null)
        {
            return 0;
        }

        return Store.GetDelta(_originMap.Get((int)glyph), scalars);
    }

    /// <summary>A glyph's VORG y origin: its own record, else the default.</summary>
    public int GetYOrigin(uint glyph)
    {
        ReadOnlySpan<byte> d = _vorg.Span;
        int lo = 0, hi = U16(d, 6) - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            uint g = U16(d, 8 + (4 * mid));
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
                return S16(d, 10 + (4 * mid));
            }
        }

        return S16(d, 4);
    }
}
