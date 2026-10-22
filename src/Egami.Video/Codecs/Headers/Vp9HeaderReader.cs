using Egami.Video.Codecs.Vp9;

namespace Egami.Video.Codecs.Headers;

/// <summary>
/// Lists a VP9 packet's header fields (VP9 bitstream specification sections 6.2 and 6.3), under the specification's names: the
/// superframe index when there is one, then for each frame its uncompressed header and the compressed header's mode and
/// reference fields, with the probability updates given as counts (<c>coef_prob_updates</c> and so on). A frame that takes its
/// size from a reference (found_ref) is read with the track's size, since one packet does not say what the reference holds.
/// Reads only the headers, never the tiles.
/// </summary>
internal static class Vp9HeaderReader
{
    public static List<CodecHeaderField> Read(ReadOnlySpan<byte> packet, int trackWidth, int trackHeight)
    {
        List<CodecHeaderField> fields = [];
        Span<int> sizes = stackalloc int[8];
        int count = Vp9StreamDecoder.ParseSuperframeIndex(packet, sizes);
        if (count > 0)
        {
            fields.Add(new("frames_in_superframe_minus_1", count - 1));
            for (int i = 0; i < count; i++)
            {
                fields.Add(new($"frame_sizes[{i}]", sizes[i]));
            }

            int at = 0;
            for (int i = 0; i < count; i++)
            {
                if (sizes[i] > packet.Length - at)
                {
                    throw new CorruptDataException("VP9", at, "A frame size in the superframe index runs past the packet.");
                }

                fields.Add(new("frame", i));
                ReadFrame(packet.Slice(at, sizes[i]), trackWidth, trackHeight, fields);
                at += sizes[i];
            }
        }
        else
        {
            ReadFrame(packet, trackWidth, trackHeight, fields);
        }

        return fields;
    }

    private static void ReadFrame(ReadOnlySpan<byte> frame, int trackWidth, int trackHeight, List<CodecHeaderField> fields)
    {
        Vp9FrameHeader h = Vp9FrameHeader.Read(frame, _ => (trackWidth, trackHeight), fields);
        if (h.ShowExistingFrame)
        {
            return;
        }

        int start = h.UncompressedHeaderSize;
        if (start + h.HeaderSizeInBytes > frame.Length)
        {
            throw new CorruptDataException("VP9", start, "The compressed header runs past the end of the frame.");
        }

        byte[] data = new byte[h.HeaderSizeInBytes + 8];
        frame.Slice(start, h.HeaderSizeInBytes).CopyTo(data);
        Vp9BoolDecoder r = default;
        if (r.Init(data, 0, h.HeaderSizeInBytes))
        {
            throw new CorruptDataException("VP9", start, "The compressed header's marker bit is set.");
        }

        int txMode = 0;
        if (!h.Lossless)
        {
            txMode = r.ReadLiteral(2);
            if (txMode == Vp9Tables.Allow32x32)
            {
                txMode += r.ReadBit();
            }
        }

        fields.Add(new("tx_mode", txMode));
        if (txMode == Vp9Tables.TxModeSelect)
        {
            fields.Add(new("tx_prob_updates", Updates(ref r, (2 * 1) + (2 * 2) + (2 * 3))));
        }

        int coefUpdates = 0;
        for (int tx = 0; tx <= Vp9Tables.TxModeToBiggestTxSize[txMode]; tx++)
        {
            if (r.ReadBit() != 0)
            {
                coefUpdates += Updates(ref r, 2 * 2 * ((3 + (5 * 6)) * 3));
            }
        }

        fields.Add(new("coef_prob_updates", coefUpdates));
        fields.Add(new("skip_prob_updates", Updates(ref r, 3)));
        if (!h.FrameIsIntraOnly)
        {
            fields.Add(new("inter_mode_prob_updates", Updates(ref r, 7 * 3)));
            if (h.InterpFilter == Vp9Tables.Switchable)
            {
                fields.Add(new("interp_filter_prob_updates", Updates(ref r, 4 * 2)));
            }

            fields.Add(new("is_inter_prob_updates", Updates(ref r, 4)));

            // Error resilient frames start from cleared sign biases, as libvpx's setup_past_independence leaves them.
            int[] bias = h.ErrorResilientMode ? new int[4] : h.RefFrameSignBias;
            bool compoundAllowed = bias[2] != bias[1] || bias[3] != bias[1];
            int referenceMode = Vp9Tables.SingleReference;
            if (compoundAllowed)
            {
                int nonSingle = r.ReadBit();
                fields.Add(new("non_single_reference", nonSingle));
                if (nonSingle != 0)
                {
                    int select = r.ReadBit();
                    fields.Add(new("reference_select", select));
                    referenceMode = select != 0 ? Vp9Tables.ReferenceModeSelect : Vp9Tables.CompoundReference;
                }
            }

            if (referenceMode == Vp9Tables.ReferenceModeSelect)
            {
                fields.Add(new("comp_mode_prob_updates", Updates(ref r, 5)));
            }

            if (referenceMode != Vp9Tables.CompoundReference)
            {
                fields.Add(new("single_ref_prob_updates", Updates(ref r, 5 * 2)));
            }

            if (referenceMode != Vp9Tables.SingleReference)
            {
                fields.Add(new("comp_ref_prob_updates", Updates(ref r, 5)));
            }

            fields.Add(new("y_mode_prob_updates", Updates(ref r, 4 * 9)));
            fields.Add(new("partition_prob_updates", Updates(ref r, 16 * 3)));
            int mvUpdates = MvUpdates(ref r, 3);
            for (int i = 0; i < 2; i++)
            {
                mvUpdates += MvUpdates(ref r, 1 + 10 + 1 + 10);
            }

            for (int i = 0; i < 2; i++)
            {
                mvUpdates += MvUpdates(ref r, (2 * 3) + 3);
            }

            if (h.AllowHighPrecisionMv)
            {
                mvUpdates += MvUpdates(ref r, 2 * 2);
            }

            fields.Add(new("mv_prob_updates", mvUpdates));
        }

        if (r.HasError)
        {
            throw new CorruptDataException("VP9", start, "The compressed header is cut off.");
        }
    }

    /// <summary>Reads <paramref name="count"/> optional probability updates (diff_update_prob) and returns how many were present.</summary>
    private static int Updates(ref Vp9BoolDecoder r, int count)
    {
        int updates = 0;
        for (int i = 0; i < count; i++)
        {
            if (r.Read(252) != 0)
            {
                Vp9Decoder<byte>.DecodeTermSubexp(ref r);
                updates++;
            }
        }

        return updates;
    }

    private static int MvUpdates(ref Vp9BoolDecoder r, int count)
    {
        int updates = 0;
        for (int i = 0; i < count; i++)
        {
            if (r.Read(252) != 0)
            {
                r.ReadLiteral(7);
                updates++;
            }
        }

        return updates;
    }
}
