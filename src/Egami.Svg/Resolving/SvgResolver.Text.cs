using Egami.Svg.Styling;

namespace Egami.Svg.Resolving;

internal sealed partial class SvgResolver
{
    /// <summary>Text, laid out into glyph runs (stage C, task 10); until then text elements are reported and skipped.</summary>
    private SvgNode? Text(SvgElement element, SvgStyle style, in SvgContext context)
    {
        _report.Note("Svg.Unsupported", "Text is not drawn yet in this preview; the text element is skipped.", element);
        return null;
    }
}
