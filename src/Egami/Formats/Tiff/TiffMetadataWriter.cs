using Egami.Metadata;

namespace Egami.Formats.Tiff;

/// <summary>
/// Writes a page's metadata into its IFD: ICC, XMP and IPTC, the EXIF tags of IFD0 and the EXIF, GPS and Interoperability
/// sub-IFDs, and the raw tags a TIFF source kept. Tags that describe the pixel layout always come from the writer.
/// </summary>
internal static class TiffMetadataWriter
{
    public static void Add(TiffIfdWriter ifd, RasterMetadata metadata, WriterOptions options)
    {
        if (options.StripMetadata)
        {
            return;
        }

        if (metadata.FormatDetails is TiffDetails details)
        {
            foreach (TiffTag tag in details.OtherTags)
            {
                // Tags holding offsets (IFD types, unknown sub-IFDs) cannot be moved safely, so they are left out.
                if (tag.Id is TiffTags.SubIfds or TiffTags.ExifIfd or TiffTags.GpsIfd or TiffTags.InteroperabilityIfd or TiffTags.FreeOffsets
                    or TiffTags.FreeByteCounts || TiffTags.IsLayout(tag.Id) && tag.Id != TiffTags.Photoshop)
                {
                    continue;
                }

                if (tag.Id == TiffTags.Photoshop && metadata.HasIptc)
                {
                    // The resources' IPTC and its digest follow the raster's IPTC, as the JPEG writer keeps them.
                    byte[] block = PhotoshopResources.WithIptc(tag.Value.Span, metadata.IptcBytes.Span);
                    ifd.AddRaw(tag.Id, 7, block.Length, block);
                    continue;
                }

                ifd.AddRaw(tag.Id, (int)tag.Type, tag.Count, Ordered(ifd, tag.Value.Span, (int)tag.Type, bigEndianSource: false));
            }
        }

        if (metadata.Exif is ExifData exif)
        {
            foreach (ExifEntry entry in exif.Entries)
            {
                if (entry.Key.Ifd != ExifIfd.Primary || TiffTags.IsLayout(entry.Key.Tag) && entry.Key.Tag != TiffTags.Orientation
                    || entry.Key.Tag is 0x8769 or 0x8825 or 0xA005)
                {
                    continue;
                }

                ifd.AddRaw(entry.Key.Tag, (int)entry.Type, entry.Count, Ordered(ifd, entry.RawValue.Span, (int)entry.Type, entry.IsBigEndian));
            }

            TiffExifWriter.AddSubIfds(ifd, exif);
        }

        ReadOnlyMemory<byte> icc = options.ProfileToWrite(metadata);
        if (!icc.IsEmpty)
        {
            ifd.AddRaw(TiffTags.IccProfile, 7, icc.Length, icc.ToArray());
        }

        ReadOnlyMemory<byte> xmp = metadata.Xmp;
        if (!xmp.IsEmpty)
        {
            ifd.AddRaw(TiffTags.Xmp, 1, xmp.Length, xmp.ToArray());
        }

        if (metadata.HasIptc)
        {
            ReadOnlyMemory<byte> iim = metadata.IptcBytes;
            if (!iim.IsEmpty)
            {
                // UNDEFINED, which libtiff and ExifTool read.
                ifd.AddRaw(TiffTags.Iptc, 7, iim.Length, iim.ToArray());
            }
        }
    }

    /// <summary>Returns values in the file's byte order: swapped when their source order differs, each value by its type's size.</summary>
    internal static byte[] Ordered(TiffIfdWriter ifd, ReadOnlySpan<byte> value, int type, bool bigEndianSource)
    {
        byte[] bytes = value.ToArray();
        if (ifd.BigEndian != bigEndianSource)
        {
            TiffFile.Swap(bytes, type is 5 or 10 ? 4 : TiffEntry.Size(type));
        }

        return bytes;
    }
}
