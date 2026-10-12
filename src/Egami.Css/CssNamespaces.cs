namespace Egami.Css;

/// <summary>
/// The namespace prefixes selectors may use (<c>svg|rect</c>, <c>[xlink|href]</c>) and the default namespace for type selectors,
/// as <c>@namespace</c> rules declare them. Prefixes are case-sensitive.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="selectors"/>
/// </example>
public sealed class CssNamespaces
{
    private readonly Dictionary<string, string> _prefixes = new(StringComparer.Ordinal);

    /// <summary>Gets the default namespace URI, or <see langword="null"/> when none is declared.</summary>
    public string? DefaultNamespace { get; private set; }

    /// <summary>Gets the declared prefixes and their namespace URIs.</summary>
    public IReadOnlyDictionary<string, string> Prefixes => _prefixes;

    /// <summary>Declares a prefix, or the default namespace; a later declaration of the same prefix replaces an earlier one.</summary>
    /// <param name="prefix">The prefix, or <see langword="null"/> for the default namespace.</param>
    /// <param name="namespaceUri">The namespace URI. An empty string means no namespace.</param>
    public void Set(string? prefix, string namespaceUri)
    {
        ArgumentNullException.ThrowIfNull(namespaceUri);
        if (prefix is null)
        {
            DefaultNamespace = namespaceUri;
        }
        else
        {
            _prefixes[prefix] = namespaceUri;
        }
    }

    /// <summary>Looks up a prefix.</summary>
    /// <param name="prefix">The prefix.</param>
    /// <param name="namespaceUri">The namespace URI when declared.</param>
    /// <returns>True when the prefix is declared.</returns>
    public bool TryGetNamespace(string prefix, out string namespaceUri)
    {
        ArgumentNullException.ThrowIfNull(prefix);
        return _prefixes.TryGetValue(prefix, out namespaceUri!);
    }
}

/// <summary>A selector's specificity (Selectors 4): IDs, then classes, attributes and pseudo-classes, then types and pseudo-elements.</summary>
/// <param name="A">The number of ID selectors.</param>
/// <param name="B">The number of class, attribute and pseudo-class selectors.</param>
/// <param name="C">The number of type and pseudo-element selectors.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="selectors"/>
/// </example>
public readonly record struct CssSpecificity(int A, int B, int C) : IComparable<CssSpecificity>
{
    /// <summary>Gets the specificity of no selector: (0, 0, 0).</summary>
    public static CssSpecificity Zero => default;

    /// <inheritdoc/>
    public int CompareTo(CssSpecificity other)
    {
        int compare = A.CompareTo(other.A);
        if (compare == 0)
        {
            compare = B.CompareTo(other.B);
        }

        return compare != 0 ? compare : C.CompareTo(other.C);
    }

    /// <summary>Adds two specificities component by component.</summary>
    /// <param name="left">The first specificity.</param>
    /// <param name="right">The second specificity.</param>
    /// <returns>The sum.</returns>
    public static CssSpecificity operator +(CssSpecificity left, CssSpecificity right) => new(left.A + right.A, left.B + right.B, left.C + right.C);

    /// <summary>Compares two specificities.</summary>
    /// <param name="left">The first specificity.</param>
    /// <param name="right">The second specificity.</param>
    /// <returns>True when <paramref name="left"/> is less specific.</returns>
    public static bool operator <(CssSpecificity left, CssSpecificity right) => left.CompareTo(right) < 0;

    /// <summary>Compares two specificities.</summary>
    /// <param name="left">The first specificity.</param>
    /// <param name="right">The second specificity.</param>
    /// <returns>True when <paramref name="left"/> is more specific.</returns>
    public static bool operator >(CssSpecificity left, CssSpecificity right) => left.CompareTo(right) > 0;

    /// <summary>Compares two specificities.</summary>
    /// <param name="left">The first specificity.</param>
    /// <param name="right">The second specificity.</param>
    /// <returns>True when <paramref name="left"/> is at most as specific.</returns>
    public static bool operator <=(CssSpecificity left, CssSpecificity right) => left.CompareTo(right) <= 0;

    /// <summary>Compares two specificities.</summary>
    /// <param name="left">The first specificity.</param>
    /// <param name="right">The second specificity.</param>
    /// <returns>True when <paramref name="left"/> is at least as specific.</returns>
    public static bool operator >=(CssSpecificity left, CssSpecificity right) => left.CompareTo(right) >= 0;

    /// <summary>Formats the specificity as <c>(a, b, c)</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"({A}, {B}, {C})";
}
