using System.IO.Compression;

using Egami.Formats;
using Egami.Metafiles.Reading;
using Egami.Pixels;

namespace Egami.Metafiles;

/// <summary>
/// WMF, EMF and EMF+ (and WMZ, EMZ) as an Egami format (design question 16): registered with
/// <see cref="EgamiContext.WithFormat"/>, <c>Raster.Load</c> draws a metafile at its own size at 96 DPI (or
/// <see cref="MetafileDecodeOptions.Dpi"/>) onto a transparent <see cref="Rgba8"/> raster, and <c>Raster.Probe</c> gives that
/// size from the header without drawing. Text uses the installed fonts. For your own fonts, a target size, a background or the
/// GDI part of a dual file, load a <see cref="Metafile"/> and call <see cref="Metafile.Rasterize{TPixel}(MetafileRenderOptions?)"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Metafiles.cs" region="metafile-format"/>
/// </example>
public static class MetafileFormat
{
    /// <summary>How many leading bytes detection looks at: an EMF header is 44 bytes in; gzip needs more to inflate that much.</summary>
    private const int HeaderLength = 512;

    /// <summary>Gets the metafile format: detected by the placeable WMF key, a META_HEADER, an EMF header, or gzip data whose start is one of them.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "Metafile",
        MimeTypes = ["image/wmf", "image/x-wmf", "image/emf", "image/x-emf", "application/x-msmetafile"],
        Extensions = ["wmf", "emf", "wmz", "emz"],
        SignatureLength = HeaderLength,
        Matches = Matches,
        Reader = new MetafileFormatReader(),
    };

    /// <summary>Returns whether the data starts like a metafile, or is gzip data that does once inflated.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        if (!MetafileReader.IsGzip(header))
        {
            return MetafileReader.LooksLikeMetafile(header);
        }

        using GZipStream inflate = new(new MemoryStream(header.ToArray(), writable: false), CompressionMode.Decompress);
        byte[] start = new byte[64];
        int total = 0;
        try
        {
            int read;
            while (total < start.Length && (read = inflate.Read(start, total, start.Length - total)) > 0)
            {
                total += read;
            }
        }
        catch (InvalidDataException)
        {
            // The header ends inside the stream: what inflated so far is enough.
        }

        return MetafileReader.LooksLikeMetafile(start.AsSpan(0, total));
    }

    /// <summary>Reads metafiles through Egami.Metafiles: the file is loaded from the input and drawn at its size.</summary>
    private sealed class MetafileFormatReader : IFormatReader
    {
        public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
        {
            Metafile metafile = Load(input);
            (int width, int height) = metafile.PixelSize(RenderOptions(options, input));
            return new RasterInfo
            {
                Format = Info,
                Width = width,
                Height = height,
                Layout = Rgba8.Layout,
                FrameCount = 1,
                ImageCount = 1,
                EstimatedBytes = 4L * width * height,
            };
        }

        public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            Metafile metafile = Load(input);
            MetafileRenderOptions render = RenderOptions(options, input);
            (int width, int height) = metafile.PixelSize(render);
            input.CheckMemory((long)System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>() * width * height);
            MetafileReport report = new();
            Raster<TPixel> raster = metafile.RasterizeCore<TPixel>(render, report);
            foreach (EgamiDiagnostic warning in metafile.Warnings.Concat(report.Warnings))
            {
                input.Warn(raster, warning.Id, warning.Message, warning.Offset);
            }

            return raster;
        }

        public Raster Read(EgamiInput input, ReaderOptions options) => Read<Rgba8>(input, options);

        private static MetafileRenderOptions RenderOptions(ReaderOptions options, EgamiInput input) => new()
        {
            Dpi = options is MetafileDecodeOptions m ? m.Dpi : 96,
            Context = input.Context,
        };

        /// <summary>Loads the metafile, its warnings kept for the raster's metadata rather than sent at once.</summary>
        private static Metafile Load(EgamiInput input)
        {
            using InputStream stream = new(input);
            return Metafile.Load(stream, new MetafileLoadOptions { Context = input.Context with { Diagnostics = null } });
        }
    }

    /// <summary>A read-only stream over the input, so the file is read without copying it first.</summary>
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
