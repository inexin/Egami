using System.Buffers.Binary;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// An AVCDecoderConfigurationRecord (avcC, ISO/IEC 14496-15 5.3.3.1), as MP4 and Matroska store it in the track's CodecPrivate:
/// the NAL length size of the samples and the parameter set NAL units.
/// </summary>
internal sealed class H264Config
{
    private H264Config(int lengthSize, List<ReadOnlyMemory<byte>> parameterSets)
    {
        LengthSize = lengthSize;
        ParameterSets = parameterSets;
    }

    /// <summary>Gets the size in bytes of each sample's NAL unit lengths (1, 2 or 4).</summary>
    internal int LengthSize { get; }

    /// <summary>Gets the SPS, PPS and SPS extension NAL units, in the record's order.</summary>
    internal List<ReadOnlyMemory<byte>> ParameterSets { get; }

    /// <summary>Reads a record, or returns null when the bytes are not one (an empty CodecPrivate means Annex B packets).</summary>
    /// <exception cref="CorruptDataException">The record is cut off or has a length size of 3.</exception>
    internal static H264Config? Parse(ReadOnlyMemory<byte> data)
    {
        ReadOnlySpan<byte> s = data.Span;
        if (s.Length < 7 || s[0] != 1)
        {
            return null;
        }

        int lengthSize = (s[4] & 3) + 1;
        if (lengthSize == 3)
        {
            throw H264Errors.Corrupt("The avcC record gives a NAL length size of 3.");
        }

        List<ReadOnlyMemory<byte>> sets = [];
        int at = 5;
        for (int kind = 0; kind < 2; kind++)
        {
            if (at >= s.Length)
            {
                throw H264Errors.Corrupt("The avcC record is cut off.");
            }

            int count = kind == 0 ? s[at] & 31 : s[at];
            at++;
            for (int i = 0; i < count; i++)
            {
                if (at + 2 > s.Length)
                {
                    throw H264Errors.Corrupt("The avcC record is cut off.");
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(s[at..]);
                at += 2;
                if (at + length > s.Length)
                {
                    throw H264Errors.Corrupt("A parameter set in the avcC record is cut off.");
                }

                if (length > 0)
                {
                    sets.Add(data.Slice(at, length));
                }

                at += length;
            }
        }

        return new H264Config(lengthSize, sets);
    }
}
