using Egami.Colors;
using Egami.Formats.Avif;
using Egami.Metadata;

namespace Egami.Formats.Heif;

/// <summary>A coded image item: its data, its codec configuration property (av1C or hvcC) and any properties that follow it.</summary>
internal sealed record HeifCodedItem(ReadOnlyMemory<byte> Data, byte[] Configuration, byte[]? Extra = null);

/// <summary>
/// An image coded whole or as a grid of equal cells in raster order (those past the right and bottom edges padded; the grid's
/// output size crops them), at a bit depth.
/// </summary>
internal sealed record HeifCodedImage(HeifCodedItem[] Cells, int Columns, int Rows, int CellWidth, int CellHeight, int Depth)
{
    internal bool IsGrid => Cells.Length > 1;
}

/// <summary>What a codec brings to the layout: the type of its coded items and the auxiliary type of an alpha image.</summary>
internal sealed record HeifImageCodec(string ItemType, string AlphaUrn);

/// <summary>A coded gain map, with its metadata and the alternate rendition's color.</summary>
internal sealed record HeifGainMapImage(HeifCodedItem Item, int Width, int Height, bool Monochrome, GainMapInfo Info, int AlternatePrimaries, int AlternateTransfer);

/// <summary>
/// A coded thumbnail of the image, linked to it by a thmb reference, with the matrix and range it was coded with (a lossless or
/// gray image's thumbnail is YCbCr, unlike the image).
/// </summary>
internal sealed record HeifThumbnailImage(HeifCodedItem Item, int Width, int Height, int Depth, bool Monochrome, int Matrix, bool FullRange, byte[]? Clap = null);

/// <summary>What goes into a file beside the pixels, as the options allow.</summary>
internal sealed record HeifImageMetadata(ReadOnlyMemory<byte> Icc, ReadOnlyMemory<byte> Exif, ReadOnlyMemory<byte> Xmp, ImageOrientation Orientation, HdrMetadata? Hdr)
{
    internal static readonly HeifImageMetadata None = new(default, default, default, ImageOrientation.Normal, null);

    /// <summary>
    /// Gets the metadata to write: the ICC profile only when the colors have no code points and it is not plain sRGB (AVIF design
    /// question 13), EXIF and XMP as they are, the orientation for irot and imir, and the HDR luminances for clli and mdcv.
    /// </summary>
    internal static HeifImageMetadata From(RasterMetadata metadata, WriterOptions options)
    {
        if (options.StripMetadata)
        {
            return None with { Hdr = metadata.Hdr };
        }

        ReadOnlyMemory<byte> icc = metadata.CodePoints is null ? new AvifWriterOptions { OmitSrgbProfile = true }.ProfileToWrite(metadata) : default;
        return new HeifImageMetadata(icc, metadata.Exif?.ToBytes() ?? default, metadata.Xmp, metadata.Exif?.Orientation ?? ImageOrientation.Normal, metadata.Hdr);
    }
}

/// <summary>
/// The item layout of a still HEIF image that AVIF and HEIC share, as libavif lays an AVIF out: ftyp, one meta box, then the
/// coded data in one mdat. The color item (or grid over hidden cells) carries ispe, pixi, the codec configuration and an nclx colr
/// (and the ICC profile, HDR luminances, irot and imir); an alpha item, a monochrome image linked by auxl; a gain map as a tmap
/// item of the alternate rendition over the color item and a hidden gain map item, in an altr group; a thumbnail linked by thmb;
/// Exif and XMP items linked by cdsc. Only the coded items and the brands differ between the codecs.
/// </summary>
internal static class HeifImageLayout
{
    /// <summary>Writes the file of an image coded whole or as a grid.</summary>
    internal static void WriteFile(Stream output, HeifImageCodec codec, string majorBrand, IReadOnlyList<string> brands, int width, int height, bool monochrome,
        bool fullRange, HeifCodedImage color, HeifCodedImage? alpha, int primaries, int transfer, int matrix, HeifImageMetadata? metadata = null,
        HeifGainMapImage? gainMap = null, HeifThumbnailImage? thumbnail = null, byte[]? clap = null)
    {
        metadata ??= HeifImageMetadata.None;
        HeifWriter heif = new(majorBrand, [.. brands]);
        int ispe = heif.AddProperty(HeifWriter.SpatialExtents(width, height));
        byte[] nclx = HeifWriter.ColorNclx(primaries, transfer, matrix, fullRange);
        uint colorItem = AddImage(heif, codec, color, width, height, "Color", hidden: false, ispe, monochrome ? 1 : 3, nclx);
        heif.PrimaryItemId = colorItem;
        if (!metadata.Icc.IsEmpty)
        {
            heif.AddProperty(colorItem, HeifWriter.ColorIcc(metadata.Icc.Span));
        }

        heif.AddProperty(colorItem, nclx);
        AddHdrProperties(heif, colorItem, metadata.Hdr);
        int clapProperty = clap is null ? -1 : heif.AddProperty(clap);
        if (clapProperty >= 0)
        {
            heif.Associate(colorItem, clapProperty, essential: true);
        }

        AddOrientation(heif, colorItem, metadata.Orientation);
        if (alpha is not null)
        {
            byte[] auxC = HeifWriter.AuxiliaryType(codec.AlphaUrn);
            uint alphaItem = AddImage(heif, codec, alpha, width, height, "Alpha", hidden: true, ispe, 1, auxC);
            heif.AddProperty(alphaItem, auxC);
            heif.Reference("auxl", alphaItem, colorItem);
            if (clapProperty >= 0)
            {
                heif.Associate(alphaItem, clapProperty, essential: true);
            }

            AddOrientation(heif, alphaItem, metadata.Orientation);
        }

        if (gainMap is not null)
        {
            // libavif's layout: a tmap item of the alternate rendition, derived from the color item and the hidden gain map item,
            // in an altr group that prefers it, so readers without gain maps show the base image.
            byte[] iso = Jpeg.JpegGainMapWriter.Iso(gainMap.Info.BaseRenditionIsHdr
                ? gainMap.Info with { BaseRenditionIsHdr = false, HdrCapacityMin = gainMap.Info.HdrCapacityMax, HdrCapacityMax = gainMap.Info.HdrCapacityMin }
                : gainMap.Info);
            uint tmapItem = heif.AddItem("tmap", (byte[])[0, .. iso], "GMap");
            uint mapItem = heif.AddItem(codec.ItemType, gainMap.Item.Data, "GMap", hidden: true);
            heif.Associate(tmapItem, ispe);
            heif.AddProperty(tmapItem, HeifWriter.ColorNclx(gainMap.AlternatePrimaries, gainMap.AlternateTransfer, matrix, true));
            AddOrientation(heif, tmapItem, metadata.Orientation);
            heif.AddProperty(mapItem, HeifWriter.SpatialExtents(gainMap.Width, gainMap.Height));
            heif.AddProperty(mapItem, HeifWriter.PixelInformation(8, gainMap.Monochrome ? 1 : 3));
            heif.AddProperty(mapItem, gainMap.Item.Configuration, essential: true);
            heif.AddProperty(mapItem, HeifWriter.ColorNclx(2, 2, gainMap.Monochrome ? 2 : 0, true));
            AddOrientation(heif, mapItem, metadata.Orientation);
            heif.Reference("dimg", tmapItem, colorItem, mapItem);
            heif.Group("altr", tmapItem, colorItem);
        }

        if (thumbnail is not null)
        {
            uint thumbItem = heif.AddItem(codec.ItemType, thumbnail.Item.Data, "Thumbnail");
            heif.AddProperty(thumbItem, HeifWriter.SpatialExtents(thumbnail.Width, thumbnail.Height));
            heif.AddProperty(thumbItem, HeifWriter.PixelInformation(thumbnail.Depth, thumbnail.Monochrome ? 1 : 3));
            heif.AddProperty(thumbItem, thumbnail.Item.Configuration, essential: true);
            heif.AddProperty(thumbItem, HeifWriter.ColorNclx(primaries, transfer, thumbnail.Matrix, thumbnail.FullRange));
            if (thumbnail.Clap is byte[] thumbClap)
            {
                heif.AddProperty(thumbItem, thumbClap, essential: true);
            }

            AddOrientation(heif, thumbItem, metadata.Orientation);
            heif.Reference("thmb", thumbItem, colorItem);
        }

        if (!metadata.Exif.IsEmpty)
        {
            // An Exif item starts with the offset of its TIFF header, 0 here.
            uint exifItem = heif.AddItem("Exif", (byte[])[0, 0, 0, 0, .. metadata.Exif.Span], "Exif");
            heif.Reference("cdsc", exifItem, colorItem);
        }

        if (!metadata.Xmp.IsEmpty)
        {
            uint xmpItem = heif.AddItem("mime", metadata.Xmp, "XMP", contentType: "application/rdf+xml");
            heif.Reference("cdsc", xmpItem, colorItem);
        }

        heif.Write(output);
    }

    /// <summary>
    /// Adds an image's items: one coded item, or a grid item (its layout in the idat) over hidden coded cells, which carry their
    /// own size, pixel information, configuration and <paramref name="cellProperty"/>. Returns the image's item, which has its size
    /// and pixel information; the caller adds the rest.
    /// </summary>
    internal static uint AddImage(HeifWriter heif, HeifImageCodec codec, HeifCodedImage image, int width, int height, string name, bool hidden, int ispe,
        int channels, byte[] cellProperty)
    {
        int depth = image.Depth;
        if (!image.IsGrid)
        {
            HeifCodedItem cell = image.Cells[0];
            uint item = heif.AddItem(codec.ItemType, cell.Data, name, hidden);
            heif.Associate(item, ispe);
            heif.AddProperty(item, HeifWriter.PixelInformation(depth, channels));
            heif.AddProperty(item, cell.Configuration, essential: true);
            if (cell.Extra is byte[] extra)
            {
                heif.AddProperty(item, extra);
            }

            return item;
        }

        // ImageGrid (HEIF 6.6.2.3): version, flags (32-bit sizes), rows and columns less one, the output size.
        bool wide = width > ushort.MaxValue || height > ushort.MaxValue;
        byte[] layout = wide
            ? [0, 1, (byte)(image.Rows - 1), (byte)(image.Columns - 1), .. HeifWriter.U32((uint)width), .. HeifWriter.U32((uint)height)]
            : [0, 0, (byte)(image.Rows - 1), (byte)(image.Columns - 1), (byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height];
        uint grid = heif.AddItem("grid", layout, name, hidden, inIdat: true);
        uint[] cells = new uint[image.Cells.Length];
        int cellIspe = heif.AddProperty(HeifWriter.SpatialExtents(image.CellWidth, image.CellHeight));
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = heif.AddItem(codec.ItemType, image.Cells[i].Data, name, hidden: true);
            heif.Associate(cells[i], cellIspe);
            heif.AddProperty(cells[i], HeifWriter.PixelInformation(depth, channels));
            heif.AddProperty(cells[i], image.Cells[i].Configuration, essential: true);
            heif.AddProperty(cells[i], cellProperty);
        }

        heif.Reference("dimg", grid, cells);
        heif.Associate(grid, ispe);
        heif.AddProperty(grid, HeifWriter.PixelInformation(depth, channels));
        return grid;
    }

    /// <summary>The clli and mdcv properties of the HDR luminances the metadata holds.</summary>
    internal static void AddHdrProperties(HeifWriter heif, uint item, HdrMetadata? hdr)
    {
        if (hdr is null)
        {
            return;
        }

        if (hdr.MaxContentLightLevel is double cll || hdr.MaxFrameAverageLightLevel is not null)
        {
            int maxCll = (int)Math.Clamp(Math.Round(hdr.MaxContentLightLevel ?? 0), 0, 65535);
            int maxFall = (int)Math.Clamp(Math.Round(hdr.MaxFrameAverageLightLevel ?? 0), 0, 65535);
            heif.AddProperty(item, HeifWriter.ContentLightLevel(maxCll, maxFall));
        }

        if (hdr.MasteringPrimaries is ColorPrimaries p && hdr.MasteringMaxLuminance is double max)
        {
            // Green, blue, red, as SMPTE ST 2086 orders them, in 0.00002 units; luminance in 0.0001 cd/m².
            ushort[] xy = [Unit(p.Green.X), Unit(p.Green.Y), Unit(p.Blue.X), Unit(p.Blue.Y), Unit(p.Red.X), Unit(p.Red.Y)];
            heif.AddProperty(item, HeifWriter.MasteringDisplay(xy, Unit(p.White.X), Unit(p.White.Y), Luminance(max), Luminance(hdr.MasteringMinLuminance ?? 0)));
        }

        static ushort Unit(double v) => (ushort)Math.Clamp(Math.Round(v * 50000), 0, 65535);

        static uint Luminance(double v) => (uint)Math.Clamp(Math.Round(v * 10000), 0, uint.MaxValue);
    }

    /// <summary>The irot and imir properties of an EXIF orientation: a mirror about the vertical axis or none, after a rotation.</summary>
    internal static void AddOrientation(HeifWriter heif, uint item, ImageOrientation orientation)
    {
        if (orientation is ImageOrientation.Normal or ImageOrientation.Unknown)
        {
            return;
        }

        // The rotation (and mirror) whose display the reader's AvifFile.Orient gives as this orientation.
        for (int turns = 0; turns < 4; turns++)
        {
            foreach (int? mirror in (ReadOnlySpan<int?>)[null, 0, 1])
            {
                if (AvifFile.Orient(turns, mirror) == orientation)
                {
                    if (turns != 0)
                    {
                        heif.AddProperty(item, HeifWriter.Rotation(turns), essential: true);
                    }

                    if (mirror is int axis)
                    {
                        heif.AddProperty(item, HeifWriter.Mirror(axis), essential: true);
                    }

                    return;
                }
            }
        }
    }

    /// <summary>A clap property cropping a coded image to <paramref name="width"/> x <paramref name="height"/> at its top-left.</summary>
    internal static byte[] CleanAperture(int codedWidth, int codedHeight, int width, int height)
    {
        // The aperture's size, then its center's offset from the image's center, as fractions (ISO/IEC 14496-12, 12.1.4.2).
        int horizontalOffset = width - codedWidth, verticalOffset = height - codedHeight;
        return HeifWriter.Box("clap", [.. HeifWriter.U32((uint)width), .. HeifWriter.U32(1), .. HeifWriter.U32((uint)height), .. HeifWriter.U32(1),
            .. HeifWriter.U32((uint)horizontalOffset), .. HeifWriter.U32(2), .. HeifWriter.U32((uint)verticalOffset), .. HeifWriter.U32(2)]);
    }
}
