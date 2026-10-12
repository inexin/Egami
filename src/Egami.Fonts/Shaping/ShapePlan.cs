// Translated from HarfBuzz's hb-ot-shape.cc (MIT); see THIRD-PARTY-NOTICES.md.
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>A feature setting as HarfBuzz's hb_feature_t: a tag, a value and the cluster range it applies to.</summary>
internal readonly record struct FeatureSetting(uint Tag, uint Value, int Start, int End)
{
    public bool IsGlobal => Start == 0 && End == int.MaxValue;
}

/// <summary>What a shape plan depends on.</summary>
internal sealed record ShapePlanKey(Script Script, LayoutDirection Direction, string LanguageTags, string Features, int GsubVariations, int GposVariations);

/// <summary>Builds a shape plan: picks the script shaper and collects its features, as HarfBuzz's hb_ot_shape_planner_t.</summary>
internal sealed class ShapePlanner
{
    public ShapePlanner(ShapeFont font, Script script, LayoutDirection direction, ReadOnlySpan<uint> scriptTags, ReadOnlySpan<uint> languageTags)
    {
        Font = font;
        Script = script;
        Direction = direction;
        Map = new ShapeMapBuilder(font.Layout.Gsub, font.Layout.Gpos, scriptTags, languageTags);
        Shaper = ShaperSelector.Select(script, direction, Map.ChosenScript[0]);
    }

    public ShapeFont Font { get; }

    public Script Script { get; }

    public LayoutDirection Direction { get; }

    public ShapeMapBuilder Map { get; }

    public ComplexShaper Shaper { get; }
}

/// <summary>A compiled shape plan: the feature map and the decisions about substitution and positioning, for one script, language,
/// direction and feature set.</summary>
internal sealed class ShapePlan
{
    public required ShapeMap Map;
    public required ComplexShaper Shaper;
    public required Script Script;
    public required LayoutDirection Direction;
    public object? ShaperData;

    public uint FracMask;
    public uint NumrMask;
    public uint DnomMask;
    public uint RtlmMask;
    public uint KernMask;
    public bool HasFrac;
    public bool HasVert;
    public bool HasGposMark;
    public bool RequestedKerning;
    public bool FallbackGlyphClasses;
    public bool ApplyGpos;
    public bool ApplyKern;
    public bool ApplyFallbackKern;
    public bool ZeroMarks;
    public bool AdjustMarkPositioningWhenZeroing;
    public bool FallbackMarkPositioning;
    public FeatureSetting[] UserFeatures = [];

    private static readonly (string Tag, FeatureFlags Flags)[] s_commonFeatures =
    [
        ("abvm", FeatureFlags.Global),
        ("blwm", FeatureFlags.Global),
        ("ccmp", FeatureFlags.Global),
        ("locl", FeatureFlags.Global),
        ("mark", FeatureFlags.GlobalManualJoiners),
        ("mkmk", FeatureFlags.GlobalManualJoiners),
        ("rlig", FeatureFlags.Global),
    ];

    private static readonly (string Tag, FeatureFlags Flags)[] s_horizontalFeatures =
    [
        ("calt", FeatureFlags.Global),
        ("clig", FeatureFlags.Global),
        ("curs", FeatureFlags.Global),
        ("dist", FeatureFlags.Global),
        ("kern", FeatureFlags.GlobalHasFallback),
        ("liga", FeatureFlags.Global),
        ("rclt", FeatureFlags.Global),
    ];

    /// <summary>Builds the plan for a font, as HarfBuzz's hb_ot_shape_plan_t::init0.</summary>
    public static ShapePlan Create(ShapeFont font, Script script, LayoutDirection direction, ReadOnlySpan<uint> scriptTags, ReadOnlySpan<uint> languageTags,
        FeatureSetting[] userFeatures, int[] variationsIndex)
    {
        ShapePlanner planner = new(font, script, direction, scriptTags, languageTags);
        CollectFeatures(planner, userFeatures);
        ShapeMap map = planner.Map.Compile(variationsIndex);

        ShapePlan plan = new()
        {
            Map = map,
            Shaper = planner.Shaper,
            Script = script,
            Direction = direction,
            UserFeatures = userFeatures,
        };

        plan.FracMask = map.Get1Mask(ShapeMapBuilder.Tag("frac"));
        plan.NumrMask = map.Get1Mask(ShapeMapBuilder.Tag("numr"));
        plan.DnomMask = map.Get1Mask(ShapeMapBuilder.Tag("dnom"));
        plan.HasFrac = plan.FracMask != 0 || (plan.NumrMask != 0 && plan.DnomMask != 0);
        plan.RtlmMask = map.Get1Mask(ShapeMapBuilder.Tag("rtlm"));
        plan.HasVert = map.Get1Mask(ShapeMapBuilder.Tag("vert")) != 0;

        uint kernTag = ShapeMapBuilder.Tag(LayoutBuffer.IsHorizontal(direction) ? "kern" : "vkrn");
        plan.KernMask = map.GetMask(kernTag);
        plan.RequestedKerning = plan.KernMask != 0;
        bool hasGposKern = map.GetFeatureIndex(1, kernTag) >= 0;
        bool disableGpos = plan.Shaper.GposTag != 0 && plan.Shaper.GposTag != map.ChosenScript[1];

        OpenTypeLayoutTables layout = font.Layout;
        plan.FallbackGlyphClasses = !layout.Gdef.HasGlyphClasses;
        bool hasGpos = !disableGpos && layout.Gpos.HasData;
        if (hasGpos)
        {
            plan.ApplyGpos = true;
        }

        bool scriptFallbackPosition = plan.Shaper.FallbackPosition;
        if (!hasGposKern || !plan.ApplyGpos)
        {
            if (layout.Kern.HasData)
            {
                plan.ApplyKern = scriptFallbackPosition;
            }
        }

        plan.ApplyFallbackKern = scriptFallbackPosition && !(plan.ApplyGpos || plan.ApplyKern);
        bool scriptZeroMarks = plan.Shaper.ZeroWidthMarks != ZeroWidthMarks.None;
        plan.ZeroMarks = scriptZeroMarks && (!plan.ApplyKern || !layout.Kern.HasMachineKerning);
        plan.HasGposMark = map.Get1Mask(ShapeMapBuilder.Tag("mark")) != 0;
        plan.AdjustMarkPositioningWhenZeroing = !plan.ApplyGpos && (!plan.ApplyKern || !layout.Kern.HasCrossKerning);
        plan.FallbackMarkPositioning = plan.AdjustMarkPositioningWhenZeroing && scriptFallbackPosition;

        plan.ShaperData = plan.Shaper.CreateData(plan);
        return plan;
    }

    private static void CollectFeatures(ShapePlanner planner, FeatureSetting[] userFeatures)
    {
        ShapeMapBuilder map = planner.Map;
        map.IsSimple = true;
        map.EnableFeature("rvrn");
        map.AddGsubPause(null);

        switch (planner.Direction)
        {
            case LayoutDirection.LeftToRight:
                map.EnableFeature("ltra");
                map.EnableFeature("ltrm");
                break;
            case LayoutDirection.RightToLeft:
                map.EnableFeature("rtla");
                map.AddFeature("rtlm");
                break;
        }

        // Automatic fractions.
        map.AddFeature("frac");
        map.AddFeature("numr");
        map.AddFeature("dnom");

        // Random!
        map.EnableFeature("rand", FeatureFlags.Random, ShapeMap.MaxValue);

        // HarfBuzz's own test features, considered required and discretionary.
        map.EnableFeature("Harf");
        map.EnableFeature("HARF");

        if (planner.Shaper is not DefaultShaper)
        {
            map.IsSimple = false;
            planner.Shaper.CollectFeatures(planner);
        }

        map.EnableFeature("Buzz");
        map.EnableFeature("BUZZ");

        foreach ((string tag, FeatureFlags flags) in s_commonFeatures)
        {
            map.AddFeature(tag, flags);
        }

        if (LayoutBuffer.IsHorizontal(planner.Direction))
        {
            foreach ((string tag, FeatureFlags flags) in s_horizontalFeatures)
            {
                map.AddFeature(tag, flags);
            }
        }
        else
        {
            map.EnableFeature("vert", FeatureFlags.GlobalSearch);
        }

        if (userFeatures.Length > 0)
        {
            map.IsSimple = false;
        }

        foreach (FeatureSetting feature in userFeatures)
        {
            map.AddFeature(feature.Tag, feature.IsGlobal ? FeatureFlags.Global : FeatureFlags.None, feature.Value);
        }

        planner.Shaper.OverrideFeatures(planner);
    }
}
