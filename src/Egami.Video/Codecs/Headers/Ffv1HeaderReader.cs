using Egami.Video.Codecs.Ffv1;

namespace Egami.Video.Codecs.Headers;

/// <summary>
/// Lists an FFV1 frame's header fields (RFC 9043 section 4): the configuration record's or a version 0 or 1 keyframe's
/// parameters, <c>keyframe</c>, and for version 3 each slice's header and footer after a <c>slice</c> field with its index.
/// </summary>
internal static class Ffv1HeaderReader
{
    public static List<CodecHeaderField> Read(TrackInfo track, ReadOnlySpan<byte> frame)
    {
        List<CodecHeaderField> fields = [];
        ReadOnlySpan<byte> record = Ffv1Codec.ConfigurationRecord(track).Span;
        Ffv1Parameters? config = Ffv1Decoder.HasConfigurationRecord(record) ? Ffv1Parameters.ReadConfigurationRecord(record, out _) : null;
        if (config is not null)
        {
            Describe(config, fields);
        }

        if (frame.IsEmpty)
        {
            return fields;
        }

        byte[] data = frame.ToArray();
        if (config is null)
        {
            var rc = new Ffv1RangeDecoder(data, 0, data.Length, Ffv1StateTable.Default);
            byte keyState = 128;
            bool keyframe = rc.ReadBit(ref keyState) != 0;
            fields.Add(new("keyframe", keyframe ? 1 : 0));
            if (keyframe)
            {
                Describe(Ffv1Parameters.ReadFrameHeader(ref rc), fields);
            }

            return fields;
        }

        List<(int Start, int End, int FooterEnd)> spans = Ffv1Decoder.FindSlices(data, data.Length, config);
        for (int s = 0; s < spans.Count; s++)
        {
            (int start, int end, _) = spans[s];
            var rc = new Ffv1RangeDecoder(data, start, end, Ffv1StateTable.Default);
            if (s == 0)
            {
                byte keyState = 128;
                fields.Add(new("keyframe", rc.ReadBit(ref keyState)));
            }

            rc.Table = config.StateTable;
            Ffv1SliceHeader header = Ffv1SliceHeader.Read(config, ref rc);
            fields.Add(new("slice", s));
            fields.Add(new("slice_x", header.X));
            fields.Add(new("slice_y", header.Y));
            fields.Add(new("slice_width_minus1", header.Width - 1));
            fields.Add(new("slice_height_minus1", header.Height - 1));
            int[] quant = [header.QuantY, header.QuantChroma, header.QuantExtra];
            for (int i = 0; i < config.QuantIndexCount; i++)
            {
                fields.Add(new($"quant_table_set_index[{i}]", quant[i]));
            }

            fields.Add(new("picture_structure", header.PictureStructure));
            fields.Add(new("sar_num", header.SarNumerator));
            fields.Add(new("sar_den", header.SarDenominator));
            fields.Add(new("slice_size", end - start));
            if (config.SliceCrc)
            {
                fields.Add(new("error_status", data[end + 3]));
            }
        }

        return fields;
    }

    private static void Describe(Ffv1Parameters p, List<CodecHeaderField> fields)
    {
        fields.Add(new("version", p.Version));
        if (p.Version >= 3)
        {
            fields.Add(new("micro_version", p.MicroVersion));
        }

        fields.Add(new("coder_type", p.CoderType));
        fields.Add(new("colorspace_type", p.ColorSpace));
        if (p.Version >= 1)
        {
            fields.Add(new("bits_per_raw_sample", p.BitsPerRawSample));
        }

        fields.Add(new("chroma_planes", p.ChromaPlanes ? 1 : 0));
        fields.Add(new("log2_h_chroma_subsample", p.Log2HChromaSubsample));
        fields.Add(new("log2_v_chroma_subsample", p.Log2VChromaSubsample));
        fields.Add(new("extra_plane", p.ExtraPlane ? 1 : 0));
        if (p.Version >= 3)
        {
            fields.Add(new("num_h_slices_minus1", p.HorizontalSlices - 1));
            fields.Add(new("num_v_slices_minus1", p.VerticalSlices - 1));
            fields.Add(new("quant_table_set_count", p.QuantTables.Length));
        }

        for (int i = 0; i < p.QuantTables.Length; i++)
        {
            fields.Add(new($"context_count[{i}]", p.ContextCounts[i]));
        }

        if (p.Version >= 3)
        {
            for (int i = 0; i < p.QuantTables.Length; i++)
            {
                fields.Add(new($"states_coded[{i}]", p.InitialStates[i] is null ? 0 : 1));
            }

            fields.Add(new("ec", p.SliceCrc ? 1 : 0));
            fields.Add(new("intra", p.IntraOnly ? 1 : 0));
        }
    }
}
