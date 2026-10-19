using System.Buffers.Binary;

using Egami.Formats.Heif;
using Egami.Pixels;

namespace Egami.Formats.Heic;

/// <summary>
/// An hvcC property, the HEVC decoder configuration record (ISO/IEC 14496-15, 8.3.3.1): the profile, tier and level, chroma format
/// and bit depths it declares, the length of the NAL unit size fields in the item data, and the parameter set NAL units. Reading
/// it is container work; the NAL units are only carried here, never parsed (the core package holds no HEVC code).
/// </summary>
internal sealed record HevcConfig
{
    /// <summary>Gets general_profile_space (0 for every profile H.265 defines).</summary>
    internal int ProfileSpace { get; init; }

    /// <summary>Gets general_tier_flag: 0 Main, 1 High.</summary>
    internal int Tier { get; init; }

    /// <summary>Gets general_profile_idc: 1 Main, 2 Main 10, 3 Main Still Picture, 4 the format range extensions, and so on.</summary>
    internal int Profile { get; init; }

    /// <summary>Gets the 32 general_profile_compatibility_flags, flag 0 in the top bit.</summary>
    internal uint ProfileCompatibility { get; init; }

    /// <summary>Gets the 48 bits of general constraint flags.</summary>
    internal ulong Constraints { get; init; }

    /// <summary>Gets general_level_idc: 30 times the level, so 93 is level 3.1.</summary>
    internal int Level { get; init; }

    /// <summary>Gets chroma_format_idc: 0 monochrome, 1 4:2:0, 2 4:2:2, 3 4:4:4.</summary>
    internal int ChromaFormatIdc { get; init; }

    /// <summary>Gets the luma bit depth.</summary>
    internal int LumaBitDepth { get; init; }

    /// <summary>Gets the chroma bit depth.</summary>
    internal int ChromaBitDepth { get; init; }

    /// <summary>Gets the bytes of each NAL unit's size field in the item data: 1, 2 or 4.</summary>
    internal int LengthSize { get; init; }

    /// <summary>Gets the NAL units of the record's arrays (VPS, SPS, PPS and SEI), in order.</summary>
    internal IReadOnlyList<ReadOnlyMemory<byte>> NalUnits { get; init; } = [];

    /// <summary>Gets the chroma format as Egami names it.</summary>
    internal ChromaFormat Chroma => ChromaFormatIdc switch
    {
        0 => ChromaFormat.Yuv400,
        2 => ChromaFormat.Yuv422,
        3 => ChromaFormat.Yuv444,
        _ => ChromaFormat.Yuv420,
    };

    /// <summary>Reads an hvcC property.</summary>
    /// <exception cref="CorruptDataException">The record is cut off, is not version 1, or declares impossible values.</exception>
    internal static HevcConfig Read(HeifFile file, HeifProperty property)
    {
        ReadOnlySpan<byte> data = file.Content(property);
        if (data.Length < 23)
        {
            throw Corrupt(file, "The hvcC property is cut off.");
        }

        if (data[0] != 1)
        {
            throw Corrupt(file, $"The hvcC property's configurationVersion is {data[0]}, not 1.");
        }

        int lengthSize = (data[21] & 3) + 1;
        if (lengthSize == 3)
        {
            throw Corrupt(file, "The hvcC property's lengthSizeMinusOne is 2; NAL unit sizes take 1, 2 or 4 bytes.");
        }

        List<ReadOnlyMemory<byte>> nals = [];
        ReadOnlyMemory<byte> memory = file.Bytes.Slice(property.Box.ContentStart, data.Length);
        int at = 23;
        for (int array = 0; array < data[22]; array++)
        {
            if (at + 3 > data.Length)
            {
                throw Corrupt(file, "The hvcC property's NAL unit arrays are cut off.");
            }

            int count = BinaryPrimitives.ReadUInt16BigEndian(data[(at + 1)..]);
            at += 3;
            for (int i = 0; i < count; i++)
            {
                if (at + 2 > data.Length || at + 2 + BinaryPrimitives.ReadUInt16BigEndian(data[at..]) > data.Length)
                {
                    throw Corrupt(file, "The hvcC property's NAL unit arrays are cut off.");
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(data[at..]);
                nals.Add(memory.Slice(at + 2, length));
                at += 2 + length;
            }
        }

        return new HevcConfig
        {
            ProfileSpace = data[1] >> 6,
            Tier = (data[1] >> 5) & 1,
            Profile = data[1] & 31,
            ProfileCompatibility = BinaryPrimitives.ReadUInt32BigEndian(data[2..]),
            Constraints = ((ulong)BinaryPrimitives.ReadUInt16BigEndian(data[6..]) << 32) | BinaryPrimitives.ReadUInt32BigEndian(data[8..]),
            Level = data[12],
            ChromaFormatIdc = data[16] & 3,
            LumaBitDepth = (data[17] & 7) + 8,
            ChromaBitDepth = (data[18] & 7) + 8,
            LengthSize = lengthSize,
            NalUnits = nals,
        };
    }

    private static CorruptDataException Corrupt(HeifFile file, string problem) => new(file.FormatName, -1, problem);
}
