using Egami.Fonts.Shaping;

namespace Egami.Fonts;

/// <summary>
/// The constants of a face's OpenType <c>MATH</c> table, which place scripts, limits, fractions, stacks, bars and radicals.
/// Distances are in font units; the two script scale-downs and the radical degree's raise are percentages. A face without
/// constants reads as all zeros.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="math"/>
/// </example>
public sealed record class MathConstants
{
    /// <summary>Gets how much smaller a first-level script is, in percent of the base size (80 means 80 %).</summary>
    public int ScriptPercentScaleDown { get; init; }

    /// <summary>Gets how much smaller a second-level script is, in percent of the base size.</summary>
    public int ScriptScriptPercentScaleDown { get; init; }

    /// <summary>Gets the least height of a sub-formula that delimiters grow to cover.</summary>
    public float DelimitedSubFormulaMinHeight { get; init; }

    /// <summary>Gets the least height of a large operator, such as an integral or a sum, in display style.</summary>
    public float DisplayOperatorMinHeight { get; init; }

    /// <summary>Gets the white space between successive lines of a matrix or stacked text.</summary>
    public float MathLeading { get; init; }

    /// <summary>Gets the height of the math axis, where fraction bars and the centres of operators sit, above the baseline.</summary>
    public float AxisHeight { get; init; }

    /// <summary>Gets the largest height of a base that takes an accent without moving it up: for taller bases the accent moves up by the difference.</summary>
    public float AccentBaseHeight { get; init; }

    /// <summary>Gets the height above which a base takes the flattened form of an accent (the flac feature).</summary>
    public float FlattenedAccentBaseHeight { get; init; }

    /// <summary>Gets the least drop of a subscript's baseline below the base's.</summary>
    public float SubscriptShiftDown { get; init; }

    /// <summary>Gets the highest a subscript's top may reach above the baseline.</summary>
    public float SubscriptTopMax { get; init; }

    /// <summary>Gets the least drop of a subscript's baseline below the bottom of an extended-shape base.</summary>
    public float SubscriptBaselineDropMin { get; init; }

    /// <summary>Gets the least rise of a superscript's baseline in uncramped style.</summary>
    public float SuperscriptShiftUp { get; init; }

    /// <summary>Gets the least rise of a superscript's baseline in cramped style.</summary>
    public float SuperscriptShiftUpCramped { get; init; }

    /// <summary>Gets the lowest a superscript's bottom may reach above the baseline.</summary>
    public float SuperscriptBottomMin { get; init; }

    /// <summary>Gets the most a superscript's baseline may sit below the top of an extended-shape base.</summary>
    public float SuperscriptBaselineDropMax { get; init; }

    /// <summary>Gets the least gap between a subscript's top and a superscript's bottom.</summary>
    public float SubSuperscriptGapMin { get; init; }

    /// <summary>Gets the highest a superscript's bottom is raised to when the gap to its subscript is too small.</summary>
    public float SuperscriptBottomMaxWithSubscript { get; init; }

    /// <summary>Gets the space after a subscript or superscript.</summary>
    public float SpaceAfterScript { get; init; }

    /// <summary>Gets the least gap between an upper limit's bottom and the top of its operator.</summary>
    public float UpperLimitGapMin { get; init; }

    /// <summary>Gets the least rise of an upper limit's baseline above the top of its operator.</summary>
    public float UpperLimitBaselineRiseMin { get; init; }

    /// <summary>Gets the least gap between a lower limit's top and the bottom of its operator.</summary>
    public float LowerLimitGapMin { get; init; }

    /// <summary>Gets the least drop of a lower limit's baseline below the bottom of its operator.</summary>
    public float LowerLimitBaselineDropMin { get; init; }

    /// <summary>Gets the least rise of a stack's top baseline, in text style.</summary>
    public float StackTopShiftUp { get; init; }

    /// <summary>Gets the least rise of a stack's top baseline, in display style.</summary>
    public float StackTopDisplayStyleShiftUp { get; init; }

    /// <summary>Gets the least drop of a stack's bottom baseline, in text style.</summary>
    public float StackBottomShiftDown { get; init; }

    /// <summary>Gets the least drop of a stack's bottom baseline, in display style.</summary>
    public float StackBottomDisplayStyleShiftDown { get; init; }

    /// <summary>Gets the least gap between a stack's top and bottom, in text style.</summary>
    public float StackGapMin { get; init; }

    /// <summary>Gets the least gap between a stack's top and bottom, in display style.</summary>
    public float StackDisplayStyleGapMin { get; init; }

    /// <summary>Gets the rise of the baseline of what sits above a stretched glyph.</summary>
    public float StretchStackTopShiftUp { get; init; }

    /// <summary>Gets the drop of the baseline of what sits below a stretched glyph.</summary>
    public float StretchStackBottomShiftDown { get; init; }

    /// <summary>Gets the least gap between a stretched glyph and what sits above it.</summary>
    public float StretchStackGapAboveMin { get; init; }

    /// <summary>Gets the least gap between a stretched glyph and what sits below it.</summary>
    public float StretchStackGapBelowMin { get; init; }

    /// <summary>Gets the least rise of a numerator's baseline, in text style.</summary>
    public float FractionNumeratorShiftUp { get; init; }

    /// <summary>Gets the least rise of a numerator's baseline, in display style.</summary>
    public float FractionNumeratorDisplayStyleShiftUp { get; init; }

    /// <summary>Gets the least drop of a denominator's baseline, in text style.</summary>
    public float FractionDenominatorShiftDown { get; init; }

    /// <summary>Gets the least drop of a denominator's baseline, in display style.</summary>
    public float FractionDenominatorDisplayStyleShiftDown { get; init; }

    /// <summary>Gets the least gap between a numerator's bottom and the fraction bar, in text style.</summary>
    public float FractionNumeratorGapMin { get; init; }

    /// <summary>Gets the least gap between a numerator's bottom and the fraction bar, in display style.</summary>
    public float FractionNumDisplayStyleGapMin { get; init; }

    /// <summary>Gets the thickness of the fraction bar.</summary>
    public float FractionRuleThickness { get; init; }

    /// <summary>Gets the least gap between the fraction bar and a denominator's top, in text style.</summary>
    public float FractionDenominatorGapMin { get; init; }

    /// <summary>Gets the least gap between the fraction bar and a denominator's top, in display style.</summary>
    public float FractionDenomDisplayStyleGapMin { get; init; }

    /// <summary>Gets the horizontal gap between the numerator and denominator of a skewed fraction.</summary>
    public float SkewedFractionHorizontalGap { get; init; }

    /// <summary>Gets the vertical gap between the numerator and denominator of a skewed fraction.</summary>
    public float SkewedFractionVerticalGap { get; init; }

    /// <summary>Gets the gap between an overbar and the top of its base.</summary>
    public float OverbarVerticalGap { get; init; }

    /// <summary>Gets the thickness of an overbar.</summary>
    public float OverbarRuleThickness { get; init; }

    /// <summary>Gets the extra white space above an overbar.</summary>
    public float OverbarExtraAscender { get; init; }

    /// <summary>Gets the gap between an underbar and the bottom of its base.</summary>
    public float UnderbarVerticalGap { get; init; }

    /// <summary>Gets the thickness of an underbar.</summary>
    public float UnderbarRuleThickness { get; init; }

    /// <summary>Gets the extra white space below an underbar.</summary>
    public float UnderbarExtraDescender { get; init; }

    /// <summary>Gets the gap between a radical's rule and its base's top, in text style.</summary>
    public float RadicalVerticalGap { get; init; }

    /// <summary>Gets the gap between a radical's rule and its base's top, in display style.</summary>
    public float RadicalDisplayStyleVerticalGap { get; init; }

    /// <summary>Gets the thickness of a radical's rule.</summary>
    public float RadicalRuleThickness { get; init; }

    /// <summary>Gets the extra white space above a radical's rule.</summary>
    public float RadicalExtraAscender { get; init; }

    /// <summary>Gets the space before a radical's degree.</summary>
    public float RadicalKernBeforeDegree { get; init; }

    /// <summary>Gets the space after a radical's degree (usually negative, so the degree tucks into the sign).</summary>
    public float RadicalKernAfterDegree { get; init; }

    /// <summary>Gets how far a radical's degree is raised, in percent of the radical sign's height.</summary>
    public int RadicalDegreeBottomRaisePercent { get; init; }

    /// <summary>Reads the constants at <paramref name="at"/>.</summary>
    internal static MathConstants Read(ReadOnlySpan<byte> d, int at)
    {
        static float Value(ReadOnlySpan<byte> d, int at, int index) => OtData.S16(d, at + 8 + (index * 4));
        return new MathConstants
        {
            ScriptPercentScaleDown = OtData.S16(d, at),
            ScriptScriptPercentScaleDown = OtData.S16(d, at + 2),
            DelimitedSubFormulaMinHeight = OtData.U16(d, at + 4),
            DisplayOperatorMinHeight = OtData.U16(d, at + 6),
            MathLeading = Value(d, at, 0),
            AxisHeight = Value(d, at, 1),
            AccentBaseHeight = Value(d, at, 2),
            FlattenedAccentBaseHeight = Value(d, at, 3),
            SubscriptShiftDown = Value(d, at, 4),
            SubscriptTopMax = Value(d, at, 5),
            SubscriptBaselineDropMin = Value(d, at, 6),
            SuperscriptShiftUp = Value(d, at, 7),
            SuperscriptShiftUpCramped = Value(d, at, 8),
            SuperscriptBottomMin = Value(d, at, 9),
            SuperscriptBaselineDropMax = Value(d, at, 10),
            SubSuperscriptGapMin = Value(d, at, 11),
            SuperscriptBottomMaxWithSubscript = Value(d, at, 12),
            SpaceAfterScript = Value(d, at, 13),
            UpperLimitGapMin = Value(d, at, 14),
            UpperLimitBaselineRiseMin = Value(d, at, 15),
            LowerLimitGapMin = Value(d, at, 16),
            LowerLimitBaselineDropMin = Value(d, at, 17),
            StackTopShiftUp = Value(d, at, 18),
            StackTopDisplayStyleShiftUp = Value(d, at, 19),
            StackBottomShiftDown = Value(d, at, 20),
            StackBottomDisplayStyleShiftDown = Value(d, at, 21),
            StackGapMin = Value(d, at, 22),
            StackDisplayStyleGapMin = Value(d, at, 23),
            StretchStackTopShiftUp = Value(d, at, 24),
            StretchStackBottomShiftDown = Value(d, at, 25),
            StretchStackGapAboveMin = Value(d, at, 26),
            StretchStackGapBelowMin = Value(d, at, 27),
            FractionNumeratorShiftUp = Value(d, at, 28),
            FractionNumeratorDisplayStyleShiftUp = Value(d, at, 29),
            FractionDenominatorShiftDown = Value(d, at, 30),
            FractionDenominatorDisplayStyleShiftDown = Value(d, at, 31),
            FractionNumeratorGapMin = Value(d, at, 32),
            FractionNumDisplayStyleGapMin = Value(d, at, 33),
            FractionRuleThickness = Value(d, at, 34),
            FractionDenominatorGapMin = Value(d, at, 35),
            FractionDenomDisplayStyleGapMin = Value(d, at, 36),
            SkewedFractionHorizontalGap = Value(d, at, 37),
            SkewedFractionVerticalGap = Value(d, at, 38),
            OverbarVerticalGap = Value(d, at, 39),
            OverbarRuleThickness = Value(d, at, 40),
            OverbarExtraAscender = Value(d, at, 41),
            UnderbarVerticalGap = Value(d, at, 42),
            UnderbarRuleThickness = Value(d, at, 43),
            UnderbarExtraDescender = Value(d, at, 44),
            RadicalVerticalGap = Value(d, at, 45),
            RadicalDisplayStyleVerticalGap = Value(d, at, 46),
            RadicalRuleThickness = Value(d, at, 47),
            RadicalExtraAscender = Value(d, at, 48),
            RadicalKernBeforeDegree = Value(d, at, 49),
            RadicalKernAfterDegree = Value(d, at, 50),
            RadicalDegreeBottomRaisePercent = OtData.S16(d, at + 8 + (51 * 4)),
        };
    }
}
