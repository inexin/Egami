using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;

using Egami.SystemDrawing.Internal;
using Egami.SystemDrawing.Interop;

using EgamiFonts = Egami.Fonts;

namespace Egami.SystemDrawing;

/// <summary>
/// A family, size, style and unit for drawing and measuring text. A family that is not installed (and has no registered stand-in)
/// falls back to the generic sans-serif family, with <see cref="Name"/> the family used and <see cref="OriginalFontName"/> the name asked for; sizes convert at the target's
/// resolution, 96 dpi for <see cref="GetHeight()"/> and <see cref="SizeInPoints"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
/// </example>
[TypeConverter(typeof(FontConverter))]
public sealed class Font : MarshalByRefObject, ICloneable, IDisposable, ISerializable
{
    private const float ScreenDpi = 96;

    /// <summary>Initializes a new instance of the <see cref="Font"/> class: another font in a new style.</summary>
    /// <param name="prototype">The font to copy.</param>
    /// <param name="newStyle">The style.</param>
    public Font(Font prototype, FontStyle newStyle)
    {
        ArgumentNullException.ThrowIfNull(prototype);
        FontFamily = prototype.FontFamily;
        Size = prototype.Size;
        Unit = prototype.Unit;
        Style = newStyle;
        GdiCharSet = prototype.GdiCharSet;
        GdiVerticalFont = prototype.GdiVerticalFont;
        OriginalFontName = prototype.OriginalFontName;
    }

    /// <summary>Initializes a new instance of the <see cref="Font"/> class, in points.</summary>
    /// <param name="family">The family.</param>
    /// <param name="emSize">The em size.</param>
    public Font(FontFamily family, float emSize)
        : this(family, emSize, FontStyle.Regular, GraphicsUnit.Point, 1, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Font"/> class, in points.</summary>
    /// <param name="family">The family.</param>
    /// <param name="emSize">The em size.</param>
    /// <param name="style">The style.</param>
    public Font(FontFamily family, float emSize, FontStyle style)
        : this(family, emSize, style, GraphicsUnit.Point, 1, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Font"/> class.</summary>
    /// <param name="family">The family.</param>
    /// <param name="emSize">The em size.</param>
    /// <param name="unit">The unit of the size.</param>
    public Font(FontFamily family, float emSize, GraphicsUnit unit)
        : this(family, emSize, FontStyle.Regular, unit, 1, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Font"/> class.</summary>
    /// <param name="family">The family.</param>
    /// <param name="emSize">The em size.</param>
    /// <param name="style">The style.</param>
    /// <param name="unit">The unit of the size.</param>
    public Font(FontFamily family, float emSize, FontStyle style, GraphicsUnit unit)
        : this(family, emSize, style, unit, 1, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Font"/> class.</summary>
    /// <param name="family">The family.</param>
    /// <param name="emSize">The em size.</param>
    /// <param name="style">The style.</param>
    /// <param name="unit">The unit of the size.</param>
    /// <param name="gdiCharSet">The GDI character set, kept and reported.</param>
    public Font(FontFamily family, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet)
        : this(family, emSize, style, unit, gdiCharSet, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Font"/> class.</summary>
    /// <param name="family">The family.</param>
    /// <param name="emSize">The em size.</param>
    /// <param name="style">The style.</param>
    /// <param name="unit">The unit of the size; not <see cref="GraphicsUnit.Display"/>.</param>
    /// <param name="gdiCharSet">The GDI character set, kept and reported.</param>
    /// <param name="gdiVerticalFont">Whether this is a vertical font, kept and reported.</param>
    /// <exception cref="ArgumentException">The size is not positive and finite, or the unit is <see cref="GraphicsUnit.Display"/>.</exception>
    public Font(FontFamily family, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet, bool gdiVerticalFont)
    {
        ArgumentNullException.ThrowIfNull(family);
        Check(emSize, unit);
        FontFamily = family;
        Size = emSize;
        Style = style;
        Unit = unit;
        GdiCharSet = gdiCharSet;
        GdiVerticalFont = gdiVerticalFont;
        OriginalFontName = family.Name;
    }

    /// <summary>Initializes a new instance of the <see cref="Font"/> class, in points.</summary>
    /// <param name="familyName">The family name.</param>
    /// <param name="emSize">The em size.</param>
    public Font(string familyName, float emSize)
        : this(familyName, emSize, FontStyle.Regular, GraphicsUnit.Point, 1, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Font"/> class, in points.</summary>
    /// <param name="familyName">The family name.</param>
    /// <param name="emSize">The em size.</param>
    /// <param name="style">The style.</param>
    public Font(string familyName, float emSize, FontStyle style)
        : this(familyName, emSize, style, GraphicsUnit.Point, 1, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Font"/> class.</summary>
    /// <param name="familyName">The family name.</param>
    /// <param name="emSize">The em size.</param>
    /// <param name="unit">The unit of the size.</param>
    public Font(string familyName, float emSize, GraphicsUnit unit)
        : this(familyName, emSize, FontStyle.Regular, unit, 1, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Font"/> class.</summary>
    /// <param name="familyName">The family name.</param>
    /// <param name="emSize">The em size.</param>
    /// <param name="style">The style.</param>
    /// <param name="unit">The unit of the size.</param>
    public Font(string familyName, float emSize, FontStyle style, GraphicsUnit unit)
        : this(familyName, emSize, style, unit, 1, false)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="Font"/> class.</summary>
    /// <param name="familyName">The family name.</param>
    /// <param name="emSize">The em size.</param>
    /// <param name="style">The style.</param>
    /// <param name="unit">The unit of the size.</param>
    /// <param name="gdiCharSet">The GDI character set, kept and reported.</param>
    public Font(string familyName, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet)
        : this(familyName, emSize, style, unit, gdiCharSet, false)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="Font"/> class. A family that cannot be found falls back to Microsoft Sans
    /// Serif; a name starting with '@' asks for a vertical font.
    /// </summary>
    /// <param name="familyName">The family name.</param>
    /// <param name="emSize">The em size.</param>
    /// <param name="style">The style.</param>
    /// <param name="unit">The unit of the size; not <see cref="GraphicsUnit.Display"/>.</param>
    /// <param name="gdiCharSet">The GDI character set, kept and reported.</param>
    /// <param name="gdiVerticalFont">Whether this is a vertical font, kept and reported.</param>
    /// <exception cref="ArgumentException">The size is not positive and finite, or the unit is <see cref="GraphicsUnit.Display"/>.</exception>
    public Font(string familyName, float emSize, FontStyle style, GraphicsUnit unit, byte gdiCharSet, bool gdiVerticalFont)
    {
        Check(emSize, unit);
        OriginalFontName = familyName ?? string.Empty;
        string name = OriginalFontName;
        if (name.StartsWith('@'))
        {
            name = name[1..];
            gdiVerticalFont = true;
        }

        FontFamily = Find(name);
        Size = emSize;
        Style = style;
        Unit = unit;
        GdiCharSet = gdiCharSet;
        GdiVerticalFont = gdiVerticalFont;
    }

    private Font(Font other)
    {
        FontFamily = other.FontFamily;
        Size = other.Size;
        Style = other.Style;
        Unit = other.Unit;
        GdiCharSet = other.GdiCharSet;
        GdiVerticalFont = other.GdiVerticalFont;
        OriginalFontName = other.OriginalFontName;
        SystemFontName = other.SystemFontName;
    }

    /// <summary>Gets the family.</summary>
    public FontFamily FontFamily { get; }

    /// <summary>Gets the em size, in <see cref="Unit"/>s.</summary>
    public float Size { get; }

    /// <summary>Gets the style.</summary>
    public FontStyle Style { get; }

    /// <summary>Gets the unit of <see cref="Size"/>.</summary>
    public GraphicsUnit Unit { get; }

    /// <summary>Gets the GDI character set given at construction; 1 (the default set) unless one was given.</summary>
    public byte GdiCharSet { get; }

    /// <summary>Gets a value indicating whether a vertical font was asked for.</summary>
    public bool GdiVerticalFont { get; }

    /// <summary>Gets the family name asked for, which differs from <see cref="Name"/> when the font fell back.</summary>
    public string OriginalFontName { get; private init; }

    /// <summary>Gets the <see cref="SystemFonts"/> name this font came from, or an empty string.</summary>
    public string SystemFontName { get; private init; } = string.Empty;

    /// <summary>Gets a value indicating whether this font came from <see cref="SystemFonts"/>.</summary>
    public bool IsSystemFont => SystemFontName.Length > 0;

    /// <summary>Gets the family name.</summary>
    public string Name => FontFamily.Name;

    /// <summary>Gets a value indicating whether the style is bold.</summary>
    public bool Bold => (Style & FontStyle.Bold) != 0;

    /// <summary>Gets a value indicating whether the style is italic.</summary>
    public bool Italic => (Style & FontStyle.Italic) != 0;

    /// <summary>Gets a value indicating whether the style is underlined.</summary>
    public bool Underline => (Style & FontStyle.Underline) != 0;

    /// <summary>Gets a value indicating whether the style is struck out.</summary>
    public bool Strikeout => (Style & FontStyle.Strikeout) != 0;

    /// <summary>Gets the em size in points, pixel and world sizes taken at 96 dpi.</summary>
    public float SizeInPoints => Unit == GraphicsUnit.Point ? Size : EmPixels(ScreenDpi) * 72 / ScreenDpi;

    /// <summary>Gets the line spacing at 96 dpi, in pixels, rounded up.</summary>
    public int Height => (int)MathF.Ceiling(GetHeight());

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    /// <param name="hdc">The device context.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static Font FromHdc(IntPtr hdc) => throw GdiErrors.WindowsOnly("Font.FromHdc", "Create the font from its family and size.");

    /// <summary>Not supported: Windows font handles. Always throws.</summary>
    /// <param name="hfont">The font handle.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static Font FromHfont(IntPtr hfont) => throw GdiErrors.WindowsOnly("Font.FromHfont", "Create the font from its family and size, or from a LOGFONT.");

    /// <summary>Creates a font from a LOGFONT, its height taken at 96 dpi.</summary>
    /// <param name="logFont">The LOGFONT.</param>
    /// <returns>The font, in points.</returns>
    public static Font FromLogFont(ref LOGFONT logFont)
    {
        string face = new string(logFont.lfFaceName).Split('\0')[0];
        bool vertical = face.StartsWith('@');
        Font probe = new(vertical ? face[1..] : face, 10);
        int height = logFont.lfHeight;
        float em;
        if (height < 0)
        {
            em = -height;
        }
        else if (height > 0)
        {
            (int emHeight, int ascent, int descent, _) = probe.FontFamily.Metrics(FontStyle.Regular);
            em = height * (float)emHeight / Math.Max(1, ascent + descent);
        }
        else
        {
            em = 12 * ScreenDpi / 72;
        }

        FontStyle style = (logFont.lfWeight >= 600 ? FontStyle.Bold : 0) | (logFont.lfItalic != 0 ? FontStyle.Italic : 0)
            | (logFont.lfUnderline != 0 ? FontStyle.Underline : 0) | (logFont.lfStrikeOut != 0 ? FontStyle.Strikeout : 0);
        return new Font(probe.FontFamily, em * 72 / ScreenDpi, style, GraphicsUnit.Point, logFont.lfCharSet, vertical) { OriginalFontName = face };
    }

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    /// <param name="logFont">The LOGFONT.</param>
    /// <param name="hdc">The device context.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static Font FromLogFont(ref LOGFONT logFont, IntPtr hdc) => throw GdiErrors.WindowsOnly("Font.FromLogFont(LOGFONT, IntPtr)", "Use FromLogFont(ref LOGFONT).");

    /// <summary>Creates a font from a LOGFONT, or from an object laid out as one, its height taken at 96 dpi.</summary>
    /// <param name="lf">A <see cref="LOGFONT"/>, or an object with LOGFONT's sequential layout.</param>
    /// <returns>The font, in points.</returns>
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("An object other than a LOGFONT is marshalled by its layout.")]
    public static Font FromLogFont(object lf)
    {
        ArgumentNullException.ThrowIfNull(lf);
        LOGFONT logFont = lf is LOGFONT l ? l : FromObject(lf);
        return FromLogFont(ref logFont);
    }

    /// <summary>Not supported: Windows device contexts. Always throws.</summary>
    /// <param name="lf">The LOGFONT.</param>
    /// <param name="hdc">The device context.</param>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public static Font FromLogFont(object lf, IntPtr hdc) => throw GdiErrors.WindowsOnly("Font.FromLogFont(object, IntPtr)", "Use FromLogFont(object).");

    /// <summary>Gets the line spacing at 96 dpi, in pixels.</summary>
    /// <returns>The line spacing.</returns>
    public float GetHeight() => GetHeight(ScreenDpi);

    /// <summary>Gets the line spacing at a resolution, in pixels; pixel and world sizes do not depend on it.</summary>
    /// <param name="dpi">The resolution, in dots per inch.</param>
    /// <returns>The line spacing.</returns>
    public float GetHeight(float dpi) => EmPixels(dpi) * LineSpacingPerEm;

    /// <summary>Gets the line spacing on a Graphics, in its page units.</summary>
    /// <param name="graphics">The Graphics.</param>
    /// <returns>The line spacing.</returns>
    public float GetHeight(Graphics graphics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        return EmIn(graphics) * LineSpacingPerEm;
    }

    /// <summary>Not supported: Windows font handles. Always throws.</summary>
    /// <returns>Never returns.</returns>
    [Obsolete(Internal.Diagnostics.HandlesMessage, DiagnosticId = Internal.Diagnostics.Handles, UrlFormat = Internal.Diagnostics.Url)]
    [EditorBrowsable(EditorBrowsableState.Advanced)]
    public IntPtr ToHfont() => throw GdiErrors.WindowsOnly("Font.ToHfont", "Use ToLogFont to describe the font.");

    /// <summary>Describes the font as a LOGFONT, its height in pixels at 96 dpi.</summary>
    /// <param name="logFont">The LOGFONT.</param>
    public void ToLogFont(out LOGFONT logFont) => logFont = ToLogFont(ScreenDpi);

    /// <summary>Describes the font as a LOGFONT, its height in pixels at a Graphics' resolution.</summary>
    /// <param name="logFont">The LOGFONT.</param>
    /// <param name="graphics">The Graphics.</param>
    public void ToLogFont(out LOGFONT logFont, Graphics graphics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        logFont = ToLogFont(graphics.DpiY);
    }

    /// <summary>Describes the font into an object laid out as a LOGFONT, its height in pixels at 96 dpi.</summary>
    /// <param name="logFont">An object with LOGFONT's sequential layout, such as a class declared like <see cref="LOGFONT"/>.</param>
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("An object other than a LOGFONT is marshalled by its layout.")]
    public void ToLogFont(object logFont) => ToObject(ToLogFont(ScreenDpi), logFont);

    /// <summary>Describes the font into an object laid out as a LOGFONT, its height in pixels at a Graphics' resolution.</summary>
    /// <param name="logFont">An object with LOGFONT's sequential layout.</param>
    /// <param name="graphics">The Graphics.</param>
    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("An object other than a LOGFONT is marshalled by its layout.")]
    public void ToLogFont(object logFont, Graphics graphics)
    {
        ArgumentNullException.ThrowIfNull(graphics);
        ToObject(ToLogFont(graphics.DpiY), logFont);
    }

    /// <summary>
    /// The font as an Egami font: the face for the style (with synthetic bold or italic where the family lacks it), sized in pixels
    /// at a resolution, for drawing with Egami.Drawing's canvases.
    /// </summary>
    /// <param name="dpi">The resolution the size converts at; 96 by default.</param>
    /// <returns>The Egami font. Underline and strikeout are not part of it; draw them as lines.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="escape-hatches"/>
    /// </example>
    public EgamiFonts.Font ToEgamiFont(float dpi = 96) => ToEgamiFont(EmPixels(dpi), kerning: true);

    /// <summary>Creates a copy of the font.</summary>
    /// <returns>The copy.</returns>
    public object Clone() => new Font(this);

    /// <summary>Releases nothing; fonts hold no native resources.</summary>
    public void Dispose()
    {
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is Font f && f.FontFamily.Equals(FontFamily) && f.Size == Size && f.Style == Style
        && f.Unit == Unit && f.GdiCharSet == GdiCharSet && f.GdiVerticalFont == GdiVerticalFont;

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Name.ToUpperInvariant(), Size, Style, Unit);

    /// <inheritdoc/>
    public override string ToString() => $"[Font: Name={Name}, Size={Size}, Units={(int)Unit}, GdiCharSet={GdiCharSet}, GdiVerticalFont={GdiVerticalFont}]";

    /// <inheritdoc/>
    void ISerializable.GetObjectData(SerializationInfo info, StreamingContext context)
    {
        ArgumentNullException.ThrowIfNull(info);
        info.AddValue("Name", string.IsNullOrEmpty(OriginalFontName) ? Name : OriginalFontName);
        info.AddValue("Size", Size);
        info.AddValue("Style", Style);
        info.AddValue("Unit", Unit);
    }

    /// <summary>A font for SystemFonts, with its name.</summary>
    internal Font AsSystemFont(string name) => new(this) { SystemFontName = name };

    /// <summary>The em in pixels at a resolution; pixel and world sizes are pixels.</summary>
    internal float EmPixels(float dpi) => Unit switch
    {
        GraphicsUnit.Point => Size * dpi / 72,
        GraphicsUnit.Inch => Size * dpi,
        GraphicsUnit.Document => Size * dpi / 300,
        GraphicsUnit.Millimeter => Size * dpi / 25.4f,
        _ => Size,
    };

    /// <summary>The em in a Graphics' world units: world sizes as they are, the others through the page unit.</summary>
    internal float EmIn(Graphics graphics) => Unit == GraphicsUnit.World ? Size : EmPixels(graphics.DpiY) / graphics.PageFactor;

    /// <summary>The line spacing as a share of the em.</summary>
    internal float LineSpacingPerEm
    {
        get
        {
            (int emHeight, _, _, int spacing) = FontFamily.Metrics(Style);
            return (float)spacing / emHeight;
        }
    }

    /// <summary>The Egami font at an em size, in whatever unit the caller draws in.</summary>
    internal EgamiFonts.Font ToEgamiFont(float em, bool kerning)
    {
        EgamiFonts.FontMatch match = FontFamily.Match(Style);
        return new EgamiFonts.Font(match.Typeface, em) { Synthesis = match.Synthesis, Kerning = kerning };
    }

    private static void Check(float emSize, GraphicsUnit unit)
    {
        if (!(emSize > 0) || float.IsInfinity(emSize))
        {
            throw new ArgumentException("Value of '" + emSize + "' is not valid for 'emSize'. 'emSize' should be greater than 0 and less than or equal to System.Single.MaxValue.", nameof(emSize));
        }

        if (unit is GraphicsUnit.Display or < GraphicsUnit.World or > GraphicsUnit.Millimeter)
        {
            throw new ArgumentException("Parameter is not valid.", nameof(unit));
        }
    }

    private static FontFamily Find(string name)
    {
        try
        {
            return new FontFamily(name);
        }
        catch (ArgumentException)
        {
            return FontFamily.GenericSansSerif;
        }
    }

    private LOGFONT ToLogFont(float dpi)
    {
        LOGFONT lf = default;
        lf.lfHeight = -(int)MathF.Round(EmPixels(dpi));
        lf.lfWeight = Bold ? 700 : 400;
        lf.lfItalic = (byte)(Italic ? 1 : 0);
        lf.lfUnderline = (byte)(Underline ? 1 : 0);
        lf.lfStrikeOut = (byte)(Strikeout ? 1 : 0);
        lf.lfCharSet = GdiCharSet;
        string face = (GdiVerticalFont ? "@" : string.Empty) + Name;
        face.AsSpan(0, Math.Min(face.Length, 31)).CopyTo(lf.lfFaceName);
        return lf;
    }

    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("An object other than a LOGFONT is marshalled by its layout.")]
    private static unsafe LOGFONT FromObject(object lf)
    {
        int size = Marshal.SizeOf(lf);
        IntPtr p = Marshal.AllocHGlobal(Math.Max(size, sizeof(LOGFONT)));
        try
        {
            new Span<byte>((void*)p, Math.Max(size, sizeof(LOGFONT))).Clear();
            Marshal.StructureToPtr(lf, p, false);
            return *(LOGFONT*)p;
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    [System.Diagnostics.CodeAnalysis.RequiresDynamicCode("An object other than a LOGFONT is marshalled by its layout.")]
    private static unsafe void ToObject(LOGFONT value, object logFont)
    {
        ArgumentNullException.ThrowIfNull(logFont);
        if (logFont is LOGFONT)
        {
            // A boxed LOGFONT is written in place, as marshalling into it would.
            System.Runtime.CompilerServices.Unsafe.Unbox<LOGFONT>(logFont) = value;
            return;
        }

        if (logFont.GetType().IsValueType)
        {
            throw new ArgumentException("A value type other than LOGFONT cannot be written through an object.", nameof(logFont));
        }

        int size = Math.Max(Marshal.SizeOf(logFont), sizeof(LOGFONT));
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            new Span<byte>((void*)p, size).Clear();
            *(LOGFONT*)p = value;
            Marshal.PtrToStructure(p, logFont);
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }
}
