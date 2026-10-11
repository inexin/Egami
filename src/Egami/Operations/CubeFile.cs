using System.Globalization;
using System.Numerics;
using System.Text;

namespace Egami.Operations;

/// <summary>
/// Reads and writes .cube LUT files: the Adobe Cube LUT Specification 1.0 (a 1D or a 3D table, with TITLE, DOMAIN_MIN and
/// DOMAIN_MAX), and DaVinci Resolve's variant with a 1D shaper before the 3D table (LUT_1D_INPUT_RANGE and LUT_3D_INPUT_RANGE).
/// </summary>
/// <remarks>
/// The reader is strict: every number must parse and be finite, the counts must match the sizes exactly, LUT_3D_SIZE may be at
/// most 256 and LUT_1D_SIZE at most 65,536, and the table is checked against <see cref="SafetyLimits.MaxTotalBytes"/> before it is
/// allocated. Anything else throws <see cref="CorruptDataException"/> naming the line.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="cube"/>
/// </example>
public static class CubeFile
{
    private const string Format = "CUBE";

    /// <summary>The longest line read, in characters; real files' lines are under 100.</summary>
    private const int MaxLine = 4096;

    /// <summary>Reads a .cube file. A 1D-only file becomes a table whose grid changes nothing, behind its 1D table as the shaper.</summary>
    /// <param name="path">The file.</param>
    /// <param name="context">The context whose limits apply, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="CorruptDataException">The file breaks the format; the message names the line.</exception>
    /// <exception cref="LimitExceededException">The table would exceed the context's limits.</exception>
    public static Lut3D Read(string path, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return Read(stream, context);
    }

    /// <summary>Reads a .cube file from a stream, which is left open.</summary>
    /// <param name="stream">The stream, read from its current position to its end.</param>
    /// <param name="context">The context whose limits apply, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="CorruptDataException">The data breaks the format; the message names the line.</exception>
    /// <exception cref="LimitExceededException">The table would exceed the context's limits.</exception>
    public static Lut3D Read(Stream stream, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        context ??= EgamiContext.Default;
        using StreamReader reader = new(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 65536, leaveOpen: true);
        return new Parser(reader, context.Limits).Parse();
    }

    /// <summary>
    /// Writes a table as a .cube file: a 3D table with nine significant digits, behind its shaper as a 1D table when it has one, or
    /// an Adobe 1D-only file when the grid changes nothing. Reading the file back gives the same values.
    /// </summary>
    /// <param name="path">The file, created or replaced.</param>
    /// <param name="lut">The table.</param>
    /// <param name="title">The TITLE line, or <see langword="null"/> for none. It may not hold quotes or line breaks.</param>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> or <paramref name="lut"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="title"/> holds a quote or a line break.</exception>
    /// <exception cref="InvalidOperationException">
    /// The table has a shaper whose input range differs per channel from its grid's, which a .cube file cannot hold.
    /// </exception>
    public static void Write(string path, Lut3D lut, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(lut);
        CheckTitle(title);
        using FileStream stream = File.Create(path);
        Write(stream, lut, title);
    }

    /// <summary>Writes a table as a .cube file to a stream, which is left open.</summary>
    /// <param name="stream">The stream.</param>
    /// <param name="lut">The table.</param>
    /// <param name="title">The TITLE line, or <see langword="null"/> for none. It may not hold quotes or line breaks.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="lut"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="title"/> holds a quote or a line break.</exception>
    /// <exception cref="InvalidOperationException">
    /// The table has a shaper whose input range differs per channel from its grid's, which a .cube file cannot hold.
    /// </exception>
    public static void Write(Stream stream, Lut3D lut, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(lut);
        CheckTitle(title);
        using StreamWriter writer = new(stream, new UTF8Encoding(false), 65536, leaveOpen: true) { NewLine = "\n" };
        if (title is not null)
        {
            writer.WriteLine($"TITLE \"{title}\"");
        }

        bool oneD = lut.Shaper is not null && IsIdentityGrid(lut);
        if (oneD)
        {
            writer.WriteLine($"LUT_1D_SIZE {lut.ShaperSize}");
            WriteDomain(writer, lut.ShaperMin, lut.ShaperMax);
            WriteTriples(writer, lut.Shaper!);
            return;
        }

        if (lut.Shaper is not null)
        {
            writer.WriteLine($"LUT_1D_SIZE {lut.ShaperSize}");
            writer.WriteLine($"LUT_3D_SIZE {lut.Size}");
            if (Uniform(lut.ShaperMin) && Uniform(lut.ShaperMax) && Uniform(lut.DomainMin) && Uniform(lut.DomainMax))
            {
                writer.WriteLine($"LUT_1D_INPUT_RANGE {Number(lut.ShaperMin.X)} {Number(lut.ShaperMax.X)}");
                writer.WriteLine($"LUT_3D_INPUT_RANGE {Number(lut.DomainMin.X)} {Number(lut.DomainMax.X)}");
            }
            else if (lut.ShaperMin == lut.DomainMin && lut.ShaperMax == lut.DomainMax)
            {
                // One range per channel, shared by both tables: DOMAIN_MIN and DOMAIN_MAX say exactly that.
                WriteDomain(writer, lut.DomainMin, lut.DomainMax);
            }
            else
            {
                throw new InvalidOperationException("A 1D-plus-3D .cube file gives each table one input range for all channels, or one range per channel for both.");
            }

            WriteTriples(writer, lut.Shaper);
            WriteTriples(writer, lut.Values);
            return;
        }

        writer.WriteLine($"LUT_3D_SIZE {lut.Size}");
        WriteDomain(writer, lut.DomainMin, lut.DomainMax);
        WriteTriples(writer, lut.Values);
    }

    private static void WriteDomain(StreamWriter writer, Vector3 min, Vector3 max)
    {
        if (min != Vector3.Zero || max != Vector3.One)
        {
            writer.WriteLine($"DOMAIN_MIN {Number(min.X)} {Number(min.Y)} {Number(min.Z)}");
            writer.WriteLine($"DOMAIN_MAX {Number(max.X)} {Number(max.Y)} {Number(max.Z)}");
        }
    }

    private static void WriteTriples(StreamWriter writer, ReadOnlySpan<float> values)
    {
        for (int i = 0; i + 2 < values.Length; i += 3)
        {
            writer.Write(Number(values[i]));
            writer.Write(' ');
            writer.Write(Number(values[i + 1]));
            writer.Write(' ');
            writer.WriteLine(Number(values[i + 2]));
        }
    }

    private static string Number(float value) => value.ToString("G9", CultureInfo.InvariantCulture);

    private static bool Uniform(Vector3 v) => v.X == v.Y && v.Y == v.Z;

    private static bool IsIdentityGrid(Lut3D lut)
    {
        if (lut.Size != 2 || lut.DomainMin != Vector3.Zero || lut.DomainMax != Vector3.One)
        {
            return false;
        }

        for (int b = 0; b < 2; b++)
        {
            for (int g = 0; g < 2; g++)
            {
                for (int r = 0; r < 2; r++)
                {
                    if (lut[r, g, b] != new Vector3(r, g, b))
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private static void CheckTitle(string? title)
    {
        if (title is not null && title.AsSpan().IndexOfAny("\"\r\n") >= 0)
        {
            throw new ArgumentException("The title may not hold quotes or line breaks.", nameof(title));
        }
    }

    /// <summary>The reader: keywords first, then the 1D table's lines, then the 3D table's.</summary>
    private sealed class Parser(StreamReader reader, SafetyLimits limits)
    {
        private int _line;
        private string? _title;
        private int _size1D, _size3D;
        private Vector3? _domainMin, _domainMax;
        private (float Min, float Max)? _range1D, _range3D;

        internal Lut3D Parse()
        {
            string? text = Next(keywords: true);
            while (text is not null && IsKeyword(text))
            {
                Keyword(text);
                text = Next(keywords: true);
            }

            if (_size1D == 0 && _size3D == 0)
            {
                throw Error("The file has no LUT_1D_SIZE or LUT_3D_SIZE.");
            }

            if (_size1D > 0 && _size3D == 0 && (_range1D is not null || _range3D is not null))
            {
                throw Error("A 1D-only file gives its range with DOMAIN_MIN and DOMAIN_MAX.");
            }

            long bytes = ((long)_size1D * 3 * sizeof(float)) + ((long)_size3D * _size3D * _size3D * 3 * sizeof(float));
            if (bytes > limits.MaxTotalBytes)
            {
                throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limits.MaxTotalBytes, bytes);
            }

            float[]? shaper = _size1D > 0 ? new float[_size1D * 3] : null;
            float[]? grid = _size3D > 0 ? new float[_size3D * _size3D * _size3D * 3] : null;
            if (shaper is not null)
            {
                text = Data(text, shaper);
            }

            if (grid is not null)
            {
                text = Data(text, grid);
            }

            if (text is not null)
            {
                throw Error("The file has more table lines than its sizes allow.");
            }

            Vector3 domainMin = _domainMin ?? Vector3.Zero, domainMax = _domainMax ?? Vector3.One;
            CheckRange(domainMin, domainMax, "DOMAIN_MIN must lie below DOMAIN_MAX on every axis.");
            if (grid is null)
            {
                // Adobe 1D: the domain is the 1D table's input range; an identity grid follows it.
                return new Lut3D(2, [0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0, 0, 0, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1], null, null, shaper, domainMin, domainMax);
            }

            if (shaper is null)
            {
                return new Lut3D(_size3D, grid, domainMin, domainMax);
            }

            // Resolve: each table's range comes from its own keyword, else from DOMAIN_MIN and DOMAIN_MAX.
            Vector3 shaperMin = _range1D is { } r1 ? new Vector3(r1.Min) : domainMin, shaperMax = _range1D is { } r1b ? new Vector3(r1b.Max) : domainMax;
            Vector3 gridMin = _range3D is { } r3 ? new Vector3(r3.Min) : domainMin, gridMax = _range3D is { } r3b ? new Vector3(r3b.Max) : domainMax;
            CheckRange(shaperMin, shaperMax, "The 1D input range is empty.");
            CheckRange(gridMin, gridMax, "The 3D input range is empty.");
            return new Lut3D(_size3D, grid, gridMin, gridMax, shaper, shaperMin, shaperMax);
        }

        private string? Data(string? text, float[] values)
        {
            Span<float> triple = stackalloc float[3];
            for (int i = 0; i < values.Length; i += 3)
            {
                if (text is null)
                {
                    throw Error($"The file ends after {i / 3} of {values.Length / 3} table lines.");
                }

                if (IsKeyword(text))
                {
                    throw Error("Keywords must come before the table.");
                }

                Numbers(text, triple);
                triple.CopyTo(values.AsSpan(i));
                text = Next(keywords: false);
            }

            return text;
        }

        private void Keyword(string text)
        {
            int space = text.IndexOfAny([' ', '\t']);
            string name = space < 0 ? text : text[..space];
            string rest = space < 0 ? string.Empty : text[(space + 1)..].Trim();
            switch (name)
            {
                case "TITLE":
                    if (_title is not null)
                    {
                        throw Error("TITLE appears twice.");
                    }

                    if (rest.Length < 2 || rest[0] != '"' || rest[^1] != '"')
                    {
                        throw Error("TITLE needs its text in double quotes.");
                    }

                    _title = rest[1..^1];
                    break;
                case "LUT_1D_SIZE":
                    _size1D = Size(rest, _size1D, Lut3D.MaxShaperSize);
                    break;
                case "LUT_3D_SIZE":
                    _size3D = Size(rest, _size3D, Lut3D.MaxSize);
                    break;
                case "DOMAIN_MIN":
                    _domainMin = Once(_domainMin, Vector(rest));
                    break;
                case "DOMAIN_MAX":
                    _domainMax = Once(_domainMax, Vector(rest));
                    break;
                case "LUT_1D_INPUT_RANGE":
                    _range1D = Once(_range1D, Pair(rest));
                    break;
                case "LUT_3D_INPUT_RANGE":
                    _range3D = Once(_range3D, Pair(rest));
                    break;
                default:
                    // Other keywords, such as a tool's own, carry nothing a table needs.
                    break;
            }

            T Once<T>(T? current, T value)
                where T : struct
            {
                return current is null ? value : throw Error($"{name} appears twice.");
            }
        }

        private int Size(string text, int current, int max)
        {
            if (current != 0)
            {
                throw Error("A size appears twice.");
            }

            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int size) || size < 2 || size > max)
            {
                throw Error($"The size must be a whole number from 2 to {max}.");
            }

            return size;
        }

        private Vector3 Vector(string text)
        {
            Span<float> v = stackalloc float[3];
            Numbers(text, v);
            return new Vector3(v[0], v[1], v[2]);
        }

        private (float, float) Pair(string text)
        {
            Span<float> v = stackalloc float[2];
            Numbers(text, v);
            if (!(v[1] > v[0]))
            {
                throw Error("The range's maximum must lie above its minimum.");
            }

            return (v[0], v[1]);
        }

        /// <summary>Parses exactly <paramref name="values"/>.Length finite numbers separated by spaces or tabs.</summary>
        private void Numbers(string text, Span<float> values)
        {
            ReadOnlySpan<char> rest = text.AsSpan();
            for (int i = 0; i < values.Length; i++)
            {
                rest = rest.TrimStart(" \t");
                int end = rest.IndexOfAny(" \t");
                ReadOnlySpan<char> token = end < 0 ? rest : rest[..end];
                if (token.IsEmpty || !float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
                {
                    throw Error($"Expected {values.Length} numbers.");
                }

                values[i] = value;
                rest = end < 0 ? [] : rest[end..];
            }

            if (!rest.TrimStart(" \t").IsEmpty)
            {
                throw Error($"Expected {values.Length} numbers.");
            }
        }

        /// <summary>The next line with content, trimmed, without its comment; null at the end.</summary>
        private string? Next(bool keywords)
        {
            while (true)
            {
                string? line = ReadLine();
                if (line is null)
                {
                    return null;
                }

                _line++;
                int hash = line.IndexOf('#', StringComparison.Ordinal);
                // A TITLE may hold a '#' inside its quotes.
                if (hash >= 0 && !(keywords && line.TrimStart().StartsWith("TITLE", StringComparison.Ordinal)))
                {
                    line = line[..hash];
                }

                line = line.Trim();
                if (line.Length > 0)
                {
                    return line;
                }
            }
        }

        private string? ReadLine()
        {
            StringBuilder? text = null;
            while (true)
            {
                int c = reader.Read();
                if (c < 0)
                {
                    return text?.ToString();
                }

                if (c == '\n')
                {
                    return text?.ToString() ?? string.Empty;
                }

                if (c == '\r')
                {
                    if (reader.Peek() == '\n')
                    {
                        reader.Read();
                    }

                    return text?.ToString() ?? string.Empty;
                }

                text ??= new StringBuilder(64);
                if (text.Length >= MaxLine)
                {
                    throw Error($"A line is longer than {MaxLine} characters.", _line + 1);
                }

                text.Append((char)c);
            }
        }

        private static bool IsKeyword(string text) => char.IsAsciiLetter(text[0]);

        private void CheckRange(Vector3 min, Vector3 max, string message)
        {
            if (!(max.X > min.X && max.Y > min.Y && max.Z > min.Z))
            {
                throw Error(message, 0);
            }
        }

        private CorruptDataException Error(string problem, int? line = null)
        {
            int at = line ?? _line;
            return new CorruptDataException(Format, -1, at > 0 ? $"line {at}: {problem}" : problem);
        }
    }
}
