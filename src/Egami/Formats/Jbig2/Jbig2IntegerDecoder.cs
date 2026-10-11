namespace Egami.Formats.Jbig2;

/// <summary>
/// One of the arithmetic integer decoding procedures IAAI to IARI (T.88 A.2): a value from a decision tree whose bits are coded
/// in 512 contexts named by the bits decoded so far.
/// </summary>
internal sealed class Jbig2IntegerDecoder
{
    /// <summary>The value <see cref="Decode"/> returns for OOB.</summary>
    internal const long Oob = long.MinValue;

    private readonly byte[] _contexts = new byte[512];

    internal void Reset() => Array.Clear(_contexts);

    /// <summary>Decodes a value, or returns <see cref="Oob"/>.</summary>
    internal long Decode(MqDecoder mq)
    {
        int prev = 1;
        int s = Bit(mq, ref prev);
        int bits;
        long offset;
        if (Bit(mq, ref prev) == 0)
        {
            (bits, offset) = (2, 0);
        }
        else if (Bit(mq, ref prev) == 0)
        {
            (bits, offset) = (4, 4);
        }
        else if (Bit(mq, ref prev) == 0)
        {
            (bits, offset) = (6, 20);
        }
        else if (Bit(mq, ref prev) == 0)
        {
            (bits, offset) = (8, 84);
        }
        else if (Bit(mq, ref prev) == 0)
        {
            (bits, offset) = (12, 340);
        }
        else
        {
            (bits, offset) = (32, 4436);
        }

        long v = 0;
        for (int i = 0; i < bits; i++)
        {
            v = (v << 1) | (uint)Bit(mq, ref prev);
        }

        v += offset;
        if (s == 0)
        {
            return v;
        }

        return v == 0 ? Oob : -v;
    }

    /// <summary>Decodes a value that must be an in-range int, not OOB.</summary>
    internal int DecodeInt(MqDecoder mq)
    {
        long value = Decode(mq);
        return value == Oob || value < int.MinValue || value > int.MaxValue
            ? throw new InvalidDataException("An arithmetic-coded value is out of range.")
            : (int)value;
    }

    private int Bit(MqDecoder mq, ref int prev)
    {
        int d = mq.Decode(ref _contexts[prev]);
        prev = prev < 256 ? (prev << 1) | d : (((prev << 1) | d) & 511) | 256;
        return d;
    }
}

/// <summary>The IAID procedure (T.88 A.3): a fixed-length symbol ID whose contexts are all the bits decoded so far.</summary>
internal sealed class Jbig2SymbolIdDecoder
{
    private readonly byte[] _contexts;
    private readonly int _length;

    internal Jbig2SymbolIdDecoder(int codeLength)
    {
        _length = codeLength;
        _contexts = new byte[1 << (codeLength + 1)];
    }

    internal int Decode(MqDecoder mq)
    {
        int prev = 1;
        for (int i = 0; i < _length; i++)
        {
            prev = (prev << 1) | mq.Decode(ref _contexts[prev]);
        }

        return prev - (1 << _length);
    }
}
