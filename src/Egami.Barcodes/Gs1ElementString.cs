using System.Diagnostics.CodeAnalysis;

using Egami.Barcodes.Gs1Data;

namespace Egami.Barcodes;

/// <summary>
/// One GS1 element string: an Application Identifier and its data field, checked against the GS1 General Specifications
/// (characters, lengths, check digits, dates and times). <see cref="Gs1.Parse(string)"/> makes a list of them from text such as
/// <c>(01)09501101530003(17)261231</c>, and the GS1 symbols encode that list.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-gs1"/>
/// </example>
public sealed class Gs1ElementString : IEquatable<Gs1ElementString>
{
    private Gs1ElementString(string ai, string value, Gs1AiSpec spec)
    {
        Ai = ai;
        Value = value;
        Spec = spec;
    }

    /// <summary>Gets the Application Identifier, 2 to 4 digits, such as <c>01</c> for a GTIN.</summary>
    public string Ai { get; }

    /// <summary>Gets the data field.</summary>
    public string Value { get; }

    /// <summary>Gets GS1's short title for the AI, such as <c>GTIN</c> or <c>BATCH/LOT</c>.</summary>
    public string Title => Spec.Title;

    /// <summary>Gets a value indicating whether the AI has a predefined length, so needs no FNC1 separator after it.</summary>
    internal bool PredefinedLength => Spec.PredefinedLength;

    private Gs1AiSpec Spec { get; }

    /// <summary>Makes an element string from an AI and its data field, and checks it.</summary>
    /// <param name="ai">The Application Identifier, such as <c>01</c>.</param>
    /// <param name="value">The data field.</param>
    /// <returns>The element string.</returns>
    /// <exception cref="FormatException">GS1 does not define the AI, or the data does not fit it; the message says how.</exception>
    public static Gs1ElementString Create(string ai, string value)
    {
        ArgumentNullException.ThrowIfNull(ai);
        ArgumentNullException.ThrowIfNull(value);
        return TryCreate(ai, value, out Gs1ElementString? element, out string? error) ? element : throw new FormatException(error);
    }

    /// <summary>Returns the element string in its human-readable form, the AI in parentheses: <c>(01)09501101530003</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => $"({Ai}){Value}";

    /// <inheritdoc/>
    public bool Equals(Gs1ElementString? other) => other is not null && Ai == other.Ai && Value == other.Value;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as Gs1ElementString);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Ai, Value);

    internal static bool TryCreate(string ai, string value, [NotNullWhen(true)] out Gs1ElementString? element, [NotNullWhen(false)] out string? error)
    {
        element = null;
        if (Gs1Dictionary.Find(ai) is not Gs1AiSpec spec)
        {
            error = $"({ai}) is not a GS1 Application Identifier.";
            return false;
        }

        error = Gs1Checks.Check(spec, value);
        if (error is not null)
        {
            error = $"({ai}) {spec.Title}: {error}";
            return false;
        }

        element = new Gs1ElementString(ai, value, spec);
        return true;
    }
}
