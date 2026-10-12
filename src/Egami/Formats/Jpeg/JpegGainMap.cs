using System.Buffers.Binary;
using System.Globalization;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Finds a JPEG's gain map among the images its MPF index lists (HDR Design question 9), in Skia's order: ISO 21496-1 metadata
/// first, then Adobe's hdrgm XMP (Ultra HDR 1.0), then Apple's HDRGainMap auxiliary image with its headroom from XMP or from
/// the maker note. Metadata parsing follows ISO 21496-1, Adobe's Gain Map Specification 1.0 and Android's Ultra HDR format,
/// with libultrahdr's reading of Apple's maker note values (Apache-2.0; see THIRD-PARTY-NOTICES).
/// </summary>
internal static class JpegGainMap
{
    /// <summary>The identifier of the ISO 21496-1 APP2 segment.</summary>
    internal static ReadOnlySpan<byte> IsoId => "urn:iso:std:iso:ts:21496:-1\0"u8;

    private const string HdrgmNamespace = "http://ns.adobe.com/hdr-gain-map/1.0/";
    private const string AppleNamespace = "http://ns.apple.com/HDRGainMap/1.0/";

    /// <summary>The most bytes a gain map image may take: as many as the safety limits allow the base image.</summary>
    private const int MaxMapBytes = 256 * 1024 * 1024;

    /// <summary>
    /// Reads the gain map into <paramref name="metadata"/> when the file has one, moving the input past the images it reads. A
    /// damaged or unusable map never fails the load: it is dropped with a Jpeg.GainMapDropped warning.
    /// </summary>
    internal static void Read(EgamiInput input, JpegHeader primary, RasterMetadata metadata)
    {
        IReadOnlyList<MpfImage> images = primary.Details.MultiPicture?.Images ?? [];
        if (images.Count < 2)
        {
            return;
        }

        GainMap? best = null;
        try
        {
            foreach (MpfImage image in images.Skip(1).Where(i => i.DataFormat == 0 && i.Offset > 0).OrderBy(i => i.Offset))
            {
                if (image.Length <= 0 || image.Length > MaxMapBytes)
                {
                    continue;
                }

                // A seekable stream is read at the offset; another one only forward, as long as the decoder has not passed it.
                byte[] bytes = new byte[image.Length];
                int read;
                if (input.Length is long length)
                {
                    read = image.Offset + image.Length <= length ? input.ReadAt(image.Offset, bytes) : 0;
                }
                else if (image.Offset >= input.Position)
                {
                    input.Skip(image.Offset - input.Position);
                    read = 0;
                    while (read < bytes.Length)
                    {
                        int n = input.Read(bytes.AsSpan(read));
                        if (n == 0)
                        {
                            break;
                        }

                        read += n;
                    }
                }
                else
                {
                    continue;
                }

                if (read < bytes.Length)
                {
                    metadata.WarningList.Add("Jpeg.GainMapDropped", "The file ends inside an image of its Multi-Picture index; no gain map was read.", image.Offset);
                    break;
                }

                GainMap? found = Candidate(input.Context, bytes, primary, metadata);
                if (found is not null && (best is null || found.Info.Format < best.Info.Format))
                {
                    best = found;
                }

                if (best?.Info.Format == GainMapFormat.Iso21496)
                {
                    break;
                }
            }
        }
        catch (EgamiException e)
        {
            metadata.WarningList.Add("Jpeg.GainMapDropped", $"The gain map could not be read ({e.Message}); the image loads as SDR only.");
            return;
        }

        metadata.GainMap = best;
    }

    /// <summary>Returns the gain map an MP image holds, or null when it is not one.</summary>
    private static GainMap? Candidate(EgamiContext context, byte[] bytes, JpegHeader primary, RasterMetadata metadata)
    {
        JpegHeader header;
        try
        {
            using MemoryStream stream = new(bytes, writable: false);
            EgamiInput input = new(stream, context, CancellationToken.None);
            header = JpegSegmentReader.ReadHeader(input, new ReaderOptions { ReadMetadata = true });
        }
        catch (EgamiException)
        {
            return null;
        }

        int channels = header.Components.Length == 1 ? 1 : 3;
        GainMapInfo? info = null;
        if (header.IsoGainMap is { Length: > 4 } iso)
        {
            info = ParseIso(iso, out string? problem);
            if (info is null)
            {
                metadata.WarningList.Add("Jpeg.GainMapDropped", $"The ISO 21496-1 gain map metadata is damaged ({problem}); the image loads as SDR only.");
                return null;
            }
        }

        XmpData? xmp = header.Metadata.XmpData;
        if (info is null && xmp is not null && xmp.GetText(new XmpName(HdrgmNamespace, "Version")) is not null)
        {
            info = ParseHdrgm(xmp, out string? problem);
            if (info is null)
            {
                metadata.WarningList.Add("Jpeg.GainMapDropped", $"The hdrgm gain map metadata is damaged ({problem}); the image loads as SDR only.");
                return null;
            }
        }

        if (info is null && xmp is not null && xmp.GetText(new XmpName(AppleNamespace, "HDRGainMapVersion")) is not null)
        {
            // The headroom is in stops: in the map's XMP in newer files, from the base image's maker note in older ones.
            double? stops = Number(xmp.GetText(new XmpName(AppleNamespace, "HDRGainMapHeadroom"))) ?? AppleStops(primary.Metadata.Exif);

            if (stops is null)
            {
                metadata.WarningList.Add("Jpeg.GainMapDropped", "Apple's gain map has no headroom in its XMP or the maker note; the image loads as SDR only.");
                return null;
            }

            info = new GainMapInfo
            {
                GainMapMin = new(0),
                GainMapMax = new(stops.Value),
                Gamma = new(1),
                OffsetSdr = new(0),
                OffsetHdr = new(0),
                HdrCapacityMin = 0,
                HdrCapacityMax = stops.Value,
                Format = GainMapFormat.Apple,
            };
        }

        if (info is null)
        {
            return null;
        }

        if (!info.UseBaseColorSpace && !header.Metadata.IccProfile.IsEmpty)
        {
            info = info with { AlternateProfile = header.Metadata.GetColorProfile() };
        }

        int width = header.Width, height = header.Height;
        return new GainMap(info, width, height, channels, bytes, () => Decode(bytes, channels, width, height));
    }

    /// <summary>Decodes the gain map JPEG to 8-bit gray or RGB rows.</summary>
    private static byte[] Decode(byte[] bytes, int channels, int width, int height)
    {
        byte[] pixels = new byte[width * height * channels];
        if (channels == 1)
        {
            using Raster<Gray8> gray = Raster.Load<Gray8>(bytes, options: new ReaderOptions { ReadMetadata = false, Format = JpegFormat.Info });
            for (int y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(gray.Row(y)).CopyTo(pixels.AsSpan(y * width));
            }
        }
        else
        {
            using Raster<Rgb8> rgb = Raster.Load<Rgb8>(bytes, options: new ReaderOptions { ReadMetadata = false, Format = JpegFormat.Info });
            for (int y = 0; y < height; y++)
            {
                System.Runtime.InteropServices.MemoryMarshal.AsBytes(rgb.Row(y)).CopyTo(pixels.AsSpan(y * width * 3));
            }
        }

        return pixels;
    }

    /// <summary>Parses ISO 21496-1 metadata (the APP2 payload after the identifier): fractions, as one channel or three.</summary>
    internal static GainMapInfo? ParseIso(byte[] data, out string? problem)
    {
        problem = null;
        int pos = 0;
        bool Need(int n, out string? why)
        {
            why = pos + n <= data.Length ? null : "it is cut short";
            return why is null;
        }

        if (!Need(5, out problem))
        {
            return null;
        }

        int minimumVersion = BinaryPrimitives.ReadUInt16BigEndian(data);
        if (minimumVersion != 0)
        {
            problem = $"minimum version {minimumVersion}";
            return null;
        }

        pos = 4;
        byte flags = data[pos++];
        int count = (flags & 0x80) != 0 ? 3 : 1;
        bool useBase = (flags & 0x40) != 0;
        bool backward = (flags & 0x04) != 0;
        bool common = (flags & 0x08) != 0;
        double[] min = new double[3], max = new double[3], gamma = new double[3], baseOffset = new double[3], alternateOffset = new double[3];
        double baseHeadroom, alternateHeadroom;
        uint U() => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan((pos += 4) - 4));
        int S() => BinaryPrimitives.ReadInt32BigEndian(data.AsSpan((pos += 4) - 4));
        if (common)
        {
            if (!Need(12 + (count * 20), out problem))
            {
                return null;
            }

            uint d = U();
            if (d == 0)
            {
                problem = "a denominator is 0";
                return null;
            }

            baseHeadroom = U() / (double)d;
            alternateHeadroom = U() / (double)d;
            for (int c = 0; c < count; c++)
            {
                min[c] = S() / (double)d;
                max[c] = S() / (double)d;
                gamma[c] = U() / (double)d;
                baseOffset[c] = S() / (double)d;
                alternateOffset[c] = S() / (double)d;
            }
        }
        else
        {
            if (!Need(16 + (count * 40), out problem))
            {
                return null;
            }

            double? Fraction(bool signed)
            {
                double n = signed ? S() : U();
                uint d = U();
                return d == 0 ? null : n / d;
            }

            double?[] values = new double?[2 + (count * 5)];
            values[0] = Fraction(false);
            values[1] = Fraction(false);
            for (int c = 0; c < count; c++)
            {
                values[2 + (c * 5)] = Fraction(true);
                values[3 + (c * 5)] = Fraction(true);
                values[4 + (c * 5)] = Fraction(false);
                values[5 + (c * 5)] = Fraction(true);
                values[6 + (c * 5)] = Fraction(true);
            }

            if (values.Any(v => v is null))
            {
                problem = "a denominator is 0";
                return null;
            }

            baseHeadroom = values[0]!.Value;
            alternateHeadroom = values[1]!.Value;
            for (int c = 0; c < count; c++)
            {
                min[c] = values[2 + (c * 5)]!.Value;
                max[c] = values[3 + (c * 5)]!.Value;
                gamma[c] = values[4 + (c * 5)]!.Value;
                baseOffset[c] = values[5 + (c * 5)]!.Value;
                alternateOffset[c] = values[6 + (c * 5)]!.Value;
            }
        }

        for (int c = count; c < 3; c++)
        {
            min[c] = min[0];
            max[c] = max[0];
            gamma[c] = gamma[0];
            baseOffset[c] = baseOffset[0];
            alternateOffset[c] = alternateOffset[0];
        }

        if (gamma.Any(g => !(g > 0)) || max.Zip(min).Any(p => p.First < p.Second))
        {
            problem = "a gamma is not positive or a maximum is below its minimum";
            return null;
        }

        return new GainMapInfo
        {
            GainMapMin = new(min[0], min[1], min[2]),
            GainMapMax = new(max[0], max[1], max[2]),
            Gamma = new(gamma[0], gamma[1], gamma[2]),
            OffsetSdr = new(baseOffset[0], baseOffset[1], baseOffset[2]),
            OffsetHdr = new(alternateOffset[0], alternateOffset[1], alternateOffset[2]),
            HdrCapacityMin = baseHeadroom,
            HdrCapacityMax = alternateHeadroom,
            BaseRenditionIsHdr = backward,
            UseBaseColorSpace = useBase,
            Format = GainMapFormat.Iso21496,
        };
    }

    /// <summary>Parses Adobe's hdrgm XMP: each value an attribute, or a sequence of three for red, green and blue.</summary>
    private static GainMapInfo? ParseHdrgm(XmpData xmp, out string? problem)
    {
        problem = null;
        GainMapValues? Values(string name, double fallback, bool required, ref string? why)
        {
            XmpName key = new(HdrgmNamespace, name);
            if (!xmp.TryGet(key, out object? value))
            {
                if (required)
                {
                    why ??= $"it has no {name}";
                    return null;
                }

                return new GainMapValues(fallback);
            }

            if (value is string text && Number(text) is double single)
            {
                return new GainMapValues(single);
            }

            if (value is XmpArray array && array.Count == 3 && array.Texts.Select(Number).ToArray() is [double r, double g, double b])
            {
                return new GainMapValues(r, g, b);
            }

            why ??= $"its {name} is not a number";
            return null;
        }

        GainMapValues? min = Values("GainMapMin", 0, false, ref problem);
        GainMapValues? max = Values("GainMapMax", 0, true, ref problem);
        GainMapValues? gamma = Values("Gamma", 1, false, ref problem);
        GainMapValues? offsetSdr = Values("OffsetSDR", 1.0 / 64, false, ref problem);
        GainMapValues? offsetHdr = Values("OffsetHDR", 1.0 / 64, false, ref problem);
        GainMapValues? capacityMin = Values("HDRCapacityMin", 0, false, ref problem);
        GainMapValues? capacityMax = Values("HDRCapacityMax", 0, true, ref problem);
        string? baseIsHdr = xmp.GetText(new XmpName(HdrgmNamespace, "BaseRenditionIsHDR"));
        if (problem is not null)
        {
            return null;
        }

        return new GainMapInfo
        {
            GainMapMin = min!.Value,
            GainMapMax = max!.Value,
            Gamma = gamma!.Value,
            OffsetSdr = offsetSdr!.Value,
            OffsetHdr = offsetHdr!.Value,
            HdrCapacityMin = capacityMin!.Value.Red,
            HdrCapacityMax = capacityMax!.Value.Red,
            BaseRenditionIsHdr = string.Equals(baseIsHdr, "True", StringComparison.OrdinalIgnoreCase),
            Format = GainMapFormat.AdobeXmp,
        };
    }

    /// <summary>
    /// Returns the HDR headroom, in stops, of Apple's maker note tags 33 and 48, by the formula Apple documents and libultrahdr and
    /// Skia apply; null when the maker note is not Apple's or lacks both.
    /// </summary>
    internal static double? AppleStops(ExifData? exif)
    {
        if (exif is null || !exif.TryGet(ExifKey.MakerNote, out ExifEntry? entry))
        {
            return null;
        }

        ReadOnlySpan<byte> note = entry.RawValue.Span;
        ReadOnlySpan<byte> header = "Apple iOS\0\0\u0001MM"u8;
        if (!note.StartsWith(header) || note.Length < header.Length + 2)
        {
            return null;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(note[header.Length..]);
        double? maker33 = null, maker48 = null;
        for (int i = 0; i < count; i++)
        {
            int at = header.Length + 2 + (i * 12);
            if (at + 12 > note.Length)
            {
                break;
            }

            int tag = BinaryPrimitives.ReadUInt16BigEndian(note[at..]);
            int type = BinaryPrimitives.ReadUInt16BigEndian(note[(at + 2)..]);
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(note[(at + 8)..]);
            if ((tag == 33 || tag == 48) && type == 10 && offset + 8 <= note.Length)
            {
                int numerator = BinaryPrimitives.ReadInt32BigEndian(note[(int)offset..]);
                uint denominator = BinaryPrimitives.ReadUInt32BigEndian(note[((int)offset + 4)..]);
                if (denominator != 0)
                {
                    double v = (double)numerator / denominator;
                    if (tag == 33)
                    {
                        maker33 = v;
                    }
                    else
                    {
                        maker48 = v;
                    }
                }
            }
        }

        if (maker33 is null && maker48 is null)
        {
            return null;
        }

        double m33 = maker33 ?? 0, m48 = maker48 ?? 0;
        return m33 < 1.0
            ? (m48 <= 0.01 ? (-20.0 * m48) + 1.8 : (-0.101 * m48) + 1.601)
            : (m48 <= 0.01 ? (-70.0 * m48) + 3.0 : (-0.303 * m48) + 2.303);
    }

    private static double? Number(string? text) =>
        text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v) ? v : null;
}
