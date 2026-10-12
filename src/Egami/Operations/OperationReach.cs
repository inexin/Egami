namespace Egami.Operations;

/// <summary>
/// How far an operation chain reads past each output pixel, in whole pixels across and down: a pixel of the result can depend on
/// input pixels up to <see cref="X"/> columns and <see cref="Y"/> rows away. A blur of σ reaches about 3σ; a color matrix reaches
/// nothing. <see cref="RasterOps.Reach"/> measures a chain, so code that runs one on part of a larger picture, such as a canvas
/// layer, can include the pixels around it that the chain will read.
/// </summary>
/// <param name="X">The reach across, in pixels.</param>
/// <param name="Y">The reach down, in pixels.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Extending.cs" region="reach"/>
/// </example>
public readonly record struct OperationReach(int X, int Y);
