using System.Text;

namespace Egami.Css;

/// <summary>
/// A CSS Syntax 3 component value: a preserved token, a function or a simple block. Declarations, preludes and values are
/// lists of these. <see cref="object.ToString"/> serializes back to CSS text.
/// </summary>
/// <remarks>
/// Component values compare by content: two functions are equal when their names and arguments are; source positions are not
/// compared.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="tokens"/>
/// </example>
public abstract record class CssComponentValue
{
    private protected CssComponentValue()
    {
    }

    /// <summary>Gets the 1-based line the value starts on, or 0 when unknown.</summary>
    public abstract int Line { get; }

    /// <summary>Gets the 1-based column the value starts at, or 0 when unknown.</summary>
    public abstract int Column { get; }

    /// <summary>Serializes the value as CSS text.</summary>
    /// <returns>The CSS text.</returns>
    public sealed override string ToString()
    {
        StringBuilder builder = new();
        CssSerializer.Write(builder, this);
        return builder.ToString();
    }

    /// <summary>Serializes a list of component values as CSS text.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The CSS text.</returns>
    public static string Serialize(IEnumerable<CssComponentValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        StringBuilder builder = new();
        CssSerializer.Write(builder, values);
        return builder.ToString();
    }

    /// <summary>Parses CSS text as a list of component values (CSS Syntax 3, "parse a list of component values").</summary>
    /// <param name="css">The CSS text.</param>
    /// <param name="options">Options for error reporting.</param>
    /// <returns>The component values, including whitespace.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="tokens"/>
    /// </example>
    public static IReadOnlyList<CssComponentValue> ParseList(string css, CssParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(css);
        return CssSyntax.ParseComponentValues(css, options ?? CssParseOptions.Default);
    }

    internal bool IsWhitespace => this is CssTokenValue { Token.Kind: CssTokenKind.Whitespace };

    internal virtual bool IsToken(CssTokenKind kind) => false;

    internal virtual bool IsDelim(char c) => false;

    internal virtual bool IsIdent(string lowercaseName) => false;
}

/// <summary>A token kept as a component value: any token other than a function or an opening bracket.</summary>
/// <param name="Token">The token.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="tokens"/>
/// </example>
public sealed record class CssTokenValue(CssToken Token) : CssComponentValue
{
    /// <inheritdoc/>
    public override int Line => Token.Line;

    /// <inheritdoc/>
    public override int Column => Token.Column;

    /// <inheritdoc/>
    public bool Equals(CssTokenValue? other) => other is not null && Token.Equals(other.Token);

    /// <inheritdoc/>
    public override int GetHashCode() => Token.GetHashCode();

    internal override bool IsToken(CssTokenKind kind) => Token.Kind == kind;

    internal override bool IsDelim(char c) => Token.IsDelim(c);

    internal override bool IsIdent(string lowercaseName) => Token.IsIdent(lowercaseName);
}

/// <summary>A function: its name and its arguments up to the closing parenthesis.</summary>
/// <param name="Name">The function name, as written (with escapes resolved).</param>
/// <param name="Arguments">The arguments, including whitespace and commas.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="tokens"/>
/// </example>
public sealed record class CssFunction(string Name, IReadOnlyList<CssComponentValue> Arguments) : CssComponentValue
{
    /// <inheritdoc/>
    public override int Line => StartLine;

    /// <inheritdoc/>
    public override int Column => StartColumn;

    internal int StartLine { get; init; }

    internal int StartColumn { get; init; }

    /// <summary>Gets whether the function's name is <paramref name="lowercaseName"/>, compared ASCII case-insensitively.</summary>
    /// <param name="lowercaseName">The name in lowercase.</param>
    /// <returns>True when the names match.</returns>
    public bool NameIs(string lowercaseName) => CssText.EqualsIgnoreAsciiCase(Name, lowercaseName);

    /// <inheritdoc/>
    public bool Equals(CssFunction? other) => other is not null && Name == other.Name && CssSerializer.SequenceEqual(Arguments, other.Arguments);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Name, Arguments.Count);
}

/// <summary>A simple block: content between matching <c>()</c>, <c>[]</c> or <c>{}</c>.</summary>
/// <param name="OpenToken">The opening character: <c>(</c>, <c>[</c> or <c>{</c>.</param>
/// <param name="Content">The content, including whitespace.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="tokens"/>
/// </example>
public sealed record class CssSimpleBlock(char OpenToken, IReadOnlyList<CssComponentValue> Content) : CssComponentValue
{
    /// <inheritdoc/>
    public override int Line => StartLine;

    /// <inheritdoc/>
    public override int Column => StartColumn;

    internal int StartLine { get; init; }

    internal int StartColumn { get; init; }

    /// <summary>Gets the closing character: <c>)</c>, <c>]</c> or <c>}</c>.</summary>
    public char CloseToken => OpenToken switch { '(' => ')', '[' => ']', _ => '}' };

    /// <inheritdoc/>
    public bool Equals(CssSimpleBlock? other) => other is not null && OpenToken == other.OpenToken && CssSerializer.SequenceEqual(Content, other.Content);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(OpenToken, Content.Count);
}
