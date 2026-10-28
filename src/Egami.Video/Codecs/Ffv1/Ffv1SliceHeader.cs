namespace Egami.Video.Codecs.Ffv1;

/// <summary>A version 3 slice header (RFC 9043 section 4.6): the slice's place on the slice grid, its quantization table sets and the picture's field order and aspect ratio.</summary>
internal readonly record struct Ffv1SliceHeader(
    int X, int Y, int Width, int Height, int QuantY, int QuantChroma, int QuantExtra, int PictureStructure, int SarNumerator, int SarDenominator)
{
    /// <summary>Reads a slice header with its own states, all 128.</summary>
    public static Ffv1SliceHeader Read(Ffv1Parameters p, ref Ffv1RangeDecoder rc)
    {
        Span<byte> state = stackalloc byte[Ffv1Parameters.ContextSize];
        state.Fill(128);
        int sx = rc.ReadField(state, p.HorizontalSlices - 1, "slice_x");
        int sy = rc.ReadField(state, p.VerticalSlices - 1, "slice_y");
        int sw = rc.ReadField(state, p.HorizontalSlices - sx - 1, "slice_width - 1") + 1;
        int sh = rc.ReadField(state, p.VerticalSlices - sy - 1, "slice_height - 1") + 1;
        Span<int> quant = stackalloc int[3];
        int last = p.QuantTables.Length - 1;
        for (int i = 0; i < p.QuantIndexCount; i++)
        {
            quant[i] = rc.ReadField(state, last, "quant_table_set_index");
        }

        int structure = rc.ReadSymbol(state, false);
        int sarNum = rc.ReadSymbol(state, false);
        int sarDen = rc.ReadSymbol(state, false);
        return new Ffv1SliceHeader(sx, sy, sw, sh, quant[0], quant[1], quant[2], structure, sarNum, sarDen);
    }
}
