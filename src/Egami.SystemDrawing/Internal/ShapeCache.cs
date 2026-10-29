using Egami.Fonts.Shaping;

using EgamiFonts = Egami.Fonts;

namespace Egami.SystemDrawing.Internal;

/// <summary>
/// The shaped runs of recent short strings, by font and text: UI code measures and draws the same labels again and again, and
/// shaping is most of what a measure costs. A run is immutable, so a cached one is the run shaping would give again. The cache
/// empties when full rather than tracking use.
/// </summary>
internal static class ShapeCache
{
    /// <summary>The longest string kept; longer ones are shaped each time.</summary>
    private const int MaxLength = 256;

    private const int Capacity = 1024;

    private static readonly Dictionary<(EgamiFonts.Font Font, string Text), EgamiFonts.GlyphRun> s_runs = [];
    private static readonly Lock s_gate = new();

    /// <summary>Shapes text with a font, or returns the run shaped for the same font, text and options before.</summary>
    /// <param name="font">The font.</param>
    /// <param name="text">The text.</param>
    /// <param name="options">The options: always the same instance for a given caller, since they are not part of the key.</param>
    /// <returns>The run.</returns>
    public static EgamiFonts.GlyphRun Shape(EgamiFonts.Font font, string text, ShapingOptions options)
    {
        if (text.Length > MaxLength)
        {
            return font.Shape(text, options);
        }

        lock (s_gate)
        {
            if (s_runs.TryGetValue((font, text), out EgamiFonts.GlyphRun? cached))
            {
                return cached;
            }
        }

        EgamiFonts.GlyphRun run = font.Shape(text, options);
        lock (s_gate)
        {
            if (s_runs.Count >= Capacity)
            {
                s_runs.Clear();
            }

            s_runs[(font, text)] = run;
        }

        return run;
    }
}
