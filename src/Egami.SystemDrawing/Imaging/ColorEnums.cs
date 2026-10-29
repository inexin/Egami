namespace Egami.SystemDrawing.Imaging;
/// <summary>What an <see cref="ImageAttributes"/> setting applies to.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="images"/>
/// </example>
public enum ColorAdjustType
{
    /// <summary>Everything that has no setting of its own.</summary>
    Default = 0,

    /// <summary>Bitmaps.</summary>
    Bitmap = 1,

    /// <summary>Brushes.</summary>
    Brush = 2,

    /// <summary>Pens.</summary>
    Pen = 3,

    /// <summary>Text.</summary>
    Text = 4,

    /// <summary>The number of types.</summary>
    Count = 5,

    /// <summary>Reserved.</summary>
    Any = 6,
}

/// <summary>Which colors a color matrix changes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="images"/>
/// </example>
public enum ColorMatrixFlag
{
    /// <summary>Every color.</summary>
    Default = 0,

    /// <summary>Every color but grays (red, green and blue equal).</summary>
    SkipGrays = 1,

    /// <summary>Grays through the gray matrix, other colors through the color matrix.</summary>
    AltGrays = 2,
}

/// <summary>A CMYK channel <see cref="ImageAttributes.SetOutputChannel(ColorChannelFlag)"/> shows as gray.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="images"/>
/// </example>
public enum ColorChannelFlag
{
    /// <summary>Cyan.</summary>
    ColorChannelC = 0,

    /// <summary>Magenta.</summary>
    ColorChannelM = 1,

    /// <summary>Yellow.</summary>
    ColorChannelY = 2,

    /// <summary>Black.</summary>
    ColorChannelK = 3,

    /// <summary>The number of channels.</summary>
    ColorChannelLast = 4,
}

/// <summary>What a color map applies to.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="images"/>
/// </example>
public enum ColorMapType
{
    /// <summary>Images.</summary>
    Default = 0,

    /// <summary>Brushes.</summary>
    Brush = 1,
}
