namespace Egami.Fonts.Shaping;

/// <summary>What a shaped glyph says about breaking the text next to it, as HarfBuzz's glyph flags.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="run"/>
/// </example>
[Flags]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "HarfBuzz's name for these flags (hb_glyph_flags_t), which shaping code knows.")]
public enum GlyphFlags
{
    /// <summary>The text can be broken before this glyph's cluster without shaping again.</summary>
    None = 0,

    /// <summary>Breaking the text before this glyph's cluster changes the shaping: shape the pieces again.</summary>
    UnsafeToBreak = 1,

    /// <summary>Shaping the text before and after this point separately and joining the results may differ from shaping it whole.</summary>
    UnsafeToConcat = 2,

    /// <summary>A tatweel (U+0640) can go before this glyph's cluster to lengthen the word without changing the shaping around it,
    /// as kashida justification needs; only set when <see cref="ShapingOptions.ProduceSafeToInsertTatweel"/> or
    /// <see cref="RunProperties.ProduceSafeToInsertTatweel"/> asks for it. Where it is set, <see cref="UnsafeToBreak"/> is too.</summary>
    SafeToInsertTatweel = 4,
}

/// <summary>How a shaped glyph moves the pen and sits relative to it, in the units of the <see cref="Font"/> it was shaped with.</summary>
/// <param name="XAdvance">How far the pen moves right after the glyph.</param>
/// <param name="YAdvance">How far the pen moves up after the glyph (vertical runs move down, so this is negative).</param>
/// <param name="XOffset">How far right of the pen the glyph is drawn.</param>
/// <param name="YOffset">How far above the pen the glyph is drawn.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="run"/>
/// </example>
public readonly record struct GlyphPosition(float XAdvance, float YAdvance, float XOffset, float YOffset);

/// <summary>Which characters a glyph's cluster covers, as HarfBuzz's cluster levels.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="run"/>
/// </example>
public enum ClusterLevel
{
    /// <summary>Marks merge into their base and clusters rise in logical order: one cluster per grapheme (the default).</summary>
    MonotoneGraphemes = 0,

    /// <summary>Clusters rise in logical order, but marks keep clusters of their own where the font keeps them apart.</summary>
    MonotoneCharacters = 1,

    /// <summary>Each glyph keeps the cluster of the character it came from where it can; clusters may go down.</summary>
    Characters = 2,
}

/// <summary>
/// A reusable buffer of shaped glyphs: glyph ids, the UTF-16 index of the text each came from, glyph flags and positions, in
/// visual order. <see cref="TextShaper.ShapeRun"/> fills it; reuse one buffer for many runs to avoid allocations.
/// </summary>
/// <remarks>Not thread-safe: one buffer per thread.</remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="run"/>
/// </example>
public sealed class GlyphBuffer
{
    // Sized by the first run loaded: a paragraph keeps one buffer per run.
    private ushort[] _glyphs = [];
    private int[] _clusters = [];
    private GlyphFlags[] _flags = [];
    private GlyphPosition[] _positions = [];

    /// <summary>Gets the number of glyphs.</summary>
    public int Count { get; private set; }

    /// <summary>Gets the glyph ids.</summary>
    public ReadOnlySpan<ushort> GlyphIds => _glyphs.AsSpan(0, Count);

    /// <summary>Gets the UTF-16 index in the text each glyph's cluster starts at.</summary>
    public ReadOnlySpan<int> Clusters => _clusters.AsSpan(0, Count);

    /// <summary>Gets each glyph's flags.</summary>
    public ReadOnlySpan<GlyphFlags> Flags => _flags.AsSpan(0, Count);

    /// <summary>Gets each glyph's advance and offset, in the units of the font the run was shaped with.</summary>
    public ReadOnlySpan<GlyphPosition> Positions => _positions.AsSpan(0, Count);

    /// <summary>Gets the run's advance: the sum of the glyphs' horizontal advances.</summary>
    public float Advance
    {
        get
        {
            float sum = 0;
            for (int i = 0; i < Count; i++)
            {
                sum += _positions[i].XAdvance;
            }

            return sum;
        }
    }

    /// <summary>Empties the buffer, keeping its storage.</summary>
    public void Clear() => Count = 0;

    /// <summary>Copies a shaped layout buffer in, scaling font units by <paramref name="scale"/>.</summary>
    internal void Load(LayoutBuffer buffer, float scale)
    {
        int n = buffer.Len;
        if (_glyphs.Length < n)
        {
            int size = _glyphs.Length == 0 ? n : Math.Max(n, _glyphs.Length * 2);
            _glyphs = new ushort[size];
            _clusters = new int[size];
            _flags = new GlyphFlags[size];
            _positions = new GlyphPosition[size];
        }

        for (int i = 0; i < n; i++)
        {
            GlyphInfo info = buffer.Info[i];
            GlyphPos pos = buffer.Pos[i];
            _glyphs[i] = info.Codepoint <= ushort.MaxValue ? (ushort)info.Codepoint : (ushort)0;
            _clusters[i] = info.Cluster;
            _flags[i] = (GlyphFlags)(info.Mask & LayoutBuffer.FlagDefined);
            _positions[i] = new GlyphPosition(pos.XAdvance * scale, pos.YAdvance * scale, pos.XOffset * scale, pos.YOffset * scale);
        }

        Count = n;
    }

    /// <summary>
    /// Replaces each horizontal advance with the glyph's hinted advance at the font's size (or its design advance, rounded, when it
    /// cannot be hinted) plus what shaping added to it (kerning, positioning) rounded to whole units, and rounds the offsets, for
    /// <see cref="FontMetrics.GridFitted"/>. Zero advances (marks) stay zero; vertical runs are left as they are.
    /// </summary>
    internal void GridFit(Typeface face, Font font)
    {
        float scale = font.Size / face.UnitsPerEm;
        for (int i = 0; i < Count; i++)
        {
            GlyphPosition p = _positions[i];
            if (p.YAdvance != 0)
            {
                continue;
            }

            float advance = p.XAdvance;
            if (advance != 0)
            {
                float design = face.GetAdvance(_glyphs[i]) * scale;
                float hinted = face.GetHintedGlyph(_glyphs[i], font.Size, font.HintingTarget)?.Advance ?? MathF.Round(design);
                advance = hinted + MathF.Round(advance - design);
            }

            _positions[i] = new GlyphPosition(advance, 0, MathF.Round(p.XOffset), MathF.Round(p.YOffset));
        }
    }
}
