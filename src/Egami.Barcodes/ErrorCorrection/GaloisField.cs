using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
    private readonly int[] _productExp;
    private readonly int[] _productLog;
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

        // For products without a branch on zero: log 0 is taken as 2 (size - 1), past every sum of two real logarithms, and
        // exponents from there on are 0.
        _productExp = new int[4 * size];
        _exp.AsSpan(0, 2 * (size - 1)).CopyTo(_productExp);
        _productLog = (int[])_log.Clone();
        _productLog[0] = 2 * (size - 1);

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

    public int Multiply(int a, int b) => _productExp[_productLog[a] + _productLog[b]];

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

    /// <summary>
    /// Evaluates a polynomial, highest power first, at α^(<paramref name="firstPower"/> + j) for each j of
    /// <paramref name="values"/>: each nonzero coefficient c of power p adds c α^((first + j) p), whose exponent grows by p from
    /// one j to the next, so each term costs one table lookup and the terms do not wait on each other.
    /// </summary>
    public void EvaluateAtPowers(ReadOnlySpan<int> coefficients, int firstPower, Span<int> values)
    {
        values.Clear();
        int order = Size - 1, n = coefficients.Length;
        int[] exp = _exp, log = _log;
        for (int i = 0; i < n; i++)
        {
            int c = coefficients[i];
            if (c == 0)
            {
                continue;
            }

            int power = (n - 1 - i) % order;
            int e = (int)((log[c] + ((long)firstPower * power)) % order);
            if (IsPrime)
            {
                for (int j = 0; j < values.Length; j++)
                {
                    int sum = values[j] + exp[e];
                    values[j] = sum >= Size ? sum - Size : sum;
                    e += power;
                    e = e >= order ? e - order : e;
                }
            }
            else
            {
                int j = 0;
                if (Avx2.IsSupported && values.Length >= 8)
                {
                    j = AddTermsGathered(exp, values, e, power, order);
                    e = (int)((e + ((long)j * power)) % order);
                }

                for (; j < values.Length; j++)
                {
                    values[j] ^= exp[e];
                    e += power;
                    e = e >= order ? e - order : e;
                }
            }
        }
    }

    /// <summary>
    /// Adds a coefficient's terms α^e, α^(e + p), α^(e + 2p), ... into eight values at a time, the powers looked up by
    /// gathering; returns how many values it did (a multiple of eight).
    /// </summary>
    private static unsafe int AddTermsGathered(int[] exp, Span<int> values, int e, int power, int order)
    {
        int count = values.Length & ~7;
        Vector256<int> exponents = Vector256.Create(0, 1, 2, 3, 4, 5, 6, 7) * Vector256.Create(power % order);
        exponents += Vector256.Create(e);

        // Lanes may pass the order once or more: bring them back below it (each lane is below 8 times the order).
        Vector256<int> orders = Vector256.Create(order);
        for (int k = 0; k < 8; k++)
        {
            exponents -= orders & Vector256.GreaterThanOrEqual(exponents, orders);
        }

        Vector256<int> step = Vector256.Create((int)(8L * power % order));
        fixed (int* table = exp)
        fixed (int* target = values)
        {
            for (int j = 0; j < count; j += 8)
            {
                Vector256<int> terms = Avx2.GatherVector256(table, exponents, 4);
                Vector256.Store(Vector256.Load(target + j) ^ terms, target + j);
                exponents += step;
                exponents -= orders & Vector256.GreaterThanOrEqual(exponents, orders);
            }
        }

        return count;
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
