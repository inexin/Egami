using System.Globalization;

using Egami.Cli.Host;

namespace Egami.Cli.Steps;

/// <summary>How a resize fills its box (CLI design Q7).</summary>
internal enum FitMode
{
    /// <summary>Fit inside the box, aspect ratio kept.</summary>
    Contain,

    /// <summary>Fill the box, aspect ratio kept, and crop what spills over.</summary>
    Cover,

    /// <summary>Stretch to the box exactly.</summary>
    Fill,

    /// <summary>Fit inside the box, then pad to it.</summary>
    Pad,
}

/// <summary>
/// The size syntax, the common subset of ImageMagick geometry: <c>W</c>, <c>xH</c>, <c>WxH</c>, <c>P%</c>, <c>P%xQ%</c>, and the
/// flags <c>!</c> (ignore the aspect ratio), <c>^</c> (fill the box, aspect kept, no crop), <c>&gt;</c> (only shrink) and
/// <c>&lt;</c> (only enlarge).
/// </summary>
/// <param name="Width">The width, or <see langword="null"/>.</param>
/// <param name="WidthPercent">Whether the width is a percentage.</param>
/// <param name="Height">The height, or <see langword="null"/>.</param>
/// <param name="HeightPercent">Whether the height is a percentage.</param>
/// <param name="Exact">The <c>!</c> flag.</param>
/// <param name="Minimum">The <c>^</c> flag.</param>
/// <param name="OnlyShrink">The <c>&gt;</c> flag.</param>
/// <param name="OnlyEnlarge">The <c>&lt;</c> flag.</param>
internal sealed record SizeSpec(double? Width, bool WidthPercent, double? Height, bool HeightPercent, bool Exact, bool Minimum, bool OnlyShrink, bool OnlyEnlarge)
{
    /// <summary>Parses the size syntax; throws <see cref="UsageException"/> with what is wrong.</summary>
    public static SizeSpec Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string body = text.Trim();
        bool exact = false, minimum = false, shrink = false, enlarge = false;
        while (body.Length > 0 && body[^1] is '!' or '^' or '<' or '>')
        {
            switch (body[^1])
            {
                case '!': exact = true; break;
                case '^': minimum = true; break;
                case '>': shrink = true; break;
                default: enlarge = true; break;
            }

            body = body[..^1];
        }

        if (shrink && enlarge)
        {
            throw Invalid(text, "> and < cannot be used together");
        }

        if (exact && minimum)
        {
            throw Invalid(text, "! and ^ cannot be used together");
        }

        int x = body.IndexOfAny(['x', 'X']);
        string widthText = x < 0 ? body : body[..x];
        string? heightText = x < 0 ? null : body[(x + 1)..];
        (double? width, bool widthPercent) = Dimension(text, widthText);
        (double? height, bool heightPercent) = heightText is null ? (null, false) : Dimension(text, heightText);
        if (width is null && height is null)
        {
            throw Invalid(text, "give a width, a height or both");
        }

        if (heightText is null && widthPercent)
        {
            (height, heightPercent) = (width, true);
        }

        return new SizeSpec(width, widthPercent, height, heightPercent, exact, minimum, shrink, enlarge);
    }

    /// <summary>The size the image gets, from its size before the resize and the fit mode.</summary>
    public Size Target(Size source, FitMode fit)
    {
        double sw = source.Width, sh = source.Height;
        double? w = Width is { } wv ? (WidthPercent ? sw * wv / 100 : wv) : null;
        double? h = Height is { } hv ? (HeightPercent ? sh * hv / 100 : hv) : null;
        double tw, th;
        if (WidthPercent || HeightPercent || Exact || fit == FitMode.Fill)
        {
            tw = w ?? sw * h!.Value / sh;
            th = h ?? sh * w!.Value / sw;
        }
        else if (w is null || h is null)
        {
            double scale = w is { } only ? only / sw : h!.Value / sh;
            tw = sw * scale;
            th = sh * scale;
        }
        else if (fit is FitMode.Cover or FitMode.Pad)
        {
            tw = w.Value;
            th = h.Value;
        }
        else
        {
            double scale = Minimum ? Math.Max(w.Value / sw, h.Value / sh) : Math.Min(w.Value / sw, h.Value / sh);
            tw = sw * scale;
            th = sh * scale;
        }

        int width = Math.Max(1, (int)Math.Round(tw, MidpointRounding.AwayFromZero));
        int height = Math.Max(1, (int)Math.Round(th, MidpointRounding.AwayFromZero));
        bool larger = width > source.Width || height > source.Height;
        bool smaller = width < source.Width || height < source.Height;
        if ((OnlyShrink && !smaller) || (OnlyEnlarge && !larger))
        {
            return source;
        }

        return new Size(width, height);
    }

    /// <summary>The size as the syntax writes it.</summary>
    public override string ToString()
    {
        if (WidthPercent && HeightPercent && Width == Height)
        {
            return Width!.Value.ToString(CultureInfo.InvariantCulture) + "%" + Flags();
        }

        string w = Width is { } wv ? wv.ToString(CultureInfo.InvariantCulture) + (WidthPercent ? "%" : string.Empty) : string.Empty;
        string h = Height is { } hv ? "x" + hv.ToString(CultureInfo.InvariantCulture) + (HeightPercent ? "%" : string.Empty) : string.Empty;
        return w + h + Flags();
    }

    private string Flags() => (Exact ? "!" : string.Empty) + (Minimum ? "^" : string.Empty) + (OnlyShrink ? ">" : string.Empty) + (OnlyEnlarge ? "<" : string.Empty);

    private static (double? Value, bool Percent) Dimension(string text, string part)
    {
        if (part.Length == 0)
        {
            return (null, false);
        }

        bool percent = part.EndsWith('%');
        string number = percent ? part[..^1] : part;
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double value) || value <= 0 || !double.IsFinite(value)
            || (!percent && (value != Math.Floor(value) || value > 1_000_000)) || (percent && value > 100_000))
        {
            throw Invalid(text, $"'{part}' is not a size");
        }

        return (value, percent);
    }

    private static UsageException Invalid(string text, string problem)
    {
        return new UsageException($"'{text}' is not a valid size ({problem}). Write W, xH, WxH or P%, optionally followed by !, ^, > or <.");
    }
}
