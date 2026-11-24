using System.Buffers;
using System.Buffers.Binary;

using Egami.Formats.Ico;
using Egami.Formats.Tiff;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Eps;

/// <summary>
/// Reads EPS by its preview, written from Adobe's EPSF 3.0 specification: the binary header's TIFF preview through Egami's TIFF
/// reader, its metafile preview through whatever format the context has registered for metafiles, or the EPSI hex bitmap, decoded
/// here. PostScript is never run.
/// </summary>
internal sealed class EpsReader : IFormatReader
{
    internal static readonly EpsReader Instance = new();

    /// <summary>The resolution a file with only PostScript is sized at for Probe: one pixel a point.</summary>
    private const double PostScriptDpi = 72;

    /// <summary>The resolution a metafile preview is drawn at unless the options say otherwise, as Egami.Metafiles does.</summary>
    private const double MetafileDpi = 96;

    private EpsReader()
    {
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        return Run(input, options, file =>
        {
            switch (Choose(input, file, out FormatInfo? metafile))
            {
                case EpsPreviewKind.Tiff:
                    return Named(IcoReader.WithInput(input, file.TiffPreview, sub => TiffReader.Instance.ReadInfo(sub, Inner(options))));
                case EpsPreviewKind.Wmf:
                    return Named(IcoReader.WithInput(input, Placeable(file), sub => metafile!.Reader!.ReadInfo(sub, options)));
                case EpsPreviewKind.Epsi:
                    EpsiSection epsi = file.Epsi!;
                    return new RasterInfo
                    {
                        Format = EpsFormat.Info,
                        Width = epsi.Width,
                        Height = epsi.Height,
                        Layout = Gray8.Layout,
                        FrameCount = 1,
                        ImageCount = 1,
                        HasMetadata = true,
                        EstimatedBytes = (long)epsi.Width * epsi.Height,
                    };
                default:
                    (int width, int height) = BoxSize(input, file, file.WmfPreview.IsEmpty ? PostScriptDpi : MetafileDpi);
                    return new RasterInfo
                    {
                        Format = EpsFormat.Info,
                        Width = width,
                        Height = height,
                        Layout = Rgba8.Layout,
                        FrameCount = 1,
                        ImageCount = 1,
                        HasMetadata = true,
                        EstimatedBytes = 4L * width * height,
                    };
            }
        });
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return Run(input, options, file => Decode(input, file, options, inner => inner.Read<TPixel>, Epsi<TPixel>));
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        return Run(input, options, file => Decode<Raster>(input, file, options, inner => inner.Read, (i, f, o) => Epsi<Gray8>(i, f, o)));
    }

    /// <summary>Reads the whole file, which the preview's offsets need, parses it and runs <paramref name="action"/>.</summary>
    private static T Run<T>(EgamiInput input, ReaderOptions options, Func<EpsFile, T> action)
    {
        (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
        try
        {
            EpsFile file = EpsFile.Parse(data.AsMemory(0, length), input.Limits);
            if (options.Recovery == RecoveryMode.Strict && file.Warnings.Count > 0)
            {
                throw new CorruptDataException(EpsFormat.Info.Name, file.Warnings[0].Offset ?? -1, file.Warnings[0].Message);
            }

            return action(file);
        }
        finally
        {
            if (pooled)
            {
                ArrayPool<byte>.Shared.Return(data);
            }
        }
    }

    /// <summary>Picks the preview Load uses, and the context's format for a metafile preview.</summary>
    private static EpsPreviewKind Choose(EgamiInput input, EpsFile file, out FormatInfo? metafile)
    {
        metafile = null;
        EpsPreviewKind kind = file.Preview;
        if (kind == EpsPreviewKind.Wmf)
        {
            metafile = MetafileFormat(input.Context, file.WmfPreview.Span);
            if (metafile is null && file.Epsi is not null)
            {
                return EpsPreviewKind.Epsi;
            }

            return metafile is null ? EpsPreviewKind.None : kind;
        }

        return kind;
    }

    /// <summary>
    /// Finds the format in <paramref name="context"/> that reads the metafile: one registered beyond Egami's own, since none of
    /// those reads metafiles and a non-placeable metafile's first bytes can pass for one of them (TGA's).
    /// </summary>
    private static FormatInfo? MetafileFormat(EgamiContext context, ReadOnlySpan<byte> wmf)
    {
        IReadOnlyList<FormatInfo> builtIn = EgamiContext.Default.Formats;
        foreach (FormatInfo format in context.Formats)
        {
            if (format.Reader is not null && !builtIn.Contains(format) && format.Matches(wmf[..Math.Min(wmf.Length, format.SignatureLength)]))
            {
                return format;
            }
        }

        return null;
    }

    /// <summary>Decodes the preview <see cref="Choose"/> picks and describes it as EPS.</summary>
    private static TRaster Decode<TRaster>(EgamiInput input, EpsFile file, ReaderOptions options, Func<IFormatReader, Func<EgamiInput, ReaderOptions, TRaster>> read, Func<EgamiInput, EpsFile, ReaderOptions, TRaster> epsi)
        where TRaster : Raster
    {
        EpsPreviewKind kind = Choose(input, file, out FormatInfo? metafile);
        WarningList notes = new();
        if (kind == EpsPreviewKind.Epsi && file.Preview == EpsPreviewKind.Wmf)
        {
            notes.Add("Eps.MetafileSkipped", "The file's metafile preview was not drawn because no format in the context reads metafiles (register Egami.Metafiles' MetafileFormat.Info); its EPSI preview was loaded instead.");
        }

        TRaster raster = kind switch
        {
            EpsPreviewKind.Tiff => IcoReader.WithInput(input, file.TiffPreview, sub => read(TiffReader.Instance)(sub, Inner(options))),
            EpsPreviewKind.Wmf => IcoReader.WithInput(input, Placeable(file), sub => read(metafile!.Reader!)(sub, options)),
            EpsPreviewKind.Epsi => epsi(input, file, options),
            _ => throw NoImage(input, file),
        };

        try
        {
            Describe(raster.Metadata, file, kind, options, input.Limits.MaxMetadataBytes);
            raster.Metadata.WarningList.Prepend(file.WarningList);
            raster.Metadata.WarningList.AddRange(notes);
            string what = kind switch { EpsPreviewKind.Tiff => "TIFF", EpsPreviewKind.Wmf => "metafile", _ => "EPSI" };
            raster.Metadata.WarningList.Add("Eps.PreviewLoaded", $"The image is the file's {what} preview, not the drawing its PostScript makes, which Egami does not run.");
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>The error for a file with no preview Egami can show.</summary>
    private static UnsupportedFormatException NoImage(EgamiInput input, EpsFile file)
    {
        string message = !file.WmfPreview.IsEmpty
            ? "The EPS file's only preview is a Windows metafile, and no format in the context reads metafiles: register Egami.Metafiles' MetafileFormat.Info with EgamiContext.WithFormat to draw it. Egami does not run PostScript."
            : file.IsEncapsulated
                ? "The EPS file holds only PostScript, with no TIFF, metafile or EPSI preview, and PostScript is not run: Egami has no image to load. Raster.Probe gives its bounding box size."
                : "The data is PostScript, not EPS with a preview, and PostScript is not run: Egami has no image to load.";
        input.Emit(DiagnosticLevel.Debug, "Eps.NoPreview", $"{message} The load fails with UnsupportedFormatException.");
        return new UnsupportedFormatException(message);
    }

    /// <summary>The bounding box's size in pixels at <paramref name="dpi"/>, for a file whose preview Probe cannot read.</summary>
    private static (int Width, int Height) BoxSize(EgamiInput input, EpsFile file, double dpi)
    {
        if (file.BoundingBox is not EpsBoundingBox box)
        {
            throw NoImage(input, file);
        }

        double width = Math.Ceiling((box.Width * dpi / 72) - 1e-3), height = Math.Ceiling((box.Height * dpi / 72) - 1e-3);
        return ((int)Math.Clamp(width, 1, int.MaxValue), (int)Math.Clamp(height, 1, int.MaxValue));
    }

    /// <summary>Puts what the EPS file says into the preview's metadata, over what the preview's own format said.</summary>
    private static void Describe(RasterMetadata metadata, EpsFile file, EpsPreviewKind kind, ReaderOptions options, long maxMetadataBytes)
    {
        metadata.FormatName = EpsFormat.Info.Name;
        metadata.FormatDetails = new EpsDetails
        {
            Preview = kind,
            BoundingBox = file.BoundingBox,
            Creator = file.Creator,
            Title = file.Title,
            CreationDate = file.CreationDate,
        };
        if (!options.ReadMetadata)
        {
            return;
        }

        AddText(metadata, "Title", file.Title);
        AddText(metadata, "Creator", file.Creator);
        AddText(metadata, "CreationDate", file.CreationDate);
        if (!file.Xmp.IsEmpty)
        {
            metadata.Xmp = file.Xmp.ToArray();
        }

        if (!file.IccProfile.IsEmpty && metadata.IccProfile.IsEmpty)
        {
            metadata.IccProfile = file.IccProfile.ToArray();
        }

        if (!file.PhotoshopResources.IsEmpty && !metadata.HasIptc)
        {
            ReadOnlyMemory<byte> iim = Egami.Metadata.PhotoshopResources.FindIptc(file.PhotoshopResources, metadata.WarningList);
            if (!iim.IsEmpty)
            {
                metadata.SetIptcSource(iim.ToArray(), maxMetadataBytes);
            }
        }
    }

    private static void AddText(RasterMetadata metadata, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            metadata.Text.Add(new TextEntry(key, value));
        }
    }

    private static RasterInfo Named(RasterInfo info) => info with { Format = EpsFormat.Info, HasMetadata = true };

    /// <summary>The options a preview's TIFF reader gets: the caller's recovery and metadata choices, nothing that reshapes.</summary>
    private static ReaderOptions Inner(ReaderOptions options) => IcoReader.Inner(options);

    /// <summary>
    /// Returns the metafile preview with a placeable header whose units an inch make it the bounding box's size at the options'
    /// resolution, as EPSF asks ("scale the picture to fit within the %%BoundingBox"). The extent is the placeable header's own,
    /// or the window extent the metafile sets; without either, or a bounding box, the metafile is handed over as it is.
    /// </summary>
    internal static ReadOnlyMemory<byte> Placeable(EpsFile file)
    {
        ReadOnlySpan<byte> wmf = file.WmfPreview.Span;
        bool placeable = wmf.Length >= 22 && BinaryPrimitives.ReadUInt32LittleEndian(wmf) == 0x9AC6CDD7;
        ReadOnlySpan<byte> body = placeable ? wmf[22..] : wmf;
        short left, top, right, bottom;
        if (placeable)
        {
            (left, top, right, bottom) = (BinaryPrimitives.ReadInt16LittleEndian(wmf[6..]), BinaryPrimitives.ReadInt16LittleEndian(wmf[8..]),
                BinaryPrimitives.ReadInt16LittleEndian(wmf[10..]), BinaryPrimitives.ReadInt16LittleEndian(wmf[12..]));
        }
        else if (!WindowBounds(body, out left, out top, out right, out bottom))
        {
            return file.WmfPreview;
        }

        int units = Math.Abs(right - left);
        if (file.BoundingBox is not EpsBoundingBox box || units == 0)
        {
            return file.WmfPreview;
        }

        double inch = Math.Round(units * 72 / box.Width);
        if (inch is < 1 or > ushort.MaxValue)
        {
            return file.WmfPreview;
        }

        byte[] result = new byte[22 + body.Length];
        Span<byte> header = result;
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0x9AC6CDD7);
        BinaryPrimitives.WriteInt16LittleEndian(header[6..], Math.Min(left, right));
        BinaryPrimitives.WriteInt16LittleEndian(header[8..], Math.Min(top, bottom));
        BinaryPrimitives.WriteInt16LittleEndian(header[10..], Math.Max(left, right));
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], Math.Max(top, bottom));
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], (ushort)inch);
        ushort checksum = 0;
        for (int i = 0; i < 20; i += 2)
        {
            checksum ^= BinaryPrimitives.ReadUInt16LittleEndian(header[i..]);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(header[20..], checksum);
        body.CopyTo(result.AsSpan(22));
        return result;
    }

    /// <summary>Finds the window origin and extent a metafile sets (META_SETWINDOWORG, META_SETWINDOWEXT), from its first records.</summary>
    private static bool WindowBounds(ReadOnlySpan<byte> wmf, out short left, out short top, out short right, out short bottom)
    {
        (left, top, right, bottom) = (0, 0, 0, 0);
        if (wmf.Length < 18 || BinaryPrimitives.ReadUInt16LittleEndian(wmf[2..]) != 9)
        {
            return false;
        }

        int x = 0, y = 0, width = 0, height = 0;
        bool extent = false;
        int position = 18;
        for (int records = 0; records < 4096 && position + 6 <= wmf.Length; records++)
        {
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(wmf[position..]);
            ushort function = BinaryPrimitives.ReadUInt16LittleEndian(wmf[(position + 4)..]);
            if (size < 3 || function == 0 || size > (uint)(wmf.Length - position) / 2)
            {
                break;
            }

            if (size >= 5 && function is 0x020B or 0x020C)
            {
                short first = BinaryPrimitives.ReadInt16LittleEndian(wmf[(position + 6)..]), second = BinaryPrimitives.ReadInt16LittleEndian(wmf[(position + 8)..]);
                if (function == 0x020B)
                {
                    (y, x) = (first, second);
                }
                else if (!extent)
                {
                    (height, width, extent) = (first, second, true);
                }
            }

            position += (int)size * 2;
        }

        if (!extent || width == 0 || height == 0 || x + width is < short.MinValue or > short.MaxValue || y + height is < short.MinValue or > short.MaxValue)
        {
            return false;
        }

        (left, top, right, bottom) = ((short)x, (short)y, (short)(x + width), (short)(y + height));
        return true;
    }

    /// <summary>
    /// Decodes the EPSI bitmap: rows of hex digits, top row first, each row padded to whole bytes, 0 white and the largest value
    /// black, as EPSF's EPSI rules give them. Missing data is white.
    /// </summary>
    private static Raster<TPixel> Epsi<TPixel>(EgamiInput input, EpsFile file, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        EpsiSection epsi = file.Epsi!;
        input.Limits.CheckFrameSize(epsi.Width, epsi.Height);
        ReadOnlySpan<byte> hex = file.PostScript.Span.Slice(epsi.Start, epsi.Length);
        int rowBytes = (int)(((long)epsi.Width * epsi.Depth + 7) / 8);
        Raster<TPixel> raster = input.CreateRaster<TPixel>(epsi.Width, epsi.Height);
        try
        {
            byte[] packed = new byte[rowBytes];
            byte[] gray = new byte[epsi.Width];
            int max = (1 << epsi.Depth) - 1;
            bool missing = false;
            for (int y = 0; y < epsi.Height; y++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                TakeHex(ref hex, packed, out int written);
                if (written < rowBytes)
                {
                    packed.AsSpan(written).Clear();
                    missing = true;
                }

                for (int x = 0; x < epsi.Width; x++)
                {
                    long bit = (long)x * epsi.Depth;
                    int value = (packed[bit >> 3] >> (8 - epsi.Depth - (int)(bit & 7))) & max;
                    gray[x] = (byte)(255 - (value * 255 / max));
                }

                PixelConvert.Convert<Gray8, TPixel>(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, Gray8>(gray), raster.Row(y));
            }

            if (missing)
            {
                if (options.Recovery == RecoveryMode.Strict)
                {
                    throw new CorruptDataException(EpsFormat.Info.Name, -1, "The EPSI preview has fewer hex digits than its size needs.") { IsTruncation = true };
                }

                input.Warn(raster, "Eps.PreviewTruncated", "The EPSI preview has fewer hex digits than its size needs; the missing pixels are white.");
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Decodes hex digits from the front of <paramref name="hex"/> into <paramref name="row"/>, skipping anything else.</summary>
    private static void TakeHex(ref ReadOnlySpan<byte> hex, Span<byte> row, out int written)
    {
        written = 0;
        int high = -1, i = 0;
        for (; i < hex.Length && written < row.Length; i++)
        {
            byte c = hex[i];
            int nibble = c switch
            {
                >= (byte)'0' and <= (byte)'9' => c - '0',
                >= (byte)'A' and <= (byte)'F' => c - 'A' + 10,
                >= (byte)'a' and <= (byte)'f' => c - 'a' + 10,
                _ => -1,
            };
            if (nibble < 0)
            {
                continue;
            }

            if (high < 0)
            {
                high = nibble;
            }
            else
            {
                row[written++] = (byte)((high << 4) | nibble);
                high = -1;
            }
        }

        hex = hex[i..];
    }
}
