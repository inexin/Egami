namespace Egami.Formats.Av1;

/// <summary>
/// What a tile parser hands its blocks to: prediction and residual in the order libaom reconstructs them, so the samples a block
/// predicts from are final when it asks for them.
/// </summary>
internal interface IAv1Reconstructor
{
    /// <summary>
    /// Predicts and reconstructs one transform block of an intra block. Called for every transform block, skipped or not.
    /// </summary>
    /// <param name="tile">The tile, whose <see cref="Av1TileParser.Block"/> is the block.</param>
    /// <param name="plane">The plane.</param>
    /// <param name="x">The transform block's left column in the plane, in samples.</param>
    /// <param name="y">The transform block's top row in the plane, in samples.</param>
    /// <param name="txSize">The transform size (<see cref="Tx"/>).</param>
    /// <param name="txType">The transform type (<see cref="TxType"/>).</param>
    /// <param name="eob">The number of coefficients in scan order up to the last nonzero one; 0 for none.</param>
    /// <param name="coeffs">The dequantized coefficients, column by column as libaom holds them; the parser clears them after.</param>
    void IntraBlock(Av1TileParser tile, int plane, int x, int y, int txSize, int txType, int eob, int[] coeffs);

    /// <summary>Predicts a whole intra block copy block, before its residual.</summary>
    void InterPrediction(Av1TileParser tile);

    /// <summary>Adds one transform block of residual to an intra block copy block.</summary>
    void InterResidual(Av1TileParser tile, int plane, int x, int y, int txSize, int txType, int eob, int[] coeffs);
}
