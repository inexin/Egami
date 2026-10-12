using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace Egami.Drawing;

public sealed partial class Path
{
    /// <summary>
    /// Reads SVG path data, the grammar of an SVG <c>d</c> attribute (SVG 1.1 and SVG 2): every command (M, L, H, V, C, S, Q, T,
    /// A, Z and their relative forms), implicit repeats, compact numbers such as <c>0.5.5</c> and arc flags without separators.
    /// Arcs become cubic curves, as <see cref="PathBuilder.ArcTo(Vector2, float, bool, bool, Vector2)"/> makes them. Empty or
    /// blank data is an empty path.
    /// </summary>
    /// <param name="data">The path data.</param>
    /// <param name="fillRule">The fill rule of the path; SVG's <c>fill-rule</c> is not part of the data.</param>
    /// <param name="lenient">
    /// Whether to keep every complete command before the first error instead of throwing, as SVG tells renderers to draw a path
    /// with an error in it.
    /// </param>
    /// <returns>The path.</returns>
    /// <exception cref="SvgPathException">The data breaks the grammar and <paramref name="lenient"/> is <see langword="false"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fillRule"/> is not defined.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="svg"/>
    /// </example>
    public static Path ParseSvg(ReadOnlySpan<char> data, FillRule fillRule = FillRule.NonZero, bool lenient = false)
    {
        if (!Enum.IsDefined(fillRule))
        {
            throw new ArgumentOutOfRangeException(nameof(fillRule), fillRule, null);
        }

        PathBuilder builder = new();
        SvgError error = SvgPathReader.Read(data, builder);
        if (error.Position >= 0 && !lenient)
        {
            throw new SvgPathException(error.Position, error.Problem);
        }

        return builder.ToPath(fillRule);
    }

    /// <summary>Reads SVG path data as <see cref="ParseSvg"/> does, returning whether it was valid instead of throwing.</summary>
    /// <param name="data">The path data.</param>
    /// <param name="path">The path, with the nonzero fill rule, or <see langword="null"/> when the data breaks the grammar.</param>
    /// <returns><see langword="true"/> if the data was valid.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="svg"/>
    /// </example>
    public static bool TryParseSvg(ReadOnlySpan<char> data, [NotNullWhen(true)] out Path? path)
    {
        PathBuilder builder = new();
        if (SvgPathReader.Read(data, builder).Position >= 0)
        {
            path = null;
            return false;
        }

        path = builder.ToPath();
        return true;
    }

    /// <summary>
    /// Writes the path as SVG path data, for a <c>d</c> attribute. Lines, quadratics and cubics are written as L, Q and C (H and V
    /// for straight lines along an axis when compact); arcs were cubics already. The fill rule is not part of path data.
    /// </summary>
    /// <param name="format">How to write it; <see langword="null"/> for absolute commands with three decimals.</param>
    /// <returns>The path data.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="svg"/>
    /// </example>
    public string ToSvg(SvgPathFormat? format = null) => SvgPathWriter.Write(this, format ?? SvgPathFormat.Default);

    /// <summary>Where SVG path data first breaks the grammar; a position of −1 means it does not.</summary>
    private readonly record struct SvgError(int Position, string Problem)
    {
        internal static SvgError None => new(-1, string.Empty);
    }

    /// <summary>
    /// Reads SVG path data into a builder one complete command at a time, so a builder holds exactly the commands before the first
    /// error. Numbers are scanned by the grammar and then converted with the invariant culture, with no allocation.
    /// </summary>
    private ref struct SvgPathReader
    {
        private readonly ReadOnlySpan<char> _text;
        private int _pos;

        private SvgPathReader(ReadOnlySpan<char> text)
        {
            _text = text;
        }

        internal static SvgError Read(ReadOnlySpan<char> text, PathBuilder builder)
        {
            SvgPathReader reader = new(text);
            return reader.Run(builder);
        }

        private SvgError Run(PathBuilder builder)
        {
            Span<float> args = stackalloc float[7];
            SkipSpace();
            if (_pos == _text.Length)
            {
                return SvgError.None;
            }

            char command = '\0';
            char previous = '\0';
            Vector2 lastControl = default;
            bool first = true;
            while (true)
            {
                bool hadComma = false;
                SkipSpace();
                if (_pos < _text.Length && _text[_pos] == ',' && command != '\0' && command is not ('Z' or 'z'))
                {
                    // A comma may separate one set of arguments from an implicit repeat of the command.
                    hadComma = true;
                    _pos++;
                    SkipSpace();
                }

                if (_pos == _text.Length)
                {
                    return hadComma ? Error("The data ends after a comma.") : SvgError.None;
                }

                int start = _pos;
                char c = _text[_pos];
                bool explicitCommand = IsCommand(c);
                if (explicitCommand)
                {
                    if (hadComma)
                    {
                        return Error("A comma comes before a command.");
                    }

                    if (first && c is not ('M' or 'm'))
                    {
                        return Error("Path data must start with a move (M or m).");
                    }

                    command = c;
                    _pos++;
                }
                else if (command == '\0')
                {
                    return Error("Path data must start with a move (M or m).");
                }
                else if (command is 'Z' or 'z')
                {
                    return Error($"Unexpected '{c}' after a close.");
                }
                else if (!IsNumberStart(c))
                {
                    return Error($"Unexpected '{c}'.");
                }

                first = false;
                char upper = char.ToUpperInvariant(command);
                if (upper == 'Z')
                {
                    builder.Close();
                    previous = command;
                    continue;
                }

                int count = upper switch
                {
                    'M' or 'L' or 'T' => 2,
                    'H' or 'V' => 1,
                    'S' or 'Q' => 4,
                    'C' => 6,
                    _ => 7,
                };

                for (int i = 0; i < count; i++)
                {
                    // After the command letter only space may come; between arguments a comma may too.
                    if (i == 0)
                    {
                        SkipSpace();
                    }
                    else
                    {
                        SkipCommaSpace();
                    }

                    if (_pos == _text.Length)
                    {
                        return Error(explicitCommand && i == 0 ? $"The '{command}' command has no arguments." : $"The '{command}' command ends early.");
                    }

                    bool flag = upper == 'A' && i is 3 or 4;
                    if (flag)
                    {
                        char f = _text[_pos];
                        if (f is not ('0' or '1'))
                        {
                            return Error("An arc flag must be 0 or 1.");
                        }

                        args[i] = f - '0';
                        _pos++;
                    }
                    else if (!ReadNumber(out args[i]))
                    {
                        return Error(_pos < _text.Length && IsNumberStart(_text[_pos]) ? "A number is malformed or out of range." : $"The '{command}' command needs a number here.");
                    }
                }

                bool relative = char.IsLower(command);
                Vector2 current = builder.CurrentPoint;
                Vector2 offset = relative ? current : Vector2.Zero;
                if (!AllFinite(args[..count], offset))
                {
                    _pos = start;
                    return Error("A coordinate is out of range.");
                }

                var mark = builder.Mark();
                switch (upper)
                {
                    case 'M':
                        builder.MoveTo(At(args, 0, offset));

                        // Further coordinate pairs after a move are lines.
                        command = relative ? 'l' : 'L';
                        break;
                    case 'L':
                        builder.LineTo(At(args, 0, offset));
                        break;
                    case 'H':
                        builder.LineTo(new Vector2(args[0] + offset.X, current.Y));
                        break;
                    case 'V':
                        builder.LineTo(new Vector2(current.X, args[0] + offset.Y));
                        break;
                    case 'C':
                        lastControl = At(args, 2, offset);
                        builder.CubicTo(At(args, 0, offset), lastControl, At(args, 4, offset));
                        break;
                    case 'S':
                        {
                            Vector2 c1 = char.ToUpperInvariant(previous) is 'C' or 'S' ? (2 * current) - lastControl : current;
                            lastControl = At(args, 0, offset);
                            builder.CubicTo(c1, lastControl, At(args, 2, offset));
                            break;
                        }

                    case 'Q':
                        lastControl = At(args, 0, offset);
                        builder.QuadTo(lastControl, At(args, 2, offset));
                        break;
                    case 'T':
                        lastControl = char.ToUpperInvariant(previous) is 'Q' or 'T' ? (2 * current) - lastControl : current;
                        builder.QuadTo(lastControl, At(args, 0, offset));
                        break;
                    default:
                        builder.ArcTo(new Vector2(args[0], args[1]), args[2], args[3] != 0, args[4] != 0, At(args, 5, offset));
                        break;
                }

                if (!builder.FiniteSince(mark))
                {
                    // A reflected control point or an arc's curves can overflow even when every number is finite.
                    builder.Rewind(mark);
                    _pos = start;
                    return Error("A coordinate is out of range.");
                }

                previous = upper == 'M' ? 'M' : command;
            }
        }

        private static Vector2 At(ReadOnlySpan<float> args, int i, Vector2 offset) => new Vector2(args[i], args[i + 1]) + offset;

        private static bool AllFinite(ReadOnlySpan<float> args, Vector2 offset)
        {
            foreach (float a in args)
            {
                if (!float.IsFinite(a + offset.X) || !float.IsFinite(a + offset.Y))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsCommand(char c) => c is 'M' or 'm' or 'L' or 'l' or 'H' or 'h' or 'V' or 'v' or 'C' or 'c' or 'S' or 's' or 'Q' or 'q' or 'T' or 't' or 'A' or 'a' or 'Z' or 'z';

        private static bool IsNumberStart(char c) => c is (>= '0' and <= '9') or '+' or '-' or '.';

        private static bool IsSpace(char c) => c is ' ' or '\t' or '\n' or '\r' or '\f';

        private void SkipSpace()
        {
            while (_pos < _text.Length && IsSpace(_text[_pos]))
            {
                _pos++;
            }
        }

        private void SkipCommaSpace()
        {
            SkipSpace();
            if (_pos < _text.Length && _text[_pos] == ',')
            {
                _pos++;
                SkipSpace();
            }
        }

        /// <summary>Reads sign? (digits (. digits?)? | . digits) exponent?, leaving the position after it; false when malformed or not finite.</summary>
        private bool ReadNumber(out float value)
        {
            value = 0;
            int start = _pos, i = _pos;
            if (i < _text.Length && _text[i] is '+' or '-')
            {
                i++;
            }

            int digits = 0;
            while (i < _text.Length && char.IsAsciiDigit(_text[i]))
            {
                i++;
                digits++;
            }

            if (i < _text.Length && _text[i] == '.')
            {
                i++;
                while (i < _text.Length && char.IsAsciiDigit(_text[i]))
                {
                    i++;
                    digits++;
                }
            }

            if (digits == 0)
            {
                return false;
            }

            if (i < _text.Length && _text[i] is 'e' or 'E')
            {
                int e = i + 1;
                if (e < _text.Length && _text[e] is '+' or '-')
                {
                    e++;
                }

                int exponentDigits = 0;
                while (e < _text.Length && char.IsAsciiDigit(_text[e]))
                {
                    e++;
                    exponentDigits++;
                }

                if (exponentDigits == 0)
                {
                    _pos = i;
                    return false;
                }

                i = e;
            }

            if (!double.TryParse(_text[start..i], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out double d)
                || !float.IsFinite((float)d))
            {
                return false;
            }

            value = (float)d;
            _pos = i;
            return true;
        }

        private readonly SvgError Error(string problem) => new(_pos, problem);
    }

    /// <summary>Writes path data; relative coordinates are taken from where a reader will be, so rounding never drifts.</summary>
    private static class SvgPathWriter
    {
        internal static string Write(Path path, SvgPathFormat format)
        {
            StringBuilder text = new();
            ReadOnlySpan<Vector2> points = path.Points;
            string numberFormat = format.Precision == 0 ? "0" : "0." + new string('#', format.Precision);
            char[] buffer = new char[64];
            double scale = Math.Pow(10, format.Precision);

            // Where a reader of what has been written stands, as the float it holds, and where its contour started. Relative offsets
            // are taken from there, so the reader's own rounding never adds up along the path.
            Vector2 current = Vector2.Zero, start = Vector2.Zero;
            bool relative = format.Relative;
            char lastCommand = '\0';
            bool lastHasDot = false;
            int p = 0;
            Span<double> values = stackalloc double[6];
            foreach (PathVerb verb in path.Verbs)
            {
                switch (verb)
                {
                    case PathVerb.Move:
                        values[0] = Value(points[p].X, current.X);
                        values[1] = Value(points[p].Y, current.Y);
                        Emit(relative ? 'm' : 'M', values[..2]);
                        current = start = new Vector2(Lands(values[0], current.X), Lands(values[1], current.Y));
                        p++;
                        break;
                    case PathVerb.Line:
                        {
                            values[0] = Value(points[p].X, current.X);
                            values[1] = Value(points[p].Y, current.Y);
                            Vector2 next = new(Lands(values[0], current.X), Lands(values[1], current.Y));
                            if (format.Compact && next.Y == current.Y && next.X != current.X)
                            {
                                Emit(relative ? 'h' : 'H', values[..1]);
                            }
                            else if (format.Compact && next.X == current.X && next.Y != current.Y)
                            {
                                values[0] = values[1];
                                Emit(relative ? 'v' : 'V', values[..1]);
                            }
                            else
                            {
                                Emit(relative ? 'l' : 'L', values[..2]);
                            }

                            current = next;
                            p++;
                            break;
                        }

                    case PathVerb.Quad:
                    case PathVerb.Cubic:
                        {
                            int n = verb == PathVerb.Quad ? 2 : 3;
                            for (int i = 0; i < n; i++)
                            {
                                values[2 * i] = Value(points[p + i].X, current.X);
                                values[(2 * i) + 1] = Value(points[p + i].Y, current.Y);
                            }

                            char command = verb == PathVerb.Quad ? 'Q' : 'C';
                            Emit(relative ? char.ToLowerInvariant(command) : command, values[..(2 * n)]);
                            current = new Vector2(Lands(values[(2 * n) - 2], current.X), Lands(values[(2 * n) - 1], current.Y));
                            p += n;
                            break;
                        }

                    default:
                        Emit(relative ? 'z' : 'Z', []);
                        current = start;
                        break;
                }
            }

            return text.ToString();

            // The number to write for a coordinate, rounded to the precision.
            double Value(float target, float from)
            {
                double r = Math.Round((relative ? target - (double)from : target) * scale) / scale;
                return r == 0 ? 0 : r;
            }

            // Where a reader lands after reading that number: it parses a float and, for a relative command, adds it in float.
            float Lands(double value, float from) => relative ? (float)value + from : (float)value;

            void Emit(char command, ReadOnlySpan<double> args)
            {
                // A compact writer leaves out a command that repeats, and the L that follows an M implicitly.
                bool implicitRepeat = format.Compact && args.Length > 0 && command is not ('M' or 'm')
                    && (command == lastCommand || (lastCommand is 'M' && command == 'L') || (lastCommand is 'm' && command == 'l'));
                if (!implicitRepeat)
                {
                    if (text.Length > 0 && !format.Compact)
                    {
                        text.Append(' ');
                    }

                    text.Append(command);
                    lastHasDot = false;
                }

                for (int i = 0; i < args.Length; i++)
                {
                    args[i].TryFormat(buffer, out int written, numberFormat, CultureInfo.InvariantCulture);
                    ReadOnlySpan<char> number = buffer.AsSpan(0, written);
                    if (format.Compact)
                    {
                        if (number.StartsWith("0."))
                        {
                            number = number[1..];
                        }
                        else if (number.StartsWith("-0."))
                        {
                            buffer[1] = '-';
                            number = buffer.AsSpan(1, written - 1);
                        }
                    }

                    bool first = i == 0 && !implicitRepeat;
                    if (!first)
                    {
                        // Compact numbers run together where the grammar can tell them apart: before a sign, or before a point
                        // when the previous number already has one.
                        bool needsSpace = !format.Compact || !(number[0] == '-' || (number[0] == '.' && lastHasDot));
                        if (needsSpace)
                        {
                            text.Append(' ');
                        }
                    }

                    text.Append(number);
                    lastHasDot = number.Contains('.');
                }

                lastCommand = command is 'M' ? 'M' : command is 'm' ? 'm' : command;
                if (command is 'Z' or 'z')
                {
                    lastCommand = '\0';
                }
            }
        }
    }
}
