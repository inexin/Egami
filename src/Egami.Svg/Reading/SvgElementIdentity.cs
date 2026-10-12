namespace Egami.Svg.Reading;

/// <summary>Compares elements by identity, hashing their <see cref="SvgElement.Serial"/>.</summary>
internal sealed class IdentityComparer : IEqualityComparer<SvgElement>
{
    /// <summary>Gets the one instance.</summary>
    public static IdentityComparer Instance { get; } = new();

    public bool Equals(SvgElement? x, SvgElement? y) => ReferenceEquals(x, y);

    public int GetHashCode(SvgElement obj) => obj.Serial;
}
