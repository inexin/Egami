namespace Egami.Operations;

/// <summary>The screen color whose spill <see cref="RasterOps.Despill(DespillColor, float, float)"/> removes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Keying.cs" region="chroma-key"/>
/// </example>
public enum DespillColor
{
    /// <summary>A green screen: green is held down to the level of red and blue.</summary>
    Green = 0,

    /// <summary>A blue screen: blue is held down to the level of red and green.</summary>
    Blue = 1,
}
