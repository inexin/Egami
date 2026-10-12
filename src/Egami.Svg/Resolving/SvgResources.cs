using System.Text;

namespace Egami.Svg.Resolving;

/// <summary>
/// Opens what a document refers to (question 6): data: URIs (base64 or percent-encoded) always; any other URI, resolved
/// against the base URI, only through <see cref="SvgLoadOptions.Resolver"/>. Egami never opens a file or a connection itself.
/// What a resolver returns is read up to the context's MaxTotalBytes.
/// </summary>
internal static class SvgResources
{
    public static byte[]? Open(string href, SvgLoadOptions options, out Uri? location, out string? problem)
    {
        location = null;
        problem = null;
        string trimmed = href.Trim();
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            byte[]? data = DataUri(trimmed, options.EffectiveContext.Limits.MaxTotalBytes);
            problem = data is null ? "the data: URI cannot be read" : null;
            return data;
        }

        if (options.Resolver is not Func<Uri, Stream?> resolver)
        {
            problem = "no resolver allows files outside the document (SvgLoadOptions.Resolver)";
            return null;
        }

        if (!Uri.TryCreate(trimmed, UriKind.RelativeOrAbsolute, out Uri? uri))
        {
            problem = "it is not a URI";
            return null;
        }

        if (!uri.IsAbsoluteUri)
        {
            if (options.BaseUri is not Uri baseUri || !Uri.TryCreate(baseUri, uri, out uri))
            {
                problem = "it is relative and the document has no base URI";
                return null;
            }
        }

        location = uri;
        Stream? stream;
        try
        {
            stream = resolver(uri);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            problem = "the resolver could not open it: " + e.Message;
            return null;
        }

        if (stream is null)
        {
            problem = "the resolver refused it";
            return null;
        }

        using (stream)
        {
            long limit = options.EffectiveContext.Limits.MaxTotalBytes;
            using MemoryStream copy = new();
            byte[] buffer = new byte[81920];
            int n;
            while ((n = stream.Read(buffer)) > 0)
            {
                if (copy.Length + n > limit)
                {
                    throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limit, copy.Length + n);
                }

                copy.Write(buffer, 0, n);
            }

            return copy.ToArray();
        }
    }

    /// <summary>Decodes a data: URI's bytes: base64 when marked so (whitespace allowed), else percent-encoded text.</summary>
    public static byte[]? DataUri(string uri, long limit)
    {
        int comma = uri.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0)
        {
            return null;
        }

        ReadOnlySpan<char> header = uri.AsSpan(5, comma - 5);
        ReadOnlySpan<char> body = uri.AsSpan(comma + 1);
        if (body.Length > limit * 2)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limit, body.Length / 2);
        }

        if (header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase))
        {
            StringBuilder clean = new(body.Length);
            foreach (char c in body)
            {
                if (!char.IsWhiteSpace(c))
                {
                    clean.Append(c);
                }
            }

            // Percent-escaped base64 (%2B for +) shows up in some files.
            string text = clean.ToString();
            if (text.Contains('%', StringComparison.Ordinal))
            {
                text = Uri.UnescapeDataString(text);
            }

            int padding = text.Length % 4;
            if (padding != 0)
            {
                text += new string('=', 4 - padding);
            }

            byte[] result = new byte[text.Length * 3 / 4];
            return Convert.TryFromBase64String(text, result, out int written) ? result[..written] : null;
        }

        return Encoding.UTF8.GetBytes(Uri.UnescapeDataString(body.ToString()));
    }

    /// <summary>Whether bytes look like SVG (markup, or gzip, which raster formats never start with) rather than a raster.</summary>
    public static bool LooksLikeSvg(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            return true;
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            data = data[3..];
        }

        foreach (byte b in data)
        {
            if (b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            {
                continue;
            }

            return b == (byte)'<';
        }

        return false;
    }
}
