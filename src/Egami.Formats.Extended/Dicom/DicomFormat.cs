using System.Diagnostics.CodeAnalysis;

using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// DICOM (PS3.10 files, .dcm): medical images with their dataset. Registered with
/// <see cref="ExtendedContextExtensions.WithDicom"/>; in preview. A load gives one frame as a viewer shows it (see
/// <see cref="DicomReaderOptions"/>), each frame of a multi-frame file is an image of its own for
/// <see cref="Raster.LoadAll(Stream, EgamiContext?, ReaderOptions?)"/>, and <see cref="DicomFile"/> reads every element.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-pixels"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public static class DicomFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extensions, signature and reader.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "DICOM",
        MimeTypes = ["application/dicom"],
        Extensions = ["dcm", "dicom", "dic"],
        SignatureLength = 132,
        Matches = DicomParser.IsPart10,
        Reader = new DicomReader(),
    };
}

/// <summary>
/// Reads DICOM pixel data: the file is read whole and parsed (<see cref="DicomFile"/>), then the frame asked for is decoded to
/// its stored values and taken through the display pipeline. Each frame is an image of its own.
/// </summary>
internal sealed class DicomReader : ExtendedReader, IMultiImageReader, IFormatReader
{
    Raster IFormatReader.Read(EgamiInput input, ReaderOptions options)
    {
        DicomReaderOptions dicom = Parsed(input, options);
        return Whole(input, dicom) ?? Read(input, dicom);
    }

    Raster<TPixel> IFormatReader.Read<TPixel>(EgamiInput input, ReaderOptions options)
    {
        DicomReaderOptions dicom = Parsed(input, options);
        if (Whole(input, dicom) is Raster whole)
        {
            if (whole is Raster<TPixel> typed)
            {
                return typed;
            }

            whole.Dispose();
        }

        return Read<TPixel>(input, dicom);
    }
    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        DicomReaderOptions dicom = Options(options);
        DicomFile file = dicom.File ?? DicomFile.Read(input, options);
        DicomImageInfo info = DicomImageInfo.Read(file);
        PixelLayoutInfo layout = DicomDisplay.LayoutOf(info, dicom);
        return new RasterInfo
        {
            Format = DicomFormat.Info,
            Width = info.Columns,
            Height = info.Rows,
            Layout = layout,
            FrameCount = 1,
            ImageCount = info.Frames,
            HasMetadata = true,
            EstimatedBytes = (long)info.Columns * info.Rows * layout.BytesPerPixel,
            Access = RasterAccess.Buffered,
            WorkingBytes = (long)info.Columns * info.Rows * info.SamplesPerPixel * sizeof(int),
        };
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        DicomReaderOptions dicom = Options(options);
        DicomFile file = dicom.File ?? DicomFile.Read(input, options);
        return Open(input, file, dicom);
    }

    /// <summary>Opens frame <see cref="DicomReaderOptions.Frame"/> of a file already read.</summary>
    internal static RowReader Open(EgamiInput input, DicomFile file, DicomReaderOptions options)
    {
        DicomImageInfo info = DicomImageInfo.Read(file);
        if (options.Frame >= info.Frames)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Frame, $"The file has {info.Frames} frames.");
        }

        DicomDisplay display = new(info, options.Frame, options);
        PixelLayoutInfo layout = display.Layout;
        return layout.ChannelOrder switch
        {
            PixelChannelOrder.Rgb when layout.BitsPerChannel > 8 => new Rows<Rgb16>(input, options, file, info, display),
            PixelChannelOrder.Rgb => new Rows<Rgb8>(input, options, file, info, display),
            _ when layout.ChannelType == PixelChannelType.Float32 => new Rows<GrayF32>(input, options, file, info, display),
            _ when layout.BitsPerChannel > 8 => new Rows<Gray16>(input, options, file, info, display),
            _ => new Rows<Gray8>(input, options, file, info, display),
        };
    }

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        DicomReaderOptions dicom = Options(options);
        DicomFile file = dicom.File ?? DicomFile.Read(input, options);
        int frames = DicomImageInfo.Read(file).Frames;
        return [.. Enumerable.Range(0, frames).Select(f => dicom with { Frame = f, File = file })];
    }

    /// <summary>Returns the caller's options as DICOM options holding the parsed file.</summary>
    private static DicomReaderOptions Parsed(EgamiInput input, ReaderOptions options)
    {
        DicomReaderOptions dicom = Options(options);
        return dicom.File is null ? dicom with { File = DicomFile.Read(input, options) } : dicom;
    }

    /// <summary>
    /// Returns a shown JPEG frame as the JPEG reader decoded it, mapped in place, with no second raster or copy, or
    /// <see langword="null"/> for a frame that goes row by row through <see cref="Open(EgamiInput, DicomFile, DicomReaderOptions)"/>.
    /// </summary>
    private static Raster? Whole(EgamiInput input, DicomReaderOptions options)
    {
        DicomFile file = options.File!;
        DicomImageInfo info = DicomImageInfo.Read(file);
        if (options.Values != DicomValues.Display || options.Frame >= info.Frames || DicomPixelDecoder.CodecFor(file.TransferSyntax.Uid) != DicomPixelDecoder.DicomCodec.Jpeg)
        {
            return null;
        }

        List<(string Name, string Message)> notes = [];
        void Note(string name, string message) => notes.Add((name, message));
        DicomDisplay display = new(info, options.Frame, options);
        DicomPixelDecoder decoder = new(info, input, options, Note);
        Raster? raster = display.Layout.ChannelOrder == PixelChannelOrder.Rgb
            ? display.Layout.BitsPerChannel == 8 ? decoder.DecodeJpegRgb(options.Frame, clean: true) : null
            : decoder.DecodeJpegGray(options.Frame, display, Note);
        if (raster is null)
        {
            return null;
        }

        RasterMetadata metadata = raster.Metadata;
        metadata.FormatName = DicomFormat.Info.Name;
        metadata.FormatDetails = display.Details.Copy();
        metadata.ColorSpace = default;
        metadata.Resolution = null;
        foreach (EgamiDiagnostic warning in file.Warnings)
        {
            input.Warn(raster, warning.Id, warning.Message, warning.Offset);
        }

        foreach ((string name, string message) in notes)
        {
            input.Warn(raster, $"Dicom.{name}", message);
        }

        return raster;
    }

    /// <summary>Returns the caller's options as DICOM options, the defaults for the DICOM ones.</summary>
    private static DicomReaderOptions Options(ReaderOptions options)
    {
        return options as DicomReaderOptions ?? new DicomReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Hdr = options.Hdr,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
        };
    }

    private sealed class Rows<TNatural> : ExtendedRows<TNatural, TNatural>
        where TNatural : unmanaged, IPixelType<TNatural>
    {
        private readonly DicomRender _render;

        public Rows(EgamiInput input, DicomReaderOptions options, DicomFile file, DicomImageInfo info, DicomDisplay display)
            : base(input, options, DicomFormat.Info, "Dicom", info.Columns, info.Rows)
        {
            foreach (EgamiDiagnostic warning in file.Warnings)
            {
                input.Warn(Metadata, warning.Id, warning.Message, warning.Offset);
            }

            void Note(string name, string message) => input.Warn(Metadata, $"Dicom.{name}", message);
            DicomPixelDecoder decoder = new(info, input, options, Note);
            Raster<Rgb8>? rgb = options.Values == DicomValues.Display && typeof(TNatural) == typeof(Rgb8) ? decoder.DecodeJpegRgb(options.Frame) : null;
            _render = rgb is not null ? new RasterRender(rgb) : display.Render(decoder.Decode(options.Frame), Note);
            Metadata.FormatDetails = display.Details;
        }

        protected override void Dispose(bool disposing)
        {
            (_render as IDisposable)?.Dispose();
            base.Dispose(disposing);
        }

        protected override bool DecodeRow(int y, Span<TNatural> row)
        {
            _render.Row(y, MemoryMarshal.AsBytes(row));
            return true;
        }
    }
}
