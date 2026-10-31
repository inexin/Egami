namespace Egami.Formats.Heif;

/// <summary>
/// Reads a MinimizedImageBox (mini, the mif3 brand: the HEIF amendment draft of ISO/IEC 23008-12 that AVIF files may use) and gives
/// the file it stands for with a regular meta box, as the amendment's Clause O.4 transforms it: the coded image, its alpha, its
/// gain map and tone map item, ICC, Exif and XMP as the items and properties a meta box would hold, so the HEIF image layer reads
/// them unchanged (AVIF extras design, question 6). Written from the draft's syntax; libavif 1.4.2's experimental reader and
/// CrabbyAvif were cross-checks only. AV1 is the only codec it knows.
/// </summary>
internal static class HeifMini
{
    internal static readonly uint Mini = IsoTypes.Of("mini");
    internal static readonly uint Mif3 = IsoTypes.Of("mif3");

    /// <summary>
    /// Builds the file a mini box stands for: <paramref name="ftyp"/>'s brands (with tmap when there is a gain map) and the meta
    /// box with the mini box's items, their data copied into an mdat.
    /// </summary>
    /// <param name="file">The whole file.</param>
    /// <param name="ftyp">The ftyp box.</param>
    /// <param name="mini">The mini box.</param>
    /// <param name="minorVersion">The ftyp box's minor version, which names the codec when the mini box does not (avif for AV1).</param>
    /// <param name="formatName">The format named in errors.</param>
    internal static byte[] Expand(ReadOnlySpan<byte> file, Iso.IsoBox ftyp, Iso.IsoBox mini, uint minorVersion, string formatName)
    {
        ReadOnlySpan<byte> content = mini.Content(file);
        Bits r = new(content, formatName, mini.StartIndex);
        if (r.Read(2) != 0)
        {
            throw r.Corrupt("has a version other than 0");
        }

        bool explicitCodecTypes = r.Flag(), floatFlag = r.Flag(), fullRange = r.Flag(), alpha = r.Flag(), explicitCicp = r.Flag(), hdr = r.Flag();
        bool icc = r.Flag(), exif = r.Flag(), xmp = r.Flag();
        int chroma = (int)r.Read(2), orientation = (int)r.Read(3) + 1;
        bool large = r.Flag();
        int width = (int)r.Read(large ? 15 : 7) + 1, height = (int)r.Read(large ? 15 : 7) + 1;
        bool horizontallyCentered = chroma is 1 or 2 && r.Flag();
        _ = chroma == 1 && r.Flag(); // chroma_is_vertically_centered: the AV1 configuration gives the siting
        if (floatFlag)
        {
            throw new UnsupportedFormatException($"The {formatName} file's mini box holds floating-point samples, which AV1 cannot code.");
        }

        int bitDepth = r.Flag() ? (int)r.Read(3) + 9 : 8;
        bool premultiplied = alpha && r.Flag();
        int primaries, transfer, matrix;
        if (explicitCicp)
        {
            primaries = (int)r.Read(8);
            transfer = (int)r.Read(8);
            matrix = chroma != 0 ? (int)r.Read(8) : 2;
        }
        else
        {
            (primaries, transfer, matrix) = (icc ? 2 : 1, icc ? 2 : 13, chroma == 0 ? 2 : 6);
        }

        if (explicitCodecTypes)
        {
            uint infe = r.Read(32), config = r.Read(32);
            if (infe != IsoTypes.Of("av01") || config != IsoTypes.Of("av1C"))
            {
                throw new UnsupportedFormatException($"The {formatName} file's mini box holds \"{IsoTypes.ToString(infe)}\" items, not av01.");
            }
        }
        else if (minorVersion != IsoTypes.Of("avif"))
        {
            throw r.Corrupt("names no codec, and the ftyp box's minor version is not avif");
        }

        bool gainMap = false, tmapIcc = false, tmapExplicitCicp = false, mapFullRange = false;
        int mapWidth = 0, mapHeight = 0, mapMatrix = 0, mapChroma = 0, mapDepth = 0;
        (int Primaries, int Transfer, int Matrix, bool FullRange) tmapColor = (1, 13, 6, true);
        Hdr main = default, tmap = default;
        if (hdr)
        {
            gainMap = r.Flag();
            if (gainMap)
            {
                mapWidth = (int)r.Read(large ? 15 : 7) + 1;
                mapHeight = (int)r.Read(large ? 15 : 7) + 1;
                mapMatrix = (int)r.Read(8);
                mapFullRange = r.Flag();
                mapChroma = (int)r.Read(2);
                _ = mapChroma is 1 or 2 && r.Flag();
                _ = mapChroma == 1 && r.Flag();
                if (r.Flag())
                {
                    throw new UnsupportedFormatException($"The {formatName} file's gain map holds floating-point samples, which AV1 cannot code.");
                }

                mapDepth = r.Flag() ? (int)r.Read(3) + 9 : 8;
                tmapIcc = r.Flag();
                tmapExplicitCicp = r.Flag();
                if (tmapExplicitCicp)
                {
                    tmapColor = ((int)r.Read(8), (int)r.Read(8), (int)r.Read(8), r.Flag());
                }
            }

            main = ReadHdr(ref r);
            if (gainMap)
            {
                tmap = ReadHdr(ref r);
            }
        }

        bool largeMetadata = (icc || exif || xmp || gainMap) && r.Flag();
        bool largeConfig = r.Flag(), largeData = r.Flag();
        int metadataBits = largeMetadata ? 20 : 10, configBits = largeConfig ? 12 : 3, dataBits = largeData ? 28 : 15;
        long iccSize = icc ? r.Read(metadataBits) + 1L : 0;
        long tmapIccSize = gainMap && tmapIcc ? r.Read(metadataBits) + 1L : 0;
        long mapMetadataSize = 0, mapDataSize = 0, mapConfigSize = 0;
        if (gainMap)
        {
            mapMetadataSize = r.Read(metadataBits);
            mapDataSize = r.Read(dataBits);
            mapConfigSize = mapDataSize != 0 ? r.Read(configBits) : 0;
        }

        long mainConfigSize = r.Read(configBits), mainDataSize = r.Read(dataBits) + 1L;
        long alphaDataSize = alpha ? r.Read(dataBits) : 0;
        long alphaConfigSize = alpha && alphaDataSize != 0 ? r.Read(configBits) : 0;
        if ((exif || xmp) && r.Flag())
        {
            throw new UnsupportedFormatException($"The {formatName} file's mini box holds compressed Exif or XMP, which Egami does not read.");
        }

        long exifSize = exif ? r.Read(metadataBits) + 1L : 0;
        long xmpSize = xmp ? r.Read(metadataBits) + 1L : 0;
        r.AlignToByte();

        int at = r.Position;
        if (mainConfigSize != 4 || (alphaConfigSize is not (0 or 4)) || (mapConfigSize is not (0 or 4)))
        {
            throw r.Corrupt("has an av1C configuration that is not 4 bytes");
        }

        byte[] mainConfig = Take(content, mainConfigSize, ref at, ref r).ToArray();
        byte[] alphaConfig = alphaConfigSize != 0 ? Take(content, alphaConfigSize, ref at, ref r).ToArray() : mainConfig;
        byte[] mapConfig = mapConfigSize != 0 ? Take(content, mapConfigSize, ref at, ref r).ToArray() : mainConfig;
        long rest = iccSize + tmapIccSize + mapMetadataSize + alphaDataSize + mapDataSize + mainDataSize + exifSize + xmpSize;
        if (rest != content.Length - at)
        {
            throw r.Corrupt($"has {content.Length - at} bytes of data where its sizes add up to {rest}");
        }

        byte[] iccData = Take(content, iccSize, ref at, ref r).ToArray();
        byte[] tmapIccData = Take(content, tmapIccSize, ref at, ref r).ToArray();
        byte[] mapMetadata = Take(content, mapMetadataSize, ref at, ref r).ToArray();
        ReadOnlyMemory<byte> alphaData = Take(content, alphaDataSize, ref at, ref r).ToArray();
        ReadOnlyMemory<byte> mapData = Take(content, mapDataSize, ref at, ref r).ToArray();
        ReadOnlyMemory<byte> mainData = Take(content, mainDataSize, ref at, ref r).ToArray();
        byte[] exifData = Take(content, exifSize, ref at, ref r).ToArray();
        byte[] xmpData = Take(content, xmpSize, ref at, ref r).ToArray();
        _ = horizontallyCentered;

        // The ftyp box's brands, with tmap when there is a tone map item for the HEIF image layer to look for.
        ReadOnlySpan<byte> brands = ftyp.Content(file);
        List<string> compatible = [];
        for (int i = 8; i + 4 <= brands.Length; i += 4)
        {
            compatible.Add(IsoTypes.ToString(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(brands[i..])));
        }

        compatible.Add(IsoTypes.ToString(minorVersion));
        if (gainMap && !compatible.Contains("tmap"))
        {
            compatible.Add("tmap");
        }

        HeifWriter writer = new(IsoTypes.ToString(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(brands)), [.. compatible.Distinct()]);
        uint color = writer.AddItem("av01", mainData);
        writer.PrimaryItemId = color;
        writer.AddProperty(color, Av1Config(mainConfig), essential: true);
        writer.AddProperty(color, HeifWriter.SpatialExtents(width, height));
        writer.AddProperty(color, HeifWriter.PixelInformation(bitDepth, chroma == 0 ? 1 : 3));
        writer.AddProperty(color, HeifWriter.ColorNclx(primaries, transfer, matrix, fullRange));
        if (icc)
        {
            writer.AddProperty(color, HeifWriter.ColorIcc(iccData));
        }

        AddHdr(writer, color, main);
        int? irot = orientation is 3 or 5 or 6 or 7 or 8 ? writer.AddProperty(HeifWriter.Rotation(orientation == 3 ? 2 : orientation is 5 or 8 ? 1 : 3)) : null;
        int? imir = orientation is 2 or 4 or 5 or 7 ? writer.AddProperty(HeifWriter.Mirror(orientation == 2 ? 1 : 0)) : null;
        void Transform(uint item)
        {
            if (irot is int rotation)
            {
                writer.Associate(item, rotation, essential: true);
            }

            if (imir is int mirror)
            {
                writer.Associate(item, mirror, essential: true);
            }
        }

        Transform(color);
        if (alpha && alphaDataSize != 0)
        {
            uint a = writer.AddItem("av01", alphaData, hidden: true);
            writer.AddProperty(a, Av1Config(alphaConfig), essential: true);
            writer.AddProperty(a, HeifWriter.SpatialExtents(width, height));
            writer.AddProperty(a, HeifWriter.PixelInformation(bitDepth, 1));
            writer.AddProperty(a, HeifWriter.AuxiliaryType("urn:mpeg:mpegB:cicp:systems:auxiliary:alpha"), essential: true);
            Transform(a);
            writer.Reference("auxl", a, color);
            if (premultiplied)
            {
                writer.Reference("prem", color, a);
            }
        }

        if (gainMap)
        {
            // The tone map item's data is its metadata after a version byte of 0.
            uint t = writer.AddItem("tmap", (byte[])[0, .. mapMetadata]);
            bool swap = orientation > 4;
            writer.AddProperty(t, HeifWriter.SpatialExtents(swap ? height : width, swap ? width : height));
            if (tmapExplicitCicp || !tmapIcc)
            {
                writer.AddProperty(t, HeifWriter.ColorNclx(tmapColor.Primaries, tmapColor.Transfer, tmapColor.Matrix, tmapColor.FullRange));
            }

            if (tmapIcc)
            {
                writer.AddProperty(t, HeifWriter.ColorIcc(tmapIccData));
            }

            AddHdr(writer, t, tmap);
            uint[] inputs = [color];
            if (mapDataSize != 0)
            {
                uint m = writer.AddItem("av01", mapData, hidden: true);
                writer.AddProperty(m, Av1Config(mapConfig), essential: true);
                writer.AddProperty(m, HeifWriter.SpatialExtents(mapWidth, mapHeight));
                writer.AddProperty(m, HeifWriter.PixelInformation(mapDepth, mapChroma == 0 ? 1 : 3));
                writer.AddProperty(m, HeifWriter.ColorNclx(2, 2, mapMatrix, mapFullRange));
                Transform(m);
                inputs = [color, m];
            }

            writer.Reference("dimg", t, inputs);
            writer.Group("altr", t, color);
        }

        if (exif)
        {
            // An Exif item starts with the offset of the TIFF header, which the mini box leaves out because it is 0.
            uint e = writer.AddItem("Exif", (byte[])[0, 0, 0, 0, .. exifData]);
            writer.Reference("cdsc", e, color);
        }

        if (xmp)
        {
            uint x = writer.AddItem("mime", xmpData, contentType: "application/rdf+xml");
            writer.Reference("cdsc", x, color);
        }

        return writer.ToArray();
    }

    private static byte[] Av1Config(byte[] config) => HeifWriter.Box("av1C", config);

    /// <summary>Takes the next <paramref name="size"/> bytes of the box's data.</summary>
    private static ReadOnlySpan<byte> Take(ReadOnlySpan<byte> content, long size, ref int at, ref Bits bits)
    {
        if (size > content.Length - at)
        {
            throw bits.Corrupt("is cut off");
        }

        ReadOnlySpan<byte> part = content.Slice(at, (int)size);
        at += (int)size;
        return part;
    }

    private static Hdr ReadHdr(ref Bits r)
    {
        bool clli = r.Flag(), mdcv = r.Flag(), cclv = r.Flag(), amve = r.Flag(), reve = r.Flag(), ndwt = r.Flag();
        Hdr hdr = default;
        if (clli)
        {
            hdr.Clli = ((int)r.Read(16), (int)r.Read(16));
        }

        if (mdcv)
        {
            ushort[] primaries = new ushort[6];
            for (int i = 0; i < 6; i++)
            {
                primaries[i] = (ushort)r.Read(16);
            }

            hdr.Mdcv = (primaries, (ushort)r.Read(16), (ushort)r.Read(16), r.Read(32), r.Read(32));
        }

        if (cclv)
        {
            r.Read(2);
            bool primaries = r.Flag(), min = r.Flag(), max = r.Flag(), average = r.Flag();
            r.Read(2);
            for (int i = 0; i < (primaries ? 6 : 0) + (min ? 1 : 0) + (max ? 1 : 0) + (average ? 1 : 0); i++)
            {
                r.Read(32);
            }
        }

        if (amve)
        {
            r.Read(32);
            r.Read(32);
        }

        if (reve)
        {
            r.Read(32);
            r.Read(32);
            r.Read(32);
            r.Read(32);
        }

        if (ndwt)
        {
            r.Read(32);
        }

        return hdr;
    }

    private static void AddHdr(HeifWriter writer, uint item, Hdr hdr)
    {
        if (hdr.Clli is (int maxCll, int maxFall))
        {
            writer.AddProperty(item, HeifWriter.ContentLightLevel(maxCll, maxFall));
        }

        if (hdr.Mdcv is (ushort[] primaries, ushort whiteX, ushort whiteY, uint max, uint min))
        {
            writer.AddProperty(item, HeifWriter.MasteringDisplay(primaries, whiteX, whiteY, max, min));
        }
    }

    /// <summary>The HDR properties a mini box gives an image: clli and mdcv; cclv, amve, reve and ndwt are skipped.</summary>
    private struct Hdr
    {
        internal (int MaxCll, int MaxFall)? Clli;
        internal (ushort[] Primaries, ushort WhiteX, ushort WhiteY, uint Max, uint Min)? Mdcv;
    }

    /// <summary>Reads the mini box's fields, most significant bit first.</summary>
    private ref struct Bits(ReadOnlySpan<byte> data, string formatName, long offset)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private long _bit;

        /// <summary>Gets the byte the next field starts in.</summary>
        internal readonly int Position => (int)(_bit >> 3);

        internal uint Read(int bits)
        {
            if (_bit + bits > (long)_data.Length * 8)
            {
                throw Corrupt("is cut off");
            }

            uint value = 0;
            for (int i = 0; i < bits; i++, _bit++)
            {
                value = (value << 1) | (uint)((_data[(int)(_bit >> 3)] >> (7 - (int)(_bit & 7))) & 1);
            }

            return value;
        }

        internal bool Flag() => Read(1) != 0;

        /// <summary>Skips the zero bits up to the next byte; any other padding breaks the box.</summary>
        internal void AlignToByte()
        {
            int padding = (int)((8 - (_bit & 7)) & 7);
            if (padding > 0 && Read(padding) != 0)
            {
                throw Corrupt("has padding bits that are not zero");
            }
        }

        internal readonly CorruptDataException Corrupt(string problem) => new(formatName, offset, $"The mini box {problem}.");
    }
}
