using System.Globalization;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Xpm;

/// <summary>
/// XPM, the X PixMap format (version 3): C source holding an array of strings, the size and color count, a color table keyed by
/// one or more characters, then a string of characters per row. Registered with <see cref="ExtendedContextExtensions.WithXpm"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="register-one"/>
/// </example>
public static class XpmFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extension, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "XPM",
        MimeTypes = ["image/x-xpixmap"],
        Extensions = ["xpm"],
        SignatureLength = 9,
        Matches = header => header.StartsWith("/* XPM */"u8),
        Reader = new XpmReader(),
        Writer = new XpmWriter(),
    };
}

/// <summary>Details only XPM has, kept in <see cref="RasterMetadata.FormatDetails"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/FormatsExtended.cs" region="details"/>
/// </example>
public sealed class XpmDetails : FormatDetails
{
    /// <summary>Gets or sets the name of the C array, such as "icon". The writer writes it back.</summary>
    public string Name { get; set; } = "image";

    /// <summary>Gets or sets the hotspot, the point a cursor points with, or <see langword="null"/> if the file has none.</summary>
    public Point? Hotspot { get; set; }

    /// <summary>Gets or sets the number of characters that name a color in the file.</summary>
    public int CharactersPerPixel { get; set; } = 1;

    /// <inheritdoc/>
    public override FormatDetails Copy()
    {
        return new XpmDetails { Name = Name, Hotspot = Hotspot, CharactersPerPixel = CharactersPerPixel };
    }
}

/// <summary>
/// Reads XPM version 3, written from "XPM Manual: The X PixMap Format" (Arnaud Le Hors, version 3.4i). Of each color's keys it
/// takes the color (c), then the gray (g and g4) and mono (m) ones; colors are None, #RGB to #RRRRGGGGBBBB, or names from
/// <see cref="XpmColors"/>. The file is read whole.
/// </summary>
internal sealed class XpmReader : ExtendedReader
{
    /// <summary>The longest color key read: 8 characters fit a 64-bit lookup key.</summary>
    private const int MaxCharactersPerPixel = 8;

    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        // The values string comes first: a probe reads no further than the first window.
        Values values = ReadValues(input, input.Peek(64 * 1024), out _);
        return Describe(XpmFormat.Info, values.Width, values.Height, Rgba8.Layout, RasterAccess.Buffered, (long)values.Width * values.Height * 4);
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        // Clamped first, so a limit of long.MaxValue cannot overflow.
        long max = Math.Min((Math.Min(input.Limits.MaxPixels, Array.MaxLength) * MaxCharactersPerPixel) + (1 << 20), Array.MaxLength);
        byte[] text = InputData.ReadToEnd(input, max, out int length);
        Values values = ReadValues(input, text.AsSpan(0, length), out int next);
        input.CheckFrameSize(values.Width, values.Height);
        input.CheckMemory((long)values.Colors * 16);
        Rows rows = new(input, options, values, text, length);
        rows.ReadColors(next);
        return rows;
    }

    /// <summary>Reads the next C string literal from the cursor; returns <see langword="false"/> at the closing brace or the end.</summary>
    private static bool NextString(ref CSource source, out int start, out int length)
    {
        start = 0;
        length = 0;
        while (true)
        {
            source.SkipSpace();
            if (source.AtEnd || source.Current == '}')
            {
                return false;
            }

            if (source.Current == '"')
            {
                break;
            }

            source.Position++;
        }

        start = ++source.Position;
        while (!source.AtEnd && source.Current != '"')
        {
            source.Position += source.Current == '\\' ? 2 : 1;
        }

        // A backslash as the last byte steps past the end.
        source.Position = Math.Min(source.Position, source.Length);
        length = source.Position - start;
        if (!source.AtEnd)
        {
            source.Position++;
        }

        return true;
    }

    private static Values ReadValues(EgamiInput input, ReadOnlySpan<byte> text, out int next)
    {
        // The marker is a comment, which the cursor would skip, so it is looked for before the cursor starts.
        int at = text.IndexOfAnyExcept(" \t\r\n"u8);
        ReadOnlySpan<byte> rest = at < 0 ? [] : text[at..];
        int marker = rest.StartsWith("/* XPM */"u8) ? 9 : rest.StartsWith("/*XPM*/"u8) ? 7 : 0;
        if (marker == 0)
        {
            throw input.Corrupt(0, "The XPM comment /* XPM */ is missing.");
        }

        CSource source = new(text) { Position = at + marker };

        // The array's name is the last identifier before its brackets: static const char * const name[] = {
        string name = "";
        while (true)
        {
            source.SkipSpace();
            if (source.AtEnd || source.Current is (byte)'[' or (byte)'{')
            {
                break;
            }

            ReadOnlySpan<byte> identifier = source.Identifier();
            if (identifier.IsEmpty)
            {
                source.Position++;
            }
            else
            {
                name = Encoding.ASCII.GetString(identifier);
            }
        }

        if (!source.SkipPast((byte)'{') || !NextString(ref source, out int start, out int length))
        {
            throw input.Corrupt(source.Position, "The XPM values string is missing.");
        }

        next = source.Position;
        CSource values = new(text.Slice(start, length));
        long width = values.Number();
        long height = values.Number();
        long colors = values.Number();
        long cpp = values.Number();
        long hotX = values.Number();
        long hotY = hotX >= 0 ? values.Number() : -1;
        if (width <= 0 || height <= 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw input.Corrupt(start, $"The XPM size {width}×{height} is missing or invalid.");
        }

        if (colors <= 0 || colors > 1 << 24 || cpp is <= 0 or > MaxCharactersPerPixel)
        {
            throw input.Corrupt(start, $"An XPM of {colors} colors of {cpp} characters each is not supported.");
        }

        Point? hotspot = hotX >= 0 && hotY >= 0 ? new Point((int)hotX, (int)hotY) : null;
        return new Values((int)width, (int)height, (int)colors, (int)cpp, string.IsNullOrEmpty(name) ? "image" : name, hotspot);
    }

    /// <summary>What the values string says.</summary>
    private sealed record Values(int Width, int Height, int Colors, int CharactersPerPixel, string Name, Point? Hotspot);

    /// <summary>The rows, each one string of the array, read in order as they are asked for.</summary>
    private sealed class Rows : ExtendedRows<Rgba8, Rgba8>
    {
        private readonly Values _values;
        private readonly byte[] _text;
        private readonly int _length;
        private readonly Dictionary<ulong, Rgba8> _colors = [];
        private readonly Rgba8[] _single = new Rgba8[256];
        private readonly bool[] _known = new bool[256];
        private int _position;
        private bool _ended;
        private bool _unknownNoted;

        public Rows(EgamiInput input, ReaderOptions options, Values values, byte[] text, int length)
            : base(input, options, XpmFormat.Info, "Xpm", values.Width, values.Height)
        {
            _values = values;
            _text = text;
            _length = length;
            Metadata.FormatDetails = new XpmDetails { Name = values.Name, Hotspot = values.Hotspot, CharactersPerPixel = values.CharactersPerPixel };
        }

        /// <summary>Reads the color table, which starts at <paramref name="position"/>.</summary>
        public void ReadColors(int position)
        {
            CSource source = new(_text.AsSpan(0, _length)) { Position = position };
            int cpp = _values.CharactersPerPixel;
            for (int i = 0; i < _values.Colors; i++)
            {
                if (!NextString(ref source, out int start, out int length) || length < cpp)
                {
                    throw Input.Corrupt(source.Position, $"The XPM color table ends after {i} of {_values.Colors} colors.");
                }

                ReadOnlySpan<byte> line = _text.AsSpan(start, length);
                ulong key = Key(line[..cpp]);
                if (!TryColor(Encoding.ASCII.GetString(line[cpp..]), out Rgba8 color))
                {
                    Damaged("ColorUnknown", $"The color of \"{Encoding.ASCII.GetString(line[..cpp])}\" is not one Egami knows; it is read as black.", start);
                    color = new Rgba8(0, 0, 0, 255);
                }

                _colors[key] = color;
                if (cpp == 1)
                {
                    _single[line[0]] = color;
                    _known[line[0]] = true;
                }
            }

            _position = source.Position;
        }

        protected override bool DecodeRow(int y, Span<Rgba8> row)
        {
            CSource source = new(_text.AsSpan(0, _length)) { Position = _position };
            if (_ended || !NextString(ref source, out int start, out int length))
            {
                _ended = true;
                row.Clear();
                return false;
            }

            _position = source.Position;
            int cpp = _values.CharactersPerPixel;
            ReadOnlySpan<byte> line = _text.AsSpan(start, length);
            int count = Math.Min(row.Length, line.Length / cpp);
            for (int x = 0; x < count; x++)
            {
                Rgba8 color;
                bool found;
                if (cpp == 1)
                {
                    found = _known[line[x]];
                    color = _single[line[x]];
                }
                else
                {
                    found = _colors.TryGetValue(Key(line.Slice(x * cpp, cpp)), out color);
                }

                if (!found)
                {
                    Unknown(start + (x * cpp));
                }

                row[x] = color;
            }

            if (count < row.Length)
            {
                row[count..].Clear();
                Damaged("RowShort", $"Row {y} has {count} of {row.Length} pixels; the rest is left transparent.", start);
            }

            return true;
        }

        private static ulong Key(ReadOnlySpan<byte> chars)
        {
            ulong key = 0;
            foreach (byte b in chars)
            {
                key = (key << 8) | b;
            }

            return key;
        }

        /// <summary>Picks the color key's value, else the gray, four-level gray and mono ones, and parses it.</summary>
        private static bool TryColor(string spec, out Rgba8 color)
        {
            string[] tokens = spec.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            foreach (string wanted in (string[])["c", "g", "g4", "m"])
            {
                for (int i = 0; i < tokens.Length; i++)
                {
                    if (tokens[i] != wanted)
                    {
                        continue;
                    }

                    // A value runs to the next key, so names with spaces ("light blue") read whole.
                    int end = i + 1;
                    while (end < tokens.Length && tokens[end] is not ("c" or "g" or "g4" or "m" or "s"))
                    {
                        end++;
                    }

                    if (end > i + 1 && XpmColors.TryParse(string.Join(' ', tokens, i + 1, end - i - 1), out color))
                    {
                        return true;
                    }
                }
            }

            color = default;
            return false;
        }

        private void Unknown(long offset)
        {
            if (Strict)
            {
                throw Input.Corrupt(offset, "A pixel names a color the XPM color table does not have.");
            }

            if (!_unknownNoted)
            {
                _unknownNoted = true;
                Warn("PixelUnknown", "Pixels name colors the color table does not have; they are left transparent.", offset);
            }
        }
    }
}

/// <summary>
/// Writes XPM version 3 with every color of the raster, as many characters a pixel as the count needs; a pixel below half alpha
/// is None. XPM keeps its color table before the pixels, so the rows wait in memory until the last one. The name and hotspot
/// come from <see cref="XpmDetails"/>.
/// </summary>
internal sealed class XpmWriter : ExtendedWriter
{
    /// <summary>The characters that name colors: printable ASCII without the quote and the backslash, space first for None.</summary>
    private const string Alphabet = " .XoO+@#$%&*=-;:>,<1234567890qwertyuipasdfghjklzxcvbnmMNBVCZASDFGHJKLPIUYTREWQ!~^/()_`'][{}|";

    protected override FormatInfo Format => XpmFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        start.ReportLosses("XPM", SaveKeeps.Alpha);
        XpmDetails? details = start.Metadata.FormatDetails as XpmDetails;
        return new Encoder<TPixel>(output, start, Xbm.XbmWriter.CName(details?.Name), details?.Hotspot);
    }

    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Stream _output;
        private readonly int _width;
        private readonly int _height;
        private readonly string _name;
        private readonly Point? _hotspot;
        private readonly Rgba8[] _row;
        private readonly int[] _indices;
        private readonly Dictionary<uint, int> _palette = [];
        private int _y;

        public Encoder(Stream output, EncodeStart<TPixel> start, string name, Point? hotspot)
        {
            _output = output;
            _width = start.Width;
            _height = start.Height;
            _name = name;
            _hotspot = hotspot;
            _row = new Rgba8[_width];
            _indices = new int[checked(_width * _height)];
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            PixelConvert.Convert<TPixel, Rgba8>(row, _row);
            Span<int> indices = _indices.AsSpan(_y * _width, _width);
            for (int x = 0; x < _width; x++)
            {
                Rgba8 p = _row[x];
                uint key = p.A < 128 ? 0 : 0xFF00_0000u | ((uint)p.R << 16) | ((uint)p.G << 8) | p.B;
                if (!_palette.TryGetValue(key, out int index))
                {
                    index = _palette.Count;
                    _palette[key] = index;
                }

                indices[x] = index;
            }

            _y++;
        }

        public override void Complete()
        {
            int colors = Math.Max(_palette.Count, 1);
            int cpp = 1;
            for (long reach = Alphabet.Length; reach < colors; reach *= Alphabet.Length)
            {
                cpp++;
            }

            string[] codes = new string[colors];
            for (int i = 0; i < colors; i++)
            {
                char[] code = new char[cpp];
                int v = i;
                for (int k = cpp - 1; k >= 0; k--)
                {
                    code[k] = Alphabet[v % Alphabet.Length];
                    v /= Alphabet.Length;
                }

                codes[i] = new string(code);
            }

            using StreamWriter writer = new(_output, new UTF8Encoding(false), 64 * 1024, leaveOpen: true) { NewLine = "\n" };
            writer.WriteLine("/* XPM */");
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"static char *{_name}[] = {{"));
            writer.WriteLine("/* columns rows colors chars-per-pixel */");
            string hot = _hotspot is Point h ? string.Create(CultureInfo.InvariantCulture, $" {h.X} {h.Y}") : "";
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"\"{_width} {_height} {colors} {cpp}{hot}\","));
            foreach ((uint key, int index) in _palette)
            {
                string value = key >> 24 == 0 ? "None" : string.Create(CultureInfo.InvariantCulture, $"#{key & 0xFFFFFF:X6}");
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"\"{codes[index]} c {value}\","));
            }

            writer.WriteLine("/* pixels */");
            StringBuilder line = new(_width * cpp);
            for (int y = 0; y < _height; y++)
            {
                line.Clear();
                foreach (int index in _indices.AsSpan(y * _width, _width))
                {
                    line.Append(codes[index]);
                }

                writer.Write('"');
                writer.Write(line);
                writer.WriteLine(y < _height - 1 ? "\"," : "\"");
            }

            writer.WriteLine("};");
        }
    }
}
