using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <summary>
/// Reads a jhgm box (design question 12): libjxl's gain map bundle of a version byte, the ISO 21496-1 metadata, an optional color
/// encoding and compressed ICC profile of the alternate image, and the gain map as a bare JPEG XL codestream; into 2.9.1's
/// <see cref="GainMap"/>, so Load gives the SDR base with <see cref="RasterMetadata.GainMap"/> and <see cref="ReaderOptions.Hdr"/>
/// renders HDR as for Ultra HDR JPEGs. A damaged box never fails the load: it is dropped with a Jxl.GainMapDropped warning.
/// </summary>
internal static class JxlGainMap
{
    internal static void Read(EgamiContext context, byte[] bundle, RasterMetadata metadata)
    {
        string? problem = null;
        try
        {
            if (bundle.Length < 8)
            {
                problem = "the box is too short";
            }
            else if (bundle[0] != 0)
            {
                problem = $"bundle version {bundle[0]} is unknown";
            }
            else
            {
                int at = 1;
                int metadataSize = BinaryPrimitives.ReadUInt16BigEndian(bundle.AsSpan(at));
                at += 2;
                byte[] iso = Take(bundle, ref at, metadataSize);
                int colorEncodingSize = Take(bundle, ref at, 1)[0];
                at += colorEncodingSize;
                long iccSize = BinaryPrimitives.ReadUInt32BigEndian(Take(bundle, ref at, 4));
                if (iccSize > bundle.Length - at)
                {
                    throw new EndOfStreamException();
                }

                // The alternate image's color encoding and ICC profile are skipped: Egami renders HDR in the base image's space.
                at += (int)iccSize;
                byte[] codestream = bundle[at..];
                if (JpegGainMapIso(iso, out problem) is GainMapInfo info)
                {
                    metadata.GainMap = Decode(context, info, codestream);
                    return;
                }
            }
        }
        catch (Exception e) when (e is EgamiException or EndOfStreamException or ArgumentOutOfRangeException)
        {
            problem = e is EgamiException ? e.Message : "the box ends early";
        }

        metadata.WarningList.Add("Jxl.GainMapDropped", $"The jhgm gain map could not be read ({problem}); the image loads as SDR only.");
    }

    private static GainMapInfo? JpegGainMapIso(byte[] iso, out string? problem) => Jpeg.JpegGainMap.ParseIso(iso, out problem);

    private static byte[] Take(byte[] data, ref int at, int count)
    {
        if (count > data.Length - at)
        {
            throw new EndOfStreamException();
        }

        byte[] part = data[at..(at + count)];
        at += count;
        return part;
    }

    /// <summary>Decodes the map codestream to 8-bit gray or RGB rows, as 2.9.1 keeps maps.</summary>
    private static GainMap Decode(EgamiContext context, GainMapInfo info, byte[] codestream)
    {
        ReaderOptions options = new() { ReadMetadata = false, Format = JxlFormat.Info };
        using Raster map = Raster.Load(codestream, context, options);
        bool gray = map.Layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
        int channels = gray ? 1 : 3;
        byte[] pixels = new byte[map.Width * map.Height * channels];
        if (gray)
        {
            using Raster<Gray8> g = map.As<Gray8>();
            for (int y = 0; y < g.Height; y++)
            {
                MemoryMarshal.AsBytes(g.Row(y)).CopyTo(pixels.AsSpan(y * g.Width));
            }
        }
        else
        {
            using Raster<Rgb8> rgb = map.As<Rgb8>();
            for (int y = 0; y < rgb.Height; y++)
            {
                MemoryMarshal.AsBytes(rgb.Row(y)).CopyTo(pixels.AsSpan(y * rgb.Width * 3));
            }
        }

        return new GainMap(info, map.Width, map.Height, channels, pixels);
    }
}
