using System.IO.Compression;
using System.Xml;

namespace Egami.Svg.Reading;

/// <summary>The DOCTYPE of a file, kept so a save writes it back (Illustrator's files define entities in it).</summary>
internal sealed record SvgDocumentType(string Name, string? PublicId, string? SystemId, string? InternalSubset);

/// <summary>What the XML reader builds: the root element and what surrounds it in the file.</summary>
internal sealed record SvgXmlContent(SvgElement Root, string? Declaration, SvgDocumentType? DocumentType, IReadOnlyList<SvgContent> Prolog, IReadOnlyList<SvgContent> Epilog);

/// <summary>
/// Reads an SVG file into the editable element tree with a locked-down <see cref="XmlReader"/>: internal DTD entities are
/// expanded (Illustrator's files rely on them) up to MaxMetadataBytes characters, external entities and DTDs are never opened,
/// and element count, nesting and file size are bounded. .svgz is detected by gzip's magic bytes.
/// </summary>
internal static class SvgXmlReader
{
    public static SvgXmlContent Read(Stream stream, SvgLimits limits, SvgReport report)
    {
        Stream input = Unzip(stream, limits);
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Parse,
            XmlResolver = null,
            MaxCharactersFromEntities = limits.MaxEntityCharacters,
            IgnoreComments = false,
            IgnoreWhitespace = false,
            IgnoreProcessingInstructions = false,
            CloseInput = false,
            ValidationType = ValidationType.None,
        };

        try
        {
            using XmlReader reader = XmlReader.Create(input, settings);
            return Build(reader, limits);
        }
        catch (XmlException e)
        {
            throw new SvgException(e.LineNumber, e.LinePosition, e.Message, e);
        }
        catch (InvalidDataException e)
        {
            throw new SvgException(0, 0, "The gzip data of the .svgz file is damaged: " + e.Message, e);
        }
        finally
        {
            if (!ReferenceEquals(input, stream))
            {
                input.Dispose();
            }
        }
    }

    private static SvgXmlContent Build(XmlReader reader, SvgLimits limits)
    {
        IXmlLineInfo? lines = reader as IXmlLineInfo;
        SvgElement? root = null;
        string? declaration = null;
        SvgDocumentType? documentType = null;
        List<SvgContent> prolog = [];
        List<SvgContent> epilog = [];
        Stack<SvgElement> open = new();
        long elements = 0;

        while (reader.Read())
        {
            int line = lines?.LineNumber ?? 0;
            int column = lines?.LinePosition ?? 0;
            SvgContent? item = null;
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    {
                        if (++elements > limits.MaxElements)
                        {
                            throw new SvgException(line, column, $"The file has more than {limits.MaxElements:N0} elements, the most a document may hold.");
                        }

                        if (open.Count >= SvgLimits.MaxDepth)
                        {
                            throw new SvgException(line, column, $"Elements nest more than {SvgLimits.MaxDepth} levels deep.");
                        }

                        SvgElement element = new(reader.LocalName, reader.NamespaceURI, reader.Prefix) { Line = line, Column = column };
                        bool empty = reader.IsEmptyElement;
                        if (reader.MoveToFirstAttribute())
                        {
                            do
                            {
                                element.Attributes.AddRead(new SvgAttribute(reader.LocalName, reader.Value, reader.NamespaceURI, reader.Prefix));
                            }
                            while (reader.MoveToNextAttribute());
                            reader.MoveToElement();
                        }

                        if (open.TryPeek(out SvgElement? parent))
                        {
                            parent.Children.AddRead(element);
                        }
                        else if (root is null)
                        {
                            root = element;
                        }

                        if (!empty)
                        {
                            open.Push(element);
                        }

                        continue;
                    }

                case XmlNodeType.EndElement:
                    open.Pop();
                    continue;
                case XmlNodeType.Text:
                case XmlNodeType.Whitespace:
                case XmlNodeType.SignificantWhitespace:
                    item = new SvgTextContent(reader.Value) { Line = line, Column = column };
                    break;
                case XmlNodeType.CDATA:
                    item = new SvgTextContent(reader.Value, isCData: true) { Line = line, Column = column };
                    break;
                case XmlNodeType.Comment:
                    item = new SvgComment(reader.Value) { Line = line, Column = column };
                    break;
                case XmlNodeType.ProcessingInstruction:
                    item = new SvgProcessingInstruction(reader.Name, reader.Value) { Line = line, Column = column };
                    break;
                case XmlNodeType.XmlDeclaration:
                    declaration = reader.Value;
                    continue;
                case XmlNodeType.DocumentType:
                    documentType = new SvgDocumentType(reader.Name, reader.GetAttribute("PUBLIC"), reader.GetAttribute("SYSTEM"), string.IsNullOrEmpty(reader.Value) ? null : reader.Value);
                    continue;
                default:
                    continue;
            }

            if (open.TryPeek(out SvgElement? owner))
            {
                // Adjacent text items of the same kind join (entity expansion can split them).
                if (item is SvgTextContent text && owner.Children.Count > 0 && owner.Children[^1] is SvgTextContent last && last.IsCData == text.IsCData)
                {
                    owner.Children.RemoveAt(owner.Children.Count - 1);
                    item = new SvgTextContent(last.Value + text.Value, text.IsCData) { Line = last.Line, Column = last.Column };
                }

                owner.Children.AddRead(item);
            }
            else if (item is not SvgTextContent)
            {
                (root is null ? prolog : epilog).Add(item);
            }
        }

        if (root is null)
        {
            throw new SvgException(0, 0, "The file has no root element.");
        }

        return new SvgXmlContent(root, declaration, documentType, prolog, epilog);
    }

    /// <summary>Opens gzip data (.svgz) when the stream starts with gzip's magic bytes, and bounds the bytes read either way.</summary>
    private static BoundedStream Unzip(Stream stream, SvgLimits limits)
    {
        Span<byte> magic = stackalloc byte[2];
        long start = stream.CanSeek ? stream.Position : 0;
        int read = stream.ReadAtLeast(magic, 2, throwOnEndOfStream: false);
        if (stream.CanSeek)
        {
            stream.Position = start;
        }

        Stream prefixed = stream.CanSeek ? stream : new PrefixStream(magic[..read].ToArray(), stream);
        bool gzip = read == 2 && magic[0] == 0x1F && magic[1] == 0x8B;
        Stream source = gzip ? new GZipStream(prefixed, CompressionMode.Decompress, leaveOpen: true) : prefixed;
        return new BoundedStream(source, limits.MaxBytes, ownsInner: gzip);
    }

    /// <summary>Gives back bytes already read from a stream that cannot seek, then the rest of it.</summary>
    private sealed class PrefixStream(byte[] prefix, Stream inner) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (_position < prefix.Length)
            {
                int n = Math.Min(buffer.Length, prefix.Length - _position);
                prefix.AsSpan(_position, n).CopyTo(buffer);
                _position += n;
                return n;
            }

            return inner.Read(buffer);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Throws <see cref="LimitExceededException"/> once more than the allowed bytes come through (a gzip bomb, say).</summary>
    private sealed class BoundedStream(Stream inner, long limit, bool ownsInner) : Stream
    {
        private long _total;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            _total += n;
            if (_total > limit)
            {
                throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limit, _total);
            }

            return n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && ownsInner)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
