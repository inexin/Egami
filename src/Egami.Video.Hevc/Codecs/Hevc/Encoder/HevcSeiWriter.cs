using Egami.Colors;
using Egami.Nal;

namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>
/// Writes the prefix SEI NAL unit of HDR10 metadata (H.265 D.2.28, D.2.35): mastering display colour volume and content light
/// level information, which the encoder puts in every IDR access unit.
/// </summary>
internal static class HevcSeiWriter
{
    /// <summary>PREFIX_SEI_NUT.</summary>
    private const int PrefixSei = 39;

    /// <summary>
    /// Gets the prefix SEI NAL unit (escaped, with its header) of the mastering display and content light level that
    /// <paramref name="hdr"/> states, or null when it states neither.
    /// </summary>
    internal static byte[]? Hdr(HdrMetadata? hdr)
    {
        if (hdr is null)
        {
            return null;
        }

        NalBitWriter w = new();
        bool any = false;
        if (hdr.MasteringPrimaries is { } p && hdr.MasteringMaxLuminance is { } max)
        {
            w.U(8, 137); // payloadType: mastering_display_colour_volume
            w.U(8, 24); // payloadSize
            foreach (Chromaticity xy in (ReadOnlySpan<Chromaticity>)[p.Green, p.Blue, p.Red])
            {
                w.U(16, Units(xy.X, 0.00002, 50000)); // display_primaries_x
                w.U(16, Units(xy.Y, 0.00002, 50000)); // display_primaries_y
            }

            w.U(16, Units(p.White.X, 0.00002, 50000)); // white_point_x
            w.U(16, Units(p.White.Y, 0.00002, 50000)); // white_point_y
            w.U(32, Units(max, 0.0001, uint.MaxValue)); // max_display_mastering_luminance
            w.U(32, Units(hdr.MasteringMinLuminance ?? 0, 0.0001, uint.MaxValue)); // min_display_mastering_luminance
            any = true;
        }

        if (hdr.MaxContentLightLevel is not null || hdr.MaxFrameAverageLightLevel is not null)
        {
            w.U(8, 144); // payloadType: content_light_level_info
            w.U(8, 4); // payloadSize
            w.U(16, Units(hdr.MaxContentLightLevel ?? 0, 1, 65535)); // max_content_light_level
            w.U(16, Units(hdr.MaxFrameAverageLightLevel ?? 0, 1, 65535)); // max_pic_average_light_level
            any = true;
        }

        if (!any)
        {
            return null;
        }

        w.TrailingBits();
        return [PrefixSei << 1, 1, .. NalFraming.Escape(w.ToArray())];
    }

    private static uint Units(double value, double unit, double max) => (uint)Math.Clamp(Math.Round(value / unit), 0, max);
}
