namespace Egami.SystemDrawing.Text;
/// <summary>How text is rendered: aliased, grayscale or ClearType, with or without hinting.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public enum TextRenderingHint
{
    /// <summary>The system default: ClearType with hinting.</summary>
    SystemDefault = 0,

    /// <summary>Aliased, hinted.</summary>
    SingleBitPerPixelGridFit = 1,

    /// <summary>Aliased.</summary>
    SingleBitPerPixel = 2,

    /// <summary>Grayscale, hinted.</summary>
    AntiAliasGridFit = 3,

    /// <summary>Grayscale.</summary>
    AntiAlias = 4,

    /// <summary>ClearType, hinted.</summary>
    ClearTypeGridFit = 5,
}
