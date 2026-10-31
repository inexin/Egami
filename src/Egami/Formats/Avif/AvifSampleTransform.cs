using System.Buffers.Binary;

using Egami.Formats.Heif;

namespace Egami.Formats.Avif;

/// <summary>
/// A sample transform derived image item's expression (sato, AVIF 1.2.0 section 4.2.3): integer operands and operators in postfix
/// notation, evaluated for each sample of the output from the samples at the same channel and place in the input images, every
/// intermediate result clamped to the expression's signed intermediate depth, and the output clamped to the item's pixi depth.
/// Written from the specification; libavif 1.4.2's reader was a cross-check only (AVIF extras design, questions 4 and 5).
/// </summary>
internal sealed class AvifSampleTransform
{
    /// <summary>The constant token.</summary>
    internal const byte Constant = 0;

    /// <summary>The unary operators: negation, absolute value, bitwise not and the index of the highest set bit.</summary>
    internal const byte Negation = 64, Absolute = 65, Not = 66, Bsr = 67;

    /// <summary>The binary operators.</summary>
    internal const byte Sum = 128, Difference = 129, Product = 130, Quotient = 131, And = 132, Or = 133, Xor = 134, Pow = 135, Min = 136, Max = 137;

    private readonly byte[] _tokens;
    private readonly long[] _constants;
    private readonly long _low;
    private readonly long _high;

    private AvifSampleTransform(int bits, byte[] tokens, long[] constants, int inputs, int depth)
    {
        IntermediateBits = bits;
        _tokens = tokens;
        _constants = constants;
        Inputs = inputs;
        Depth = depth;
        _low = bits == 64 ? long.MinValue : -(1L << (bits - 1));
        _high = bits == 64 ? long.MaxValue : (1L << (bits - 1)) - 1;
        int height = 0;
        foreach (byte token in tokens)
        {
            height += token <= 32 ? 1 : token >= Sum ? -1 : 0;
            MaxStack = Math.Max(MaxStack, height);
        }
    }

    /// <summary>Gets the signed intermediate depth: 8, 16, 32 or 64 bits.</summary>
    internal int IntermediateBits { get; }

    /// <summary>Gets the number of input images the item references.</summary>
    internal int Inputs { get; }

    /// <summary>Gets the bits of an output sample, from the item's pixi property.</summary>
    internal int Depth { get; }

    /// <summary>Gets the most values the stack holds at once.</summary>
    internal int MaxStack { get; }

    /// <summary>Gets the tokens in order.</summary>
    internal ReadOnlySpan<byte> Tokens => _tokens;

    /// <summary>
    /// Parses a sato item's data for <paramref name="inputs"/> input images and an output of <paramref name="depth"/> bits. Gives
    /// null for an item readers ignore (another version, a reserved token); throws for an expression that breaks the rules.
    /// </summary>
    internal static AvifSampleTransform? Parse(ReadOnlySpan<byte> data, int inputs, int depth, string formatName)
    {
        if (data.Length < 2)
        {
            throw Corrupt(formatName, "is shorter than 2 bytes");
        }

        if (data[0] >> 6 != 0)
        {
            return null;
        }

        int bits = 8 << (data[0] & 3);
        int count = data[1];
        if (count == 0)
        {
            throw Corrupt(formatName, "has no tokens");
        }

        byte[] tokens = new byte[count];
        long[] constants = new long[count];
        int at = 2, height = 0;
        bool reserved = false;
        for (int t = 0; t < count; t++)
        {
            if (at >= data.Length)
            {
                throw Corrupt(formatName, "is cut off");
            }

            byte token = data[at++];
            tokens[t] = token;
            if (token == Constant)
            {
                int size = bits / 8;
                if (data.Length - at < size)
                {
                    throw Corrupt(formatName, "is cut off");
                }

                constants[t] = size switch
                {
                    1 => (sbyte)data[at],
                    2 => BinaryPrimitives.ReadInt16BigEndian(data[at..]),
                    4 => BinaryPrimitives.ReadInt32BigEndian(data[at..]),
                    _ => BinaryPrimitives.ReadInt64BigEndian(data[at..]),
                };
                at += size;
                height++;
            }
            else if (token <= 32)
            {
                if (token > inputs)
                {
                    throw Corrupt(formatName, $"reads input image {token} of {inputs}");
                }

                height++;
            }
            else if (token is >= Negation and <= Bsr)
            {
                if (height < 1)
                {
                    throw Corrupt(formatName, "applies an operator to an empty stack");
                }
            }
            else if (token is >= Sum and <= Max)
            {
                if (height < 2)
                {
                    throw Corrupt(formatName, "applies a binary operator to fewer than two values");
                }

                height--;
            }
            else
            {
                reserved = true;
            }
        }

        if (at != data.Length)
        {
            throw Corrupt(formatName, "has bytes after its last token");
        }

        if (reserved)
        {
            return null;
        }

        return height != 1 ? throw Corrupt(formatName, $"leaves {height} values on the stack, not one") : new AvifSampleTransform(bits, tokens, constants, inputs, depth);
    }

    /// <summary>Writes a sato item's data: version 0, the intermediate depth, and the tokens with their constants.</summary>
    internal static byte[] Write(int bits, ReadOnlySpan<(byte Token, long Constant)> tokens)
    {
        int size = bits / 8;
        List<byte> data = [(byte)(System.Numerics.BitOperations.Log2((uint)bits) - 3), (byte)tokens.Length];
        Span<byte> value = stackalloc byte[8];
        foreach ((byte token, long constant) in tokens)
        {
            data.Add(token);
            if (token == Constant)
            {
                BinaryPrimitives.WriteInt64BigEndian(value, constant);
                data.AddRange(value[(8 - size)..]);
            }
        }

        return [.. data];
    }

    /// <summary>
    /// Evaluates the expression over a row: <paramref name="inputs"/> holds each input's samples of the row, and
    /// <paramref name="output"/> gets the results clamped to 0 to 2^Depth - 1. <paramref name="stack"/> holds
    /// <see cref="MaxStack"/> rows of scratch at least as long as the row.
    /// </summary>
    internal void Row(ReadOnlySpan<ushort[]> inputs, ReadOnlySpan<int> offsets, Span<ushort> output, long[][] stack)
    {
        int width = output.Length, top = 0;
        for (int t = 0; t < _tokens.Length; t++)
        {
            byte token = _tokens[t];
            if (token == Constant)
            {
                stack[top++].AsSpan(0, width).Fill(_constants[t]);
            }
            else if (token <= 32)
            {
                ReadOnlySpan<ushort> source = inputs[token - 1].AsSpan(offsets[token - 1], width);
                Span<long> target = stack[top++].AsSpan(0, width);
                for (int x = 0; x < width; x++)
                {
                    target[x] = source[x];
                }
            }
            else if (token <= Bsr)
            {
                Span<long> value = stack[top - 1].AsSpan(0, width);
                for (int x = 0; x < width; x++)
                {
                    value[x] = Unary(token, value[x]);
                }
            }
            else
            {
                Span<long> left = stack[top - 2].AsSpan(0, width);
                ReadOnlySpan<long> right = stack[top - 1].AsSpan(0, width);
                for (int x = 0; x < width; x++)
                {
                    left[x] = Binary(token, left[x], right[x]);
                }

                top--;
            }
        }

        long max = (1L << Depth) - 1;
        ReadOnlySpan<long> result = stack[0].AsSpan(0, width);
        for (int x = 0; x < width; x++)
        {
            output[x] = (ushort)Math.Clamp(result[x], 0, max);
        }
    }

    /// <summary>Evaluates the expression for one output sample from each input's sample, clamped to 0 to 2^Depth - 1.</summary>
    internal long Evaluate(ReadOnlySpan<long> samples) => Math.Clamp(EvaluateUnclamped(samples), 0, (1L << Depth) - 1);

    /// <summary>Evaluates the expression for one sample from each input's sample, before the output is clamped to its depth.</summary>
    internal long EvaluateUnclamped(ReadOnlySpan<long> samples)
    {
        Span<long> stack = stackalloc long[Math.Max(1, MaxStack)];
        int top = 0;
        for (int t = 0; t < _tokens.Length; t++)
        {
            byte token = _tokens[t];
            if (token == Constant)
            {
                stack[top++] = _constants[t];
            }
            else if (token <= 32)
            {
                stack[top++] = samples[token - 1];
            }
            else if (token <= Bsr)
            {
                stack[top - 1] = Unary(token, stack[top - 1]);
            }
            else
            {
                stack[top - 2] = Binary(token, stack[top - 2], stack[top - 1]);
                top--;
            }
        }

        return stack[0];
    }

    private static CorruptDataException Corrupt(string formatName, string problem) => new(formatName, -1, $"The sample transform (sato) item {problem}.");

    /// <summary>Clamps an exact result to the intermediate depth.</summary>
    private long Clamp(Int128 value) => value < _low ? _low : value > _high ? _high : (long)value;

    private long Unary(byte token, long l) => token switch
    {
        Negation => Clamp(-(Int128)l),
        Absolute => Clamp(Int128.Abs(l)),
        Not => ~l,
        _ => l <= 0 ? 0 : System.Numerics.BitOperations.Log2((ulong)l),
    };

    private long Binary(byte token, long l, long r) => token switch
    {
        Sum => Clamp((Int128)l + r),
        Difference => Clamp((Int128)l - r),
        Product => Clamp((Int128)l * r),
        Quotient => r == 0 ? l : Clamp((Int128)l / r),
        And => l & r,
        Or => l | r,
        Xor => l ^ r,
        Pow => Power(l, r),
        Min => Math.Min(l, r),
        _ => Math.Max(l, r),
    };

    /// <summary>Raises L to the power R, truncated toward zero; 0 when L is 0, and clamped to the intermediate depth.</summary>
    private long Power(long l, long r)
    {
        if (l is 0 or 1)
        {
            return l;
        }

        if (l == -1)
        {
            return (r & 1) == 0 ? 1 : -1;
        }

        if (r <= 0)
        {
            // |L| > 1: L^0 is 1, and a negative power lies strictly between -1 and 1.
            return r == 0 ? 1 : 0;
        }

        Int128 result = l;
        for (long i = 1; i < r; i++)
        {
            result *= l;
            if (result < _low || result > _high)
            {
                return l > 0 || (r & 1) == 0 ? _high : _low;
            }
        }

        return (long)result;
    }
}
