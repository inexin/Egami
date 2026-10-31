using Egami.Formats.Avif;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// The sato expression interpreter (AVIF 1.2.0 section 4.2.3): every operator, every intermediate depth with its clamping, the
/// rules a parser rejects, and the items a reader ignores.
/// </summary>
public class AvifSampleTransformTests
{
    private const byte C = AvifSampleTransform.Constant;

    [Theory]
    [InlineData(AvifSampleTransform.Sum, 7, 5, 12)]
    [InlineData(AvifSampleTransform.Difference, 7, 5, 2)]
    [InlineData(AvifSampleTransform.Product, 7, 5, 35)]
    [InlineData(AvifSampleTransform.Quotient, 7, 2, 3)]
    [InlineData(AvifSampleTransform.Quotient, -7, 2, -3)]
    [InlineData(AvifSampleTransform.Quotient, 7, 0, 7)]
    [InlineData(AvifSampleTransform.And, 12, 10, 8)]
    [InlineData(AvifSampleTransform.Or, 12, 10, 14)]
    [InlineData(AvifSampleTransform.Xor, 12, 10, 6)]
    [InlineData(AvifSampleTransform.Pow, 3, 4, 81)]
    [InlineData(AvifSampleTransform.Pow, 0, 0, 0)]
    [InlineData(AvifSampleTransform.Pow, -2, 3, -8)]
    [InlineData(AvifSampleTransform.Pow, 2, -1, 0)]
    [InlineData(AvifSampleTransform.Pow, -1, -3, -1)]
    [InlineData(AvifSampleTransform.Pow, 5, 0, 1)]
    [InlineData(AvifSampleTransform.Min, 7, -5, -5)]
    [InlineData(AvifSampleTransform.Max, 7, -5, 7)]
    public void BinaryOperators(byte op, long left, long right, long expected)
    {
        Assert.Equal(expected, Raw(32, [(C, left), (C, right), (op, 0)]));
    }

    [Theory]
    [InlineData(AvifSampleTransform.Negation, 5, -5)]
    [InlineData(AvifSampleTransform.Absolute, -5, 5)]
    [InlineData(AvifSampleTransform.Not, 5, -6)]
    [InlineData(AvifSampleTransform.Bsr, 1, 0)]
    [InlineData(AvifSampleTransform.Bsr, 255, 7)]
    [InlineData(AvifSampleTransform.Bsr, 256, 8)]
    [InlineData(AvifSampleTransform.Bsr, 0, 0)]
    [InlineData(AvifSampleTransform.Bsr, -4, 0)]
    public void UnaryOperators(byte op, long operand, long expected)
    {
        Assert.Equal(expected, Raw(32, [(C, operand), (op, 0)]));
    }

    [Theory]
    [InlineData(8, 100, 100, AvifSampleTransform.Sum, 127)]
    [InlineData(8, -100, 100, AvifSampleTransform.Difference, -128)]
    [InlineData(16, 300, 300, AvifSampleTransform.Product, 32767)]
    [InlineData(32, int.MaxValue, 1, AvifSampleTransform.Sum, int.MaxValue)]
    [InlineData(64, long.MaxValue, 2, AvifSampleTransform.Product, long.MaxValue)]
    [InlineData(64, long.MinValue, 1, AvifSampleTransform.Difference, long.MinValue)]
    [InlineData(16, 3, 20, AvifSampleTransform.Pow, 32767)]
    [InlineData(16, -3, 21, AvifSampleTransform.Pow, -32768)]
    public void ResultsClampToTheIntermediateDepth(int bits, long left, long right, byte op, long expected)
    {
        Assert.Equal(expected, Raw(bits, [(C, left), (C, right), (op, 0)]));
    }

    [Fact]
    public void NegatingTheLowestValueClamps()
    {
        Assert.Equal(127, Raw(8, [(C, -128), (AvifSampleTransform.Negation, 0)]));
    }

    [Fact]
    public void TheOutputClampsToThePixiDepthAndSamplesComeFromTheInputs()
    {
        // (input1 << 4) + input2 - 128, the 12-bit and 8-bit recipe, and its clamping at both ends.
        byte[] data = AvifSampleTransform.Write(32, [(C, 16), (1, 0), (AvifSampleTransform.Product, 0), (2, 0), (AvifSampleTransform.Sum, 0), (C, 128), (AvifSampleTransform.Difference, 0)]);
        AvifSampleTransform sato = AvifSampleTransform.Parse(data, 2, 16, "AVIF")!;
        Assert.Equal((4000 * 16) + 200 - 128, sato.Evaluate([4000, 200]));
        Assert.Equal(65535, sato.Evaluate([4095, 255]));
        Assert.Equal(0, sato.Evaluate([0, 0]));

        ushort[] first = [1, 2, 4095], second = [128, 0, 255];
        ushort[] output = new ushort[3];
        long[][] stack = [new long[3], new long[3]];
        sato.Row([first, second], [0, 0], output, stack);
        Assert.Equal([16, 0, 65535], output);
    }

    [Theory]
    [InlineData(new byte[] { 0x00, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x80 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x40 })]
    [InlineData(new byte[] { 0x00, 0x02, 0x01, 0x01 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x03 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x00 })]
    [InlineData(new byte[] { 0x00, 0x01, 0x01, 0x00 })]
    public void BrokenExpressionsAreRejected(byte[] data)
    {
        // An empty expression, an operator on too few values, values left over, an input past the last, a cut constant, extra bytes.
        Assert.ThrowsAny<CorruptDataException>(() => AvifSampleTransform.Parse(data, 2, 16, "AVIF"));
    }

    [Theory]
    [InlineData(new byte[] { 0x40, 0x01, 0x01 })]
    [InlineData(new byte[] { 0x00, 0x02, 0x01, 0x21 })]
    [InlineData(new byte[] { 0x00, 0x02, 0x01, 0x44 })]
    [InlineData(new byte[] { 0x00, 0x03, 0x01, 0x01, 0x8A })]
    public void UnknownVersionsAndReservedTokensAreIgnored(byte[] data)
    {
        Assert.Null(AvifSampleTransform.Parse(data, 2, 16, "AVIF"));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(16)]
    [InlineData(32)]
    [InlineData(64)]
    public void ConstantsAreReadAtTheIntermediateDepth(int bits)
    {
        long value = bits == 8 ? -100 : -1L << (bits - 2);
        Assert.Equal(value, Raw(bits, [(C, value)]));
    }

    /// <summary>Evaluates an expression of constants at an intermediate depth, before the output is clamped to its pixi depth.</summary>
    private static long Raw(int bits, (byte, long)[] tokens)
    {
        AvifSampleTransform sato = AvifSampleTransform.Parse(AvifSampleTransform.Write(bits, tokens), 2, 16, "AVIF")!;
        return sato.EvaluateUnclamped([]);
    }
}
