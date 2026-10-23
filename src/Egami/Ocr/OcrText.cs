using System.Globalization;
using System.Text;

namespace Egami.Ocr;

/// <summary>Helpers the hOCR and ALTO readers and writers share.</summary>
internal static class OcrText
{
    /// <summary>Reads the whole stream as text (UTF-8 unless a byte order mark says otherwise), at most <paramref name="maxBytes"/> bytes.</summary>
    public static string ReadAll(Stream stream, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using MemoryStream copy = new();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (copy.Length + read > maxBytes)
            {
                throw new LimitExceededException(nameof(OcrReadOptions.MaxBytes), maxBytes, copy.Length + read);
            }

            copy.Write(buffer, 0, read);
        }

        copy.Position = 0;
        using StreamReader reader = new(copy, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Trims the text and turns each run of white space into one space.</summary>
    public static string Collapse(ReadOnlySpan<char> text)
    {
        StringBuilder result = new(text.Length);
        bool space = false;
        foreach (char c in text.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                space = true;
                continue;
            }

            if (space)
            {
                result.Append(' ');
                space = false;
            }

            result.Append(c);
        }

        return result.ToString();
    }

    /// <summary>Replaces the characters XML cannot hold (controls, lone surrogates) with U+FFFD.</summary>
    public static string ForXml(string text)
    {
        StringBuilder? result = null;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            bool valid;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                result?.Append(c).Append(text[i + 1]);
                i++;
                continue;
            }

            valid = c is '\t' or '\n' or '\r' || (c >= 0x20 && c < 0xD800) || (c >= 0xE000 && c < 0xFFFE);
            if (!valid && result is null)
            {
                result = new StringBuilder(text.Length).Append(text, 0, i);
            }

            result?.Append(valid ? c : '�');
        }

        return result?.ToString() ?? text;
    }

    /// <summary>Formats a number for a file: invariant, no exponent, at most three decimals.</summary>
    public static string Number(double value)
    {
        return Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Parses an invariant number, or returns <see langword="null"/>.</summary>
    public static double? ParseNumber(ReadOnlySpan<char> text)
    {
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value) ? value : null;
    }

    /// <summary>Returns whether a position from a file is a usable pixel coordinate: finite and within ten million pixels of the origin.</summary>
    public static bool IsCoordinate(double value)
    {
        return Math.Abs(value) <= 10_000_000;
    }

    /// <summary>Returns the bounds of every word on the page, as a size from the origin, for pages that do not give theirs.</summary>
    public static Size SizeOf(OcrResult page)
    {
        if (page.ImageSize.Width > 0 && page.ImageSize.Height > 0)
        {
            return page.ImageSize;
        }

        int width = 0, height = 0;
        foreach (OcrWord word in page.Words)
        {
            Rect box = word.Box.Bounds;
            width = Math.Max(width, box.X + box.Width);
            height = Math.Max(height, box.Y + box.Height);
        }

        return new Size(width, height);
    }
}
