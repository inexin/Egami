using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Egami.Formats.Gif;

/// <summary>How an LZW decode ended.</summary>
internal enum LzwStatus
{
    /// <summary>The end-of-information code arrived.</summary>
    Complete,

    /// <summary>The output filled up before the end-of-information code; the usual ending for well-formed files.</summary>
    OutputFull,

    /// <summary>The data ran out first.</summary>
    DataEnded,

    /// <summary>A code that cannot exist yet arrived: the data is corrupt.</summary>
    BadCode,
}

/// <summary>
/// GIF's variable-length LZW (GIF89a appendix F, after Welch 1984). Each table entry keeps the code before it, its last byte, its
/// first byte and its length, so a string is written backwards straight into its place in the output with no stack. Codes are
/// read least significant bit first from a 64-bit buffer. The table stops growing at 4,096 codes until a clear code arrives.
/// </summary>
internal sealed class LzwDecoder : IDisposable
{
    private const int MaxCodes = 4096;

    private readonly ushort[] _prefix = ArrayPool<ushort>.Shared.Rent(MaxCodes);
    private readonly ushort[] _length = ArrayPool<ushort>.Shared.Rent(MaxCodes);
    private readonly byte[] _suffix = ArrayPool<byte>.Shared.Rent(MaxCodes);
    private readonly byte[] _first = ArrayPool<byte>.Shared.Rent(MaxCodes);

    /// <summary>
    /// Decodes <paramref name="data"/> (the image data with its sub-block lengths removed) into <paramref name="output"/>, stopping
    /// when it is full, at the end code, when the data ends, or at a bad code.
    /// </summary>
    /// <param name="minCodeSize">The minimum code size from the file, 1 to 11.</param>
    /// <param name="data">The LZW data.</param>
    /// <param name="output">Where the indices go.</param>
    /// <param name="written">How many indices were written.</param>
    /// <returns>Why decoding stopped.</returns>
    public LzwStatus Decode(int minCodeSize, ReadOnlySpan<byte> data, Span<byte> output, out int written)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minCodeSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minCodeSize, 11);
        int clear = 1 << minCodeSize;
        int end = clear + 1;
        ref ushort prefix = ref MemoryMarshal.GetArrayDataReference(_prefix);
        ref ushort length = ref MemoryMarshal.GetArrayDataReference(_length);
        ref byte suffix = ref MemoryMarshal.GetArrayDataReference(_suffix);
        ref byte first = ref MemoryMarshal.GetArrayDataReference(_first);
        for (int i = 0; i < clear; i++)
        {
            Unsafe.Add(ref suffix, i) = (byte)i;
            Unsafe.Add(ref first, i) = (byte)i;
            Unsafe.Add(ref length, i) = 1;
        }

        int next = clear + 2;
        int codeSize = minCodeSize + 1;
        int mask = (1 << codeSize) - 1;
        int previous = -1;
        int at = 0;
        int position = 0;
        ulong bits = 0;
        int count = 0;
        while (true)
        {
            if (count < codeSize)
            {
                if (position + 8 <= data.Length)
                {
                    // Bytes past the ones counted stay in the buffer; the next refill ORs the same bytes in at the same place.
                    bits |= BinaryPrimitives.ReadUInt64LittleEndian(data[position..]) << count;
                    int bytes = (63 - count) >> 3;
                    position += bytes;
                    count += bytes * 8;
                }
                else
                {
                    while (count <= 56 && position < data.Length)
                    {
                        bits |= (ulong)data[position++] << count;
                        count += 8;
                    }

                    if (count < codeSize)
                    {
                        written = at;
                        return LzwStatus.DataEnded;
                    }
                }
            }

            int code = (int)bits & mask;
            bits >>= codeSize;
            count -= codeSize;

            if (code == clear)
            {
                next = clear + 2;
                codeSize = minCodeSize + 1;
                mask = (1 << codeSize) - 1;
                previous = -1;
                continue;
            }

            if (code == end)
            {
                written = at;
                return LzwStatus.Complete;
            }

            int stringCode;
            if (previous < 0)
            {
                if (code >= clear)
                {
                    written = at;
                    return LzwStatus.BadCode;
                }

                stringCode = code;
            }
            else
            {
                if (code > next || (code == next && next == MaxCodes))
                {
                    written = at;
                    return LzwStatus.BadCode;
                }

                if (next < MaxCodes)
                {
                    // The new entry is the previous string plus the first byte of this one; when this code is the entry being made
                    // (the KwKwK case), that first byte is the previous string's own first byte.
                    byte firstByte = code == next ? Unsafe.Add(ref first, previous) : Unsafe.Add(ref first, code);
                    Unsafe.Add(ref prefix, next) = (ushort)previous;
                    Unsafe.Add(ref suffix, next) = firstByte;
                    Unsafe.Add(ref first, next) = Unsafe.Add(ref first, previous);
                    Unsafe.Add(ref length, next) = (ushort)(Unsafe.Add(ref length, previous) + 1);
                    next++;
                }

                stringCode = code;
            }

            // Codes widen once the next entry needs another bit. Checked after every code, not only after an addition, so a
            // minimum code size of 1 (whose first entry already needs 3 bits) works too.
            if (next >= 1 << codeSize && codeSize < 12)
            {
                codeSize++;
                mask = (1 << codeSize) - 1;
            }

            previous = code;
            int stringLength = Unsafe.Add(ref length, stringCode);
            if (at + stringLength <= output.Length)
            {
                WriteString(ref prefix, ref suffix, stringCode, stringLength, ref MemoryMarshal.GetReference(output.Slice(at, stringLength)));
                at += stringLength;
                if (at == output.Length)
                {
                    written = at;
                    return LzwStatus.OutputFull;
                }
            }
            else
            {
                // The string runs past the end: write it whole elsewhere and keep what fits.
                Span<byte> whole = stackalloc byte[MaxCodes];
                WriteString(ref prefix, ref suffix, stringCode, stringLength, ref MemoryMarshal.GetReference(whole));
                whole[..(output.Length - at)].CopyTo(output[at..]);
                written = output.Length;
                return LzwStatus.OutputFull;
            }
        }
    }

    public void Dispose()
    {
        ArrayPool<ushort>.Shared.Return(_prefix);
        ArrayPool<ushort>.Shared.Return(_length);
        ArrayPool<byte>.Shared.Return(_suffix);
        ArrayPool<byte>.Shared.Return(_first);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteString(ref ushort prefix, ref byte suffix, int code, int length, ref byte target)
    {
        for (int i = length - 1; i > 0; i--)
        {
            Unsafe.Add(ref target, i) = Unsafe.Add(ref suffix, code);
            code = Unsafe.Add(ref prefix, code);
        }

        target = Unsafe.Add(ref suffix, code);
    }
}

/// <summary>
/// GIF's LZW encoder. Strings are found with an open-addressing hash of (prefix code, next byte); when the 4,096 codes are used up
/// it sends a clear code and starts again. Codes go least significant bit first through a 64-bit buffer into 255-byte sub-blocks.
/// </summary>
internal sealed class LzwEncoder : IDisposable
{
    private const int MaxCodes = 4096;
    private const int HashSize = 8192;

    // Each slot holds (prefix << 8 | byte) + 1 in the high bits and the code in the low 12; 0 is empty.
    private readonly int[] _keys = ArrayPool<int>.Shared.Rent(HashSize);
    private readonly ushort[] _codes = ArrayPool<ushort>.Shared.Rent(HashSize);

    /// <summary>Encodes <paramref name="indices"/> as one image's data: minimum code size byte, sub-blocks, terminator.</summary>
    /// <param name="minCodeSize">The minimum code size, 2 to 8 for GIF.</param>
    /// <param name="indices">The palette indices, row by row; each below 2^<paramref name="minCodeSize"/>.</param>
    /// <param name="output">Where the bytes go.</param>
    public void Encode(int minCodeSize, ReadOnlySpan<byte> indices, IBufferWriter<byte> output)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minCodeSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minCodeSize, 11);
        Span<byte> head = output.GetSpan(1);
        head[0] = (byte)minCodeSize;
        output.Advance(1);

        SubBlockWriter writer = new(output);
        int clear = 1 << minCodeSize;
        int end = clear + 1;
        int[] keys = _keys;
        ushort[] codes = _codes;
        Array.Clear(keys, 0, HashSize);
        int next = clear + 2;
        int codeSize = minCodeSize + 1;
        writer.Put(clear, codeSize);

        if (indices.IsEmpty)
        {
            writer.Put(end, codeSize);
            writer.Finish();
            return;
        }

        int prefix = indices[0];
        for (int i = 1; i < indices.Length; i++)
        {
            int value = indices[i];
            int key = ((prefix << 8) | value) + 1;
            int slot = Hash(prefix, value);
            while (true)
            {
                int stored = keys[slot];
                if (stored == key)
                {
                    prefix = codes[slot];
                    goto Found;
                }

                if (stored == 0)
                {
                    break;
                }

                slot = (slot + 1) & (HashSize - 1);
            }

            writer.Put(prefix, codeSize);
            if (next < MaxCodes)
            {
                keys[slot] = key;
                codes[slot] = (ushort)next;
                next++;
                if (next > 1 << codeSize && codeSize < 12)
                {
                    codeSize++;
                }
            }
            else
            {
                // The table is full: the decoder has one entry fewer, so it reads this clear code at 12 bits as well.
                writer.Put(clear, codeSize);
                Array.Clear(keys, 0, HashSize);
                next = clear + 2;
                codeSize = minCodeSize + 1;
            }

            prefix = value;
        Found:;
        }

        writer.Put(prefix, codeSize);

        // Having read that last code, the decoder's table is as large as this one (it adds its entry one code later), and it
        // widens its codes by the same rule before reading the end code.
        if (next >= 1 << codeSize && codeSize < 12)
        {
            codeSize++;
        }

        writer.Put(end, codeSize);
        writer.Finish();
    }

    public void Dispose()
    {
        ArrayPool<int>.Shared.Return(_keys);
        ArrayPool<ushort>.Shared.Return(_codes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(int prefix, int value)
    {
        return (int)(((uint)((prefix << 8) | value) * 2654435761u) >> 19);
    }

    /// <summary>Packs codes into 255-byte sub-blocks, each with its length in front, and the 0 terminator at the end.</summary>
    private ref struct SubBlockWriter(IBufferWriter<byte> output)
    {
        private Span<byte> _block;
        private int _length;
        private ulong _bits;
        private int _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Put(int code, int size)
        {
            _bits |= (ulong)code << _count;
            _count += size;
            if (_count >= 32)
            {
                Drain();
            }
        }

        public void Finish()
        {
            while (_count > 0)
            {
                Byte((byte)_bits);
                _bits >>= 8;
                _count -= 8;
            }

            CloseBlock();
            Span<byte> terminator = output.GetSpan(1);
            terminator[0] = 0;
            output.Advance(1);
        }

        private void Drain()
        {
            while (_count >= 8)
            {
                Byte((byte)_bits);
                _bits >>= 8;
                _count -= 8;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Byte(byte value)
        {
            if (_length == 0)
            {
                _block = output.GetSpan(256);
            }

            _block[1 + _length++] = value;
            if (_length == 255)
            {
                CloseBlock();
            }
        }

        private void CloseBlock()
        {
            if (_length > 0)
            {
                _block[0] = (byte)_length;
                output.Advance(_length + 1);
                _length = 0;
            }
        }
    }
}

/// <summary>Reads GIF data sub-blocks (a length byte, then that many bytes, until a length of 0).</summary>
internal static class GifSubBlocks
{
    /// <summary>
    /// Reads sub-blocks up to the terminator into <paramref name="buffer"/> (pooled, grown as needed) without their lengths.
    /// </summary>
    /// <returns>The number of data bytes, and whether the terminator was reached before the data ended.</returns>
    internal static (int Length, bool Complete) ReadAll(EgamiInput input, ref byte[] buffer)
    {
        int length = 0;
        while (true)
        {
            int size;
            try
            {
                size = input.ReadByte();
            }
            catch (CorruptDataException)
            {
                return (length, false);
            }

            if (size == 0)
            {
                return (length, true);
            }

            if (length + size > buffer.Length)
            {
                byte[] larger = ArrayPool<byte>.Shared.Rent(Math.Max(buffer.Length * 2, length + size));
                buffer.AsSpan(0, length).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(buffer);
                buffer = larger;
            }

            int read = input.Read(buffer.AsSpan(length, size));
            length += read;
            if (read < size)
            {
                return (length, false);
            }
        }
    }

    /// <summary>Skips sub-blocks up to the terminator.</summary>
    /// <returns>Whether the terminator was reached before the data ended.</returns>
    internal static bool Skip(EgamiInput input)
    {
        try
        {
            while (true)
            {
                int size = input.ReadByte();
                if (size == 0)
                {
                    return true;
                }

                input.Skip(size);
            }
        }
        catch (CorruptDataException)
        {
            return false;
        }
    }
}
