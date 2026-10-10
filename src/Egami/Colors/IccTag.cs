namespace Egami.Colors;

/// <summary>One tag of an ICC profile: its signature, the type signature its data starts with, and the data.</summary>
/// <param name="Signature">The tag signature, such as "rXYZ", "A2B0" or "desc", without trailing spaces.</param>
/// <param name="Type">The type signature at the start of the data, such as "XYZ", "curv" or "mAB", without trailing spaces.</param>
/// <param name="Data">The tag's bytes as stored, type signature included.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ColorManagement.cs" region="icc"/>
/// </example>
public readonly record struct IccTag(string Signature, string Type, ReadOnlyMemory<byte> Data);
