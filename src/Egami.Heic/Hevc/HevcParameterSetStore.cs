using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>
/// The parameter sets seen so far (by id) and the parsing of non-VCL NAL units and slice segment headers, as a decoder meets
/// them. With a trace list, each NAL unit's elements are recorded by name, as HM's trace lists them.
/// </summary>
internal sealed class HevcParameterSetStore
{
    private readonly HevcVps?[] _vps = new HevcVps?[16];
    private readonly HevcSps?[] _sps = new HevcSps?[16];
    private readonly HevcPps?[] _pps = new HevcPps?[64];
    private HevcSliceHeader? _lastIndependent;
    private HevcSps? _activeSps;

    /// <summary>Gets or sets where each NAL unit's elements are recorded, with the NAL unit type, or null.</summary>
    internal List<(int NalType, List<(string Name, long Value)> Elements)>? Trace { get; set; }

    /// <summary>Gets the SPS and PPS a PPS id refers to.</summary>
    /// <exception cref="CorruptDataException">Either is missing.</exception>
    internal (HevcSps Sps, HevcPps Pps) Active(int ppsId)
    {
        HevcPps pps = _pps[ppsId] ?? throw HevcErrors.Corrupt($"A slice refers to PPS {ppsId}, which the stream does not have.");
        HevcSps sps = _sps[pps.SpsId] ?? throw HevcErrors.Corrupt($"PPS {ppsId} refers to SPS {pps.SpsId}, which the stream does not have.");
        return (sps, pps);
    }

    /// <summary>
    /// Parses a NAL unit: a parameter set is stored, a slice segment header is returned with its NAL unit, an SEI unit's decoded
    /// picture hash is returned in <paramref name="hash"/>; other units are skipped.
    /// </summary>
    internal HevcSliceHeader? Parse(HevcNal nal, out HevcPictureHash? hash)
    {
        hash = null;
        List<(string Name, long Value)>? elements = Trace is null ? null : [];
        Trace?.Add((nal.Type, elements!));
        if (nal.LayerId != 0)
        {
            // Layers above the base (multi-layer HEVC) are left out (design question 4).
            return null;
        }

        NalBitReader r = nal.Reader(elements);
        switch (nal.Type)
        {
            case HevcNal.Vps:
                {
                    HevcVps vps = HevcVps.Read(r);
                    _vps[vps.Id] = vps;
                    return null;
                }

            case HevcNal.Sps:
                {
                    HevcSps sps = HevcSps.Read(r);
                    _sps[sps.Id] = sps;
                    return null;
                }

            case HevcNal.Pps:
                {
                    HevcPps pps = HevcPps.Read(r);
                    _pps[pps.Id] = pps;
                    return null;
                }

            case HevcNal.Aud:
                r.U(3, "pic_type");
                r.TrailingBits();
                return null;

            case HevcNal.PrefixSei:
            case HevcNal.SuffixSei:
                {
                    if (nal.Type == HevcNal.SuffixSei)
                    {
                        int components = (_activeSps ?? _sps.FirstOrDefault(s => s is not null))?.ChromaFormatIdc == 0 ? 1 : 3;
                        try
                        {
                            hash = HevcPictureHash.Read(r, components);
                        }
                        catch (CorruptDataException)
                        {
                            // A damaged SEI message only loses the check it carries.
                            hash = null;
                        }
                    }

                    return null;
                }

            default:
                if (!nal.IsSlice)
                {
                    return null;
                }

                if (nal.Type >= 22)
                {
                    // Reserved VCL types (22 to 31) are ignored, as H.265 asks of decoders.
                    return null;
                }

                HevcSliceHeader header = HevcSliceHeader.Read(r, nal, Active, _lastIndependent);
                if (!header.Dependent)
                {
                    _lastIndependent = header;
                }

                _activeSps = Active(header.PpsId).Sps;

                return header;
        }
    }
}
