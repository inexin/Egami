namespace Egami.Formats.Ccitt;

/// <summary>
/// The parameters of CCITT fax data as a PDF's CCITTFaxDecode filter gives them in its DecodeParms dictionary, with PDF's
/// defaults. One record serves both <see cref="CcittFax.Decode"/> and <see cref="CcittReaderOptions"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ccitt.cs" region="ccitt-filter"/>
/// </example>
public sealed record CcittFaxParameters
{
    /// <summary>
    /// Gets the coding: below 0 pure Group 4 (ITU-T T.6), 0 (the default) one-dimensional Group 3, above 0 mixed Group 3
    /// (ITU-T T.4), where the bit after each EOL says whether the next row is one- or two-dimensional.
    /// </summary>
    public int K { get; init; }

    /// <summary>Gets whether an EOL is required before each row. EOLs are accepted where present either way. Defaults to false.</summary>
    public bool EndOfLine { get; init; }

    /// <summary>
    /// Gets whether rows are byte-aligned: with <see cref="K"/> below 0 each row starts on a byte boundary; otherwise fill bits
    /// before each EOL make it end on one, and a row without an EOL starts on one. Defaults to false.
    /// </summary>
    public bool EncodedByteAlign { get; init; }

    /// <summary>Gets the row width in pixels. Defaults to 1728.</summary>
    public int Columns { get; init; } = 1728;

    /// <summary>
    /// Gets the number of rows, or 0 (the default) to decode until the data or its end-of-block code ends. Rows the data does
    /// not reach are white.
    /// </summary>
    public int Rows { get; init; }

    /// <summary>
    /// Gets whether the data ends with an end-of-block code: RTC (six EOLs) for Group 3, EOFB (two EOLs) for Group 4. Decoding
    /// stops there either way. Defaults to true.
    /// </summary>
    public bool EndOfBlock { get; init; } = true;

    /// <summary>
    /// Gets the bit sense of <see cref="CcittFax.Decode"/>'s output: false (the default) gives 0 for black and 1 for white, as
    /// PDF defines the filter; true gives 1 for black. <see cref="CcittFormat"/> always loads black as 0 and white as 255.
    /// </summary>
    public bool BlackIs1 { get; init; }

    /// <summary>
    /// Gets how many damaged rows are tolerated when <see cref="EndOfLine"/> is true and <see cref="K"/> is 0 or above: each is
    /// skipped to the next EOL and repeats the row before it. After that, or without EOLs, decoding stops at the first damaged
    /// row, keeps the rows before it, leaves the rest white and warns. Defaults to 0.
    /// </summary>
    public int DamagedRowsBeforeError { get; init; }

    /// <summary>Throws when a value is outside what the parameters can mean.</summary>
    internal void Check(string name)
    {
        if (Columns < 1)
        {
            throw new ArgumentOutOfRangeException(name, Columns, "Columns must be at least 1.");
        }

        if (Rows < 0)
        {
            throw new ArgumentOutOfRangeException(name, Rows, "Rows must be 0 or more.");
        }

        if (DamagedRowsBeforeError < 0)
        {
            throw new ArgumentOutOfRangeException(name, DamagedRowsBeforeError, "DamagedRowsBeforeError must be 0 or more.");
        }
    }
}

/// <summary>
/// Raw CCITT fax decoding as a PDF filter: Group 3 and Group 4 data (ITU-T T.4 and T.6) with PDF's CCITTFaxDecode parameters,
/// byte-aligned Group 4 included, which a TIFF wrapper cannot express. To load the data as an image instead, use
/// <see cref="CcittFormat"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ccitt.cs" region="ccitt-filter"/>
/// </example>
public static class CcittFax
{
    /// <summary>
    /// Decodes CCITT fax data to packed rows, as PDF's CCITTFaxDecode filter gives them: one bit a pixel, most significant bit
    /// first, each row padded to whole bytes with 0 bits, black 0 and white 1 unless <see cref="CcittFaxParameters.BlackIs1"/>.
    /// </summary>
    /// <remarks>
    /// A damaged row ends decoding (after <see cref="CcittFaxParameters.DamagedRowsBeforeError"/> skipped rows, when it
    /// applies): the rows before it are kept, the rest are white, and a warning goes to the context's
    /// <see cref="EgamiContext.Diagnostics"/>. Data that ends before <see cref="CcittFaxParameters.Rows"/> rows also warns.
    /// </remarks>
    /// <param name="data">The coded data, as the stream holds it after the filters before CCITTFaxDecode.</param>
    /// <param name="parameters">The stream's DecodeParms.</param>
    /// <param name="rows">The number of rows returned: <see cref="CcittFaxParameters.Rows"/> when it is above 0, else the rows decoded.</param>
    /// <param name="context">The context whose safety limits and diagnostics apply, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The rows, <c>(Columns + 7) / 8</c> bytes each.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="parameters"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside what it can mean, such as Columns below 1.</exception>
    /// <exception cref="CorruptDataException">Not one row decodes.</exception>
    /// <exception cref="LimitExceededException">The image breaks a safety limit.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Ccitt.cs" region="ccitt-filter"/>
    /// </example>
    public static byte[] Decode(ReadOnlySpan<byte> data, CcittFaxParameters parameters, out int rows, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Check(nameof(parameters));
        context ??= EgamiContext.Default;
        byte[] packed;
        List<(string Id, string Message)> warnings;
        try
        {
            packed = DecodeRows(data, parameters, context.Limits, out rows, out warnings);
        }
        catch (InvalidDataException e)
        {
            throw new CorruptDataException(CcittFormat.Info.Name, 0, e.Message);
        }

        if (!parameters.BlackIs1)
        {
            Invert(packed, parameters.Columns);
        }

        if (warnings.Count > 0)
        {
            using DiagnosticReporter diagnostics = context.StartDiagnostics();
            foreach ((string id, string message) in warnings)
            {
                diagnostics.Report(DiagnosticLevel.Warning, id, message);
            }
        }

        return packed;
    }

    /// <summary>
    /// Decodes to packed rows with black runs as 1 bits: <see cref="CcittFaxParameters.Rows"/> rows when given, the rows not
    /// reached left 0, else the rows decoded. Throws <see cref="InvalidDataException"/> when no row decodes.
    /// </summary>
    internal static byte[] DecodeRows(ReadOnlySpan<byte> data, CcittFaxParameters p, SafetyLimits limits, out int rows, out List<(string Id, string Message)> warnings)
    {
        int rowBytes = (p.Columns + 7) / 8;
        limits.CheckFrameSize(p.Columns, Math.Max(p.Rows, 1));
        long cap = p.Rows > 0 ? p.Rows : MaxRows(p.Columns, limits);
        CcittDecoder decoder = new(p.K, p.EndOfLine, p.EncodedByteAlign, p.Columns, p.Rows, p.DamagedRowsBeforeError);
        byte[] output = new byte[rowBytes * (p.Rows > 0 ? p.Rows : (int)Math.Min(cap, 64))];
        while (true)
        {
            int done = decoder.RowsDone;
            if ((long)(done + 1) * rowBytes > output.Length)
            {
                if (p.Rows > 0 || decoder.Ended)
                {
                    break;
                }

                if (done >= cap)
                {
                    // Rows 0 and the data goes on: the image would break MaxPixels or MaxHeight.
                    limits.CheckFrameSize(p.Columns, done + 1L);
                    throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limits.MaxTotalBytes, (done + 1L) * rowBytes);
                }

                Array.Resize(ref output, (int)(Math.Min(cap, done * 2L) * rowBytes));
            }

            if (!decoder.NextRow(data, output.AsSpan(done * rowBytes, rowBytes)))
            {
                break;
            }
        }

        if (decoder.RowsDone == 0)
        {
            throw new InvalidDataException(decoder.Damage ?? "The CCITT data holds no row.");
        }

        warnings = [];
        if (decoder.SkippedRows > 0)
        {
            warnings.Add(("Ccitt.RowsSkipped", $"{decoder.SkippedRows} damaged rows were skipped to the next EOL, each repeating the row before it."));
        }

        if (decoder.Damage is not null)
        {
            warnings.Add(("Ccitt.Damaged", $"{decoder.Damage} Decoding stopped after {decoder.RowsDone} rows; the rest are white."));
        }
        else if (p.Rows > 0 && decoder.RowsDone < p.Rows)
        {
            warnings.Add(("Ccitt.Truncated", $"The CCITT data ends after {decoder.RowsDone} of {p.Rows} rows; the rest are white."));
        }

        rows = p.Rows > 0 ? p.Rows : decoder.RowsDone;
        if (output.Length != rows * rowBytes)
        {
            Array.Resize(ref output, rows * rowBytes);
        }

        return output;
    }

    /// <summary>The most rows an image of this width may have under the limits, for Rows 0.</summary>
    internal static long MaxRows(int columns, SafetyLimits limits)
    {
        long rowBytes = (columns + 7) / 8;
        return Math.Min(Math.Min(limits.MaxHeight, limits.MaxPixels / columns), Math.Min(limits.MaxTotalBytes, Array.MaxLength) / rowBytes);
    }

    /// <summary>Flips every pixel bit of packed rows, leaving each row's padding bits 0.</summary>
    private static void Invert(byte[] packed, int columns)
    {
        int rowBytes = (columns + 7) / 8;
        byte padding = (byte)(0xFF >> (columns & 7));
        Span<ulong> words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(packed.AsSpan());
        foreach (ref ulong word in words)
        {
            word = ~word;
        }

        for (int i = words.Length * 8; i < packed.Length; i++)
        {
            packed[i] = (byte)~packed[i];
        }

        if ((columns & 7) != 0)
        {
            for (int at = rowBytes - 1; at < packed.Length; at += rowBytes)
            {
                packed[at] &= (byte)~padding;
            }
        }
    }
}
