namespace Egami.SystemDrawing.Text;

/// <summary>The generic families: <see cref="FontFamily.GenericSerif"/>, <see cref="FontFamily.GenericSansSerif"/> and <see cref="FontFamily.GenericMonospace"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
/// </example>
public enum GenericFontFamilies
{
    /// <summary>A serif family: Times New Roman, as on Windows.</summary>
    Serif = 0,

    /// <summary>A sans-serif family: Microsoft Sans Serif, as on Windows.</summary>
    SansSerif = 1,

    /// <summary>A monospace family: Courier New, as on Windows.</summary>
    Monospace = 2,
}

/// <summary>What an ampersand does in DrawString: nothing, underline the next character, or vanish.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="text"/>
/// </example>
public enum HotkeyPrefix
{
    /// <summary>Ampersands are drawn.</summary>
    None = 0,

    /// <summary>An ampersand is not drawn and underlines the next character; two draw one.</summary>
    Show = 1,

    /// <summary>An ampersand is not drawn and underlines nothing; two draw one.</summary>
    Hide = 2,
}
