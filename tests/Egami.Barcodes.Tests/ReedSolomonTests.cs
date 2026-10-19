using Egami.Barcodes.ErrorCorrection;

namespace Egami.Barcodes.Tests;

/// <summary>Reed-Solomon encoding and decoding with errors and erasures over every field the symbologies use.</summary>
public class ReedSolomonTests
{
    public static TheoryData<string, int, int> Codes => new()
    {
        // Field, codeword length, check symbols: sizes the symbologies use, and the longest each field allows.
        { "QR", 26, 10 },
        { "QR", 255, 30 },
        { "Data Matrix", 8, 5 },
        { "Data Matrix", 255, 68 },
        { "PDF417", 40, 16 },
        { "PDF417", 928, 512 },
        { "Aztec 4-bit", 7, 5 },
        { "Aztec 4-bit", 10, 6 },
        { "Aztec 4-bit", 15, 8 },
        { "Aztec 6-bit", 63, 20 },
        { "Aztec 10-bit", 400, 120 },
        { "Aztec 12-bit", 1437, 400 },
    };

    [Theory]
    [MemberData(nameof(Codes))]
    public void RoundTripsAtTheCorrectionLimit(string name, int length, int checkCount)
    {
        GaloisField field = Field(name);
        var random = new Random(length * 31 + checkCount);
        int[] codeword = RandomCodeword(field, random, length, checkCount);

        // Splits of the check symbols between errors (two each) and erasures (one each): every split for small codes, about
        // twenty for large ones, always with all erasures and all errors.
        int step = Math.Max(1, checkCount / 40);
        for (int errors = 0; 2 * errors <= checkCount; errors = errors + step > checkCount / 2 && errors < checkCount / 2 ? checkCount / 2 : errors + step)
        {
            int erasures = checkCount - (2 * errors);
            int[] received = (int[])codeword.Clone();
            int[] positions = [.. Enumerable.Range(0, length).OrderBy(_ => random.Next()).Take(errors + erasures)];
            foreach (int position in positions)
            {
                received[position] = field.Add(received[position], 1 + random.Next(field.Size - 1));
            }

            int corrected = ReedSolomon.Decode(field, received, checkCount, positions.AsSpan(errors));
            Assert.Equal(errors + erasures, corrected);
            Assert.Equal(codeword, received);
        }
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void ErasuresThatAreRightCountAsNoChange(string name, int length, int checkCount)
    {
        GaloisField field = Field(name);
        var random = new Random(length);
        int[] codeword = RandomCodeword(field, random, length, checkCount);
        int[] received = (int[])codeword.Clone();
        received[1] = field.Add(received[1], 1);

        // Position 1 is wrong and erased; positions 0 and 2 are erased but right.
        Assert.Equal(1, ReedSolomon.Decode(field, received, checkCount, [0, 1, 2]));
        Assert.Equal(codeword, received);
        Assert.Equal(0, ReedSolomon.Decode(field, received, checkCount));
    }

    [Theory]
    [MemberData(nameof(Codes))]
    public void PastTheLimitNeverReturnsAWrongWord(string name, int length, int checkCount)
    {
        GaloisField field = Field(name);
        var random = new Random(length + 7);
        int[] codeword = RandomCodeword(field, random, length, checkCount);
        for (int trial = 0; trial < 20; trial++)
        {
            int[] received = (int[])codeword.Clone();
            foreach (int position in Enumerable.Range(0, length).OrderBy(_ => random.Next()).Take((checkCount / 2) + 1))
            {
                received[position] = field.Add(received[position], 1 + random.Next(field.Size - 1));
            }

            int[] before = (int[])received.Clone();
            int corrected = ReedSolomon.Decode(field, received, checkCount);

            // Either a failure that leaves the symbols alone, or a miscorrection to some other codeword.
            if (corrected < 0)
            {
                Assert.Equal(before, received);
            }
            else
            {
                Assert.True(ReedSolomon.IsCodeword(field, received, checkCount));
            }
        }
    }

    [Fact]
    public void QrCheckSymbolsMatchTheStandardsExample()
    {
        // "HELLO WORLD" as version 1-M (ISO/IEC 18004 worked example): 16 data codewords, 10 check codewords.
        int[] data = [32, 91, 11, 120, 209, 114, 220, 77, 67, 64, 236, 17, 236, 17, 236, 17];
        int[] check = new int[10];
        ReedSolomon.Encode(GaloisField.Qr, data, check);
        Assert.Equal([196, 35, 39, 119, 235, 215, 231, 226, 93, 23], check);
    }

    [Fact]
    public void QrGeneratorMatchesTheStandardsTable()
    {
        // The generator for 7 check codewords, as powers of α (ISO/IEC 18004 Annex A).
        int[] generator = GaloisField.Qr.Generator(7);
        Assert.Equal([0, 87, 229, 146, 149, 238, 102, 21], generator.Select(GaloisField.Qr.Log));
    }

    [Fact]
    public void DataMatrixCheckSymbolsMatchTheStandardsExample()
    {
        // "123456" in a 10 x 10 symbol (ISO/IEC 16022 worked example): digit pairs 12, 34, 56 as 142, 164, 186.
        int[] check = new int[5];
        ReedSolomon.Encode(GaloisField.DataMatrix, [142, 164, 186], check);
        Assert.Equal([114, 25, 5, 88, 102], check);
    }

    [Fact]
    public void Pdf417CheckSymbolsAreNegatedRemainders()
    {
        // In GF(929) the stored check symbols make the whole codeword a multiple of the generator.
        GaloisField field = GaloisField.Pdf417;
        int[] data = [5, 453, 178, 121, 239];
        int[] check = new int[8];
        ReedSolomon.Encode(field, data, check);
        Assert.True(ReedSolomon.IsCodeword(field, [.. data, .. check], 8));
        Assert.All(check, c => Assert.InRange(c, 0, 928));
    }

    [Fact]
    public void FieldsAreFields()
    {
        foreach (GaloisField field in new[] { GaloisField.Qr, GaloisField.DataMatrix, GaloisField.Pdf417, GaloisField.Aztec4, GaloisField.Aztec6, GaloisField.Aztec10, GaloisField.Aztec12 })
        {
            // A primitive element runs through every non-zero element once.
            var seen = new HashSet<int>();
            for (int i = 0; i < field.Size - 1; i++)
            {
                Assert.True(seen.Add(field.Exp(i)), $"{field}: α^{i} repeats");
            }

            for (int a = 1; a < field.Size; a += 1 + (field.Size / 64))
            {
                Assert.Equal(1, field.Multiply(a, field.Inverse(a)));
                Assert.Equal(0, field.Add(a, field.Negate(a)));
            }
        }

        Assert.Same(GaloisField.DataMatrix, GaloisField.Aztec8);
    }

    [Fact]
    public void BadArgumentsFail()
    {
        int[] codeword = new int[10];
        Assert.Equal(-1, ReedSolomon.Decode(GaloisField.Qr, codeword, 0));
        Assert.Equal(-1, ReedSolomon.Decode(GaloisField.Qr, codeword, 10));
        Assert.Equal(-1, ReedSolomon.Decode(GaloisField.Aztec4, new int[16], 4));
        codeword[0] = 1;
        Assert.Equal(-1, ReedSolomon.Decode(GaloisField.Qr, codeword, 4, [0, 1, 2, 3, 4]));
        Assert.Equal(-1, ReedSolomon.Decode(GaloisField.Qr, codeword, 4, [10]));
    }

    private static int[] RandomCodeword(GaloisField field, Random random, int length, int checkCount)
    {
        int[] codeword = new int[length];
        for (int i = 0; i < length - checkCount; i++)
        {
            codeword[i] = random.Next(field.Size);
        }

        ReedSolomon.Encode(field, codeword.AsSpan(0, length - checkCount), codeword.AsSpan(length - checkCount));
        Assert.True(ReedSolomon.IsCodeword(field, codeword, checkCount));
        return codeword;
    }

    private static GaloisField Field(string name) => name switch
    {
        "QR" => GaloisField.Qr,
        "Data Matrix" => GaloisField.DataMatrix,
        "PDF417" => GaloisField.Pdf417,
        "Aztec 4-bit" => GaloisField.Aztec4,
        "Aztec 6-bit" => GaloisField.Aztec6,
        "Aztec 10-bit" => GaloisField.Aztec10,
        "Aztec 12-bit" => GaloisField.Aztec12,
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
