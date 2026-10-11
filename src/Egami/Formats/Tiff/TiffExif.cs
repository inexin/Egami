using Egami.Metadata;

namespace Egami.Formats.Tiff;

/// <summary>
/// TIFF is the structure EXIF is built on: a page's IFD0 tags that EXIF defines (ImageDescription, Make, Model, Orientation,
/// Software, DateTime, Artist, Copyright and the rest) and its EXIF, GPS and Interoperability sub-IFDs become
/// <see cref="RasterMetadata.Exif"/>, so TIFF to JPEG keeps them. Tags Egami does not map stay raw in
/// <see cref="TiffDetails.OtherTags"/> (question 8).
/// </summary>
internal static class TiffExif
{
    /// <summary>Gets whether an IFD0 tag is one EXIF defines for the primary image, besides the pixel layout.</summary>
    internal static bool IsPrimary(int tag) => tag is TiffTags.ImageDescription or TiffTags.Make or TiffTags.Model or TiffTags.Orientation
        or TiffTags.XResolution or TiffTags.YResolution or TiffTags.ResolutionUnit or TiffTags.TransferFunction or TiffTags.Software
        or TiffTags.DateTime or TiffTags.Artist or TiffTags.HostComputer or TiffTags.WhitePoint or TiffTags.PrimaryChromaticities
        or TiffTags.Copyright or TiffTags.DocumentName or TiffTags.PageName
        or 0x4746 or 0x4749 or (>= 0x9C9B and <= 0x9C9F);

    public static void Read(TiffImage image, TiffPage page, RasterMetadata metadata, ref long kept)
    {
        TiffFile file = image.File;
        TiffIfd ifd = page.Ifd;
        TiffDetails details = (TiffDetails)metadata.FormatDetails!;
        ExifData exif = new();
        bool any = false;
        foreach (TiffEntry entry in ifd.Entries.Values.OrderBy(e => e.Tag))
        {
            if (entry.ValueAt < 0 || entry.Type > 12)
            {
                continue;
            }

            if (IsPrimary(entry.Tag))
            {
                kept += entry.ByteCount;
                file.Input.CheckMetadataBytes(kept);
                exif.Set(new ExifKey((ushort)entry.Tag, ExifIfd.Primary), (ExifType)entry.Type, file.LittleEndianBytes(entry));
                any = true;
                continue;
            }

            if (TiffTags.IsLayout(entry.Tag) && entry.Tag != TiffTags.Photoshop)
            {
                continue;
            }

            kept += entry.ByteCount;
            file.Input.CheckMetadataBytes(kept);
            details.OtherTags.Add(new TiffTag(entry.Tag, (ExifType)entry.Type, (int)entry.Count, file.LittleEndianBytes(entry)));
        }

        any |= SubIfd(file, ifd, TiffTags.ExifIfd, ExifIfd.Exif, exif, ref kept, metadata);
        any |= SubIfd(file, ifd, TiffTags.GpsIfd, ExifIfd.Gps, exif, ref kept, metadata);
        if (any)
        {
            metadata.Exif = exif;
        }

        if (!metadata.HasIptc && details.OtherTags.FirstOrDefault(t => t.Id == TiffTags.Photoshop) is TiffTag photoshop)
        {
            ReadOnlyMemory<byte> iim = PhotoshopResources.FindIptc(photoshop.Value, metadata.WarningList);
            if (!iim.IsEmpty)
            {
                metadata.SetIptcSource(iim, file.Input.Limits.MaxMetadataBytes);
            }
        }
    }

    /// <summary>Copies the entries of an EXIF, GPS or Interoperability sub-IFD into <paramref name="exif"/>.</summary>
    private static bool SubIfd(TiffFile file, TiffIfd parent, int pointer, ExifIfd target, ExifData exif, ref long kept, RasterMetadata metadata)
    {
        if (!parent.TryGet(pointer, out TiffEntry entry) || entry.ValueAt < 0)
        {
            return false;
        }

        long offset = (long)file.Integer(entry, 0);
        TiffIfd ifd;
        try
        {
            ifd = file.ReadIfd(offset);
        }
        catch (CorruptDataException)
        {
            metadata.WarningList.Add($"The {target} IFD at {offset} is damaged; it was skipped.");
            return false;
        }

        bool any = false;
        foreach (TiffEntry e in ifd.Entries.Values.OrderBy(e => e.Tag))
        {
            if (e.ValueAt < 0 || e.Type > 12)
            {
                continue;
            }

            if (target == ExifIfd.Exif && e.Tag == TiffTags.InteroperabilityIfd)
            {
                any |= offset != (long)file.Integer(e, 0) && SubIfd(file, ifd, e.Tag, ExifIfd.Interoperability, exif, ref kept, metadata);
                continue;
            }

            kept += e.ByteCount;
            file.Input.CheckMetadataBytes(kept);
            exif.Set(new ExifKey((ushort)e.Tag, target), (ExifType)e.Type, file.LittleEndianBytes(e));
            any = true;
        }

        return any;
    }
}
