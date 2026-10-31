namespace Egami.Formats.Jpeg;

/// <summary>
/// The probability estimation of the QM-coder (T.81 table D.3), shared by <see cref="JpegArithmeticDecoder"/> and
/// <see cref="JpegArithmeticEncoder"/>. A context is one byte: the index of its state in the low 7 bits, its MPS sense in the top bit.
/// </summary>
internal static class JpegQmTables
{
    /// <summary>The state of the fixed 0.5 estimate used for signs and refinement bits: Qe X'5A1D', never adapted.</summary>
    internal const byte FixedState = 113;

    /// <summary>
    /// Table D.3, one entry per state: Qe_Value in bits 16 to 31, Next_Index_MPS in bits 8 to 15, Switch_MPS in bit 7 and
    /// Next_Index_LPS in bits 0 to 6. Entry 113 is the fixed estimate, which leads back to itself.
    /// </summary>
    internal static readonly int[] States =
    [
        State(0x5A1D, 1, 1, 1), State(0x2586, 14, 2, 0), State(0x1114, 16, 3, 0), State(0x080B, 18, 4, 0),
        State(0x03D8, 20, 5, 0), State(0x01DA, 23, 6, 0), State(0x00E5, 25, 7, 0), State(0x006F, 28, 8, 0),
        State(0x0036, 30, 9, 0), State(0x001A, 33, 10, 0), State(0x000D, 35, 11, 0), State(0x0006, 9, 12, 0),
        State(0x0003, 10, 13, 0), State(0x0001, 12, 13, 0), State(0x5A7F, 15, 15, 1), State(0x3F25, 36, 16, 0),
        State(0x2CF2, 38, 17, 0), State(0x207C, 39, 18, 0), State(0x17B9, 40, 19, 0), State(0x1182, 42, 20, 0),
        State(0x0CEF, 43, 21, 0), State(0x09A1, 45, 22, 0), State(0x072F, 46, 23, 0), State(0x055C, 48, 24, 0),
        State(0x0406, 49, 25, 0), State(0x0303, 51, 26, 0), State(0x0240, 52, 27, 0), State(0x01B1, 54, 28, 0),
        State(0x0144, 56, 29, 0), State(0x00F5, 57, 30, 0), State(0x00B7, 59, 31, 0), State(0x008A, 60, 32, 0),
        State(0x0068, 62, 33, 0), State(0x004E, 63, 34, 0), State(0x003B, 32, 35, 0), State(0x002C, 33, 9, 0),
        State(0x5AE1, 37, 37, 1), State(0x484C, 64, 38, 0), State(0x3A0D, 65, 39, 0), State(0x2EF1, 67, 40, 0),
        State(0x261F, 68, 41, 0), State(0x1F33, 69, 42, 0), State(0x19A8, 70, 43, 0), State(0x1518, 72, 44, 0),
        State(0x1177, 73, 45, 0), State(0x0E74, 74, 46, 0), State(0x0BFB, 75, 47, 0), State(0x09F8, 77, 48, 0),
        State(0x0861, 78, 49, 0), State(0x0706, 79, 50, 0), State(0x05CD, 48, 51, 0), State(0x04DE, 50, 52, 0),
        State(0x040F, 50, 53, 0), State(0x0363, 51, 54, 0), State(0x02D4, 52, 55, 0), State(0x025C, 53, 56, 0),
        State(0x01F8, 54, 57, 0), State(0x01A4, 55, 58, 0), State(0x0160, 56, 59, 0), State(0x0125, 57, 60, 0),
        State(0x00F6, 58, 61, 0), State(0x00CB, 59, 62, 0), State(0x00AB, 61, 63, 0), State(0x008F, 61, 32, 0),
        State(0x5B12, 65, 65, 1), State(0x4D04, 80, 66, 0), State(0x412C, 81, 67, 0), State(0x37D8, 82, 68, 0),
        State(0x2FE8, 83, 69, 0), State(0x293C, 84, 70, 0), State(0x2379, 86, 71, 0), State(0x1EDF, 87, 72, 0),
        State(0x1AA9, 87, 73, 0), State(0x174E, 72, 74, 0), State(0x1424, 72, 75, 0), State(0x119C, 74, 76, 0),
        State(0x0F6B, 74, 77, 0), State(0x0D51, 75, 78, 0), State(0x0BB6, 77, 79, 0), State(0x0A40, 77, 48, 0),
        State(0x5832, 80, 81, 1), State(0x4D1C, 88, 82, 0), State(0x438E, 89, 83, 0), State(0x3BDD, 90, 84, 0),
        State(0x34EE, 91, 85, 0), State(0x2EAE, 92, 86, 0), State(0x299A, 93, 87, 0), State(0x2516, 86, 71, 0),
        State(0x5570, 88, 89, 1), State(0x4CA9, 95, 90, 0), State(0x44D9, 96, 91, 0), State(0x3E22, 97, 92, 0),
        State(0x3824, 99, 93, 0), State(0x32B4, 99, 94, 0), State(0x2E17, 93, 86, 0), State(0x56A8, 95, 96, 1),
        State(0x4F46, 101, 97, 0), State(0x47E5, 102, 98, 0), State(0x41CF, 103, 99, 0), State(0x3C3D, 104, 100, 0),
        State(0x375E, 99, 93, 0), State(0x5231, 105, 102, 0), State(0x4C0F, 106, 103, 0), State(0x4639, 107, 104, 0),
        State(0x415E, 103, 99, 0), State(0x5627, 105, 106, 1), State(0x50E7, 108, 107, 0), State(0x4B85, 109, 103, 0),
        State(0x5597, 110, 109, 0), State(0x504F, 111, 107, 0), State(0x5A10, 110, 111, 1), State(0x5522, 112, 109, 0),
        State(0x59EB, 112, 111, 1), State(0x5A1D, 113, 113, 0),
    ];

    private static int State(int qe, int nextLps, int nextMps, int switchMps)
    {
        return (qe << 16) | (nextMps << 8) | (switchMps << 7) | nextLps;
    }
}
