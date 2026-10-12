using System.Globalization;
using System.Text;

namespace Egami.Css;

/// <summary>The kinds of token the CSS Syntax 3 tokenizer produces.</summary>
public enum CssTokenKind
{
    /// <summary>An identifier, such as <c>red</c> or <c>--x</c>.</summary>
    Ident,

    /// <summary>A function name with its opening parenthesis, such as <c>rgb(</c>.</summary>
    Function,

    /// <summary>An at-keyword, such as <c>@media</c>.</summary>
    AtKeyword,

    /// <summary>A hash, such as <c>#fff</c> or <c>#main</c>.</summary>
    Hash,

    /// <summary>A quoted string.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720", Justification = "The CSS Syntax token's name.")]
    String,

    /// <summary>A string cut off by a newline.</summary>
    BadString,

    /// <summary>An unquoted <c>url(...)</c>.</summary>
    Url,

    /// <summary>An unquoted <c>url(...)</c> with characters it may not contain.</summary>
    BadUrl,

    /// <summary>Any other single character, such as <c>*</c> or <c>&gt;</c>.</summary>
    Delim,

    /// <summary>A number.</summary>
    Number,

    /// <summary>A number followed by <c>%</c>.</summary>
    Percentage,

    /// <summary>A number followed by a unit, such as <c>12px</c>.</summary>
    Dimension,

    /// <summary>One or more whitespace characters.</summary>
    Whitespace,

    /// <summary><c>&lt;!--</c>.</summary>
    Cdo,

    /// <summary><c>--&gt;</c>.</summary>
    Cdc,

    /// <summary><c>:</c>.</summary>
    Colon,

    /// <summary><c>;</c>.</summary>
    Semicolon,

    /// <summary><c>,</c>.</summary>
    Comma,

    /// <summary><c>[</c>.</summary>
    LeftSquare,

    /// <summary><c>]</c>.</summary>
    RightSquare,

    /// <summary><c>(</c>.</summary>
    LeftParenthesis,

    /// <summary><c>)</c>.</summary>
    RightParenthesis,

    /// <summary><c>{</c>.</summary>
    LeftCurly,

    /// <summary><c>}</c>.</summary>
    RightCurly,

    /// <summary>The end of the input. <see cref="CssTokenizer.Tokenize(string, CssParseOptions?)"/> does not return it.</summary>
    EndOfFile,
}

/// <summary>One CSS token, with its position in the source.</summary>
/// <remarks>
/// Two tokens are equal when they have the same kind, value, number, unit and flags; their positions are not compared.
/// </remarks>
public readonly struct CssToken : IEquatable<CssToken>
{
    private readonly string? _value;
    private readonly string? _unit;

    /// <summary>Creates a token.</summary>
    /// <param name="kind">The kind of token.</param>
    /// <param name="value">The value: see <see cref="Value"/>.</param>
    /// <param name="number">The numeric value of a number, percentage or dimension.</param>
    /// <param name="unit">The unit of a dimension.</param>
    /// <param name="isInteger">Whether a number, percentage or dimension was written as an integer.</param>
    /// <param name="isIdHash">Whether a hash would be a valid identifier.</param>
    /// <param name="line">The 1-based source line, or 0 when unknown.</param>
    /// <param name="column">The 1-based source column, or 0 when unknown.</param>
    public CssToken(CssTokenKind kind, string? value = null, double number = 0, string? unit = null, bool isInteger = false, bool isIdHash = false, int line = 0, int column = 0)
    {
        Kind = kind;
        _value = value;
        Number = number;
        _unit = unit;
        IsInteger = isInteger;
        IsIdHash = isIdHash;
        Line = line;
        Column = column;
    }

    /// <summary>Gets the kind of token.</summary>
    public CssTokenKind Kind { get; }

    /// <summary>
    /// Gets the token's value, with escapes resolved: the name of an identifier, function, at-keyword or hash; the text of a
    /// string or url; the character of a delim; and the number as written (such as <c>+1.50</c>) for a number, percentage or
    /// dimension. Empty for other kinds.
    /// </summary>
    public string Value => _value ?? "";

    /// <summary>Gets the numeric value of a number, percentage or dimension.</summary>
    public double Number { get; }

    /// <summary>Gets the unit of a dimension, with escapes resolved, or an empty string.</summary>
    public string Unit => _unit ?? "";

    /// <summary>Gets whether a number, percentage or dimension was written without a fraction or exponent.</summary>
    public bool IsInteger { get; }

    /// <summary>Gets whether a hash has the type "id", that is, its name would be a valid identifier.</summary>
    public bool IsIdHash { get; }

    /// <summary>Gets the 1-based line the token starts on, or 0 when unknown.</summary>
    public int Line { get; }

    /// <summary>Gets the 1-based column the token starts at, in UTF-16 code units, or 0 when unknown.</summary>
    public int Column { get; }

    /// <summary>Gets whether this is a delim token for <paramref name="c"/>.</summary>
    /// <param name="c">The character.</param>
    /// <returns>True when the token is that delim.</returns>
    public bool IsDelim(char c) => Kind == CssTokenKind.Delim && Value.Length == 1 && Value[0] == c;

    /// <summary>Gets whether this is an identifier equal to <paramref name="name"/>, compared ASCII case-insensitively.</summary>
    /// <param name="name">The name, in lowercase.</param>
    /// <returns>True when the token is that identifier.</returns>
    public bool IsIdent(string name) => Kind == CssTokenKind.Ident && CssText.EqualsIgnoreAsciiCase(Value, name);

    /// <inheritdoc/>
    public bool Equals(CssToken other)
    {
        return Kind == other.Kind && Value == other.Value && Number.Equals(other.Number) && Unit == other.Unit && IsInteger == other.IsInteger && IsIdHash == other.IsIdHash;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is CssToken other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Kind, Value, Number, Unit);

    /// <summary>Compares two tokens.</summary>
    /// <param name="left">The first token.</param>
    /// <param name="right">The second token.</param>
    /// <returns>True when they are equal.</returns>
    public static bool operator ==(CssToken left, CssToken right) => left.Equals(right);

    /// <summary>Compares two tokens.</summary>
    /// <param name="left">The first token.</param>
    /// <param name="right">The second token.</param>
    /// <returns>True when they differ.</returns>
    public static bool operator !=(CssToken left, CssToken right) => !left.Equals(right);

    /// <summary>Serializes the token as CSS text.</summary>
    /// <returns>The CSS text.</returns>
    public override string ToString()
    {
        StringBuilder builder = new();
        CssSerializer.WriteToken(builder, this);
        return builder.ToString();
    }

    internal string NumberText => _value is { Length: > 0 } ? _value : Number.ToString("R", CultureInfo.InvariantCulture);
}
