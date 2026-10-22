namespace Egami.Video.Codecs.Av1;

/// <summary>
/// The AV1 codec configuration record (av1C, AV1 Codec ISO Media File Format Binding section 2.3), which MP4 stores in its av1C
/// box and Matroska and WebM in a V_AV1 track's CodecPrivate: four bytes of profile, level, tier and color configuration, then
/// configOBUs (the sequence header OBU, and metadata OBUs that hold for the whole track).
/// </summary>
internal readonly record struct Av1Config(int Profile, int Level, int Tier, bool HighBitDepth, bool TwelveBit, bool Monochrome, int SubsamplingX,
    int SubsamplingY, int ChromaSamplePosition, ReadOnlyMemory<byte> ConfigObus)
{
    /// <summary>Reads an av1C record, or returns null when it is not one (no marker bit, another version, or under four bytes).</summary>
    public static Av1Config? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0x81)
        {
            return null;
        }

        return new Av1Config(data[1] >> 5, data[1] & 31, data[2] >> 7, ((data[2] >> 6) & 1) != 0, ((data[2] >> 5) & 1) != 0,
            ((data[2] >> 4) & 1) != 0, (data[2] >> 3) & 1, (data[2] >> 2) & 1, data[2] & 3, data[4..].ToArray());
    }

    /// <summary>Builds an av1C record from a sequence header OBU (its whole bytes, header included) and what it says.</summary>
    public static byte[] Build(ReadOnlySpan<byte> sequenceObu, Av1SequenceInfo sequence)
    {
        using MemoryStream stream = new();
        stream.WriteByte(0x81); // marker, version 1
        stream.WriteByte((byte)((sequence.Profile << 5) | sequence.Level));
        stream.WriteByte((byte)((sequence.Tier << 7) | (sequence.HighBitDepth ? 0x40 : 0) | (sequence.TwelveBit ? 0x20 : 0) | (sequence.Monochrome ? 0x10 : 0)
            | (sequence.SubsamplingX << 3) | (sequence.SubsamplingY << 2) | sequence.ChromaSamplePosition));
        stream.WriteByte(0); // reserved, no initial_presentation_delay
        Av1Obu obu = Av1Obus.Read(sequenceObu, 0);
        Av1Obus.WriteWithSize(stream, sequenceObu, obu);
        return stream.ToArray();
    }

    /// <summary>Builds the av1C record of the first sequence header in low-overhead data, or returns null when there is none.</summary>
    public static byte[]? FromObus(ReadOnlySpan<byte> obus)
    {
        foreach (Av1Obu obu in Av1Obus.Split(obus))
        {
            if (obu.Type == Av1Obus.SequenceHeader)
            {
                ReadOnlySpan<byte> bytes = obus[obu.Start..obu.End];
                return Build(bytes, Av1SequenceInfo.Parse(obus.Slice(obu.PayloadStart, obu.PayloadLength)));
            }
        }

        return null;
    }
}
