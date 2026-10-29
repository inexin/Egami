namespace Egami.SystemDrawing.Imaging.Effects;

/// <summary>Which color channels a curve effect adjusts.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="effects"/>
/// </example>
public enum CurveChannel
{
    /// <summary>All.</summary>
    All = 0,

    /// <summary>Red.</summary>
    Red = 1,

    /// <summary>Green.</summary>
    Green = 2,

    /// <summary>Blue.</summary>
    Blue = 3,
}
