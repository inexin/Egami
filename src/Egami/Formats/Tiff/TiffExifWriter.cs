using Egami.Metadata;

namespace Egami.Formats.Tiff;

/// <summary>Writes the EXIF, GPS and Interoperability sub-IFDs of a raster's EXIF into the file, before the page's IFD that points at them.</summary>
internal static class TiffExifWriter
{
    public static void AddSubIfds(TiffIfdWriter page, ExifData exif)
    {
        List<ExifEntry> exifTags = [.. exif.Entries.Where(e => e.Key.Ifd == ExifIfd.Exif && e.Key.Tag != TiffTags.InteroperabilityIfd)];
        List<ExifEntry> gpsTags = [.. exif.Entries.Where(e => e.Key.Ifd == ExifIfd.Gps)];
        List<ExifEntry> interopTags = [.. exif.Entries.Where(e => e.Key.Ifd == ExifIfd.Interoperability)];
        int pointerType = page.Big ? 18 : 13;
        if (exifTags.Count > 0 || interopTags.Count > 0)
        {
            long? interop = interopTags.Count > 0 ? Write(page, interopTags, null) : null;
            page.Add(TiffTags.ExifIfd, pointerType, (ulong)Write(page, exifTags, interop));
        }

        if (gpsTags.Count > 0)
        {
            page.Add(TiffTags.GpsIfd, pointerType, (ulong)Write(page, gpsTags, null));
        }
    }

    private static long Write(TiffIfdWriter page, List<ExifEntry> entries, long? interop)
    {
        TiffIfdWriter ifd = page.Sub();
        foreach (ExifEntry entry in entries)
        {
            ifd.AddRaw(entry.Key.Tag, (int)entry.Type, entry.Count, TiffMetadataWriter.Ordered(ifd, entry.RawValue.Span, (int)entry.Type, entry.IsBigEndian));
        }

        if (interop is long at)
        {
            ifd.Add(TiffTags.InteroperabilityIfd, page.Big ? 18 : 13, (ulong)at);
        }

        return ifd.Write(link: false);
    }
}
