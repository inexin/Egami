using System.Text;
using System.Xml;

namespace Egami.Ocr;

/// <summary>
/// Reads and writes ALTO (Analyzed Layout and Text Object), the XML format of library digitization that ABBYY, Tesseract and
/// Transkribus write: pages (<c>Page</c>), lines (<c>TextLine</c>) and words (<c>String</c>) with their positions, <c>WC</c>
/// confidence and <c>LANG</c>.
/// </summary>
/// <remarks>
/// The reader takes ALTO 1 to 4 by element names, in any namespace. Positions in 1/10 mm or 1/1200 inch become pixels at
/// <see cref="OcrReadOptions.Dpi"/>. A <c>HYP</c> hyphen joins the word before it. No DTD or schema is ever fetched. The
/// writer writes ALTO 4 in pixels.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="alto"/>
/// </example>
public static class Alto
{
    private const string FormatName = "ALTO";
    private const string Namespace = "http://www.loc.gov/standards/alto/ns-v4#";

    /// <summary>Reads every page of an ALTO file.</summary>
    /// <param name="stream">The file, from its current position. It is left open.</param>
    /// <param name="options">The limits and the resolution for physical units, or <see langword="null"/> for <see cref="OcrReadOptions.Default"/>.</param>
    /// <returns>One result per <c>Page</c>, in file order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="CorruptDataException">The file is not well-formed XML, or has no <c>alto</c> root element.</exception>
    /// <exception cref="LimitExceededException">The file breaks a limit of <paramref name="options"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="alto"/>
    /// </example>
    public static IReadOnlyList<OcrResult> Read(Stream stream, OcrReadOptions? options = null)
    {
        options ??= OcrReadOptions.Default;
        return Parse(OcrText.ReadAll(stream, options.MaxBytes), options);
    }

    /// <summary>Reads every page of ALTO text.</summary>
    /// <param name="alto">The ALTO document.</param>
    /// <param name="options">The limits and the resolution for physical units, or <see langword="null"/> for <see cref="OcrReadOptions.Default"/>.</param>
    /// <returns>One result per <c>Page</c>, in document order.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="alto"/> is <see langword="null"/>.</exception>
    /// <exception cref="CorruptDataException">The text is not well-formed XML, or has no <c>alto</c> root element.</exception>
    /// <exception cref="LimitExceededException">The text breaks a limit of <paramref name="options"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="alto"/>
    /// </example>
    public static IReadOnlyList<OcrResult> Parse(string alto, OcrReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(alto);
        options ??= OcrReadOptions.Default;
        if (alto.Length > options.MaxBytes)
        {
            throw new LimitExceededException(nameof(OcrReadOptions.MaxBytes), options.MaxBytes, alto.Length);
        }

        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            MaxCharactersFromEntities = 1024,
            MaxCharactersInDocument = options.MaxBytes,
        };

        try
        {
            using StringReader text = new(alto);
            using XmlReader reader = XmlReader.Create(text, settings);
            return Read(reader, options);
        }
        catch (XmlException e)
        {
            throw new CorruptDataException(FormatName, -1, $"Line {e.LineNumber}, position {e.LinePosition}: {e.Message}", e);
        }
    }

    /// <summary>Writes pages as an ALTO 4 document in UTF-8, in pixels, one <c>Page</c> per result.</summary>
    /// <param name="stream">Where to write, from its current position. It is left open.</param>
    /// <param name="pages">The pages.</param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or <paramref name="pages"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Ocr.cs" region="alto"/>
    /// </example>
    public static void Write(Stream stream, IEnumerable<OcrResult> pages)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(pages);
        XmlWriterSettings settings = new() { Encoding = new UTF8Encoding(false), Indent = true, IndentChars = " ", NewLineChars = "\n", CloseOutput = false };
        using XmlWriter xml = XmlWriter.Create(stream, settings);
        xml.WriteStartDocument();
        xml.WriteStartElement("alto", Namespace);
        xml.WriteStartElement("Description", Namespace);
        xml.WriteElementString("MeasurementUnit", Namespace, "pixel");
        xml.WriteEndElement();
        xml.WriteStartElement("Layout", Namespace);
        int pageNumber = 0, lineNumber = 0, wordNumber = 0;
        foreach (OcrResult page in pages)
        {
            ArgumentNullException.ThrowIfNull(page, nameof(pages));
            Size size = OcrText.SizeOf(page);
            xml.WriteStartElement("Page", Namespace);
            xml.WriteAttributeString("ID", $"page_{pageNumber}");
            xml.WriteAttributeString("WIDTH", OcrText.Number(size.Width));
            xml.WriteAttributeString("HEIGHT", OcrText.Number(size.Height));
            xml.WriteAttributeString("PHYSICAL_IMG_NR", OcrText.Number(pageNumber));
            xml.WriteStartElement("PrintSpace", Namespace);
            Position(xml, new Rect(0, 0, size.Width, size.Height));
            xml.WriteStartElement("TextBlock", Namespace);
            xml.WriteAttributeString("ID", $"block_{pageNumber}");
            Position(xml, new Rect(0, 0, size.Width, size.Height));
            foreach (OcrLine line in page.Lines)
            {
                if (line.Words.Count == 0)
                {
                    continue;
                }

                xml.WriteStartElement("TextLine", Namespace);
                xml.WriteAttributeString("ID", $"line_{lineNumber++}");
                Position(xml, line.Bounds);
                for (int i = 0; i < line.Words.Count; i++)
                {
                    OcrWord word = line.Words[i];
                    if (i > 0)
                    {
                        xml.WriteStartElement("SP", Namespace);
                        xml.WriteEndElement();
                    }

                    xml.WriteStartElement("String", Namespace);
                    xml.WriteAttributeString("ID", $"string_{wordNumber++}");
                    Position(xml, word.Box.Bounds);
                    if (word.Confidence is double confidence)
                    {
                        xml.WriteAttributeString("WC", OcrText.Number(Math.Clamp(confidence, 0, 1)));
                    }

                    int angle = (int)MathF.Round(word.Box.Angle / 90) * 90 % 360;
                    if (angle != 0)
                    {
                        xml.WriteAttributeString("ROTATION", OcrText.Number(angle));
                    }

                    if (!string.IsNullOrEmpty(word.Language))
                    {
                        xml.WriteAttributeString("LANG", OcrText.ForXml(word.Language));
                    }

                    xml.WriteAttributeString("CONTENT", OcrText.ForXml(word.Text));
                    xml.WriteEndElement();
                }

                xml.WriteEndElement();
            }

            xml.WriteEndElement();
            xml.WriteEndElement();
            xml.WriteEndElement();
            pageNumber++;
        }

        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndDocument();
    }

    private static void Position(XmlWriter xml, Rect box)
    {
        xml.WriteAttributeString("HPOS", OcrText.Number(box.X));
        xml.WriteAttributeString("VPOS", OcrText.Number(box.Y));
        xml.WriteAttributeString("WIDTH", OcrText.Number(box.Width));
        xml.WriteAttributeString("HEIGHT", OcrText.Number(box.Height));
    }

    private static List<OcrResult> Read(XmlReader reader, OcrReadOptions options)
    {
        if (reader.MoveToContent() != XmlNodeType.Element || !reader.LocalName.Equals("alto", StringComparison.OrdinalIgnoreCase))
        {
            throw new CorruptDataException(FormatName, -1, "The root element is not alto.");
        }

        List<OcrResult> pages = [];
        List<OcrLine>? lines = null;
        List<OcrWord>? line = null;
        Size pageSize = default;
        double scale = 1;
        string?[] languages = new string?[3]; // block, line, word
        int words = 0;

        void EndLine()
        {
            if (line is { Count: > 0 })
            {
                (lines ??= []).Add(new OcrLine(line));
            }

            line = null;
        }

        void EndPage()
        {
            EndLine();
            if (lines is not null)
            {
                pages.Add(new OcrResult(lines) { ImageSize = pageSize });
            }

            lines = null;
            pageSize = default;
        }

        bool positioned = false;
        while (positioned || reader.Read())
        {
            positioned = false;
            if (reader.NodeType == XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "Page":
                        EndPage();
                        break;
                    case "TextLine":
                        EndLine();
                        languages[1] = null;
                        break;
                    case "TextBlock":
                        languages[0] = null;
                        break;
                }

                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "MeasurementUnit":
                    // Leaves the reader on the node after the element, which the loop must not skip.
                    string unit = reader.ReadElementContentAsString().Trim();
                    positioned = true;
                    scale = unit switch
                    {
                        "mm10" => options.Dpi / 254,
                        "inch1200" => options.Dpi / 1200,
                        _ => 1,
                    };
                    break;

                case "Page":
                    EndPage();
                    lines = [];
                    double width = Number(reader, "WIDTH") * scale, height = Number(reader, "HEIGHT") * scale;
                    pageSize = width is >= 0 and <= int.MaxValue && height is >= 0 and <= int.MaxValue ? new Size((int)Math.Round(width), (int)Math.Round(height)) : default;
                    if (reader.IsEmptyElement)
                    {
                        EndPage();
                    }

                    break;

                case "TextBlock":
                    languages[0] = Language(reader);
                    break;

                case "TextLine":
                    EndLine();
                    languages[1] = Language(reader);
                    line = [];
                    if (reader.IsEmptyElement)
                    {
                        EndLine();
                        languages[1] = null;
                    }

                    break;

                case "String":
                    string text = OcrText.Collapse(reader.GetAttribute("CONTENT") ?? string.Empty);
                    double x = Number(reader, "HPOS"), y = Number(reader, "VPOS"), w = Number(reader, "WIDTH"), h = Number(reader, "HEIGHT");
                    if (text.Length == 0 || !OcrText.IsCoordinate(x * scale) || !OcrText.IsCoordinate(y * scale) || !OcrText.IsCoordinate(w * scale) || !OcrText.IsCoordinate(h * scale) || w < 0 || h < 0)
                    {
                        break;
                    }

                    if (++words > options.MaxWords)
                    {
                        throw new LimitExceededException(nameof(OcrReadOptions.MaxWords), options.MaxWords, words);
                    }

                    double? confidence = OcrText.ParseNumber(reader.GetAttribute("WC")) is double wc ? Math.Clamp(wc, 0, 1) : null;
                    int angle = OcrText.ParseNumber(reader.GetAttribute("ROTATION")) is double rotation && Math.Abs(rotation) <= 3600 ? (int)Math.Round(rotation) : 0;
                    OcrQuad box = OcrQuad.FromBounds((float)(x * scale), (float)(y * scale), (float)(w * scale), (float)(h * scale), angle);
                    string? language = Language(reader) ?? languages[1] ?? languages[0];
                    OcrWord word = new(text, box, confidence, language);
                    if (line is null)
                    {
                        (lines ??= []).Add(new OcrLine([word]));
                    }
                    else
                    {
                        line.Add(word);
                    }

                    break;

                case "HYP":
                    string hyphen = reader.GetAttribute("CONTENT") ?? string.Empty;
                    if (line is { Count: > 0 } && hyphen.Length > 0)
                    {
                        line[^1] = line[^1] with { Text = line[^1].Text + OcrText.Collapse(hyphen) };
                    }

                    break;
            }
        }

        EndPage();
        return pages;
    }

    private static double Number(XmlReader reader, string name)
    {
        return OcrText.ParseNumber(reader.GetAttribute(name)) ?? double.NaN;
    }

    private static string? Language(XmlReader reader)
    {
        string? language = reader.GetAttribute("LANG")?.Trim();
        return string.IsNullOrEmpty(language) ? null : language;
    }
}
