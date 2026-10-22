using Egami.Colors;

namespace Egami.Video.Codecs.Av1;

/// <summary>
/// What a temporal unit holds, read without decoding: its sequence header, the start of each frame header (section 5.9.2, up to
/// error_resilient_mode, which needs no reference frames), and its metadata OBUs (section 5.8): content light level, mastering
/// display, ITU-T T.35 (HDR10+), scalability and timecode. <see cref="Read"/> can also list every field it reads.
/// </summary>
internal sealed class Av1UnitInfo
{
    private const int KeyFrame = 0;
    private const int SwitchFrame = 3;

    /// <summary>Gets the unit's first sequence header, or null when it has none.</summary>
    public Av1SequenceInfo? Sequence { get; private set; }

    /// <summary>Gets where the unit's first sequence header OBU lies (header included), or null when it has none.</summary>
    public (int Start, int End)? SequenceObu { get; private set; }

    /// <summary>Gets whether the unit's first frame is a key frame shown now: a point a decoder can start from.</summary>
    public bool IsShownKeyFrame { get; private set; }

    /// <summary>Gets whether the unit decodes a key frame, shown or not.</summary>
    public bool HasKeyFrame { get; private set; }

    /// <summary>Gets whether the unit shows a frame: one with show_frame, or one shown again with show_existing_frame.</summary>
    public bool ShowsFrame { get; private set; }

    /// <summary>Gets whether the unit has a frame header at all.</summary>
    public bool HasFrame { get; private set; }

    /// <summary>Gets the content light level metadata (max_cll, max_fall), or null.</summary>
    public (int MaxCll, int MaxFall)? ContentLightLevel { get; private set; }

    /// <summary>Gets the mastering display metadata, as Egami's HDR metadata, or null.</summary>
    public HdrMetadata? MasteringDisplay { get; private set; }

    /// <summary>Gets the first ITU-T T.35 payload that is HDR10+, or null.</summary>
    public Hdr10PlusMetadata? Hdr10Plus { get; private set; }

    /// <summary>
    /// Reads a temporal unit of low-overhead OBUs. <paramref name="sequence"/> is the sequence header in force when the unit has
    /// none of its own (its frame headers start differently with a reduced still picture header, frame ids or a decoder model).
    /// </summary>
    /// <exception cref="CorruptDataException">The OBUs or a header in them are broken.</exception>
    public static Av1UnitInfo Read(ReadOnlySpan<byte> unit, Av1SequenceInfo? sequence, List<CodecHeaderField>? fields = null)
    {
        Av1UnitInfo info = new();
        bool first = true;
        foreach (Av1Obu obu in Av1Obus.Split(unit))
        {
            ReadOnlySpan<byte> payload = unit.Slice(obu.PayloadStart, obu.PayloadLength);
            if (fields is not null)
            {
                fields.Add(new("obu_type", obu.Type));
                if (obu.HasExtension)
                {
                    fields.Add(new("temporal_id", obu.TemporalId));
                    fields.Add(new("spatial_id", obu.SpatialId));
                }

                fields.Add(new("obu_size", obu.PayloadLength));
            }

            switch (obu.Type)
            {
                case Av1Obus.SequenceHeader:
                    Av1SequenceInfo parsed = Av1SequenceInfo.Parse(payload, fields);
                    if (info.Sequence is null)
                    {
                        info.Sequence = parsed;
                        info.SequenceObu = (obu.Start, obu.End);
                    }

                    sequence = parsed;
                    break;
                case Av1Obus.FrameHeader or Av1Obus.Frame:
                    (bool showExisting, int frameType, bool show) = ReadFrameStart(payload, sequence, fields);
                    info.HasFrame = true;
                    info.ShowsFrame |= showExisting || show;
                    info.HasKeyFrame |= !showExisting && frameType == KeyFrame;
                    if (first)
                    {
                        info.IsShownKeyFrame = !showExisting && frameType == KeyFrame && show;
                        first = false;
                    }

                    break;
                case Av1Obus.Metadata:
                    info.ReadMetadata(payload, fields);
                    break;
            }
        }

        return info;
    }

    /// <summary>
    /// Returns whether a temporal unit starts with a shown key frame, from as much of its start as is given (the first few hundred
    /// bytes hold the sequence header and the first frame header's start), or null when the bytes given hold no frame header.
    /// </summary>
    public static bool? StartsWithShownKeyFrame(ReadOnlySpan<byte> unitStart)
    {
        Av1SequenceInfo? sequence = null;
        int at = 0;
        try
        {
            while (at < unitStart.Length)
            {
                Av1Obu obu = Av1Obus.ReadHeader(unitStart, at);
                int available = Math.Min(obu.PayloadLength, unitStart.Length - obu.PayloadStart);
                ReadOnlySpan<byte> payload = unitStart.Slice(obu.PayloadStart, Math.Max(0, available));
                if (obu.Type == Av1Obus.SequenceHeader && available == obu.PayloadLength)
                {
                    sequence = Av1SequenceInfo.Parse(payload);
                }
                else if (obu.StartsFrame)
                {
                    (bool showExisting, int frameType, bool show) = ReadFrameStart(payload, sequence);
                    return !showExisting && frameType == KeyFrame && show;
                }

                at = obu.End;
            }
        }
        catch (CorruptDataException)
        {
        }

        return null;
    }

    /// <summary>
    /// Reads the start of an uncompressed header (section 5.9.2): show_existing_frame, then frame_to_show_map_idx, or frame_type,
    /// show_frame, showable_frame and error_resilient_mode. A reduced still picture header codes none of them: a shown key frame.
    /// </summary>
    public static (bool ShowExisting, int FrameType, bool Show) ReadFrameStart(ReadOnlySpan<byte> payload, Av1SequenceInfo? sequence, List<CodecHeaderField>? fields = null)
    {
        if (sequence is { ReducedStillPictureHeader: true })
        {
            return (false, KeyFrame, true);
        }

        Av1BitReader r = new(payload, fields);
        bool timing = sequence is { DecoderModelInfoPresent: true, EqualPictureInterval: false };
        if (r.Flag("show_existing_frame"))
        {
            r.Int(3, "frame_to_show_map_idx");
            if (timing)
            {
                r.Bits(sequence!.FramePresentationTimeLength, "frame_presentation_time");
            }

            if (sequence is { FrameIdNumbersPresent: true })
            {
                r.Bits(sequence.FrameIdLength, "display_frame_id");
            }

            return (true, -1, true);
        }

        int frameType = r.Int(2, "frame_type");
        bool show = r.Flag("show_frame");
        if (show && timing)
        {
            r.Bits(sequence!.FramePresentationTimeLength, "frame_presentation_time");
        }

        if (!show)
        {
            r.Flag("showable_frame");
        }

        if (frameType != SwitchFrame && !(frameType == KeyFrame && show))
        {
            r.Flag("error_resilient_mode");
        }

        return (false, frameType, show);
    }

    /// <summary>Gets an ITU-T T.35 payload from its country code to its last byte before the trailing bits.</summary>
    public static ReadOnlySpan<byte> T35Payload(ReadOnlySpan<byte> metadata)
    {
        // The last byte that is not zero holds the trailing one bit (section 6.7.2).
        int end = metadata.Length;
        while (end > 0 && metadata[end - 1] == 0)
        {
            end--;
        }

        return end > 0 && metadata[end - 1] == 0x80 ? metadata[..(end - 1)] : metadata[..end];
    }

    private void ReadMetadata(ReadOnlySpan<byte> payload, List<CodecHeaderField>? fields)
    {
        (ulong type, int length) = Av1Obus.ReadLeb128(payload);
        if (length < 0)
        {
            throw new CorruptDataException(Av1Obus.FormatName, 0, "An AV1 metadata OBU's type is cut off.");
        }

        fields?.Add(new("metadata_type", (long)type));
        ReadOnlySpan<byte> body = payload[length..];
        Av1BitReader r = new(body, fields);
        switch (type)
        {
            case 1:
                int maxCll = r.Int(16, "max_cll");
                int maxFall = r.Int(16, "max_fall");
                ContentLightLevel ??= (maxCll, maxFall);
                break;
            case 2:
                double[] xy = new double[8];
                for (int i = 0; i < 3; i++)
                {
                    xy[2 * i] = r.Int(16, $"primary_chromaticity_x[{i}]") / 65536.0;
                    xy[(2 * i) + 1] = r.Int(16, $"primary_chromaticity_y[{i}]") / 65536.0;
                }

                xy[6] = r.Int(16, "white_point_chromaticity_x") / 65536.0;
                xy[7] = r.Int(16, "white_point_chromaticity_y") / 65536.0;
                double max = r.Bits(32, "luminance_max") / 256.0;
                double min = r.Bits(32, "luminance_min") / 16384.0;
                MasteringDisplay ??= new HdrMetadata
                {
                    MasteringPrimaries = new ColorPrimaries(new Chromaticity(xy[0], xy[1]), new Chromaticity(xy[2], xy[3]), new Chromaticity(xy[4], xy[5]), new Chromaticity(xy[6], xy[7])),
                    MasteringMaxLuminance = max,
                    MasteringMinLuminance = min,
                };
                break;
            case 3:
                if (r.Int(8, "scalability_mode_idc") == 14)
                {
                    ReadScalabilityStructure(ref r);
                }

                break;
            case 4:
                if (fields is not null)
                {
                    int country = r.Int(8, "itu_t_t35_country_code");
                    if (country == 0xFF)
                    {
                        r.Int(8, "itu_t_t35_country_code_extension_byte");
                    }

                    fields.Add(new("itu_t_t35_payload_bytes", T35Payload(body).Length - (country == 0xFF ? 2 : 1)));
                }

                Hdr10Plus ??= Hdr10PlusMetadata.Parse(T35Payload(body).ToArray());
                break;
            case 5:
                ReadTimecode(ref r);
                break;
        }
    }

    private static void ReadScalabilityStructure(ref Av1BitReader r)
    {
        int layers = r.Int(2, "spatial_layers_cnt_minus_1") + 1;
        bool dimensions = r.Flag("spatial_layer_dimensions_present_flag");
        bool description = r.Flag("spatial_layer_description_present_flag");
        bool temporalGroup = r.Flag("temporal_group_description_present_flag");
        r.Int(3, "scalability_structure_reserved_3bits");
        if (dimensions)
        {
            for (int i = 0; i < layers; i++)
            {
                r.Int(16, $"spatial_layer_max_width[{i}]");
                r.Int(16, $"spatial_layer_max_height[{i}]");
            }
        }

        if (description)
        {
            for (int i = 0; i < layers; i++)
            {
                r.Int(8, $"spatial_layer_ref_id[{i}]");
            }
        }

        if (temporalGroup)
        {
            int size = r.Int(8, "temporal_group_size");
            for (int i = 0; i < size; i++)
            {
                r.Int(3, $"temporal_group_temporal_id[{i}]");
                r.Flag($"temporal_group_temporal_switching_up_point_flag[{i}]");
                r.Flag($"temporal_group_spatial_switching_up_point_flag[{i}]");
                int refs = r.Int(3, $"temporal_group_ref_cnt[{i}]");
                for (int j = 0; j < refs; j++)
                {
                    r.Int(8, $"temporal_group_ref_pic_diff[{i}][{j}]");
                }
            }
        }
    }

    private static void ReadTimecode(ref Av1BitReader r)
    {
        r.Int(5, "counting_type");
        bool full = r.Flag("full_timestamp_flag");
        r.Flag("discontinuity_flag");
        r.Flag("cnt_dropped_flag");
        r.Int(9, "n_frames");
        if (full)
        {
            r.Int(6, "seconds_value");
            r.Int(6, "minutes_value");
            r.Int(5, "hours_value");
        }
        else if (r.Flag("seconds_flag"))
        {
            r.Int(6, "seconds_value");
            if (r.Flag("minutes_flag"))
            {
                r.Int(6, "minutes_value");
                if (r.Flag("hours_flag"))
                {
                    r.Int(5, "hours_value");
                }
            }
        }

        int offsetLength = r.Int(5, "time_offset_length");
        if (offsetLength > 0)
        {
            r.Bits(offsetLength, "time_offset_value");
        }
    }
}
