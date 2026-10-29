using System.ComponentModel;

using Egami.SystemDrawing.Text;

namespace Egami.SystemDrawing;

/// <summary>
/// How DrawString and MeasureString lay text out: alignment along and across the lines, wrapping, trimming, clipping, tab stops,
/// hotkey prefixes and the ranges MeasureCharacterRanges measures.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
/// </example>
public sealed class StringFormat : MarshalByRefObject, ICloneable, IDisposable
{
    private static readonly StringFormat s_default = new();
    private static readonly StringFormat s_typographic = new(StringFormatFlags.FitBlackBox | StringFormatFlags.LineLimit | StringFormatFlags.NoClip) { Trimming = StringTrimming.None, Typographic = true };

    private StringAlignment _alignment;
    private StringAlignment _lineAlignment;
    private StringTrimming _trimming = StringTrimming.Character;
    private HotkeyPrefix _hotkeyPrefix;
    private float _firstTabOffset;
    private float[] _tabStops = [];
    private CharacterRange[] _ranges = [];

    /// <summary>Initializes a new instance of the <see cref="StringFormat"/> class with the defaults: near alignment, character trimming.</summary>
    public StringFormat()
        : this(0, 0)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="StringFormat"/> class with flags.</summary>
    /// <param name="options">The flags.</param>
    public StringFormat(StringFormatFlags options)
        : this(options, 0)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="StringFormat"/> class with flags and a language.</summary>
    /// <param name="options">The flags.</param>
    /// <param name="language">The language id, kept and reported; Egami does not substitute digits.</param>
    public StringFormat(StringFormatFlags options, int language)
    {
        FormatFlags = options;
        _ = language;
    }

    /// <summary>Initializes a new instance of the <see cref="StringFormat"/> class as a copy of another.</summary>
    /// <param name="format">The format to copy.</param>
    public StringFormat(StringFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        FormatFlags = format.FormatFlags;
        _alignment = format._alignment;
        _lineAlignment = format._lineAlignment;
        _trimming = format._trimming;
        _hotkeyPrefix = format._hotkeyPrefix;
        _firstTabOffset = format._firstTabOffset;
        _tabStops = (float[])format._tabStops.Clone();
        _ranges = (CharacterRange[])format._ranges.Clone();
        DigitSubstitutionLanguage = format.DigitSubstitutionLanguage;
        DigitSubstitutionMethod = format.DigitSubstitutionMethod;
        Typographic = format.Typographic;
    }

    /// <summary>
    /// Gets the shared default format: no flags, near alignment, character trimming. It is one object for the whole process, so a
    /// change to it shows wherever it is used, as in System.Drawing.
    /// </summary>
    public static StringFormat GenericDefault => s_default;

    /// <summary>
    /// Gets the shared typographic format: <see cref="StringFormatFlags.FitBlackBox"/>, <see cref="StringFormatFlags.LineLimit"/>
    /// and <see cref="StringFormatFlags.NoClip"/>, no trimming, and none of the default format's padding or widening.
    /// </summary>
    public static StringFormat GenericTypographic => s_typographic;

    /// <summary>Gets or sets the flags.</summary>
    public StringFormatFlags FormatFlags { get; set; }

    /// <summary>Gets or sets the alignment along a line.</summary>
    /// <exception cref="InvalidEnumArgumentException">The value is not a <see cref="StringAlignment"/>.</exception>
    public StringAlignment Alignment
    {
        get => _alignment;
        set => _alignment = Valid(value, nameof(value));
    }

    /// <summary>Gets or sets the alignment of the lines within the layout rectangle.</summary>
    /// <exception cref="InvalidEnumArgumentException">The value is not a <see cref="StringAlignment"/>.</exception>
    public StringAlignment LineAlignment
    {
        get => _lineAlignment;
        set => _lineAlignment = Valid(value, nameof(value));
    }

    /// <summary>Gets or sets how a line that does not fit is cut.</summary>
    /// <exception cref="InvalidEnumArgumentException">The value is not a <see cref="StringTrimming"/>.</exception>
    public StringTrimming Trimming
    {
        get => _trimming;
        set => _trimming = value is < StringTrimming.None or > StringTrimming.EllipsisPath ? throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(StringTrimming)) : value;
    }

    /// <summary>Gets or sets what ampersands do.</summary>
    /// <exception cref="InvalidEnumArgumentException">The value is not a <see cref="Text.HotkeyPrefix"/>.</exception>
    public HotkeyPrefix HotkeyPrefix
    {
        get => _hotkeyPrefix;
        set => _hotkeyPrefix = value is < HotkeyPrefix.None or > HotkeyPrefix.Hide ? throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(HotkeyPrefix)) : value;
    }

    /// <summary>Gets the language whose digits would be substituted.</summary>
    public int DigitSubstitutionLanguage { get; private set; }

    /// <summary>Gets how digits would be substituted.</summary>
    public StringDigitSubstitute DigitSubstitutionMethod { get; private set; }

    /// <summary>Gets a value indicating whether this lays text out typographically: no padding, design advances.</summary>
    internal bool Typographic { get; private init; }

    /// <summary>Gets the ranges MeasureCharacterRanges measures.</summary>
    internal ReadOnlySpan<CharacterRange> Ranges => _ranges;

    /// <summary>Sets the digit substitution; kept and reported, Egami draws digits as written.</summary>
    /// <param name="language">The language id.</param>
    /// <param name="substitute">The method.</param>
    public void SetDigitSubstitution(int language, StringDigitSubstitute substitute)
    {
        DigitSubstitutionLanguage = language;
        DigitSubstitutionMethod = substitute;
    }

    /// <summary>Gets the tab stops.</summary>
    /// <param name="firstTabOffset">The distance from the line start to the first tab stop's interval start.</param>
    /// <returns>The intervals, each from the stop before it.</returns>
    public float[] GetTabStops(out float firstTabOffset)
    {
        firstTabOffset = _firstTabOffset;
        return (float[])_tabStops.Clone();
    }

    /// <summary>Sets the tab stops. Without any, a tab moves to the next multiple of four ems.</summary>
    /// <param name="firstTabOffset">The distance from the line start to the first interval's start; not negative.</param>
    /// <param name="tabStops">The intervals, each from the stop before it.</param>
    /// <exception cref="ArgumentException"><paramref name="firstTabOffset"/> is negative.</exception>
    public void SetTabStops(float firstTabOffset, float[] tabStops)
    {
        ArgumentNullException.ThrowIfNull(tabStops);
        if (firstTabOffset < 0)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(firstTabOffset));
        }

        _firstTabOffset = firstTabOffset;
        _tabStops = (float[])tabStops.Clone();
    }

    /// <summary>Sets the ranges MeasureCharacterRanges measures.</summary>
    /// <param name="ranges">Up to 32 ranges.</param>
    /// <exception cref="OverflowException">There are more than 32 ranges.</exception>
    public void SetMeasurableCharacterRanges(CharacterRange[] ranges)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (ranges.Length > 32)
        {
            throw new OverflowException("Arithmetic operation resulted in an overflow.");
        }

        _ranges = (CharacterRange[])ranges.Clone();
    }

    /// <summary>Creates a copy of the format.</summary>
    /// <returns>The copy.</returns>
    public object Clone() => new StringFormat(this);

    /// <summary>Releases nothing; formats hold no native resources.</summary>
    public void Dispose()
    {
    }

    /// <inheritdoc/>
    public override string ToString() => $"[StringFormat, FormatFlags={FormatFlags}]";

    /// <summary>The tab stops as distances from the line start, or none.</summary>
    internal float[] TabPositions()
    {
        float[] positions = new float[_tabStops.Length];
        float at = _firstTabOffset;
        for (int i = 0; i < positions.Length; i++)
        {
            at += _tabStops[i];
            positions[i] = at;
        }

        return positions;
    }

    private static StringAlignment Valid(StringAlignment value, string name) =>
        value is < StringAlignment.Near or > StringAlignment.Far ? throw new InvalidEnumArgumentException(name, (int)value, typeof(StringAlignment)) : value;
}

/// <summary>A range of characters: where it starts and how many characters it holds.</summary>
/// <param name="First">The index of the first character.</param>
/// <param name="Length">The number of characters.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
/// </example>
public struct CharacterRange(int First, int Length) : IEquatable<CharacterRange>
{
    /// <summary>Gets or sets the index of the first character.</summary>
    public int First { get; set; } = First;

    /// <summary>Gets or sets the number of characters.</summary>
    public int Length { get; set; } = Length;

    /// <summary>Compares two ranges.</summary>
    /// <param name="cr1">The first range.</param>
    /// <param name="cr2">The second range.</param>
    /// <returns><see langword="true"/> when they start at the same character and are as long.</returns>
    public static bool operator ==(CharacterRange cr1, CharacterRange cr2) => cr1.Equals(cr2);

    /// <summary>Compares two ranges.</summary>
    /// <param name="cr1">The first range.</param>
    /// <param name="cr2">The second range.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool operator !=(CharacterRange cr1, CharacterRange cr2) => !cr1.Equals(cr2);

    /// <inheritdoc/>
    public readonly bool Equals(CharacterRange other) => First == other.First && Length == other.Length;

    /// <inheritdoc/>
    public override readonly bool Equals(object? obj) => obj is CharacterRange other && Equals(other);

    /// <inheritdoc/>
    public override readonly int GetHashCode() => HashCode.Combine(First, Length);
}
