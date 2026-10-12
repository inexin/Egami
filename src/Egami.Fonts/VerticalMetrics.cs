namespace Egami.Fonts;

/// <summary>One set of a face's ascent, descent and line gap as stored in a table, in font units with y up.</summary>
/// <param name="Ascent">The distance above the baseline, usually positive.</param>
/// <param name="Descent">The distance below the baseline, usually negative.</param>
/// <param name="LineGap">The extra space between lines.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Text.cs" region="glyphs"/>
/// </example>
public readonly record struct VerticalMetrics(int Ascent, int Descent, int LineGap);
