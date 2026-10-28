using Egami.Fonts.Hinting;

namespace Egami.Fonts;

public sealed partial class Typeface
{
    // Hinted glyphs kept per face; cleared when this many are held, since each holds its points.
    private const int MaxHintedGlyphs = 8192;

    private readonly object _hintingLock = new();
    private HintingData? _hintingData;
    private GlyphHinter?[]? _hinters;
    private Dictionary<(TrueTypeTarget Target, int Ppem, ushort Glyph), HintedGlyph?>? _hinted;
    private GlyphHinter? _monochrome;
    private Dictionary<(int Ppem, ushort Glyph), float?>? _compatible;

    /// <summary>Gets the face's TrueType hinting tables, read once.</summary>
    internal HintingData HintingData
    {
        get
        {
            lock (_hintingLock)
            {
                return _hintingData ??= _glyf is null ? HintingData.None : HintingData.Read(this);
            }
        }
    }

    /// <summary>
    /// Gets a value indicating whether <see cref="FontHinting.Full"/> can hint this face: TrueType outlines with instructions, not
    /// variable.
    /// </summary>
    internal bool CanRunInstructions => _glyf is not null && !IsVariable && _instance is null && HintingData.HasHinting;

    /// <summary>The whole ppem a size hints at: rounded, at least 1.</summary>
    internal static int HintingPpem(float pixelsPerEm) => Math.Max(1, (int)MathF.Round(MathF.Abs(pixelsPerEm)));

    /// <summary>
    /// GDI's compatible width of a glyph at a size, in whole pixels: hdmx when it holds the ppem; else, where LTSH says the glyph
    /// scales linearly from this ppem, its phantom points rounded (round(pp2) − round(pp1)); else the advance its instructions give
    /// for a monochrome rasterizer. Null when the face cannot be hinted. Cached per ppem.
    /// </summary>
    internal float? GetCompatibleAdvance(ushort glyph, float pixelsPerEm)
    {
        if (!CanRunInstructions || glyph >= GlyphCount)
        {
            return null;
        }

        int ppem = HintingPpem(pixelsPerEm);
        HintingData data = HintingData;
        if (data.Hdmx.TryGetValue(ppem, out byte[]? widths) && glyph < widths.Length)
        {
            return widths[glyph];
        }

        float em = data.IntegerPpem ? ppem : MathF.Abs(pixelsPerEm);
        if (glyph < data.Ltsh.Length && data.Ltsh[glyph] != 0 && ppem >= data.Ltsh[glyph])
        {
            float scale = em / UnitsPerEm;
            float pp1 = (GetGlyphExtents(glyph).XMin - GetLeftSideBearing(glyph)) * scale;
            return MathF.Round(pp1 + (GetAdvance(glyph) * scale)) - MathF.Round(pp1);
        }

        lock (_hintingLock)
        {
            _compatible ??= [];
            if (_compatible.TryGetValue((ppem, glyph), out float? cached))
            {
                return cached;
            }

            if (_compatible.Count >= MaxHintedGlyphs)
            {
                _compatible.Clear();
            }

            _monochrome ??= new GlyphHinter(this, _glyf!, data, InterpreterTarget.Monochrome);
            float? advance;
            try
            {
                advance = _monochrome.Hint(glyph, ppem, (long)Math.Round(em * 64 * 65536 / UnitsPerEm))?.Advance is float a ? MathF.Round(a) : null;
            }
            catch (CorruptDataException)
            {
                advance = null;
            }

            _compatible[(ppem, glyph)] = advance;
            return advance;
        }
    }

    /// <summary>
    /// A glyph hinted by its instructions for a target at a size in pixels per em, or null when the face, the size (gasp) or its
    /// programs do not allow it. The size is rounded to a whole ppem, and scaled from it when head.flags bit 3 asks (nearly
    /// always).
    /// </summary>
    internal HintedGlyph? GetHintedGlyph(ushort glyph, float pixelsPerEm, TrueTypeTarget target)
    {
        if (!CanRunInstructions || glyph >= GlyphCount)
        {
            return null;
        }

        int ppem = HintingPpem(pixelsPerEm);
        HintingData data = HintingData;
        if (data.GaspVersion >= 0 && data.GaspFor(ppem) is GaspFlags flags && (flags & (GaspFlags.GridFit | GaspFlags.SymmetricGridFit)) == 0)
        {
            return null;
        }

        lock (_hintingLock)
        {
            _hinted ??= [];
            if (_hinted.TryGetValue((target, ppem, glyph), out HintedGlyph? cached))
            {
                return cached;
            }

            if (_hinted.Count >= MaxHintedGlyphs)
            {
                _hinted.Clear();
            }

            _hinters ??= new GlyphHinter?[3];
            GlyphHinter hinter = _hinters[(int)target] ??= new GlyphHinter(this, _glyf!, data, target switch
            {
                TrueTypeTarget.Gdi => InterpreterTarget.Gdi,
                TrueTypeTarget.FreeType => InterpreterTarget.FreeType,
                _ => InterpreterTarget.GdiPlus,
            });

            float em = data.IntegerPpem ? ppem : MathF.Abs(pixelsPerEm);
            long scale = (long)Math.Round(em * 64 * 65536 / UnitsPerEm);
            HintedGlyph? hinted;
            try
            {
                hinted = hinter.Hint(glyph, ppem, scale);
            }
            catch (CorruptDataException)
            {
                hinted = null;
            }

            _hinted[(target, ppem, glyph)] = hinted;
            return hinted;
        }
    }
}
