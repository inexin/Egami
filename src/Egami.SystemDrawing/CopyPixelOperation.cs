namespace Egami.SystemDrawing;
/// <summary>The raster operation of <see cref="Graphics.CopyFromScreen(System.Drawing.Point, System.Drawing.Point, System.Drawing.Size, CopyPixelOperation)"/>, which needs Windows and always throws.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="graphics"/>
/// </example>
public enum CopyPixelOperation
{
    /// <summary>Do not mirror.</summary>
    NoMirrorBitmap = -2147483648,

    /// <summary>Black.</summary>
    Blackness = 66,

    /// <summary>Not (source or destination).</summary>
    NotSourceErase = 1114278,

    /// <summary>Not source.</summary>
    NotSourceCopy = 3342344,

    /// <summary>Source and not destination.</summary>
    SourceErase = 4457256,

    /// <summary>Not destination.</summary>
    DestinationInvert = 5570569,

    /// <summary>Pattern xor destination.</summary>
    PatInvert = 5898313,

    /// <summary>Source xor destination.</summary>
    SourceInvert = 6684742,

    /// <summary>Source and destination.</summary>
    SourceAnd = 8913094,

    /// <summary>Not source or destination.</summary>
    MergePaint = 12255782,

    /// <summary>Source and pattern.</summary>
    MergeCopy = 12583114,

    /// <summary>Source.</summary>
    SourceCopy = 13369376,

    /// <summary>Source or destination.</summary>
    SourcePaint = 15597702,

    /// <summary>Pattern.</summary>
    PatCopy = 15728673,

    /// <summary>Pattern or not source or destination.</summary>
    PatPaint = 16452105,

    /// <summary>White.</summary>
    Whiteness = 16711778,

    /// <summary>Include layered windows.</summary>
    CaptureBlt = 1073741824,
}
