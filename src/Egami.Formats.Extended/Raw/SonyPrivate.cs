using System.Buffers.Binary;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Sony's SR2 private IFD, which holds an ARW's black level, white level and as-shot white balance, encrypted with a 32-bit key
/// and a pad cipher, decrypted as the Sony ARW clean-room specification (section 7) describes.
/// </summary>
internal static class SonyPrivate
{
    public static void Read(RawTiff tiff, RawIfd ifd0, RawFrame frame, bool twelveBit)
    {
        if (!ifd0.TryGet(RawTags.DngPrivateData, out RawEntry entry) || entry.ByteCount < 4)
        {
            return;
        }

        long at = tiff.BigEndian ? BinaryPrimitives.ReadUInt32BigEndian(ifd0.Bytes(entry)) : BinaryPrimitives.ReadUInt32LittleEndian(ifd0.Bytes(entry));
        if (tiff.ReadIfd(at) is not RawIfd sr2 || !sr2.Has(0x7200) || !sr2.Has(0x7201) || !sr2.TryGet(0x7221, out RawEntry keyEntry) || keyEntry.ByteCount < 4)
        {
            return;
        }

        long offset = sr2.Int(0x7200), length = sr2.Int(0x7201) & ~3L;
        uint key = BinaryPrimitives.ReadUInt32LittleEndian(sr2.Bytes(keyEntry));
        if (offset <= 0 || length <= 0 || length > 1 << 20 || !tiff.Source.Ensure(tiff.Origin + offset + length))
        {
            return;
        }

        byte[] plain = tiff.Data.AsSpan((int)(tiff.Origin + offset), (int)length).ToArray();
        Decrypt(plain, key);

        // The decrypted block is an IFD at the same absolute place: parse it from a copy of the file with it put back.
        byte[] copy = (byte[])tiff.Data.Clone();
        plain.CopyTo(copy, (int)(tiff.Origin + offset));
        RawTiff decrypted = new(new RawSource(copy, tiff.Length), tiff.BigEndian, tiff.Origin);
        if (decrypted.ReadIfd(offset) is not RawIfd ifd)
        {
            return;
        }

        int shift = twelveBit ? 2 : 0;
        if (ifd.Doubles(0x7310, 4) is { Length: 4 } black)
        {
            frame.BlackRows = frame.BlackColumns = 2;
            frame.Black = [.. black.Select(v => v / (1 << shift))];
            frame.HasOwnLevels = true;
        }

        if (ifd.Doubles(0x787F, 3) is { Length: > 0 } white && white[0] > 0)
        {
            frame.White = [white[0] / (1 << shift)];
        }

        if (ifd.Doubles(0x7313, 4) is { Length: 4 } rggb && rggb.All(v => v > 0))
        {
            frame.AsShotNeutral = [rggb[1] / rggb[0], 1, rggb[1] / rggb[3]];
        }
        else if (ifd.Doubles(0x7303, 4) is { Length: 4 } grbg && grbg.All(v => v > 0))
        {
            frame.AsShotNeutral = [grbg[0] / grbg[1], 1, grbg[0] / grbg[2]];
        }
    }

    /// <summary>The pad cipher: seeded from the key, then each 4-byte group XORed with the next pad word's big-endian bytes.</summary>
    internal static void Decrypt(Span<byte> data, uint key)
    {
        uint[] pad = new uint[128];
        for (int p = 0; p < 4; p++)
        {
            key = (key * 48828125) + 1;
            pad[p] = key;
        }

        pad[3] = (pad[3] << 1) | ((pad[0] ^ pad[2]) >> 31);
        for (int p = 4; p < 127; p++)
        {
            pad[p] = ((pad[p - 4] ^ pad[p - 2]) << 1) | ((pad[p - 3] ^ pad[p - 1]) >> 31);
        }

        int i = 127;
        for (int at = 0; at + 4 <= data.Length; at += 4, i++)
        {
            pad[i & 127] = pad[(i + 1) & 127] ^ pad[(i + 65) & 127];
            uint word = BinaryPrimitives.ReadUInt32BigEndian(data[at..]) ^ pad[i & 127];
            BinaryPrimitives.WriteUInt32BigEndian(data[at..], word);
        }
    }
}
