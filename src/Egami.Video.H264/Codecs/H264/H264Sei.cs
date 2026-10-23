using Egami.Colors;
using Egami.Nal;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// The SEI messages of one access unit the decoder uses (H.264 D.1): recovery point, mastering display colour volume, content
/// light level, alternative transfer characteristics and ITU-T T.35 user data (HDR10+). Other messages are skipped.
/// </summary>
internal sealed class H264Sei
{
    /// <summary>Gets recovery_frame_cnt of a recovery point SEI, or -1 when the access unit has none.</summary>
    internal int RecoveryFrameCount { get; private set; } = -1;

    internal HdrMetadata? Hdr { get; private set; }

    /// <summary>Gets preferred_transfer_characteristics of an alternative transfer SEI, or -1.</summary>
    internal int PreferredTransfer { get; private set; } = -1;

    /// <summary>Gets the payload of an ITU-T T.35 message, from itu_t_t35_country_code on, or null.</summary>
    internal byte[]? T35 { get; private set; }

    /// <summary>Reads the SEI messages of a NAL unit into this access unit's set. A broken message ends the walk quietly.</summary>
    internal void Read(byte[] rbsp)
    {
        try
        {
            NalSei.Read(new NalBitReader(rbsp, 1), this, static (r, type, size, sei) => sei.ReadPayload(r, type, size));
        }
        catch (CorruptDataException)
        {
            // SEI is advisory: what was read before the damage stays, and decoding goes on.
        }
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

                T35 ??= payload;
                break;
            case 6:
                RecoveryFrameCount = (int)r.Ue("recovery_frame_cnt", 65535);
                r.Flag("exact_match_flag");
                r.Flag("broken_link_flag");
                r.U(2, "changing_slice_group_idc");
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
            case 147 when size >= 1:
                PreferredTransfer = (int)r.U(8, "preferred_transfer_characteristics");
                break;
        }
    }
}
