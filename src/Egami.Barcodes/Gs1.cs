using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Egami.Barcodes;

/// <summary>
/// Reads GS1 element strings from their human-readable form, such as <c>(01)09501101530003(17)261231(10)AB12</c>, and checks each
/// against the GS1 General Specifications: the AI is defined, the characters and lengths fit it, and check digits, dates and times
/// are right. GS1-128, GS1 Data Matrix and GS1 QR encode the result.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Barcodes.cs" region="barcode-gs1"/>
/// </example>
public static class Gs1
{
    /// <summary>
    /// Reads element strings written with each AI in parentheses. A parenthesis inside a data field is written with a backslash
    /// before it, <c>\(</c>.
    /// </summary>
    /// <param name="text">The element strings, such as <c>(01)09501101530003(10)AB12</c>.</param>
    /// <returns>The element strings in order.</returns>
    /// <exception cref="FormatException">The text is not element strings, or one does not fit its AI; the message says which and how.</exception>
    public static IReadOnlyList<Gs1ElementString> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return TryParse(text, out IReadOnlyList<Gs1ElementString>? elements, out string? error) ? elements : throw new FormatException(error);
    }

    /// <summary>Reads element strings written with each AI in parentheses, without throwing.</summary>
    /// <param name="text">The element strings, such as <c>(01)09501101530003(10)AB12</c>.</param>
    /// <param name="elements">The element strings in order, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the text holds valid element strings.</returns>
    public static bool TryParse(string? text, [NotNullWhen(true)] out IReadOnlyList<Gs1ElementString>? elements) =>
        TryParse(text, out elements, out _);

    internal static bool TryParse(string? text, [NotNullWhen(true)] out IReadOnlyList<Gs1ElementString>? elements, [NotNullWhen(false)] out string? error)
    {
        elements = null;
        if (string.IsNullOrEmpty(text))
        {
            error = "There are no element strings.";
            return false;
        }

        var list = new List<Gs1ElementString>();
        var value = new StringBuilder();
        int at = 0;
        while (at < text.Length)
        {
            int close = text.IndexOf(')', at);
            if (text[at] != '(' || close < 0)
            {
                error = $"Expected an AI in parentheses at position {at + 1}.";
                return false;
            }

            string ai = text[(at + 1)..close];
            if (ai.Length is < 2 or > 4 || !ai.All(char.IsAsciiDigit))
            {
                error = $"'({ai})' at position {at + 1} is not an AI of 2 to 4 digits.";
                return false;
            }

            value.Clear();
            at = close + 1;
            while (at < text.Length && text[at] != '(')
            {
                if (text[at] == '\\' && at + 1 < text.Length && text[at + 1] is '(' or ')' or '\\')
                {
                    at++;
                }

                value.Append(text[at++]);
            }

            if (!Gs1ElementString.TryCreate(ai, value.ToString(), out Gs1ElementString? element, out error))
            {
                return false;
            }

            list.Add(element);
        }

        elements = list;
        error = null;
        return true;
    }
}
