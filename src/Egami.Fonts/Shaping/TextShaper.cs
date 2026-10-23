using Egami.Fonts.Unicode;

namespace Egami.Fonts.Shaping;

/// <summary>What one run of text is shaped as: its script, language, direction, features and cluster level.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="run"/>
/// </example>
public readonly record struct RunProperties
{
    /// <summary>Gets the script, or null to take it from the first character that has one.</summary>
    public Script? Script { get; init; }

    /// <summary>Gets the BCP 47 language tag, such as <c>ur</c> or <c>sr-Latn</c>, or null for the font's default forms.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the direction; <see cref="TextDirection.Auto"/> takes the script's (right to left for Arabic or Hebrew).</summary>
    public TextDirection Direction { get; init; }

    /// <summary>Gets the feature settings, in order; later settings of a tag override earlier ones.</summary>
    public IReadOnlyList<FontFeature>? Features { get; init; }

    /// <summary>Gets how characters group into clusters.</summary>
    public ClusterLevel ClusterLevel { get; init; }

    /// <summary>Gets whether glyphs say where a tatweel can lengthen a word (<see cref="GlyphFlags.SafeToInsertTatweel"/>), as
    /// HarfBuzz's <c>HB_BUFFER_FLAG_PRODUCE_SAFE_TO_INSERT_TATWEEL</c>.</summary>
    public bool ProduceSafeToInsertTatweel { get; init; }
}

/// <summary>
/// OpenType text shaping in managed code: a run of text in one font becomes positioned glyphs with GSUB substitutions, GPOS
/// positioning and the script rules HarfBuzz applies.
/// </summary>
/// <remarks>Stateless and thread-safe; each call needs its own <see cref="GlyphBuffer"/>.</remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="run"/>
/// </example>
public static class TextShaper
{
    [ThreadStatic]
    private static LayoutBuffer? t_buffer;

    /// <summary>
    /// Shapes one run, as HarfBuzz's hb_shape: no bidi, script or font splitting; the run is one script in one direction. Clusters
    /// are UTF-16 indexes into <paramref name="text"/>; the characters around the run are context for joining scripts.
    /// </summary>
    /// <param name="text">The text the run is part of.</param>
    /// <param name="start">The index of the run's first UTF-16 unit.</param>
    /// <param name="length">The number of UTF-16 units in the run.</param>
    /// <param name="font">The font and size; positions are in its units.</param>
    /// <param name="properties">The run's script, language, direction, features and cluster level.</param>
    /// <param name="buffer">Receives the glyphs in visual order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="buffer"/> is <see langword="null"/>, or <paramref name="font"/> has no typeface.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The run is not inside <paramref name="text"/>.</exception>
    public static void ShapeRun(ReadOnlySpan<char> text, int start, int length, Font font, RunProperties properties, GlyphBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(font.Typeface, nameof(font));
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(length, text.Length - start);

        LayoutBuffer layout = t_buffer ??= new LayoutBuffer();
        Typeface face = font.EffectiveTypeface;
        Shape(text, start, length, face, properties, font.Kerning, layout);
        buffer.Load(layout, font.Size / face.UnitsPerEm);
    }

    /// <summary>
    /// Shapes text: splits it into lines at hard breaks, resolves each line's bidi levels, splits it into runs of one level, script,
    /// span and font (falling back per grapheme cluster as <see cref="ShapingOptions.Fallback"/> says), shapes each run as
    /// <see cref="ShapeRun"/> does and orders the runs for display. Control characters other than the breaks (a tab, for one) give
    /// no glyph. Nothing wraps.
    /// </summary>
    /// <param name="text">The text.</param>
    /// <param name="font">The font and size.</param>
    /// <param name="options">The direction, language, script, features, fallback, cluster level and spans; null for
    /// <see cref="ShapingOptions.Default"/>.</param>
    /// <returns>The shaped lines; an empty text gives one empty line.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null, or <paramref name="font"/> has no typeface.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A span is not inside the text, or two spans overlap.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Shaping.cs" region="shape"/>
    /// </example>
    public static ShapedText Shape(string text, Font font, ShapingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(font.Typeface, nameof(font));
        return TextItemizer.Shape(text, font, options ?? ShapingOptions.Default);
    }

    /// <summary>Shapes a run into the engine's buffer, in font units.</summary>
    internal static void Shape(ReadOnlySpan<char> text, int start, int length, Typeface face, RunProperties properties, bool kerning, LayoutBuffer layout)
    {
        layout.Clear();
        int end = start + length;
        for (int i = start; i < end; i++)
        {
            int cluster = i;
            int cp = TextSegmenter.CodePointAt(text, i, out int size);
            if (i + size > end)
            {
                cp = text[i];
                size = 1;
            }

            layout.Add((uint)cp, cluster);
            i += size - 1;
        }

        RunRequest request = new()
        {
            Script = properties.Script,
            Direction = properties.Direction switch
            {
                TextDirection.LeftToRight => LayoutDirection.LeftToRight,
                TextDirection.RightToLeft => LayoutDirection.RightToLeft,
                TextDirection.TopToBottom => LayoutDirection.TopToBottom,
                _ => LayoutDirection.Invalid,
            },
            LanguageTags = LanguageTags.FromBcp47(properties.Language),
            ClusterLevel = (LayoutClusterLevel)properties.ClusterLevel,
            BeginningOfText = start == 0,
            EndOfText = end == text.Length,
            ProduceSafeToInsertTatweel = properties.ProduceSafeToInsertTatweel,
        };

        List<FeatureSetting> features = [];
        if (!kerning)
        {
            features.Add(new FeatureSetting(ShapeMapBuilder.Tag("kern"), 0, 0, int.MaxValue));
        }

        if (properties.Features is { } list)
        {
            foreach (FontFeature feature in list)
            {
                features.Add(feature.ToSetting());
            }
        }

        request.Features = [.. features];
        request.PreContext = Context(text, start, -1);
        request.PostContext = Context(text, end, 1);
        RunShaper.ShapeLoaded(face, request, layout);
    }

    /// <summary>Up to five code points before (<paramref name="step"/> -1) or after the run, nearest first.</summary>
    private static int[] Context(ReadOnlySpan<char> text, int at, int step)
    {
        List<int> context = [];
        int i = at;
        while (context.Count < 5)
        {
            if (step < 0)
            {
                if (i <= 0)
                {
                    break;
                }

                context.Add(TextSegmenter.CodePointBefore(text, i, out int size));
                i -= size;
            }
            else
            {
                if (i >= text.Length)
                {
                    break;
                }

                context.Add(TextSegmenter.CodePointAt(text, i, out int size));
                i += size;
            }
        }

        return [.. context];
    }
}
