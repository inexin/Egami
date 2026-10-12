using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>What one run is shaped with, as HarfBuzz's segment properties, features and buffer flags.</summary>
internal sealed class RunRequest
{
    /// <summary>The script, or null to take the first character's that is not Common or Inherited.</summary>
    public Script? Script;

    /// <summary>The direction, or Invalid to take the script's.</summary>
    public LayoutDirection Direction = LayoutDirection.Invalid;

    /// <summary>The OpenType language system tags to try, in order; empty for the font's default.</summary>
    public uint[] LanguageTags = [];

    public FeatureSetting[] Features = [];
    public LayoutClusterLevel ClusterLevel;
    public bool BeginningOfText;
    public bool EndOfText;
    public bool PreserveDefaultIgnorables;
    public bool RemoveDefaultIgnorables;
    public bool DoNotInsertDottedCircle;
    public bool ProduceUnsafeToConcat;
    public uint Invisible;
    public uint NotFound;
    public uint NotFoundVariationSelector = uint.MaxValue;

    /// <summary>The code points before and after the run, nearest first.</summary>
    public int[] PreContext = [];
    public int[] PostContext = [];
}

/// <summary>Shapes one run of code points in one font: the engine's entry point below the public API.</summary>
internal static class RunShaper
{
    /// <summary>Shapes <paramref name="codePoints"/>, each with its cluster, into <paramref name="buffer"/>.</summary>
    public static void Shape(Typeface face, ReadOnlySpan<int> codePoints, ReadOnlySpan<int> clusters, RunRequest request, LayoutBuffer buffer)
    {
        buffer.Clear();
        for (int i = 0; i < codePoints.Length; i++)
        {
            buffer.Add((uint)codePoints[i], clusters[i]);
        }

        ShapeLoaded(face, request, buffer);
    }

    /// <summary>Shapes the code points already in <paramref name="buffer"/>.</summary>
    public static void ShapeLoaded(Typeface face, RunRequest request, LayoutBuffer buffer)
    {
        Script script = request.Script ?? GuessScript(buffer);
        LayoutDirection direction = request.Direction;
        if (direction == LayoutDirection.Invalid)
        {
            direction = OtShaper.HorizontalDirection(script);
            if (direction == LayoutDirection.Invalid)
            {
                direction = LayoutDirection.LeftToRight;
            }
        }

        buffer.Direction = direction;
        buffer.ClusterLevel = request.ClusterLevel;
        buffer.BeginningOfText = request.BeginningOfText;
        buffer.EndOfText = request.EndOfText;
        buffer.PreserveDefaultIgnorables = request.PreserveDefaultIgnorables;
        buffer.RemoveDefaultIgnorables = request.RemoveDefaultIgnorables;
        buffer.DoNotInsertDottedCircle = request.DoNotInsertDottedCircle;
        buffer.ProduceUnsafeToConcat = request.ProduceUnsafeToConcat;
        buffer.Invisible = request.Invisible;
        buffer.NotFound = request.NotFound;
        buffer.NotFoundVariationSelector = request.NotFoundVariationSelector;
        buffer.PreContext = request.PreContext;
        buffer.PostContext = request.PostContext;

        ShapeFont font = new(face);
        int[] coords = font.NormalizedCoordinates;
        int[] variations = [font.Layout.Gsub.FindFeatureVariations(coords), font.Layout.Gpos.FindFeatureVariations(coords)];
        string languages = string.Join(",", request.LanguageTags);
        string features = string.Join(",", request.Features.Select(f => $"{f.Tag}:{f.Value}:{f.Start}:{f.End}"));
        ShapePlanKey key = new(script, direction, languages, features, variations[0], variations[1]);
        uint[] scriptTags = OtShaper.ScriptTags(script);
        ShapePlan plan = font.Layout.GetPlan(key, _ => ShapePlan.Create(font, script, direction, scriptTags, request.LanguageTags, request.Features, variations));
        OtShaper.Shape(plan, font, buffer);
    }

    /// <summary>HarfBuzz's guess: the script of the first character that is not Common, Inherited or Unknown.</summary>
    public static Script GuessScript(LayoutBuffer buffer)
    {
        for (int i = 0; i < buffer.Len; i++)
        {
            uint cp = buffer.Info[i].Codepoint;
            if (cp > 0x10FFFF)
            {
                continue;
            }

            Script s = UnicodeData.GetScript((int)cp);
            if (!s.IsShared)
            {
                return s;
            }
        }

        return Script.Unknown;
    }
}
