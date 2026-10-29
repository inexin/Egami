using System.ComponentModel;

using Egami.SystemDrawing.Internal;
using Egami.SystemDrawing.Text;

using EgamiFonts = Egami.Fonts;

namespace Egami.SystemDrawing;

/// <summary>
/// A family of faces, such as Arial with its bold and italic faces, found among the installed fonts or a
/// <see cref="PrivateFontCollection"/>. A missing family is an error, as in System.Drawing, unless
/// <see cref="SystemDrawingOptions.FontSubstitutes"/> gives a stand-in: set it to Egami.Fonts.Substitutes'
/// <c>SubstituteFonts.Find</c> for metric-compatible fonts (Liberation, Carlito, Caladea) under the requested name.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
/// </example>
public sealed class FontFamily : MarshalByRefObject, IDisposable
{
    private readonly EgamiFonts.FontFamily _family;

    /// <summary>Initializes a new instance of the <see cref="FontFamily"/> class: an installed family.</summary>
    /// <param name="name">The family name, matched without regard to case.</param>
    /// <exception cref="ArgumentException">No such family is installed and no registered substitute has one.</exception>
    public FontFamily(string name)
        : this(name, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="FontFamily"/> class: a family of a collection.</summary>
    /// <param name="name">The family name, matched without regard to case.</param>
    /// <param name="fontCollection">The collection, or <see langword="null"/> for the installed fonts.</param>
    /// <exception cref="ArgumentException">The collection has no such family and no registered substitute has one.</exception>
    public FontFamily(string name, FontCollection? fontCollection)
    {
        ArgumentNullException.ThrowIfNull(name);
        EgamiFonts.FontCollection fonts = fontCollection?.Fonts ?? EgamiFonts.FontCollection.System;
        _family = TextFonts.Find(fonts, name, out bool substituted) ?? throw new ArgumentException($"Font '{name}' cannot be found.", nameof(name));
        Name = substituted ? name.Trim() : _family.Name;
    }

    /// <summary>Initializes a new instance of the <see cref="FontFamily"/> class: a generic family.</summary>
    /// <param name="genericFamily">Serif (Times New Roman), sans-serif (Microsoft Sans Serif) or monospace (Courier New).</param>
    /// <exception cref="ArgumentException">No font at all is installed.</exception>
    public FontFamily(GenericFontFamilies genericFamily)
    {
        (string name, EgamiFonts.GenericFamily generic) = genericFamily switch
        {
            GenericFontFamilies.Serif => (TextFonts.Serif, EgamiFonts.GenericFamily.Serif),
            GenericFontFamilies.Monospace => (TextFonts.Monospace, EgamiFonts.GenericFamily.Monospace),
            _ => (TextFonts.SansSerif, EgamiFonts.GenericFamily.SansSerif),
        };
        _family = TextFonts.Generic(name, generic, out bool substituted) ?? throw new ArgumentException("No fonts are installed.", nameof(genericFamily));
        Name = substituted || _family.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ? name : _family.Name;
    }

    /// <summary>Gets the installed families.</summary>
    public static FontFamily[] Families => InstalledFontCollection.Shared.Families;

    /// <summary>Gets a monospace family: Courier New, as on Windows.</summary>
    public static FontFamily GenericMonospace => new(GenericFontFamilies.Monospace);

    /// <summary>Gets a sans-serif family: Microsoft Sans Serif, as on Windows.</summary>
    public static FontFamily GenericSansSerif => new(GenericFontFamilies.SansSerif);

    /// <summary>Gets a serif family: Times New Roman, as on Windows.</summary>
    public static FontFamily GenericSerif => new(GenericFontFamilies.Serif);

    /// <summary>Gets the family name: the installed family's own spelling, or the requested name when a registered substitute stands in.</summary>
    public string Name { get; }

    internal EgamiFonts.FontFamily Faces => _family;

    internal FontFamily(EgamiFonts.FontFamily family)
    {
        _family = family;
        Name = family.Name;
    }

    /// <summary>Gets the installed families. Use <see cref="Families"/>.</summary>
    /// <param name="graphics">Not used.</param>
    /// <returns>The installed families.</returns>
    [Obsolete("FontFamily.GetFamilies has been deprecated. Use Families instead.")]
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static FontFamily[] GetFamilies(Graphics graphics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        return Families;
    }

    /// <summary>Gets the ascent of a style's face, in font design units: the OS/2 Windows ascent.</summary>
    /// <param name="style">The style.</param>
    /// <returns>The ascent.</returns>
    public int GetCellAscent(FontStyle style) => Metrics(style).Ascent;

    /// <summary>Gets the descent of a style's face, in font design units: the OS/2 Windows descent.</summary>
    /// <param name="style">The style.</param>
    /// <returns>The descent.</returns>
    public int GetCellDescent(FontStyle style) => Metrics(style).Descent;

    /// <summary>Gets the em height of a style's face, in font design units: its units per em.</summary>
    /// <param name="style">The style.</param>
    /// <returns>The em height.</returns>
    public int GetEmHeight(FontStyle style) => Metrics(style).EmHeight;

    /// <summary>Gets the distance between baselines of a style's face, in font design units: the hhea ascender, descender and line gap.</summary>
    /// <param name="style">The style.</param>
    /// <returns>The line spacing.</returns>
    public int GetLineSpacing(FontStyle style) => Metrics(style).LineSpacing;

    /// <summary>Gets the family name in a language; Egami gives the same name for every language.</summary>
    /// <param name="language">The language id, such as 0 for neutral or 1033 for US English.</param>
    /// <returns>The name.</returns>
    public string GetName(int language) => Name;

    /// <summary>Gets a value indicating whether a style can be drawn: always, since bold and italic are synthesized when missing.</summary>
    /// <param name="style">The style.</param>
    /// <returns><see langword="true"/>.</returns>
    public bool IsStyleAvailable(FontStyle style) => true;

    /// <summary>Releases nothing; families hold no native resources.</summary>
    public void Dispose()
    {
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is FontFamily other && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase) && ReferenceEquals(_family.Faces[0], other._family.Faces[0]);

    /// <inheritdoc/>
    public override int GetHashCode() => StringComparer.OrdinalIgnoreCase.GetHashCode(Name);

    /// <inheritdoc/>
    public override string ToString() => $"[{nameof(FontFamily)}: Name={Name}]";

    /// <summary>The face for a style, with the bold and italic to synthesize.</summary>
    internal EgamiFonts.FontMatch Match(FontStyle style) => _family.Match(TextFonts.Style(style));

    /// <summary>A style's vertical metrics in design units, as GDI+ reports them.</summary>
    internal (int EmHeight, int Ascent, int Descent, int LineSpacing) Metrics(FontStyle style)
    {
        EgamiFonts.Typeface face = Match(style).Typeface;
        EgamiFonts.VerticalMetrics win = face.WinMetrics, hhea = face.HheaMetrics;

        // The cell is the Windows metrics, or the typographic ones when the font asks for them (USE_TYPO_METRICS).
        EgamiFonts.VerticalMetrics typo = face.TypoMetrics;
        if (GdiAscent.UsesTypoMetrics(face) && (typo.Ascent != 0 || typo.Descent != 0))
        {
            win = typo;
        }

        int ascent = win.Ascent, descent = -win.Descent;
        if (ascent == 0 && descent == 0)
        {
            ascent = hhea.Ascent;
            descent = -hhea.Descent;
        }

        int spacing = hhea.Ascent - hhea.Descent + hhea.LineGap;
        return (face.UnitsPerEm, ascent, descent, spacing > 0 ? spacing : ascent + descent);
    }
}
