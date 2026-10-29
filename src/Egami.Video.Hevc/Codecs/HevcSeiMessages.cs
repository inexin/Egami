using Egami.Colors;
using Egami.Nal;

namespace Egami.Video.Codecs;

/// <summary>
/// The prefix SEI messages of an access unit the HEVC adapter uses (H.265 D.2): mastering display colour volume, content light
/// level and ITU-T T.35 user data (HDR10+). Egami.Heic's decoder reads the messages decoding needs; these only describe the
/// frames. Other messages are skipped.
/// </summary>
internal sealed class HevcSeiMessages
{
    /// <summary>Gets the mastering display and content light level, or null when the unit has neither.</summary>
    internal HdrMetadata? Hdr { get; private set; }

    /// <summary>Gets the HDR10+ metadata of the first ITU-T T.35 message that holds it, or null.</summary>
    internal Hdr10PlusMetadata? Hdr10Plus { get; private set; }

    /// <summary>Reads a prefix SEI NAL unit, its two-byte header included. A broken message ends the walk quietly.</summary>
    internal static HevcSeiMessages Read(byte[] unit)
    {
        HevcSeiMessages sei = new();
        try
        {
            NalSei.Read(new NalBitReader(unit, 2), sei, static (r, type, size, s) => s.ReadPayload(r, type, size));
        }
        catch (CorruptDataException)
        {
            // SEI is advisory: what was read before the damage stays, and decoding goes on.
        }

        return sei;
    }

    private void ReadPayload(NalBitReader r, int type, int size)
    {
        switch (type)
        {
            case 4 when size >= 3:
                byte[] payload = new byte[size];
                for (int i = 0; i < size; i++)
                {
                    payload[i] = (byte)r.U(8, "itu_t_t35_payload_byte");
                }

                Hdr10Plus ??= Hdr10PlusMetadata.Parse(payload);
                break;
            case 137 when size >= 24:
                {
                    // Display primaries in G, B, R order then the white point, x and y in 0.00002 units; luminance in 0.0001 cd/m2.
                    Span<double> c = stackalloc double[8];
                    for (int i = 0; i < 8; i++)
                    {
                        c[i] = r.U(16, "display_primaries") * 0.00002;
                    }

                    double max = r.U(32, "max_display_mastering_luminance") * 0.0001;
                    double min = r.U(32, "min_display_mastering_luminance") * 0.0001;
                    Hdr = (Hdr ?? new HdrMetadata()) with
                    {
                        MasteringPrimaries = new ColorPrimaries(new(c[4], c[5]), new(c[0], c[1]), new(c[2], c[3]), new(c[6], c[7])),
                        MasteringMaxLuminance = max,
                        MasteringMinLuminance = min,
                    };
                    break;
                }

            case 144 when size >= 4:
                Hdr = (Hdr ?? new HdrMetadata()) with
                {
                    MaxContentLightLevel = r.U(16, "max_content_light_level"),
                    MaxFrameAverageLightLevel = r.U(16, "max_pic_average_light_level"),
                };
                break;
        }
    }
}
