using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Egami.Cli.Hosting;

/// <summary>Writes indented JSON to standard output with <see cref="Utf8JsonWriter"/>, so no reflection is needed.</summary>
internal static class JsonOut
{
    private static readonly JsonWriterOptions Options = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Writes one JSON document to a text writer, ending with a newline.</summary>
    public static void Write(TextWriter output, Action<Utf8JsonWriter> write)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, Options))
        {
            write(writer);
        }

        output.Write(Encoding.UTF8.GetString(buffer.ToArray()).Replace("\r\n", "\n", StringComparison.Ordinal));
        output.Write('\n');
    }

    /// <summary>Writes a number property, or null.</summary>
    public static void Number(this Utf8JsonWriter writer, string name, double? value)
    {
        if (value is { } number && double.IsFinite(number))
        {
            writer.WriteNumber(name, number);
        }
        else
        {
            writer.WriteNull(name);
        }
    }

    /// <summary>Writes a string property, or null.</summary>
    public static void Text(this Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
