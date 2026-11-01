using Egami.Metadata;

namespace Egami.Formats.Tiff;

/// <summary>Maps a page's tags onto <see cref="RasterMetadata"/>: format details, resolution, ICC, XMP, IPTC and EXIF.</summary>
internal static class TiffMetadata
{
    public static void Read(TiffImage image, TiffPage page, RasterMetadata metadata, ReaderOptions options)
    {
        TiffFile file = image.File;
        TiffIfd ifd = page.Ifd;
        metadata.FormatDetails = Details(image, page);
        metadata.ColorSpace = page.Float && page.Compression is not (TiffCodes.SgiLog or TiffCodes.SgiLog24) || page.Photometric is 32844 or 32845
            ? ColorSpaceHint.Linear
            : ColorSpaceHint.Srgb;
        if (ifd.TryGet(TiffTags.XResolution, out TiffEntry xEntry) && ifd.TryGet(TiffTags.YResolution, out TiffEntry yEntry)
            && xEntry.ValueAt >= 0 && yEntry.ValueAt >= 0)
        {
            double x = file.Numbers(xEntry, 16)[0];
            double y = file.Numbers(yEntry, 16)[0];
            ResolutionUnit unit = file.Integer(ifd, TiffTags.ResolutionUnit, 2) switch
            {
                1 => ResolutionUnit.None,
                3 => ResolutionUnit.PerCentimeter,
                _ => ResolutionUnit.PerInch,
            };
            if (x > 0 && y > 0 && double.IsFinite(x) && double.IsFinite(y))
            {
                metadata.Resolution = new Resolution(x, y, unit);
            }
        }

        if (!options.ReadMetadata)
        {
            return;
        }

        long kept = 0;
        byte[]? Blob(int tag)
        {
            if (!ifd.TryGet(tag, out TiffEntry entry) || entry.ValueAt < 0)
            {
                return null;
            }

            kept += entry.ByteCount;
            file.Input.CheckMetadataBytes(kept);
            return file.Bytes(entry);
        }

        if (Blob(TiffTags.IccProfile) is byte[] icc)
        {
            metadata.IccProfile = icc;
            if (!page.Float)
            {
                metadata.ColorSpace = ColorSpaceHint.EmbeddedIcc;
            }
        }

        if (Blob(TiffTags.Xmp) is byte[] xmp)
        {
            metadata.Xmp = xmp;
        }

        if (ifd.TryGet(TiffTags.Iptc, out TiffEntry iptcEntry) && iptcEntry.ValueAt >= 0)
        {
            // IPTC is stored as BYTE, UNDEFINED or (by Photoshop) LONG; the bytes are the IIM records either way.
            kept += iptcEntry.ByteCount;
            file.Input.CheckMetadataBytes(kept);
            byte[] iim = file.Bytes(iptcEntry);
            metadata.SetIptcSource(iim, file.Input.Limits.MaxMetadataBytes);
        }

        TiffExif.Read(image, page, metadata, ref kept);
    }

    private static TiffDetails Details(TiffImage image, TiffPage page)
    {
        TiffDetails details = new()
        {
            PageIndex = page.Index,
            IsReducedResolution = page.IsReduced,
            ByteOrder = image.File.BigEndian ? TiffByteOrder.BigEndian : TiffByteOrder.LittleEndian,
            Layout = image.File.Big ? TiffLayout.Big : TiffLayout.Classic,
            Compression = (TiffCompression)page.Compression,
            Predictor = (TiffPredictor)page.Predictor,
            Photometric = (TiffPhotometric)page.Photometric,
            BitsPerSample = page.Compression is TiffCodes.SgiLog or TiffCodes.SgiLog24
                ? (int)image.File.Integer(page.Ifd, TiffTags.BitsPerSample, 16)
                : page.Bits,
            SampleFormat = (TiffSampleFormat)(int)image.File.Integer(page.Ifd, TiffTags.SampleFormat, 1) is var format
                && format is TiffSampleFormat.UnsignedInteger or TiffSampleFormat.SignedInteger or TiffSampleFormat.Float
                ? format
                : TiffSampleFormat.UnsignedInteger,
            Planar = page.Planar,
            TileWidth = page.Tiled ? page.UnitWidth : null,
            TileHeight = page.Tiled ? page.UnitHeight : null,
            RowsPerStrip = page.Tiled ? null : page.UnitHeight,
            ExtraSamples = [.. page.Extra.Select(e => (TiffExtraSample)e)],
            InkNames = [.. page.InkNames],
            Unchanged = true,
            JpegLossless = page.LosslessJpeg,
        };
        return details;
    }
}
