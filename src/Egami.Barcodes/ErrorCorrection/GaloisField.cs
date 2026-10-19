namespace Egami.Barcodes.ErrorCorrection;

/// <summary>
/// A finite field for Reed-Solomon codes: GF(2^m) from a primitive polynomial, where adding is XOR, or the prime field GF(929)
/// that PDF417 uses. Multiplying and dividing go through log and antilog tables in both. <see cref="GeneratorBase"/> is the
/// power of the first root of the code's generator polynomial, which each standard fixes.
/// </summary>
internal sealed class GaloisField
{
    private readonly int[] _exp;
    private readonly int[] _log;
    private readonly int[]?[] _generators;

    private GaloisField(string name, int size, int polynomial, int primitive, int generatorBase, bool isPrime)
    {
        Name = name;
        Size = size;
        GeneratorBase = generatorBase;
        IsPrime = isPrime;
        _exp = new int[2 * size];
        _log = new int[size];
        int x = 1;
        for (int i = 0; i < size - 1; i++)
        {
            _exp[i] = x;
            _log[x] = i;
            if (isPrime)
            {
                x = x * primitive % size;
            }
            else
            {
                x <<= 1;
                if (x >= size)
                {
                    x ^= polynomial;
                }
            }
        }

        // Doubled so a product's exponent needs no modulo.
        for (int i = size - 1; i < _exp.Length; i++)
        {
            _exp[i] = _exp[i - (size - 1)];
        }

        _generators = new int[]?[size];
    }

    /// <summary>QR: GF(256) with x^8 + x^4 + x^3 + x^2 + 1, roots from α^0 (ISO/IEC 18004).</summary>
    public static GaloisField Qr { get; } = new("QR", 256, 0x11D, 2, 0, false);

    /// <summary>Data Matrix and Aztec's 8-bit words: GF(256) with x^8 + x^5 + x^3 + x^2 + 1, roots from α^1 (ISO/IEC 16022, 24778).</summary>
    public static GaloisField DataMatrix { get; } = new("Data Matrix", 256, 0x12D, 2, 1, false);

    /// <summary>PDF417: the prime field GF(929) with primitive element 3, roots from 3^1 (ISO/IEC 15438).</summary>
    public static GaloisField Pdf417 { get; } = new("PDF417", 929, 0, 3, 1, true);

    /// <summary>Aztec's mode message: GF(16) with x^4 + x + 1, roots from α^1 (ISO/IEC 24778).</summary>
    public static GaloisField Aztec4 { get; } = new("Aztec 4-bit", 16, 0x13, 2, 1, false);

    /// <summary>Aztec's 6-bit words: GF(64) with x^6 + x + 1, roots from α^1.</summary>
    public static GaloisField Aztec6 { get; } = new("Aztec 6-bit", 64, 0x43, 2, 1, false);

    /// <summary>Aztec's 8-bit words: the same field as <see cref="DataMatrix"/>.</summary>
    public static GaloisField Aztec8 => DataMatrix;

    /// <summary>Aztec's 10-bit words: GF(1024) with x^10 + x^3 + 1, roots from α^1.</summary>
    public static GaloisField Aztec10 { get; } = new("Aztec 10-bit", 1024, 0x409, 2, 1, false);

    /// <summary>Aztec's 12-bit words: GF(4096) with x^12 + x^6 + x^5 + x^3 + 1, roots from α^1.</summary>
    public static GaloisField Aztec12 { get; } = new("Aztec 12-bit", 4096, 0x1069, 2, 1, false);

    /// <summary>Gets the field's name, for messages and tests.</summary>
    public string Name { get; }

    /// <summary>Gets the number of elements; symbols are 0 to Size - 1.</summary>
    public int Size { get; }

    /// <summary>Gets the power of the primitive element that is the generator polynomial's first root.</summary>
    public int GeneratorBase { get; }

    /// <summary>Gets a value indicating whether this is a prime field rather than GF(2^m).</summary>
    public bool IsPrime { get; }

    /// <summary>Returns the primitive element to a power, which may be any non-negative number.</summary>
    public int Exp(int power) => _exp[power % (Size - 1)];

    /// <summary>Returns the discrete logarithm of a non-zero element.</summary>
    public int Log(int a)
    {
        System.Diagnostics.Debug.Assert(a != 0);
        return _log[a];
    }

    public int Add(int a, int b) => IsPrime ? (a + b) % Size : a ^ b;

    public int Subtract(int a, int b) => IsPrime ? (a - b + Size) % Size : a ^ b;

    public int Negate(int a) => IsPrime ? (Size - a) % Size : a;

    public int Multiply(int a, int b) => a == 0 || b == 0 ? 0 : _exp[_log[a] + _log[b]];

    public int Divide(int a, int b)
    {
        System.Diagnostics.Debug.Assert(b != 0);
        return a == 0 ? 0 : _exp[_log[a] + (Size - 1) - _log[b]];
    }

    public int Inverse(int a)
    {
        System.Diagnostics.Debug.Assert(a != 0);
        return _exp[(Size - 1) - _log[a]];
    }

    /// <summary>Returns <paramref name="a"/> added to itself <paramref name="n"/> times, as formal derivatives need.</summary>
    public int MultiplyByInteger(int a, int n) => IsPrime ? (int)((long)a * n % Size) : (n & 1) != 0 ? a : 0;

    /// <summary>
    /// Returns the generator polynomial for a number of check symbols, highest power first with the leading 1:
    /// (x - α^b)(x - α^(b+1))…(x - α^(b+count-1)). Built once per count and kept.
    /// </summary>
    public int[] Generator(int count)
    {
        System.Diagnostics.Debug.Assert(count > 0 && count < Size);
        int[]? generator = Volatile.Read(ref _generators[count]);
        if (generator is not null)
        {
            return generator;
        }

        generator = new int[count + 1];
        generator[0] = 1;
        for (int i = 0; i < count; i++)
        {
            // Multiply by (x - root): each coefficient takes the next one's product with -root.
            int root = Negate(Exp(GeneratorBase + i));
            for (int j = i + 1; j > 0; j--)
            {
                generator[j] = Add(generator[j], Multiply(generator[j - 1], root));
            }
        }

        Interlocked.CompareExchange(ref _generators[count], generator, null);
        return _generators[count]!;
    }

    /// <inheritdoc/>
    public override string ToString() => Name;
}
