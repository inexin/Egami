namespace Egami.Barcodes.ErrorCorrection;

/// <summary>
/// Systematic Reed-Solomon codes over a <see cref="GaloisField"/>. A codeword is the data followed by the check symbols, first
/// symbol at the highest power, and as a polynomial it is a multiple of the field's generator polynomial. In a prime field that
/// makes the check symbols the negated remainder, as PDF417 stores them. Decoding corrects errors and erasures (symbols known to
/// be unreadable) together, as long as twice the errors plus the erasures fit in the check symbols.
/// </summary>
internal static class ReedSolomon
{
    /// <summary>Computes the check symbols for the data.</summary>
    /// <param name="field">The field.</param>
    /// <param name="data">The data symbols.</param>
    /// <param name="check">Receives the check symbols; its length is their number.</param>
    public static void Encode(GaloisField field, ReadOnlySpan<int> data, Span<int> check)
    {
        int count = check.Length;
        int[] generator = field.Generator(count);
        check.Clear();

        // Long division of data · x^count by the generator; check holds the running remainder, highest power first.
        foreach (int symbol in data)
        {
            int factor = field.Add(symbol, check[0]);
            check[1..].CopyTo(check);
            check[^1] = 0;
            if (factor != 0)
            {
                for (int i = 0; i < count; i++)
                {
                    check[i] = field.Subtract(check[i], field.Multiply(factor, generator[i + 1]));
                }
            }
        }

        // data · x^count - remainder is the multiple of the generator.
        for (int i = 0; i < count; i++)
        {
            check[i] = field.Negate(check[i]);
        }
    }

    /// <summary>Corrects a codeword in place.</summary>
    /// <param name="field">The field.</param>
    /// <param name="codeword">The data followed by the check symbols.</param>
    /// <param name="checkCount">The number of check symbols at the end.</param>
    /// <param name="erasures">The indexes of symbols known to be wrong, or empty.</param>
    /// <returns>The number of symbols changed (errors and erasures that were wrong), or -1 if the codeword cannot be corrected; it is then left as it was.</returns>
    public static int Decode(GaloisField field, Span<int> codeword, int checkCount, ReadOnlySpan<int> erasures = default)
    {
        int n = codeword.Length;
        if (checkCount <= 0 || checkCount >= n || n >= field.Size || erasures.Length > checkCount)
        {
            return -1;
        }

        // Syndromes S_j = r(α^(b+j)).
        int[] syndromes = new int[checkCount];
        field.EvaluateAtPowers(codeword, field.GeneratorBase, syndromes);
        bool clean = !syndromes.AsSpan().ContainsAnyExcept(0);

        if (clean)
        {
            return 0;
        }

        // Erasure locator Γ(x) = Π (1 - X_k x), lowest power first, where X_k = α^(power of the erased symbol).
        int[] erasureLocator = [1];
        foreach (int index in erasures)
        {
            if ((uint)index >= (uint)n)
            {
                return -1;
            }

            erasureLocator = Multiply(field, erasureLocator, [1, field.Negate(field.Exp(n - 1 - index))]);
        }

        // Forney syndromes T = Γ S mod x^checkCount; from index e on they are power sums over the errors alone (without
        // erasures, Γ = 1 and T = S).
        int e = erasures.Length;
        int[] forney = e == 0 ? syndromes : Multiply(field, erasureLocator, syndromes);
        int[] errorLocator = BerlekampMassey(field, forney.AsSpan(e, checkCount - e));
        int errors = errorLocator.Length - 1;
        if ((2 * errors) + e > checkCount)
        {
            return -1;
        }

        // Λ = σ Γ locates errors and erasures.
        int[] locator = e == 0 ? errorLocator : Multiply(field, errorLocator, erasureLocator);

        // Chien search over the codeword's positions: Λ at X^-1 = α^-(n - 1 - i), which grows by a factor α from one position
        // to the next, so each term λ_k X^-k is kept and multiplied by α^k (the terms are independent of each other).
        int degree = locator.Length - 1;
        Span<int> positions = degree <= 256 ? stackalloc int[degree] : new int[degree];
        Span<int> terms = degree <= 256 ? stackalloc int[degree + 1] : new int[degree + 1], steps = degree <= 256 ? stackalloc int[degree + 1] : new int[degree + 1];
        int first = field.Exp((field.Size - 1) - ((n - 1) % (field.Size - 1)));
        for (int k = 0, power = 1; k <= degree; k++, power = field.Multiply(power, first))
        {
            terms[k] = field.Multiply(locator[k], power);
            steps[k] = field.Exp(k);
        }

        int found = 0;
        for (int i = 0; i < n && found <= degree; i++)
        {
            int value = 0;
            for (int k = 0; k <= degree; k++)
            {
                value = field.Add(value, terms[k]);
                terms[k] = field.Multiply(terms[k], steps[k]);
            }

            if (value == 0)
            {
                if (found == degree)
                {
                    return -1;
                }

                positions[found++] = i;
            }
        }

        if (found != degree)
        {
            return -1;
        }

        // Ω = S Λ mod x^checkCount gives the values. Forney: Y_k = -X_k^(1-b) Ω(X_k^-1) / Λ'(X_k^-1).
        int[] evaluator = Multiply(field, syndromes, locator);
        Array.Resize(ref evaluator, Math.Min(evaluator.Length, checkCount));
        int[] derivative = new int[Math.Max(degree, 1)];
        for (int i = 1; i <= degree; i++)
        {
            derivative[i - 1] = field.MultiplyByInteger(locator[i], i);
        }

        Span<int> values = degree <= 256 ? stackalloc int[degree] : new int[degree];
        for (int k = 0; k < degree; k++)
        {
            int power = n - 1 - positions[k];
            int inverse = field.Exp((field.Size - 1) - (power % (field.Size - 1)));
            int denominator = Evaluate(field, derivative, inverse);
            if (denominator == 0)
            {
                return -1;
            }

            int scale = field.Exp(((1 - field.GeneratorBase) * power % (field.Size - 1)) + (field.Size - 1));
            values[k] = field.Negate(field.Multiply(scale, field.Divide(Evaluate(field, evaluator, inverse), denominator)));
        }

        int changed = 0;
        for (int k = 0; k < degree; k++)
        {
            if (values[k] != 0)
            {
                codeword[positions[k]] = field.Subtract(codeword[positions[k]], values[k]);
                changed++;
            }
        }

        // A pattern past the correction limit can land on another codeword's locator; check the result is a codeword: its
        // syndromes are the received word's less each correction's share, Y_k X_k^(b+j).
        bool codewordNow = true;
        for (int j = 0; j < checkCount && codewordNow; j++)
        {
            int syndrome = syndromes[j];
            for (int k = 0; k < degree; k++)
            {
                int power = (n - 1 - positions[k]) % (field.Size - 1);
                int exponent = (int)((long)(field.GeneratorBase + j) * power % (field.Size - 1));
                syndrome = field.Subtract(syndrome, field.Multiply(values[k], field.Exp(exponent)));
            }

            codewordNow = syndrome == 0;
        }

        if (!codewordNow)
        {
            for (int k = 0; k < degree; k++)
            {
                codeword[positions[k]] = field.Add(codeword[positions[k]], values[k]);
            }

            return -1;
        }

        return changed;
    }

    /// <summary>Returns whether the symbols form a codeword: every syndrome is zero.</summary>
    public static bool IsCodeword(GaloisField field, ReadOnlySpan<int> codeword, int checkCount)
    {
        Span<int> syndromes = checkCount <= 256 ? stackalloc int[checkCount] : new int[checkCount];
        field.EvaluateAtPowers(codeword, field.GeneratorBase, syndromes);
        return !syndromes.ContainsAnyExcept(0);
    }

    /// <summary>The shortest linear recurrence of a power-sum sequence: its connection polynomial, lowest power first.</summary>
    private static int[] BerlekampMassey(GaloisField field, ReadOnlySpan<int> sequence)
    {
        int[] connection = new int[sequence.Length + 1], previous = new int[sequence.Length + 1], scratch = new int[sequence.Length + 1];
        connection[0] = previous[0] = 1;
        int length = 0, shift = 1, previousDiscrepancy = 1;
        int[] exp = field.ProductExp, log = field.ProductLog;
        for (int r = 0; r < sequence.Length; r++)
        {
            int discrepancy = sequence[r];
            if (field.IsPrime)
            {
                for (int i = 1; i <= length; i++)
                {
                    discrepancy = field.Add(discrepancy, field.Multiply(connection[i], sequence[r - i]));
                }
            }
            else
            {
                // Binary fields: addition is exclusive or, products from the tables directly.
                for (int i = 1; i <= length; i++)
                {
                    discrepancy ^= exp[log[connection[i]] + log[sequence[r - i]]];
                }
            }

            if (discrepancy == 0)
            {
                shift++;
                continue;
            }

            int factor = field.Divide(discrepancy, previousDiscrepancy);
            if (2 * length <= r)
            {
                connection.CopyTo(scratch, 0);
                Subtract(field, connection, previous, factor, shift);
                (previous, scratch) = (scratch, previous);
                length = r + 1 - length;
                previousDiscrepancy = discrepancy;
                shift = 1;
            }
            else
            {
                Subtract(field, connection, previous, factor, shift);
                shift++;
            }
        }

        return connection.AsSpan(0, length + 1).ToArray();
    }

    /// <summary>target -= factor · x^shift · source.</summary>
    private static void Subtract(GaloisField field, int[] target, int[] source, int factor, int shift)
    {
        if (!field.IsPrime)
        {
            int[] exp = field.ProductExp, log = field.ProductLog;
            int logFactor = log[factor];
            for (int i = 0; i + shift < target.Length; i++)
            {
                target[i + shift] ^= exp[logFactor + log[source[i]]];
            }

            return;
        }

        for (int i = 0; i + shift < target.Length; i++)
        {
            target[i + shift] = field.Subtract(target[i + shift], field.Multiply(factor, source[i]));
        }
    }

    /// <summary>Multiplies two polynomials, lowest power first.</summary>
    private static int[] Multiply(GaloisField field, ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    {
        int[] product = new int[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == 0)
            {
                continue;
            }

            for (int j = 0; j < b.Length; j++)
            {
                product[i + j] = field.Add(product[i + j], field.Multiply(a[i], b[j]));
            }
        }

        return product;
    }

    /// <summary>Evaluates a polynomial, lowest power first, by Horner's rule.</summary>
    private static int Evaluate(GaloisField field, ReadOnlySpan<int> polynomial, int x)
    {
        int value = 0;
        for (int i = polynomial.Length - 1; i >= 0; i--)
        {
            value = field.Add(field.Multiply(value, x), polynomial[i]);
        }

        return value;
    }
}
