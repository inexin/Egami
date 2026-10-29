using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>An HEVC NAL unit: its header fields and its RBSP (H.265 7.3.1).</summary>
internal sealed class HevcNal
{
    internal const int TrailN = 0;
    internal const int RaslN = 8;
    internal const int RaslR = 9;
    internal const int BlaWLp = 16;
    internal const int BlaWRadl = 17;
    internal const int BlaNLp = 18;
    internal const int IdrWRadl = 19;
    internal const int IdrNLp = 20;
    internal const int CraNut = 21;
    internal const int Vps = 32;
    internal const int Sps = 33;
    internal const int Pps = 34;
    internal const int Aud = 35;
    internal const int Eos = 36;
    internal const int Eob = 37;
    internal const int Fd = 38;
    internal const int PrefixSei = 39;
    internal const int SuffixSei = 40;

    private HevcNal(int type, int layerId, int temporalIdPlus1, byte[] rbsp, int[] removed)
    {
        Type = type;
        LayerId = layerId;
        TemporalIdPlus1 = temporalIdPlus1;
        Rbsp = rbsp;
        Removed = removed;
    }

    /// <summary>Gets nal_unit_type.</summary>
    internal int Type { get; }

    /// <summary>Gets nuh_layer_id.</summary>
    internal int LayerId { get; }

    /// <summary>Gets nuh_temporal_id_plus1.</summary>
    internal int TemporalIdPlus1 { get; }

    /// <summary>Gets the NAL unit with emulation prevention bytes removed, its two header bytes included.</summary>
    internal byte[] Rbsp { get; }

    /// <summary>Gets the positions in the escaped NAL unit of the emulation prevention bytes removed, in order.</summary>
    internal int[] Removed { get; }

    /// <summary>Gets whether this is a coded slice segment (VCL NAL unit types 0 to 31).</summary>
    internal bool IsSlice => Type < 32;

    /// <summary>Gets whether this is an IRAP picture's slice (BLA, IDR, CRA, and the reserved IRAP types).</summary>
    internal bool IsIrap => Type is >= BlaWLp and <= 23;

    /// <summary>Gets whether this is an IDR picture's slice.</summary>
    internal bool IsIdr => Type is IdrWRadl or IdrNLp;

    /// <summary>Parses one NAL unit (no start code or length field).</summary>
    /// <exception cref="CorruptDataException">The unit is shorter than its header, or forbidden_zero_bit is set.</exception>
    internal static HevcNal Parse(ReadOnlySpan<byte> nal)
    {
        if (nal.Length < 2)
        {
            throw HevcErrors.Corrupt("A NAL unit is shorter than its two-byte header.");
        }

        if ((nal[0] & 0x80) != 0)
        {
            throw HevcErrors.Corrupt("A NAL unit's forbidden_zero_bit is 1.");
        }

        List<int> removed = [];
        byte[] rbsp = NalFraming.Unescape(nal, removed);
        return new HevcNal((nal[0] >> 1) & 63, ((nal[0] & 1) << 5) | (nal[1] >> 3), nal[1] & 7, rbsp, [.. removed]);
    }

    /// <summary>Reads the header's fields into a trace (forbidden_zero_bit, nal_unit_type, nuh_layer_id, nuh_temporal_id_plus1).</summary>
    internal NalBitReader Reader(List<(string Name, long Value)>? trace)
    {
        NalBitReader reader = new(Rbsp) { Trace = trace };
        reader.U(1, "forbidden_zero_bit");
        reader.U(6, "nal_unit_type");
        reader.U(6, "nuh_layer_id");
        reader.U(3, "nuh_temporal_id_plus1");
        return reader;
    }
}
