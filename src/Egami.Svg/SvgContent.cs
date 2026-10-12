namespace Egami.Svg;

/// <summary>
/// One item of an <see cref="SvgElement"/>'s content, in file order: an element, text, a comment or a processing instruction.
/// </summary>
/// <remarks>
/// An item belongs to at most one parent. Adding one that already has a parent throws; <see cref="Clone"/> it or
/// <see cref="Remove"/> it first.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="edit"/>
/// </example>
public abstract class SvgContent
{
    private protected SvgContent()
    {
    }

    /// <summary>Gets the element this item belongs to, or <see langword="null"/> for a root or an unattached item.</summary>
    public SvgElement? Parent { get; internal set; }

    /// <summary>Gets the document whose tree holds this item, or <see langword="null"/> when it is not in a document.</summary>
    public SvgDocument? Document
    {
        get
        {
            SvgContent item = this;
            while (item.Parent is SvgElement parent)
            {
                item = parent;
            }

            return (item as SvgElement)?.OwnerDocument;
        }
    }

    /// <summary>Gets the 1-based line this item started on in the file it was read from, or 0 for an item made in code.</summary>
    public int Line { get; internal set; }

    /// <summary>Gets the 1-based column this item started at in the file it was read from, or 0 for an item made in code.</summary>
    public int Column { get; internal set; }

    /// <summary>Removes this item from its parent, if it has one.</summary>
    public void Remove() => Parent?.Children.Remove(this);

    /// <summary>Makes a deep copy without a parent: an element is copied with its attributes and all its content.</summary>
    /// <returns>The copy.</returns>
    public abstract SvgContent Clone();

    /// <summary>Tells the document, if any, that its tree changed, so its render tree is rebuilt on the next draw.</summary>
    private protected void Changed() => Document?.Touch();
}

/// <summary>Character data in an element: text or a CDATA section, kept apart so a save writes it back the same way.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="edit"/>
/// </example>
public sealed class SvgTextContent : SvgContent
{
    private string _value;

    /// <summary>Initializes a new instance of the <see cref="SvgTextContent"/> class.</summary>
    /// <param name="value">The text, with entities already expanded.</param>
    /// <param name="isCData">Whether a save writes it as a CDATA section.</param>
    public SvgTextContent(string value, bool isCData = false)
    {
        ArgumentNullException.ThrowIfNull(value);
        _value = value;
        IsCData = isCData;
    }

    /// <summary>Gets or sets the text, with entities already expanded.</summary>
    public string Value
    {
        get => _value;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _value = value;
            Changed();
        }
    }

    /// <summary>Gets a value indicating whether a save writes the text as a CDATA section.</summary>
    public bool IsCData { get; }

    /// <inheritdoc/>
    public override SvgContent Clone() => new SvgTextContent(_value, IsCData) { Line = Line, Column = Column };

    /// <inheritdoc/>
    public override string ToString() => _value;
}

/// <summary>An XML comment, kept so a save writes it back.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="edit"/>
/// </example>
public sealed class SvgComment : SvgContent
{
    /// <summary>Initializes a new instance of the <see cref="SvgComment"/> class.</summary>
    /// <param name="value">The comment's text, without the <c>&lt;!--</c> and <c>--&gt;</c> markers.</param>
    public SvgComment(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Value = value;
    }

    /// <summary>Gets the comment's text.</summary>
    public string Value { get; }

    /// <inheritdoc/>
    public override SvgContent Clone() => new SvgComment(Value) { Line = Line, Column = Column };

    /// <inheritdoc/>
    public override string ToString() => $"<!--{Value}-->";
}

/// <summary>An XML processing instruction, such as <c>&lt;?xml-stylesheet ...?&gt;</c>, kept so a save writes it back.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="edit"/>
/// </example>
public sealed class SvgProcessingInstruction : SvgContent
{
    /// <summary>Initializes a new instance of the <see cref="SvgProcessingInstruction"/> class.</summary>
    /// <param name="target">The target, such as <c>xml-stylesheet</c>.</param>
    /// <param name="data">The text after the target.</param>
    public SvgProcessingInstruction(string target, string data)
    {
        ArgumentException.ThrowIfNullOrEmpty(target);
        ArgumentNullException.ThrowIfNull(data);
        Target = target;
        Data = data;
    }

    /// <summary>Gets the target, such as <c>xml-stylesheet</c>.</summary>
    public string Target { get; }

    /// <summary>Gets the text after the target.</summary>
    public string Data { get; }

    /// <inheritdoc/>
    public override SvgContent Clone() => new SvgProcessingInstruction(Target, Data) { Line = Line, Column = Column };

    /// <inheritdoc/>
    public override string ToString() => $"<?{Target} {Data}?>";
}
