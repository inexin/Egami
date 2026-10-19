using System.Globalization;

namespace Egami.Barcodes.Gs1Data;

/// <summary>
/// The GS1 Application Identifiers, read once from the GS1 Barcode Syntax Dictionary (gs1-syntax-dictionary.txt, Apache-2.0,
/// embedded as it is published): each AI's data components, their types, lengths and checks, and whether the AI has a
/// predefined length, so needs no FNC1 after it.
/// </summary>
internal static class Gs1Dictionary
{
    private static readonly Lazy<Dictionary<string, Gs1AiSpec>> s_entries = new(Load);

    /// <summary>Returns the AI's specification, or <see langword="null"/> for an AI GS1 does not define.</summary>
    public static Gs1AiSpec? Find(string ai) => s_entries.Value.GetValueOrDefault(ai);

    private static Dictionary<string, Gs1AiSpec> Load()
    {
        using Stream stream = typeof(Gs1Dictionary).Assembly.GetManifestResourceStream("Egami.Barcodes.gs1-syntax-dictionary.txt")
            ?? throw new InvalidOperationException("The GS1 syntax dictionary is missing from the assembly.");
        using var reader = new StreamReader(stream);
        var entries = new Dictionary<string, Gs1AiSpec>(StringComparer.Ordinal);
        while (reader.ReadLine() is string line)
        {
            int hash = line.IndexOf('#', StringComparison.Ordinal);
            string[] tokens = (hash >= 0 ? line[..hash] : line).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length < 2)
            {
                continue;
            }

            string title = hash >= 0 ? line[(hash + 1)..].Trim() : "";
            int next = 1;
            bool predefinedLength = false;
            if (tokens[1].All(c => !char.IsAsciiLetterOrDigit(c) && c != '['))
            {
                predefinedLength = tokens[1].Contains('*', StringComparison.Ordinal);
                next = 2;
            }

            var components = new List<Gs1Component>();
            for (; next < tokens.Length && Gs1Component.TryParse(tokens[next], out Gs1Component component); next++)
            {
                components.Add(component);
            }

            var spec = new Gs1AiSpec(title, predefinedLength, [.. components]);
            string[] range = tokens[0].Split('-');
            if (range.Length == 1)
            {
                entries[range[0]] = spec;
            }
            else
            {
                for (int ai = int.Parse(range[0], CultureInfo.InvariantCulture); ai <= int.Parse(range[1], CultureInfo.InvariantCulture); ai++)
                {
                    entries[ai.ToString(CultureInfo.InvariantCulture).PadLeft(range[0].Length, '0')] = spec;
                }
            }
        }

        return entries;
    }
}

/// <summary>One AI's data field: its title, whether its length is predefined, and its components in order.</summary>
internal sealed record class Gs1AiSpec(string Title, bool PredefinedLength, Gs1Component[] Components);

/// <summary>
/// One component of an AI's data field: <c>N</c> digits, <c>X</c> GS1 character set 82, <c>Y</c> character set 39 or <c>Z</c>
/// base64url, a fixed length or up to a maximum, optional or not, with the names of the checks (linters) it must pass.
/// </summary>
internal readonly record struct Gs1Component(char Type, int Length, bool Variable, bool Optional, string[] Checks)
{
    public static bool TryParse(string token, out Gs1Component component)
    {
        component = default;
        string[] parts = token.Split(',');
        string type = parts[0];
        bool optional = type.StartsWith('[') && type.EndsWith(']');
        if (optional)
        {
            type = type[1..^1];
        }

        if (type.Length < 2 || type[0] is not ('N' or 'X' or 'Y' or 'Z'))
        {
            return false;
        }

        bool variable = type.AsSpan(1).StartsWith("..", StringComparison.Ordinal);
        if (!int.TryParse(type.AsSpan(variable ? 3 : 1), NumberStyles.None, CultureInfo.InvariantCulture, out int length))
        {
            return false;
        }

        component = new Gs1Component(type[0], length, variable, optional, parts[1..]);
        return true;
    }
}
