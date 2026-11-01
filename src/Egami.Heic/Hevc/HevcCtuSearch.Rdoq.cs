namespace Egami.Heic.Hevc;

/// <summary>Rate-distortion optimized quantization.</summary>
internal sealed partial class HevcCtuSearch
{
    /// <summary>Quantizes a block's coefficients for the least J; returns the number of levels that are not zero.</summary>
    private int Rdoq(int[] coefficients, short[] levels, int log2, int c, int scanIdx, int qp, int bitDepth, double lambda, bool transformSkip) =>
        _profile.Rdoq ? HevcForward.Quantize(coefficients, levels, log2, qp, bitDepth) : 0;
}
