using System.Globalization;
using System.Text;

using Egami.Cli.Host;

namespace Egami.Cli.Pipeline;

/// <summary>One input of a command: a file, or standard input.</summary>
/// <param name="Path">The full path, or <see langword="null"/> for standard input.</param>
/// <param name="Display">The path as the user wrote it or the glob found it, for messages.</param>
/// <param name="Directory">The folder below the glob's fixed start, with forward slashes and no trailing slash, for <c>{dir}</c>.</param>
/// <param name="Index">The input's position among all inputs, from 0, for <c>{index}</c>.</param>
internal sealed record InputFile(string? Path, string Display, string Directory, int Index)
{
    /// <summary>Gets a value indicating whether the input is standard input.</summary>
    public bool IsStdin => Path is null;

    /// <summary>Gets the file name without its extension, <c>stdin</c> for standard input.</summary>
    public string Name => Path is null ? "stdin" : System.IO.Path.GetFileNameWithoutExtension(Path);

    /// <summary>Gets the extension without its dot, empty for standard input.</summary>
    public string Extension => Path is null ? string.Empty : System.IO.Path.GetExtension(Path).TrimStart('.');
}

/// <summary>
/// An output path that may hold placeholders: <c>{name}</c> (the input's file name without extension), <c>{ext}</c> (its
/// extension), <c>{dir}</c> (its folder below the glob's start), <c>{index}</c> (its position from 0, with an optional zero
/// pad such as <c>{index:000}</c>), <c>{frame}</c> (the frame number, for <c>frames split</c>) and <c>{format}</c> (the output
/// format's extension). <c>{{</c> and <c>}}</c> write braces.
/// </summary>
internal sealed class OutputTemplate
{
    private static readonly string[] Placeholders = ["name", "ext", "dir", "index", "frame", "format"];

    private readonly List<(string? Literal, string? Name, string? Pad)> _parts;

    private OutputTemplate(string text, List<(string? Literal, string? Name, string? Pad)> parts)
    {
        Text = text;
        _parts = parts;
    }

    /// <summary>Gets the template as written.</summary>
    public string Text { get; }

    /// <summary>Gets a value indicating whether the template writes to standard output.</summary>
    public bool IsStdout => Text == "-";

    /// <summary>Gets a value indicating whether the template has any placeholder.</summary>
    public bool HasPlaceholders => _parts.Any(p => p.Name is not null);

    /// <summary>Gets a value indicating whether the template differs per input (it names the input or its index).</summary>
    public bool IsPerInput => _parts.Any(p => p.Name is "name" or "index" or "dir");

    /// <summary>Gets a value indicating whether the template uses <c>{frame}</c> or <c>{index}</c>, so it names one file per frame.</summary>
    public bool NamesFrames => _parts.Any(p => p.Name is "frame" or "index");

    /// <summary>Parses a template; throws <see cref="UsageException"/> on an unknown placeholder or an unclosed brace.</summary>
    public static OutputTemplate Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        List<(string?, string?, string?)> parts = [];
        StringBuilder literal = new();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '{' && i + 1 < text.Length && text[i + 1] == '{')
            {
                literal.Append('{');
                i++;
            }
            else if (c == '}' && i + 1 < text.Length && text[i + 1] == '}')
            {
                literal.Append('}');
                i++;
            }
            else if (c == '{')
            {
                int end = text.IndexOf('}', i + 1);
                if (end < 0)
                {
                    throw new UsageException($"The output '{text}' has a '{{' with no '}}'; write {{{{ for a brace.");
                }

                string inner = text[(i + 1)..end];
                int colon = inner.IndexOf(':', StringComparison.Ordinal);
                string name = (colon < 0 ? inner : inner[..colon]).Trim().ToLowerInvariant();
                string? pad = colon < 0 ? null : inner[(colon + 1)..];
                if (!Placeholders.Contains(name))
                {
                    throw new UsageException($"The output '{text}' has an unknown placeholder {{{inner}}}; use {string.Join(", ", Placeholders.Select(p => "{" + p + "}"))}.");
                }

                if (pad is not null && (name is not ("index" or "frame") || pad.Length == 0 || pad.Length > 9 || pad.Any(ch => ch != '0')))
                {
                    throw new UsageException($"The output '{text}' pads {{{inner}}} wrongly; only {{index}} and {{frame}} take a pad of zeros, such as {{index:000}}.");
                }

                if (literal.Length > 0)
                {
                    parts.Add((literal.ToString(), null, null));
                    literal.Clear();
                }

                parts.Add((null, name, pad));
                i = end;
            }
            else if (c == '}')
            {
                throw new UsageException($"The output '{text}' has a '}}' with no '{{'; write }}}} for a brace.");
            }
            else
            {
                literal.Append(c);
            }
        }

        if (literal.Length > 0)
        {
            parts.Add((literal.ToString(), null, null));
        }

        return new OutputTemplate(text, parts);
    }

    /// <summary>The path for one input (and frame), with the output format's extension for <c>{format}</c>.</summary>
    public string Resolve(InputFile input, string formatExtension, int frame = 0)
    {
        StringBuilder text = new();
        bool emptyDir = false;
        foreach ((string? literal, string? name, string? pad) in _parts)
        {
            if (literal is not null)
            {
                // An empty {dir} takes the separator after it along, so "{dir}/{name}" never starts at the root.
                text.Append(emptyDir && literal[0] is '/' or '\\' ? literal[1..] : literal);
                emptyDir = false;
                continue;
            }

            emptyDir = name == "dir" && input.Directory.Length == 0;
            switch (name)
            {
                case "name": text.Append(input.Name); break;
                case "ext": text.Append(input.Extension); break;
                case "dir": text.Append(input.Directory); break;
                case "format": text.Append(formatExtension); break;
                case "index": text.Append(input.Index.ToString(pad is null ? "0" : pad, CultureInfo.InvariantCulture)); break;
                default: text.Append(frame.ToString(pad is null ? "0" : pad, CultureInfo.InvariantCulture)); break;
            }
        }

        return text.ToString();
    }
}
