namespace Egami.Fonts.Layout;

/// <summary>A text style over a stretch of a <see cref="RichText"/>, in UTF-16 units.</summary>
/// <param name="Start">The first UTF-16 index.</param>
/// <param name="Length">The number of UTF-16 units.</param>
/// <param name="Style">The style; its unset fields come from the paragraph's default style.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public readonly record struct StyleSpan(int Start, int Length, TextStyle Style)
{
    /// <summary>Gets the index just past the span.</summary>
    public int End => Start + Length;
}

/// <summary>
/// An inline box laid out with the text, such as an icon or an image, which the caller draws: it stands for an object replacement
/// character (U+FFFC) in the text, so bidi, line breaking and indexes treat it as one character.
/// </summary>
/// <param name="Width">The box's width in canvas units.</param>
/// <param name="Height">The box's height in canvas units.</param>
/// <param name="Alignment">How the box sits on its line.</param>
/// <param name="BaselineOffset">For <see cref="PlaceholderAlignment.Baseline"/>, the distance from the box's top to the point
/// that sits on the text's baseline.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public readonly record struct Placeholder(float Width, float Height, PlaceholderAlignment Alignment = PlaceholderAlignment.Baseline, float BaselineOffset = 0);

/// <summary>How a <see cref="Placeholder"/> sits on its line.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public enum PlaceholderAlignment
{
    /// <summary>The point <see cref="Placeholder.BaselineOffset"/> below the box's top sits on the baseline.</summary>
    Baseline = 0,

    /// <summary>The box's bottom sits on the baseline.</summary>
    AboveBaseline = 1,

    /// <summary>The box's top sits on the baseline.</summary>
    BelowBaseline = 2,

    /// <summary>The box's top is the line box's top.</summary>
    Top = 3,

    /// <summary>The box's bottom is the line box's bottom.</summary>
    Bottom = 4,

    /// <summary>The box's middle is the middle of the text's ascent and descent.</summary>
    Middle = 5,
}

/// <summary>
/// Text with styles: a string, the <see cref="StyleSpan"/>s over it (sorted, never overlapping) and the
/// <see cref="Placeholder"/>s for its object replacement characters, in order. Immutable; build one with
/// <see cref="RichTextBuilder"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public sealed class RichText
{
    /// <summary>Initializes a new instance of the <see cref="RichText"/> class.</summary>
    /// <param name="text">The text.</param>
    /// <param name="spans">Styles over parts of the text, in any order but not overlapping; text outside them takes the
    /// paragraph's default style.</param>
    /// <param name="placeholders">One placeholder for each U+FFFC in the text, in order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or a span's style is null.</exception>
    /// <exception cref="ArgumentException">A span is empty, outside the text or overlaps another, or the placeholders do not
    /// match the text's U+FFFC characters.</exception>
    public RichText(string text, IEnumerable<StyleSpan>? spans = null, IEnumerable<Placeholder>? placeholders = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        StyleSpan[] sorted = [.. (spans ?? []).OrderBy(s => s.Start)];
        int end = 0;
        foreach (StyleSpan span in sorted)
        {
            if (span.Style is null)
            {
                throw new ArgumentNullException(nameof(spans), "A span has no style.");
            }

            if (span.Start < 0 || span.Length <= 0 || span.Length > text.Length - span.Start)
            {
                throw new ArgumentException($"The span at {span.Start} with length {span.Length} is empty or not inside the text.", nameof(spans));
            }

            if (span.Start < end)
            {
                throw new ArgumentException($"The span at {span.Start} overlaps the span before it.", nameof(spans));
            }

            end = span.End;
        }

        Placeholder[] boxes = [.. placeholders ?? []];
        int objects = text.AsSpan().Count('\uFFFC');
        if (boxes.Length != objects)
        {
            throw new ArgumentException($"The text has {objects} object replacement characters but {boxes.Length} placeholders were given.", nameof(placeholders));
        }

        foreach (Placeholder box in boxes)
        {
            if (!float.IsFinite(box.Width) || !float.IsFinite(box.Height) || box.Width < 0 || box.Height < 0 || !float.IsFinite(box.BaselineOffset)
                || !Enum.IsDefined(box.Alignment))
            {
                throw new ArgumentException("A placeholder's size is negative or not finite, or its alignment is not defined.", nameof(placeholders));
            }
        }

        Text = text;
        Spans = sorted;
        Placeholders = boxes;
    }

    /// <summary>Gets the text.</summary>
    public string Text { get; }

    /// <summary>Gets the style spans, sorted by start.</summary>
    public IReadOnlyList<StyleSpan> Spans { get; }

    /// <summary>Gets the placeholders, one per U+FFFC in the text, in order.</summary>
    public IReadOnlyList<Placeholder> Placeholders { get; }

    /// <summary>The index of the span covering a UTF-16 index, or -1.</summary>
    internal int SpanAt(int index)
    {
        int lo = 0, hi = Spans.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            StyleSpan s = Spans[mid];
            if (index < s.Start)
            {
                hi = mid - 1;
            }
            else if (index >= s.End)
            {
                lo = mid + 1;
            }
            else
            {
                return mid;
            }
        }

        return -1;
    }
}

/// <summary>
/// Builds a <see cref="RichText"/>: <see cref="Push"/> a style, <see cref="Append(string)"/> text, <see cref="Pop"/> back to the
/// enclosing style. Styles nest: a pushed style's unset fields come from the one it is pushed on, and the result has one flat
/// span per stretch of equal style.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ParagraphLayout.cs" region="rich-text"/>
/// </example>
public sealed class RichTextBuilder
{
    private readonly System.Text.StringBuilder _text = new();
    private readonly List<StyleSpan> _spans = [];
    private readonly List<Placeholder> _placeholders = [];
    private readonly Stack<TextStyle> _styles = new();

    /// <summary>Gets the number of styles pushed and not popped.</summary>
    public int Depth => _styles.Count;

    /// <summary>Gets the length of the text so far, in UTF-16 units.</summary>
    public int Length => _text.Length;

    /// <summary>Starts a style; text appended until the matching <see cref="Pop"/> takes it.</summary>
    /// <param name="style">The style; unset fields come from the style it is pushed on.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="style"/> is null.</exception>
    public RichTextBuilder Push(TextStyle style)
    {
        ArgumentNullException.ThrowIfNull(style);
        _styles.Push(_styles.Count == 0 ? style : style.InheritFrom(_styles.Peek()));
        return this;
    }

    /// <summary>Ends the last style pushed.</summary>
    /// <returns>This builder.</returns>
    /// <exception cref="InvalidOperationException">No style is pushed.</exception>
    public RichTextBuilder Pop()
    {
        if (_styles.Count == 0)
        {
            throw new InvalidOperationException("No style is pushed.");
        }

        _styles.Pop();
        return this;
    }

    /// <summary>Appends text in the current style.</summary>
    /// <param name="text">The text.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The text contains U+FFFC, which only <see cref="AddPlaceholder"/> adds.</exception>
    public RichTextBuilder Append(string text) => Append(text.AsSpan());

    /// <summary>Appends text in the current style.</summary>
    /// <param name="text">The text.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentException">The text contains U+FFFC, which only <see cref="AddPlaceholder"/> adds.</exception>
    public RichTextBuilder Append(ReadOnlySpan<char> text)
    {
        if (text.Contains('\uFFFC'))
        {
            throw new ArgumentException("Object replacement characters are added with AddPlaceholder.", nameof(text));
        }

        Add(text);
        return this;
    }

    /// <summary>Adds an inline box, as a U+FFFC in the current style.</summary>
    /// <param name="placeholder">The box.</param>
    /// <returns>This builder.</returns>
    public RichTextBuilder AddPlaceholder(Placeholder placeholder)
    {
        _placeholders.Add(placeholder);
        Add("\uFFFC");
        return this;
    }

    /// <summary>Builds the rich text; styles still pushed end with the text.</summary>
    /// <returns>The rich text.</returns>
    public RichText ToRichText() => new(_text.ToString(), _spans, _placeholders);

    private void Add(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return;
        }

        int start = _text.Length;
        _text.Append(text);
        if (_styles.Count == 0)
        {
            return;
        }

        TextStyle style = _styles.Peek();
        if (_spans.Count > 0 && _spans[^1].End == start && ReferenceEquals(_spans[^1].Style, style))
        {
            _spans[^1] = _spans[^1] with { Length = _spans[^1].Length + text.Length };
        }
        else
        {
            _spans.Add(new StyleSpan(start, text.Length, style));
        }
    }
}
