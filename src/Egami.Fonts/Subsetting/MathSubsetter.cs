// After the OpenType MATH table specification, with HarfBuzz's hb-ot-math-table.hh subset methods (MIT; see
// THIRD-PARTY-NOTICES.md) as the reference for what a subset keeps.
using static Egami.Fonts.Shaping.OtData;

namespace Egami.Fonts.Subsetting;

/// <summary>
/// Writes a MATH table cut down to the kept glyphs and renumbered through the glyph map: the constants as they are, the italics
/// corrections, top accent attachments, extended shapes and kerning of kept glyphs, and the constructions of kept glyphs with
/// their kept variants and the assemblies whose parts are all kept. With a <see cref="VariationBake"/> each value takes its
/// variation delta and loses its device table.
/// </summary>
internal sealed class MathSubsetter
{
    private const int ConstantRecords = 51;

    private readonly ReadOnlyMemory<byte> _data;
    private readonly GlyphRemap _glyphs;
    private readonly VariationBake? _bake;
    private readonly OtGraph _graph = new();

    private MathSubsetter(ReadOnlyMemory<byte> data, GlyphRemap glyphs, VariationBake? bake)
    {
        _data = data;
        _glyphs = glyphs;
        _bake = bake;
    }

    private ReadOnlySpan<byte> D => _data.Span;

    /// <summary>The table, or null when its offsets cannot all fit.</summary>
    public static byte[]? Write(ReadOnlyMemory<byte> data, GlyphRemap glyphs, VariationBake? bake)
    {
        var writer = new MathSubsetter(data, glyphs, bake);
        OtObject root = writer.Header();
        return OtGraph.Pack(root, breadthFirst: true) ?? OtGraph.Pack(root, breadthFirst: false);
    }

    private OtObject Header()
    {
        ReadOnlySpan<byte> d = D;
        OtWriter w = OtGraph.Writer();
        w.U16(1);
        w.U16(0);
        w.Offset16(Constants(Off16(d, 0, 4)));
        w.Offset16(GlyphInfo(Off16(d, 0, 6)));
        w.Offset16(Variants(Off16(d, 0, 8)));
        return _graph.Add(w);
    }

    private OtObject? Constants(int t)
    {
        if (t == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> d = D;
        OtWriter w = OtGraph.Writer();
        for (int i = 0; i < 4; i++)
        {
            w.U16(U16(d, t + (i * 2)));
        }

        for (int i = 0; i < ConstantRecords; i++)
        {
            Value(w, t, t + 8 + (i * 4));
        }

        w.U16(U16(d, t + 8 + (ConstantRecords * 4)));
        return _graph.Add(w);
    }

    /// <summary>A MathValueRecord whose device offset counts from <paramref name="parent"/>.</summary>
    private void Value(OtWriter w, int parent, int at)
    {
        ReadOnlySpan<byte> d = D;
        int device = Off16(d, parent, at + 2);
        if (_bake is not null)
        {
            w.U16(S16(d, at) + _bake.Delta(d, device));
            w.Offset16(null);
            return;
        }

        w.U16(U16(d, at));
        w.Offset16(LayoutCommon.Device(_graph, d, device));
    }

    private OtObject? GlyphInfo(int t)
    {
        if (t == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> d = D;
        OtWriter w = OtGraph.Writer();
        w.Offset16(CoveredValues(Off16(d, t, t)));
        w.Offset16(CoveredValues(Off16(d, t, t + 2)));
        int extended = Off16(d, t, t + 4);
        w.Offset16(extended == 0 ? null : LayoutCommon.WriteCoverage(_graph, [.. LayoutCommon.Covered(d, extended, _glyphs).Select(c => _glyphs.Map(c.Old))]));
        w.Offset16(KernInfo(Off16(d, t, t + 6)));
        return _graph.Add(w);
    }

    /// <summary>MathItalicsCorrectionInfo or MathTopAccentAttachment: a coverage and a value per covered glyph.</summary>
    private OtObject? CoveredValues(int t)
    {
        if (t == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> d = D;
        int count = U16(d, t + 2);
        List<(int New, int Index)> kept = [.. LayoutCommon.Covered(d, Off16(d, t, t), _glyphs).Where(c => c.Index < count).Select(c => (_glyphs.Map(c.Old), c.Index))];
        OtWriter w = OtGraph.Writer();
        w.Offset16(LayoutCommon.WriteCoverage(_graph, [.. kept.Select(k => k.New)]));
        w.U16(kept.Count);
        foreach ((_, int index) in kept)
        {
            Value(w, t, t + 4 + (index * 4));
        }

        return _graph.Add(w);
    }

    private OtObject? KernInfo(int t)
    {
        if (t == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> d = D;
        int count = U16(d, t + 2);
        List<(int New, int Index)> kept = [.. LayoutCommon.Covered(d, Off16(d, t, t), _glyphs).Where(c => c.Index < count).Select(c => (_glyphs.Map(c.Old), c.Index))];
        OtWriter w = OtGraph.Writer();
        w.Offset16(LayoutCommon.WriteCoverage(_graph, [.. kept.Select(k => k.New)]));
        w.U16(kept.Count);
        foreach ((_, int index) in kept)
        {
            int record = t + 4 + (index * 8);
            for (int corner = 0; corner < 4; corner++)
            {
                w.Offset16(Kern(Off16(d, t, record + (corner * 2))));
            }
        }

        return _graph.Add(w);
    }

    private OtObject? Kern(int t)
    {
        if (t == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> d = D;
        int heights = U16(d, t);
        OtWriter w = OtGraph.Writer();
        w.U16(heights);
        for (int i = 0; i < (2 * heights) + 1; i++)
        {
            Value(w, t, t + 2 + (i * 4));
        }

        return _graph.Add(w);
    }

    private OtObject? Variants(int t)
    {
        if (t == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> d = D;
        int vertical = U16(d, t + 6);
        int horizontal = U16(d, t + 8);
        (OtObject Coverage, List<OtObject?> Constructions) v = Constructions(t, Off16(d, t, t + 2), vertical, t + 10);
        (OtObject Coverage, List<OtObject?> Constructions) h = Constructions(t, Off16(d, t, t + 4), horizontal, t + 10 + (vertical * 2));
        OtWriter w = OtGraph.Writer();
        w.U16(U16(d, t));
        w.Offset16(v.Coverage);
        w.Offset16(h.Coverage);
        w.U16(v.Constructions.Count);
        w.U16(h.Constructions.Count);
        v.Constructions.ForEach(w.Offset16);
        h.Constructions.ForEach(w.Offset16);
        return _graph.Add(w);
    }

    private (OtObject Coverage, List<OtObject?> Constructions) Constructions(int variants, int coverage, int count, int offsets)
    {
        ReadOnlySpan<byte> d = D;
        List<int> covered = [];
        List<OtObject?> constructions = [];
        if (coverage != 0)
        {
            foreach ((int old, int index) in LayoutCommon.Covered(d, coverage, _glyphs))
            {
                if (index < count && Construction(Off16(d, variants, offsets + (index * 2))) is { } construction)
                {
                    covered.Add(_glyphs.Map(old));
                    constructions.Add(construction);
                }
            }
        }

        return (LayoutCommon.WriteCoverage(_graph, covered), constructions);
    }

    /// <summary>A glyph's construction with its kept variants and its assembly when every part is kept; null when neither is left.</summary>
    private OtObject? Construction(int t)
    {
        if (t == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> d = D;
        OtObject? assembly = Assembly(Off16(d, t, t));
        int count = U16(d, t + 2);
        List<(int Glyph, int Advance)> variants = [];
        for (int i = 0; i < count; i++)
        {
            int record = t + 4 + (i * 4);
            if (_glyphs.TryMap(U16(d, record), out int now))
            {
                variants.Add((now, U16(d, record + 2)));
            }
        }

        if (assembly is null && variants.Count == 0)
        {
            return null;
        }

        OtWriter w = OtGraph.Writer();
        w.Offset16(assembly);
        w.U16(variants.Count);
        foreach ((int glyph, int advance) in variants)
        {
            w.U16(glyph);
            w.U16(advance);
        }

        return _graph.Add(w);
    }

    private OtObject? Assembly(int t)
    {
        if (t == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> d = D;
        int parts = U16(d, t + 4);
        OtWriter w = OtGraph.Writer();
        Value(w, t, t);
        w.U16(parts);
        for (int i = 0; i < parts; i++)
        {
            int part = t + 6 + (i * 10);
            if (!_glyphs.TryMap(U16(d, part), out int now))
            {
                return null;
            }

            w.U16(now);
            for (int field = 2; field < 10; field += 2)
            {
                w.U16(U16(d, part + field));
            }
        }

        return _graph.Add(w);
    }
}
