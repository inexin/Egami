namespace Egami.Barcodes.Matrix;

/// <summary>A growing sequence of bits, most significant first, for building codewords.</summary>
internal sealed class BitBuffer
{
    private readonly List<bool> _bits = [];

    public int Count => _bits.Count;

    public bool this[int index] => _bits[index];

    /// <summary>Appends the low <paramref name="count"/> bits of a value, highest first.</summary>
    public void Append(int value, int count)
    {
        for (int i = count - 1; i >= 0; i--)
        {
            _bits.Add(((value >> i) & 1) != 0);
        }
    }

    /// <summary>Returns the bits as bytes, the last one padded with zeros.</summary>
    public byte[] ToBytes()
    {
        byte[] bytes = new byte[(_bits.Count + 7) / 8];
        for (int i = 0; i < _bits.Count; i++)
        {
            if (_bits[i])
            {
                bytes[i >> 3] |= (byte)(0x80 >> (i & 7));
            }
        }

        return bytes;
    }
}
