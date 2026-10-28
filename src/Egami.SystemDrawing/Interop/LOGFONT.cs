using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Egami.SystemDrawing.Interop;

/// <summary>
/// Windows' LOGFONT: a font described by height, weight, style and face name. <see cref="Font.ToLogFont(out LOGFONT)"/> fills
/// one and <see cref="Font.FromLogFont(ref LOGFONT)"/> reads one; no GDI handle is involved.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
/// </example>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public unsafe struct LOGFONT
{
    /// <summary>The height in logical units: negative for the em height, positive for the cell height.</summary>
    public int lfHeight;

    /// <summary>The average character width, or 0 for the face's own.</summary>
    public int lfWidth;

    /// <summary>The angle of the baseline, in tenths of a degree.</summary>
    public int lfEscapement;

    /// <summary>The angle of each character's baseline, in tenths of a degree.</summary>
    public int lfOrientation;

    /// <summary>The weight, from 0 to 1000: 400 is normal, 700 bold.</summary>
    public int lfWeight;

    /// <summary>1 for italic.</summary>
    public byte lfItalic;

    /// <summary>1 for underlined.</summary>
    public byte lfUnderline;

    /// <summary>1 for struck out.</summary>
    public byte lfStrikeOut;

    /// <summary>The character set, such as 0 for ANSI or 1 for the default.</summary>
    public byte lfCharSet;

    /// <summary>The output precision.</summary>
    public byte lfOutPrecision;

    /// <summary>The clipping precision.</summary>
    public byte lfClipPrecision;

    /// <summary>The output quality.</summary>
    public byte lfQuality;

    /// <summary>The pitch and family bits.</summary>
    public byte lfPitchAndFamily;

    private fixed char _lfFaceName[32];

    /// <summary>Gets the face name: up to 32 characters, ended by a zero character when shorter.</summary>
    [UnscopedRef]
    public Span<char> lfFaceName => MemoryMarshal.CreateSpan(ref _lfFaceName[0], 32);

    /// <summary>Gets a value indicating whether the face name starts with '@', which asks for a vertical font.</summary>
    internal readonly bool IsGdiVerticalFont => _lfFaceName[0] == '@';
}
