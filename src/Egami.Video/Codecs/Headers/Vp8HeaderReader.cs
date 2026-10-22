namespace Egami.Video.Codecs.Headers;

/// <summary>
/// Lists a VP8 frame's header fields (RFC 6386 sections 9 and 19), under the specification's names: the uncompressed frame tag
/// and keyframe size, then the first partition's bool-coded fields up to the motion vector probability updates. Lists hold
/// one field per entry (<c>quantizer_update_value[2]</c>); fields a frame does not code (such as the golden refresh flags of a
/// keyframe) are left out rather than given their implied values. Reads only the header, never the macroblocks.
/// </summary>
internal static class Vp8HeaderReader
{
    public static List<CodecHeaderField> Read(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 3)
        {
            throw new CorruptDataException("VP8", 0, $"The frame has {frame.Length} bytes, too few for its 3-byte tag.");
        }

        List<CodecHeaderField> fields = [];
        int tag = frame[0] | (frame[1] << 8) | (frame[2] << 16);
        bool key = (tag & 1) == 0;
        int firstPartSize = tag >> 5;
        fields.Add(new("key_frame", key ? 1 : 0));
        fields.Add(new("version", (tag >> 1) & 7));
        fields.Add(new("show_frame", (tag >> 4) & 1));
        fields.Add(new("first_part_size", firstPartSize));
        int start = 3;
        if (key)
        {
            if (frame.Length < 10 || frame[3] != 0x9D || frame[4] != 0x01 || frame[5] != 0x2A)
            {
                throw new CorruptDataException("VP8", 3, "A keyframe lacks the start code 9D 01 2A.");
            }

            int width = frame[6] | (frame[7] << 8), height = frame[8] | (frame[9] << 8);
            fields.Add(new("width", width & 0x3FFF));
            fields.Add(new("horizontal_scale", width >> 14));
            fields.Add(new("height", height & 0x3FFF));
            fields.Add(new("vertical_scale", height >> 14));
            start = 10;
        }

        BoolReader b = new(frame[start..Math.Min(frame.Length, start + firstPartSize)]);
        if (key)
        {
            Add(fields, "color_space", b.Literal(1));
            Add(fields, "clamping_type", b.Literal(1));
        }

        if (Add(fields, "segmentation_enabled", b.Literal(1)) != 0)
        {
            int updateMap = Add(fields, "update_mb_segmentation_map", b.Literal(1));
            if (Add(fields, "update_segment_feature_data", b.Literal(1)) != 0)
            {
                Add(fields, "segment_feature_mode", b.Literal(1));
                for (int i = 0; i < 4; i++)
                {
                    Add(fields, $"quantizer_update_value[{i}]", b.Literal(1) != 0 ? b.Signed(7) : 0);
                }

                for (int i = 0; i < 4; i++)
                {
                    Add(fields, $"loop_filter_update_value[{i}]", b.Literal(1) != 0 ? b.Signed(6) : 0);
                }
            }

            if (updateMap != 0)
            {
                for (int i = 0; i < 3; i++)
                {
                    Add(fields, $"segment_prob[{i}]", b.Literal(1) != 0 ? b.Literal(8) : 255);
                }
            }
        }

        Add(fields, "filter_type", b.Literal(1));
        Add(fields, "loop_filter_level", b.Literal(6));
        Add(fields, "sharpness_level", b.Literal(3));
        if (Add(fields, "loop_filter_adj_enable", b.Literal(1)) != 0 && Add(fields, "mode_ref_lf_delta_update", b.Literal(1)) != 0)
        {
            for (int i = 0; i < 4; i++)
            {
                if (b.Literal(1) != 0)
                {
                    Add(fields, $"ref_frame_delta_magnitude[{i}]", b.Signed(6));
                }
            }

            for (int i = 0; i < 4; i++)
            {
                if (b.Literal(1) != 0)
                {
                    Add(fields, $"mb_mode_delta_magnitude[{i}]", b.Signed(6));
                }
            }
        }

        Add(fields, "log2_nbr_of_dct_partitions", b.Literal(2));
        Add(fields, "y_ac_qi", b.Literal(7));
        foreach (string delta in (ReadOnlySpan<string>)["y_dc_delta", "y2_dc_delta", "y2_ac_delta", "uv_dc_delta", "uv_ac_delta"])
        {
            Add(fields, delta, b.Literal(1) != 0 ? b.Signed(4) : 0);
        }

        if (key)
        {
            Add(fields, "refresh_entropy_probs", b.Literal(1));
        }
        else
        {
            int golden = Add(fields, "refresh_golden_frame", b.Literal(1));
            int alternate = Add(fields, "refresh_alternate_frame", b.Literal(1));
            if (golden == 0)
            {
                Add(fields, "copy_buffer_to_golden", b.Literal(2));
            }

            if (alternate == 0)
            {
                Add(fields, "copy_buffer_to_alternate", b.Literal(2));
            }

            Add(fields, "sign_bias_golden", b.Literal(1));
            Add(fields, "sign_bias_alternate", b.Literal(1));
            Add(fields, "refresh_entropy_probs", b.Literal(1));
            Add(fields, "refresh_last", b.Literal(1));
        }

        // The coefficient probability updates are listed as how many there were; the values themselves are the entropy state.
        int updates = 0;
        foreach (byte probability in Vp8HeaderTables.CoeffUpdateProbs)
        {
            if (b.Bool(probability) != 0)
            {
                b.Literal(8);
                updates++;
            }
        }

        Add(fields, "coeff_prob_updates", updates);
        if (Add(fields, "mb_no_coeff_skip", b.Literal(1)) != 0)
        {
            Add(fields, "prob_skip_false", b.Literal(8));
        }

        if (!key)
        {
            Add(fields, "prob_intra", b.Literal(8));
            Add(fields, "prob_last", b.Literal(8));
            Add(fields, "prob_gf", b.Literal(8));
            if (b.Literal(1) != 0)
            {
                for (int i = 0; i < 4; i++)
                {
                    Add(fields, $"intra_16x16_prob[{i}]", b.Literal(8));
                }
            }

            if (b.Literal(1) != 0)
            {
                for (int i = 0; i < 3; i++)
                {
                    Add(fields, $"intra_chroma_prob[{i}]", b.Literal(8));
                }
            }

            int mvUpdates = 0;
            foreach (byte probability in Vp8HeaderTables.MvUpdateProbs)
            {
                if (b.Bool(probability) != 0)
                {
                    b.Literal(7);
                    mvUpdates++;
                }
            }

            Add(fields, "mv_prob_updates", mvUpdates);
        }

        if (b.PastEnd)
        {
            throw new CorruptDataException("VP8", start, $"The first partition ({firstPartSize} bytes, {frame.Length - start} present) ends inside the frame header.");
        }

        return fields;
    }

    private static int Add(List<CodecHeaderField> fields, string name, int value)
    {
        fields.Add(new CodecHeaderField(name, value));
        return value;
    }

    /// <summary>The boolean decoder of RFC 6386 section 7.3, bit by bit; past the data it reads zeros and says so.</summary>
    private ref struct BoolReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _position;
        private uint _value;
        private uint _range;
        private int _bitCount;

        public BoolReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _range = 255;
            _value = (uint)((Next() << 8) | Next());
            _bitCount = 0;
        }

        public bool PastEnd { get; private set; }

        public int Bool(int probability)
        {
            uint split = 1 + (((_range - 1) * (uint)probability) >> 8);
            uint bigSplit = split << 8;
            int bit;
            if (_value >= bigSplit)
            {
                bit = 1;
                _range -= split;
                _value -= bigSplit;
            }
            else
            {
                bit = 0;
                _range = split;
            }

            while (_range < 128)
            {
                _value <<= 1;
                _range <<= 1;
                if (++_bitCount == 8)
                {
                    _bitCount = 0;
                    _value |= (uint)Next();
                }
            }

            return bit;
        }

        public int Literal(int bits)
        {
            int value = 0;
            while (bits-- > 0)
            {
                value = (value << 1) | Bool(128);
            }

            return value;
        }

        /// <summary>A magnitude of <paramref name="bits"/> bits followed by a sign bit.</summary>
        public int Signed(int bits)
        {
            int magnitude = Literal(bits);
            return Literal(1) != 0 ? -magnitude : magnitude;
        }

        private int Next()
        {
            if (_position < _data.Length)
            {
                return _data[_position++];
            }

            // Two bytes are read ahead, so the data runs out only once more than that is wanted.
            if (++_position > _data.Length + 2)
            {
                PastEnd = true;
            }

            return 0;
        }
    }
}
