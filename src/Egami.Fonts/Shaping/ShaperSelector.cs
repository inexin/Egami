// Translated from HarfBuzz's hb-ot-shaper.hh (MIT); see THIRD-PARTY-NOTICES.md.
using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>Picks the script shaper for a run, as HarfBuzz's hb_ot_shaper_categorize.</summary>
internal static partial class ShaperSelector
{
    private static readonly uint s_dflt = ShapeMapBuilder.Tag("DFLT");
    private static readonly uint s_latn = ShapeMapBuilder.Tag("latn");
    private static readonly uint s_mymr = ShapeMapBuilder.Tag("mymr");

    public static ComplexShaper Select(Script script, LayoutDirection direction, uint chosenGsubScript)
    {
        string code = script.Code;
        bool designedForDefault = chosenGsubScript == s_dflt || chosenGsubScript == s_latn;
        switch (code)
        {
            case "Arab":
            case "Syrc":
                // Arabic shapes with the Arabic shaper even without an Arabic script in the font, for its fallback shaping; only
                // horizontally.
                return (chosenGsubScript != s_dflt || code == "Arab") && LayoutBuffer.IsHorizontal(direction)
                    ? ArabicShaper.Instance
                    : DefaultShaper.Instance;
            case "Thai":
            case "Laoo":
                return ThaiShaper.Instance;
            case "Hang":
                return HangulShaper.Instance;
            case "Hebr":
                return HebrewShaper.Instance;
            case "Beng":
            case "Deva":
            case "Gujr":
            case "Guru":
            case "Knda":
            case "Mlym":
            case "Orya":
            case "Taml":
            case "Telu":
                if (designedForDefault)
                {
                    return DefaultShaper.Instance;
                }

                return (chosenGsubScript & 0xFF) == '3' ? UseShaper.Instance : IndicShaper.Instance;
            case "Khmr":
                return KhmerShaper.Instance;
            case "Mymr":
                return designedForDefault || chosenGsubScript == s_mymr ? DefaultShaper.Instance : MyanmarShaper.Instance;
            case "Qaag":
                return MyanmarShaper.Zawgyi;
            default:
                if (s_useScripts.Contains(code))
                {
                    return designedForDefault ? DefaultShaper.Instance : UseShaper.Instance;
                }

                return DefaultShaper.Instance;
        }
    }
}
