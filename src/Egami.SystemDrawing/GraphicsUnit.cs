namespace Egami.SystemDrawing;

/// <summary>The unit of a measure: the world's, the display's, pixels, points, inches, document units or millimeters.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="bitmap"/>
/// </example>
public enum GraphicsUnit
{
    /// <summary>The world coordinate system's unit.</summary>
    World = 0,

    /// <summary>The display's unit: pixels on screens, 1/100 inch on printers.</summary>
    Display = 1,

    /// <summary>A device pixel.</summary>
    Pixel = 2,

    /// <summary>A printer's point, 1/72 inch.</summary>
    Point = 3,

    /// <summary>An inch.</summary>
    Inch = 4,

    /// <summary>A document unit, 1/300 inch.</summary>
    Document = 5,

    /// <summary>A millimeter.</summary>
    Millimeter = 6,
}
