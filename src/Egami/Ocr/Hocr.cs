using System.Globalization;
using System.Net;
using System.Text;

namespace Egami.Ocr;

/// <summary>
/// Reads and writes hOCR, the HTML format that Tesseract, OCRopus, kraken and others write: pages (<c>ocr_page</c>), lines
/// (<c>ocr_line</c> and its kin) and words (<c>ocrx_word</c>) with their <c>bbox</c>, <c>x_wconf</c>, <c>textangle</c> and
/// <c>lang</c>.
/// </summary>
/// <remarks>
/// The reader takes HTML as browsers do, not only well-formed XHTML: unknown tags, missing end tags and entities are fine, and
/// no DTD or other file is ever fetched. Lines turned by <c>textangle</c> 90, 180 or 270 give turned word quads. Words outside
/// a line each get a line of their own.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="hocr"/>
/// </example>
public static class Hocr
{
    private const string FormatName = "hOCR";
    private const int MaxDepth = 1024;

    private static readonly HashSet<string> LineClasses = new(StringComparer.Ordinal) { "ocr_line", "ocrx_line", "ocr_textfloat", "ocr_header", "ocr_caption" };

    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    private enum Role
    {
        Other,
        Page,
        Line,
        Word,
    }

    /// <summary>Reads every page of an hOCR file.</summary>
    /// <param name="stream">The file, from its current position. It is left open.</param>
    /// <param name="options">The limits, or <see langword="null"/> for <see cref="OcrReadOptions.Default"/>.</param>
    /// <returns>One result per <c>ocr_page</c>, in file order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="CorruptDataException">The file has no <c>ocr_page</c> and no <c>ocrx_word</c>.</exception>
    /// <exception cref="LimitExceededException">The file breaks a limit of <paramref name="options"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="hocr"/>
    /// </example>
    public static IReadOnlyList<OcrResult> Read(Stream stream, OcrReadOptions? options = null)
    {
        options ??= OcrReadOptions.Default;
        return Parse(OcrText.ReadAll(stream, options.MaxBytes), options);
    }

    /// <summary>Reads every page of hOCR text.</summary>
    /// <param name="hocr">The hOCR document.</param>
    /// <param name="options">The limits, or <see langword="null"/> for <see cref="OcrReadOptions.Default"/>.</param>
    /// <returns>One result per <c>ocr_page</c>, in document order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hocr"/> is <see langword="null"/>.</exception>
    /// <exception cref="CorruptDataException">The text has no <c>ocr_page</c> and no <c>ocrx_word</c>.</exception>
    /// <exception cref="LimitExceededException">The text breaks a limit of <paramref name="options"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="hocr"/>
    /// </example>
    public static IReadOnlyList<OcrResult> Parse(string hocr, OcrReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(hocr);
        options ??= OcrReadOptions.Default;
        if (hocr.Length > options.MaxBytes)
        {
            throw new LimitExceededException(nameof(OcrReadOptions.MaxBytes), options.MaxBytes, hocr.Length);
        }

        return new Reader(hocr, options).Run();
    }

    /// <summary>Writes pages as an hOCR (XHTML) document in UTF-8, one <c>ocr_page</c> per result.</summary>
    /// <param name="stream">Where to write, from its current position. It is left open.</param>
    /// <param name="pages">The pages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="pages"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="hocr"/>
    /// </example>
    public static void Write(Stream stream, IEnumerable<OcrResult> pages)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(pages);
        using StreamWriter writer = new(stream, new UTF8Encoding(false), 65536, leaveOpen: true) { NewLine = "\n" };
        writer.WriteLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        writer.WriteLine("<html xmlns=\"http://www.w3.org/1999/xhtml\">");
        writer.WriteLine(" <head>");
        writer.WriteLine("  <title></title>");
        writer.WriteLine("  <meta http-equiv=\"Content-Type\" content=\"text/html;charset=utf-8\"/>");
        writer.WriteLine("  <meta name=\"ocr-system\" content=\"Egami\"/>");
        writer.WriteLine("  <meta name=\"ocr-capabilities\" content=\"ocr_page ocr_line ocrx_word ocrp_lang ocrp_wconf\"/>");
        writer.WriteLine(" </head>");
        writer.WriteLine(" <body>");
        int pageNumber = 0;
        foreach (OcrResult page in pages)
        {
            ArgumentNullException.ThrowIfNull(page, nameof(pages));
            pageNumber++;
            Size size = OcrText.SizeOf(page);
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  <div class=\"ocr_page\" id=\"page_{pageNumber}\" title=\"bbox 0 0 {size.Width} {size.Height}; ppageno {pageNumber - 1}\">"));
            int lineNumber = 0, wordNumber = 0;
            foreach (OcrLine line in page.Lines)
            {
                if (line.Words.Count == 0)
                {
                    continue;
                }

                lineNumber++;
                int angle = (int)MathF.Round(line.Words[0].Box.Angle / 90) * 90 % 360;
                writer.Write(string.Create(CultureInfo.InvariantCulture, $"   <span class=\"ocr_line\" id=\"line_{pageNumber}_{lineNumber}\" title=\"{Box(line.Bounds)}"));
                writer.WriteLine(angle != 0 ? $"; textangle {angle}\">" : "\">");
                foreach (OcrWord word in line.Words)
                {
                    wordNumber++;
                    writer.Write(string.Create(CultureInfo.InvariantCulture, $"    <span class=\"ocrx_word\" id=\"word_{pageNumber}_{wordNumber}\" title=\"{Box(word.Box.Bounds)}"));
                    if (word.Confidence is double confidence)
                    {
                        writer.Write(string.Create(CultureInfo.InvariantCulture, $"; x_wconf {(int)Math.Round(Math.Clamp(confidence, 0, 1) * 100)}"));
                    }

                    writer.Write('"');
                    if (!string.IsNullOrEmpty(word.Language))
                    {
                        writer.Write($" lang=\"{Escape(word.Language)}\"");
                    }

                    writer.WriteLine($">{Escape(word.Text)}</span>");
                }

                writer.WriteLine("   </span>");
            }

            writer.WriteLine("  </div>");
        }

        writer.WriteLine(" </body>");
        writer.WriteLine("</html>");
    }

    private static string Box(Rect box)
    {
        return string.Create(CultureInfo.InvariantCulture, $"bbox {box.X} {box.Y} {box.X + box.Width} {box.Y + box.Height}");
    }

    private static string Escape(string text)
    {
        return WebUtility.HtmlEncode(OcrText.ForXml(text));
    }

    /// <summary>One open element: its name, what it is to hOCR, and what it passes down.</summary>
    private sealed class Element(string name, Role role, string? language)
    {
        public string Name { get; } = name;

        public Role Role { get; } = role;

        public string? Language { get; } = language;
    }

    /// <summary>A tolerant scan of the document's tags, with a stack of open elements.</summary>
    private sealed class Reader(string text, OcrReadOptions options)
    {
        private readonly List<OcrResult> _pages = [];
        private readonly List<Element> _open = [];
        private readonly StringBuilder _wordText = new();
        private List<OcrLine>? _lines;
        private List<OcrWord>? _line;
        private Size _pageSize;
        private int _lineAngle;
        private OcrQuad? _wordBox;
        private double? _wordConfidence;
        private string? _wordLanguage;
        private int _words;
        private bool _sawPage;
        private int _pos;

        public List<OcrResult> Run()
        {
            while (_pos < text.Length)
            {
                int lt = text.IndexOf('<', _pos);
                int end = lt < 0 ? text.Length : lt;
                if (end > _pos && InWord)
                {
                    _wordText.Append(WebUtility.HtmlDecode(text[_pos..end]));
                }

                if (lt < 0)
                {
                    break;
                }

                _pos = lt;
                Tag();
            }

            while (_open.Count > 0)
            {
                Close(_open.Count - 1);
            }

            EndPage();
            if (!_sawPage && _pages.Count == 0)
            {
                throw new CorruptDataException(FormatName, -1, "The document has no ocr_page and no ocrx_word element.");
            }

            return _pages;
        }

        private bool InWord => _wordBox is not null;

        private void Tag()
        {
            if (string.CompareOrdinal(text, _pos, "<!--", 0, 4) == 0)
            {
                int close = text.IndexOf("-->", _pos + 4, StringComparison.Ordinal);
                _pos = close < 0 ? text.Length : close + 3;
                return;
            }

            if (_pos + 1 < text.Length && text[_pos + 1] is '!' or '?')
            {
                int close = text.IndexOf('>', _pos);
                _pos = close < 0 ? text.Length : close + 1;
                return;
            }

            bool endTag = _pos + 1 < text.Length && text[_pos + 1] == '/';
            int i = _pos + (endTag ? 2 : 1);
            int nameStart = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is ':' or '-' or '_'))
            {
                i++;
            }

            if (i == nameStart)
            {
                // A '<' that starts no tag is text.
                if (InWord)
                {
                    _wordText.Append('<');
                }

                _pos++;
                return;
            }

            string name = text[nameStart..i].ToLowerInvariant();
            Dictionary<string, string> attributes = new(StringComparer.OrdinalIgnoreCase);
            bool selfClosing = Attributes(ref i, attributes);
            _pos = i;
            if (endTag)
            {
                int match = _open.FindLastIndex(e => e.Name == name);
                if (match >= 0)
                {
                    Close(match);
                }

                return;
            }

            if (name is "script" or "style")
            {
                int close = text.IndexOf("</" + name, _pos, StringComparison.OrdinalIgnoreCase);
                _pos = close < 0 ? text.Length : close;
                return;
            }

            Open(name, attributes, selfClosing || VoidElements.Contains(name));
        }

        /// <summary>Reads attributes up to the tag's '>'; returns whether the tag ends with "/>".</summary>
        private bool Attributes(ref int i, Dictionary<string, string> attributes)
        {
            while (i < text.Length)
            {
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                if (i >= text.Length)
                {
                    break;
                }

                if (text[i] == '>')
                {
                    i++;
                    return false;
                }

                if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>')
                {
                    i += 2;
                    return true;
                }

                int nameStart = i;
                while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '=' and not '>' && !(text[i] == '/' && i + 1 < text.Length && text[i + 1] == '>'))
                {
                    i++;
                }

                string name = text[nameStart..i];
                if (name.Length == 0)
                {
                    i++;
                    continue;
                }

                while (i < text.Length && char.IsWhiteSpace(text[i]))
                {
                    i++;
                }

                string value = string.Empty;
                if (i < text.Length && text[i] == '=')
                {
                    i++;
                    while (i < text.Length && char.IsWhiteSpace(text[i]))
                    {
                        i++;
                    }

                    if (i < text.Length && text[i] is '"' or '\'')
                    {
                        char quote = text[i];
                        int close = text.IndexOf(quote, i + 1);
                        close = close < 0 ? text.Length : close;
                        value = text[(i + 1)..close];
                        i = Math.Min(text.Length, close + 1);
                    }
                    else
                    {
                        int valueStart = i;
                        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '>')
                        {
                            i++;
                        }

                        value = text[valueStart..i];
                    }
                }

                attributes.TryAdd(name, WebUtility.HtmlDecode(value));
            }

            return false;
        }

        private void Open(string name, Dictionary<string, string> attributes, bool empty)
        {
            string? language = attributes.GetValueOrDefault("lang") ?? attributes.GetValueOrDefault("xml:lang");
            language = string.IsNullOrWhiteSpace(language) ? (_open.Count > 0 ? _open[^1].Language : null) : language.Trim();
            string[] classes = attributes.GetValueOrDefault("class")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];
            string title = attributes.GetValueOrDefault("title") ?? string.Empty;

            Role role = Array.IndexOf(classes, "ocr_page") >= 0 ? Role.Page
                : Array.IndexOf(classes, "ocrx_word") >= 0 ? Role.Word
                : classes.Any(LineClasses.Contains) ? Role.Line
                : Role.Other;
            if (role != Role.Other)
            {
                // A page, line or word cannot start inside a word: the word's end tag is missing, as HTML allows.
                int word = _open.FindLastIndex(e => e.Role == Role.Word);
                if (word >= 0)
                {
                    Close(word);
                }
            }

            switch (role)
            {
                case Role.Page:
                    StartPage(title);
                    break;
                case Role.Word:
                    StartWord(title, language);
                    break;
                case Role.Line:
                    StartLine(title);
                    break;
            }

            if (empty)
            {
                if (role != Role.Other)
                {
                    End(role);
                }

                return;
            }

            if (_open.Count >= MaxDepth)
            {
                throw new CorruptDataException(FormatName, _pos, $"Elements nest deeper than {MaxDepth} levels.");
            }

            _open.Add(new Element(name, role, language));
        }

        /// <summary>Closes the element at <paramref name="index"/> and every element opened inside it.</summary>
        private void Close(int index)
        {
            for (int i = _open.Count - 1; i >= index; i--)
            {
                Role role = _open[i].Role;
                _open.RemoveAt(i);
                End(role);
            }
        }

        private void End(Role role)
        {
            switch (role)
            {
                case Role.Word:
                    EndWord();
                    break;
                case Role.Line:
                    EndLine();
                    break;
                case Role.Page:
                    EndPage();
                    break;
            }
        }

        private void StartPage(string title)
        {
            EndPage();
            _sawPage = true;
            _lines = [];
            _pageSize = default;
            if (Property(title, "bbox") is [_, _, double right, double bottom, ..] && right >= 0 && bottom >= 0 && OcrText.IsCoordinate(right) && OcrText.IsCoordinate(bottom))
            {
                _pageSize = new Size((int)right, (int)bottom);
            }
        }

        private void EndPage()
        {
            EndLine();
            if (_lines is not null)
            {
                _pages.Add(new OcrResult(_lines) { ImageSize = _pageSize });
                _lines = null;
            }

            _pageSize = default;
        }

        private void StartLine(string title)
        {
            EndLine();
            _line = [];
            _lineAngle = Property(title, "textangle") is [double angle, ..] ? (int)Math.Round(angle) : 0;
        }

        private void EndLine()
        {
            if (_line is { Count: > 0 })
            {
                Lines.Add(new OcrLine(_line));
            }

            _line = null;
            _lineAngle = 0;
        }

        private List<OcrLine> Lines => _lines ??= [];

        private void StartWord(string title, string? language)
        {
            _wordText.Clear();
            _wordConfidence = Property(title, "x_wconf") is [double confidence, ..] ? Math.Clamp(confidence / 100, 0, 1) : null;
            _wordLanguage = language;
            _wordBox = Property(title, "bbox") is [double x0, double y0, double x1, double y1, ..] && x1 >= x0 && y1 >= y0
                    && OcrText.IsCoordinate(x0) && OcrText.IsCoordinate(y0) && OcrText.IsCoordinate(x1) && OcrText.IsCoordinate(y1)
                ? OcrQuad.FromBounds((float)x0, (float)y0, (float)(x1 - x0), (float)(y1 - y0), _line is null ? 0 : _lineAngle)
                : OcrQuad.FromBounds(0, 0, 0, 0);
        }

        private void EndWord()
        {
            if (_wordBox is not OcrQuad box)
            {
                return;
            }

            _wordBox = null;
            string word = OcrText.Collapse(_wordText.ToString());
            _wordText.Clear();
            if (word.Length == 0)
            {
                return;
            }

            if (++_words > options.MaxWords)
            {
                throw new LimitExceededException(nameof(OcrReadOptions.MaxWords), options.MaxWords, _words);
            }

            OcrWord result = new(word, box, _wordConfidence, _wordLanguage);
            if (_line is not null)
            {
                _line.Add(result);
            }
            else
            {
                // A word outside a line is a line of its own; outside an ocr_page, Lines starts a page of its own.
                Lines.Add(new OcrLine([result]));
            }
        }

        /// <summary>Returns the numbers of one property of an hOCR title, such as "bbox 10 20 30 40", or an empty list.</summary>
        private static List<double> Property(string title, string name)
        {
            List<double> numbers = [];
            foreach (string part in title.Split(';'))
            {
                ReadOnlySpan<char> property = part.AsSpan().Trim();
                if (!property.StartsWith(name, StringComparison.Ordinal) || (property.Length > name.Length && !char.IsWhiteSpace(property[name.Length])))
                {
                    continue;
                }

                foreach (Range token in property[name.Length..].Split(' '))
                {
                    ReadOnlySpan<char> value = property[name.Length..][token].Trim();
                    if (value.Length > 0 && OcrText.ParseNumber(value) is double number)
                    {
                        numbers.Add(number);
                    }
                }

                return numbers;
            }

            return numbers;
        }
    }
}
