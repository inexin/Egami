namespace Egami;

/// <summary>A width and height in pixels.</summary>
/// <param name="Width">The width.</param>
/// <param name="Height">The height.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="scaled-decode"/>
/// </example>
public readonly record struct Size(int Width, int Height);
