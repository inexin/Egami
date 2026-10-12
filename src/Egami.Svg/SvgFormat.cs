using System.IO.Compression;
using System.Text;

using Egami.Formats;
using Egami.Pixels;

namespace Egami.Svg;

/// <summary>
/// SVG and SVGZ as an Egami format: registered with <see cref="EgamiContext.WithFormat"/>, <c>Raster.Load</c> draws an SVG file
/// at its own size (times <see cref="SvgDecodeOptions.Scale"/>) and <c>Raster.Probe</c> gives that size without drawing. The
/// format only reads; nothing outside the file is opened, so images it links are not drawn, and text uses the machine's fonts.
/// For a resolver, your own fonts, a target size or a background, load an <see cref="SvgDocument"/> and call
/// <see cref="SvgDocument.Rasterize{TPixel}(SvgRenderOptions?)"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="svg-format"/>
/// </example>
public static class SvgFormat
{
    /// <summary>How many leading bytes detection looks at: an XML declaration, comments and a DOCTYPE may come before the root.</summary>
    private const int HeaderLength = 4096;

    /// <summary>Gets the SVG format: detected by an svg root element (after any XML declaration, comments and DOCTYPE), or by gzip data whose start is one.</summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="svg-format"/>
    /// </example>
    public static FormatInfo Info { get; } = new()
    {
        Name = "SVG",
        MimeTypes = ["image/svg+xml"],
        Extensions = ["svg", "svgz"],
        SignatureLength = HeaderLength,
        Matches = Matches,
        Reader = new SvgFormatReader(),
    };

    /// <summary>Returns whether the data starts like an SVG document, or is gzip data that does once inflated.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        if (header.Length >= 3 && header[0] == 0x1F && header[1] == 0x8B && header[2] == 8)
        {
            return IsSvgText(Inflate(header));
        }

        return IsSvgText(header);
    }

    /// <summary>Inflates as much of a gzip stream's start as the bytes hold.</summary>
    private static byte[] Inflate(ReadOnlySpan<byte> gzip)
    {
        using MemoryStream source = new(gzip.ToArray(), writable: false);
        using GZipStream inflate = new(source, CompressionMode.Decompress);
        byte[] buffer = new byte[HeaderLength];
        int total = 0;
        try
        {
            int read;
            while (total < buffer.Length && (read = inflate.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }
        }
        catch (InvalidDataException)
        {
            // The header ends inside the stream: what inflated so far is enough.
        }

        return buffer[..total];
    }

    /// <summary>Returns whether text starts with an svg root, after any byte order mark, XML declaration, comments, processing instructions and DOCTYPE.</summary>
    private static bool IsSvgText(ReadOnlySpan<byte> bytes)
    {
        string text = bytes switch
        {
            [0xFF, 0xFE, ..] => Encoding.Unicode.GetString(bytes[2..(2 + ((bytes.Length - 2) & ~1))]),
            [0xFE, 0xFF, ..] => Encoding.BigEndianUnicode.GetString(bytes[2..(2 + ((bytes.Length - 2) & ~1))]),
            [0xEF, 0xBB, 0xBF, ..] => Encoding.UTF8.GetString(bytes[3..]),
            _ => Encoding.UTF8.GetString(bytes),
        };

        int i = 0;
        while (true)
        {
            while (i < text.Length && text[i] is ' ' or '\t' or '\r' or '\n')
            {
                i++;
            }

            if (i >= text.Length)
            {
                return false;
            }

            ReadOnlySpan<char> rest = text.AsSpan(i);
            int end;
            if (rest.StartsWith("<?", StringComparison.Ordinal))
            {
                end = rest.IndexOf("?>", StringComparison.Ordinal) is int pi and >= 0 ? pi + 1 : -1;
            }
            else if (rest.StartsWith("<!--", StringComparison.Ordinal))
            {
                end = rest.IndexOf("-->", StringComparison.Ordinal) is int comment and >= 0 ? comment + 2 : -1;
            }
            else if (rest.StartsWith("<!DOCTYPE", StringComparison.Ordinal))
            {
                // An internal subset may hold '>' in its declarations: the DOCTYPE ends at the first '>' after it.
                int subset = rest.IndexOfAny('[', '>');
                int close = subset >= 0 && rest[subset] == '[' ? rest.IndexOf(']') : subset;
                end = close < 0 ? -1 : rest[close..].IndexOf('>') is int gt and >= 0 ? close + gt : -1;
            }
            else
            {
                return IsSvgRoot(rest);
            }

            if (end < 0)
            {
                // The header ends inside the prolog: a DOCTYPE or a namespace naming SVG is enough.
                return text.Contains("<!DOCTYPE svg", StringComparison.Ordinal) || text.Contains(SvgNamespaces.Svg, StringComparison.Ordinal);
            }

            i += end + 1;
        }
    }

    /// <summary>Returns whether a start tag is svg, with or without a prefix.</summary>
    private static bool IsSvgRoot(ReadOnlySpan<char> tag)
    {
        if (tag.Length < 2 || tag[0] != '<')
        {
            return false;
        }

        int nameEnd = 1;
        while (nameEnd < tag.Length && tag[nameEnd] is not (' ' or '\t' or '\r' or '\n' or '>' or '/'))
        {
            nameEnd++;
        }

        ReadOnlySpan<char> name = tag[1..nameEnd];
        return name[(name.IndexOf(':') + 1)..].SequenceEqual("svg");
    }

    /// <summary>Reads SVG through Egami.Svg: the document is loaded from the input and drawn at its size.</summary>
    private sealed class SvgFormatReader : IFormatReader
    {
        public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
        {
            SvgDocument document = Load(input, options);
            (double width, double height) = Size(document, options);
            return new RasterInfo
            {
                Format = Info,
                Width = (int)Math.Min(width, int.MaxValue),
                Height = (int)Math.Min(height, int.MaxValue),
                Layout = Rgba8.Layout,
                FrameCount = 1,
                ImageCount = 1,
                EstimatedBytes = (long)Math.Min(4 * width * height, long.MaxValue),
            };
        }

        public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            SvgDocument document = Load(input, options);
            (double w, double h) = Size(document, options);
            SafetyLimits limits = input.Limits;
            if (w > limits.MaxWidth)
            {
                throw new LimitExceededException(nameof(SafetyLimits.MaxWidth), limits.MaxWidth, (long)Math.Min(w, long.MaxValue));
            }

            if (h > limits.MaxHeight)
            {
                throw new LimitExceededException(nameof(SafetyLimits.MaxHeight), limits.MaxHeight, (long)Math.Min(h, long.MaxValue));
            }

            if (w * h > limits.MaxPixels)
            {
                throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), limits.MaxPixels, (long)Math.Min(w * h, long.MaxValue));
            }

            input.CheckMemory((long)(System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>() * w * h));
            Raster<TPixel> raster = document.Rasterize<TPixel>(new SvgRenderOptions { Width = (int)w, Height = (int)h, Context = input.Context });
            foreach (EgamiDiagnostic warning in document.Warnings)
            {
                input.Warn(raster, warning.Id, warning.Message, warning.Offset);
            }

            return raster;
        }

        public Raster Read(EgamiInput input, ReaderOptions options) => Read<Rgba8>(input, options);

        /// <summary>Loads the document with no resolver, its warnings kept for the raster's metadata rather than sent at once.</summary>
        private static SvgDocument Load(EgamiInput input, ReaderOptions options)
        {
            SvgLoadOptions load = new()
            {
                Strict = options.Recovery == RecoveryMode.Strict,
                Context = input.Context with { Diagnostics = null },
            };

            try
            {
                using InputStream stream = new(input);
                return SvgDocument.Load(stream, load);
            }
            catch (SvgException e)
            {
                throw new CorruptDataException(Info.Name, input.Position, e.Message, e);
            }
        }

        /// <summary>The raster size: the document's size times the scale, rounded up, at least one pixel.</summary>
        private static (double Width, double Height) Size(SvgDocument document, ReaderOptions options)
        {
            float scale = options is SvgDecodeOptions svg ? svg.Scale : 1;
            return (Math.Max(1, Math.Ceiling(document.Size.X * (double)scale)), Math.Max(1, Math.Ceiling(document.Size.Y * (double)scale)));
        }
    }

    /// <summary>A read-only stream over the input, so the document is read without copying the file first.</summary>
    private sealed class InputStream(EgamiInput input) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => input.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => input.Read(buffer);

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
