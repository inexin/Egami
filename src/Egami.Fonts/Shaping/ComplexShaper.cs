// Shaper hooks as HarfBuzz's hb_ot_shaper_t (MIT); see THIRD-PARTY-NOTICES.md.
namespace Egami.Fonts.Shaping;

/// <summary>When a shaper zeroes the advances of marks.</summary>
internal enum ZeroWidthMarks
{
    None,
    ByGdefEarly,
    ByGdefLate,
}

/// <summary>How a shaper wants text normalized before GSUB.</summary>
internal enum NormalizationMode
{
    None,
    Decomposed,
    ComposedDiacritics,
    ComposedDiacriticsNoShortCircuit,
    Auto,
}

/// <summary>A script shaper's hooks into the shaping pipeline; the default shaper uses none of them.</summary>
internal abstract class ComplexShaper
{
    public virtual void CollectFeatures(ShapePlanner planner)
    {
    }

    public virtual void OverrideFeatures(ShapePlanner planner)
    {
    }

    public virtual object? CreateData(ShapePlan plan) => null;

    public virtual void PreprocessText(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
    }

    public virtual void PostprocessGlyphs(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
    }

    public virtual NormalizationMode NormalizationPreference => NormalizationMode.Auto;

    /// <summary>Overrides canonical decomposition; null uses Unicode's.</summary>
    public virtual bool HasDecompose => false;

    public virtual bool Decompose(NormalizeContext c, int ab, out int a, out int b)
    {
        a = 0;
        b = 0;
        return false;
    }

    /// <summary>Overrides canonical composition; null uses Unicode's.</summary>
    public virtual bool HasCompose => false;

    public virtual bool Compose(NormalizeContext c, int a, int b, out int ab)
    {
        ab = 0;
        return false;
    }

    public virtual void SetupMasks(ShapePlan plan, LayoutBuffer buffer, ShapeFont font)
    {
    }

    /// <summary>The GPOS script tag the shaper requires, or 0 for any.</summary>
    public virtual uint GposTag => 0;

    public virtual bool HasReorderMarks => false;

    public virtual void ReorderMarks(ShapePlan plan, LayoutBuffer buffer, int start, int end)
    {
    }

    public virtual ZeroWidthMarks ZeroWidthMarks => ZeroWidthMarks.ByGdefLate;

    public virtual bool FallbackPosition => true;
}

/// <summary>The shaper for scripts without their own: common features only.</summary>
internal sealed class DefaultShaper : ComplexShaper
{
    public static readonly DefaultShaper Instance = new();
}
