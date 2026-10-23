namespace Egami.Video.Audio.Codecs.Alac;

/// <summary>
/// The encoder's bit buffer (Apple's BitBuffer write side): MSB-first bits into a byte array, each write changing only its own
/// bits, so a frame can be rewound and written again as an escape frame.
/// </summary>
internal sealed class AlacBitWriter(int capacity)
{
    /// <summary>Room past the end for the adaptive Golomb coder's 32-bit read-modify-writes.</summary>
    private const int Padding = 16;

    /// <summary>Gets the buffer.</summary>
    public byte[] Buffer { get; } = new byte[capacity + Padding];

    /// <summary>Gets or sets the bit position.</summary>
    public int Position { get; set; }

    /// <summary>Gets the bytes written up to the position, which must be byte aligned.</summary>
    public ReadOnlySpan<byte> Written => Buffer.AsSpan(0, Position >> 3);

    /// <summary>Clears the buffer and starts again at bit 0.</summary>
    public void Reset()
    {
        Buffer.AsSpan(0, Math.Min(Buffer.Length, (Position >> 3) + 8)).Clear();
        Position = 0;
    }

    /// <summary>Writes the low <paramref name="count"/> bits of <paramref name="value"/>, 1 to 32 (BitBufferWrite).</summary>
    public void Write(uint value, int count)
    {
        if (count <= 0)
        {
            return;
        }

        int index = Position >> 3;
        int free = 8 - (Position & 7);
        Position += count;
        while (count > 0)
        {
            int take = Math.Min(free, count);
            uint chunk = value >> (count - take);
            int shift = free - take;
            int mask = (0xFF >> (8 - take)) << shift;
            Buffer[index] = (byte)((Buffer[index] & ~mask) | (((int)(byte)chunk << shift) & mask));
            count -= take;
            free -= take;
            if (free == 0)
            {
                free = 8;
                index++;
            }
        }
    }

    /// <summary>Pads to the next byte boundary with zeros.</summary>
    public void AlignWithZeros()
    {
        if ((Position & 7) != 0)
        {
            Write(0, 8 - (Position & 7));
        }
    }
}
