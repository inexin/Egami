using System.Globalization;
using System.Numerics;

using Egami.Cli.Host;
using Egami.Colors;
using Egami.Css;
using Egami.Pixels;

namespace Egami.Cli.Steps;

/// <summary>
/// Parses option values as the command line spells them: invariant numbers, CSS colors, <c>x,y</c> points, <c>x,y,w,h</c>
/// rectangles, <c>WxH</c> sizes, kebab-case choices, profile names and image paths. Every parser throws
/// <see cref="UsageException"/> with a message naming the value.
/// </summary>
internal static class ValueParsers
{
    private static readonly AsyncLocal<CliSession?> CurrentSession = new();

    /// <summary>Gets or sets the session that image values load through (its context, limits and working folder).</summary>
    public static CliSession? Session
    {
        get => CurrentSession.Value;
        set => CurrentSession.Value = value;
    }

    public static object Int(string text) => ParseInt(text);

    public static object Long(string text)
    {
        return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value)
            ? value
            : throw new UsageException($"'{text}' is not a whole number.");
    }

    public static object Byte(string text)
    {
        return byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte value)
            ? value
            : throw new UsageException($"'{text}' is not a whole number from 0 to 255.");
    }

    public static object ULong(string text)
    {
        return ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong value)
            ? value
            : throw new UsageException($"'{text}' is not a whole number of 0 or more.");
    }

    public static object Float(string text) => (float)ParseDouble(text);

    public static object Double(string text) => ParseDouble(text);

    public static object Bool(string text)
    {
        return text.ToLowerInvariant() switch
        {
            "true" or "yes" or "on" or "1" => true,
            "false" or "no" or "off" or "0" => false,
            _ => throw new UsageException($"'{text}' is not true or false."),
        };
    }

    public static object Text(string text) => text;

    public static object IntList(string text)
    {
        return text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(ParseInt).ToArray();
    }

    /// <summary>A CSS color: a name (<c>white</c>, <c>transparent</c>), <c>#rgb</c>, <c>#rrggbbaa</c>, <c>rgb(…)</c> or <c>hsl(…)</c>.</summary>
    public static object Color(string text) => ParseColor(text);

    public static object Point(string text)
    {
        int[] parts = Numbers(text, 2, "x,y");
        return new Point(parts[0], parts[1]);
    }

    public static object Rect(string text) => ParseRect(text);

    public static object Size(string text)
    {
        int x = text.IndexOfAny(['x', 'X']);
        if (x <= 0 || !int.TryParse(text.AsSpan(0, x), NumberStyles.Integer, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(text.AsSpan(x + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int height))
        {
            throw new UsageException($"'{text}' is not a size; write it as WxH, such as 640x480.");
        }

        return new Size(width, height);
    }

    public static object Vector2(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
        {
            throw new UsageException($"'{text}' is not a point; write it as x,y.");
        }

        return new Vector2((float)ParseDouble(parts[0]), (float)ParseDouble(parts[1]));
    }

    /// <summary>A color profile by name (<c>srgb</c>, <c>display-p3</c>, <c>adobe-rgb</c> …) or the path of an ICC file.</summary>
    public static object Profile(string text) => ParseProfile(text);

    /// <summary>An image file, loaded through the session's context so SVG and metafiles work and the limits apply.</summary>
    public static object Image(string text)
    {
        CliSession session = Session ?? throw new InvalidOperationException("No session to load images with.");
        return session.LoadImage(text);
    }

    /// <summary>One of a set of names, matched without case and ignoring dashes and underscores.</summary>
    public static long Choice(string text, string[] names, long[] values)
    {
        string key = Normalize(text);
        for (int i = 0; i < names.Length; i++)
        {
            if (Normalize(names[i]) == key)
            {
                return values[i];
            }
        }

        throw new UsageException($"'{text}' is not one of {string.Join(", ", names)}.");
    }

    /// <summary>A comma-separated set of flag names, combined.</summary>
    public static long Flags(string text, string[] names, long[] values)
    {
        long result = 0;
        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            result |= Choice(part, names, values);
        }

        return result;
    }

    /// <summary>An enum value by its kebab-case or C# name.</summary>
    public static T Enum<T>(string text)
        where T : struct, Enum
    {
        string key = Normalize(text);
        foreach (T value in System.Enum.GetValues<T>())
        {
            if (Normalize(value.ToString()) == key)
            {
                return value;
            }
        }

        throw new UsageException($"'{text}' is not one of {string.Join(", ", System.Enum.GetNames<T>().Select(Kebab))}.");
    }

    public static int ParseInt(string text)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : throw new UsageException($"'{text}' is not a whole number.");
    }

    public static double ParseDouble(string text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value
            : throw new UsageException($"'{text}' is not a number.");
    }

    public static Rgba8 ParseColor(string text)
    {
        if (!CssColor.TryParse(text, out CssColor color) || color.IsCurrentColor)
        {
            throw new UsageException($"'{text}' is not a color; use a CSS color such as white, #ff8800 or rgb(0 0 0 / 50%).");
        }

        (byte r, byte g, byte b, byte a) = color.ToRgba8();
        return new Rgba8(r, g, b, a);
    }

    public static Rect ParseRect(string text)
    {
        int[] parts = Numbers(text, 4, "x,y,width,height");
        return new Rect(parts[0], parts[1], parts[2], parts[3]);
    }

    public static ColorProfile ParseProfile(string text)
    {
        return Normalize(text) switch
        {
            "srgb" => ColorProfile.Srgb,
            "linearsrgb" => ColorProfile.LinearSrgb,
            "displayp3" or "p3" => ColorProfile.DisplayP3,
            "adobergb" => ColorProfile.AdobeRgb,
            "prophotorgb" or "prophoto" => ColorProfile.ProPhotoRgb,
            "rec2020" => ColorProfile.Rec2020,
            "rec2100pq" or "pq" => ColorProfile.Rec2100Pq,
            "rec2100hlg" or "hlg" => ColorProfile.Rec2100Hlg,
            "gray" or "graysrgb" => ColorProfile.GraySrgb,
            "graylinear" => ColorProfile.GrayLinear,
            "fogra39" => ColorProfile.Fogra39,
            _ => LoadProfile(text),
        };
    }

    /// <summary>The names <see cref="ParseProfile"/> knows.</summary>
    public static IReadOnlyList<string> ProfileNames { get; } =
        ["srgb", "linear-srgb", "display-p3", "adobe-rgb", "pro-photo-rgb", "rec2020", "rec2100-pq", "rec2100-hlg", "gray", "gray-linear", "fogra39"];

    /// <summary>Turns <c>TopLeft</c> into <c>top-left</c>.</summary>
    public static string Kebab(string name)
    {
        System.Text.StringBuilder text = new(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                bool afterLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                bool acronymEnd = i > 0 && char.IsUpper(name[i - 1]) && i + 1 < name.Length && char.IsLower(name[i + 1]);
                if (afterLower || acronymEnd)
                {
                    text.Append('-');
                }

                text.Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    private static ColorProfile LoadProfile(string path)
    {
        string full = Session?.ResolvePath(path) ?? path;
        if (!File.Exists(full))
        {
            throw new UsageException($"'{path}' is neither a profile name ({string.Join(", ", ProfileNames)}) nor an ICC file.");
        }

        return ColorProfile.FromIcc(File.ReadAllBytes(full));
    }

    private static int[] Numbers(string text, int count, string shape)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != count)
        {
            throw new UsageException($"'{text}' needs {count} whole numbers: {shape}.");
        }

        return parts.Select(ParseInt).ToArray();
    }

    private static string Normalize(string text)
    {
        return text.Replace("-", string.Empty, StringComparison.Ordinal).Replace("_", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
    }
}
