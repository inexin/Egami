using System.Security.Cryptography;

using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>
/// The decoded picture hash SEI message (H.265 D.2.20, D.3.19): an MD5, CRC or checksum of each colour component of a decoded
/// picture, which a decoder can check its output against.
/// </summary>
internal sealed class HevcPictureHash
{
    private HevcPictureHash(int type, byte[][] values)
    {
        Type = type;
        Values = values;
    }

    /// <summary>Gets hash_type: 0 MD5, 1 CRC, 2 checksum.</summary>
    internal int Type { get; }

    /// <summary>Gets each component's hash, big-endian (16 bytes for MD5, 2 for CRC, 4 for checksum).</summary>
    internal byte[][] Values { get; }

    /// <summary>Reads the SEI messages of an SEI NAL unit and returns the decoded picture hash, or null when it has none.</summary>
    internal static HevcPictureHash? Read(NalBitReader r, int components)
    {
        HashReader reader = new(components);
        NalSei.Read(r, reader, static (bits, type, size, state) => state.Payload(bits, type, size));
        return reader.Hash;
    }

    /// <summary>
    /// Computes this hash type over a decoded component (D.3.19): the samples row by row, one byte each up to 8 bits and two
    /// bytes (little-endian) above.
    /// </summary>
    internal byte[] Compute(ushort[] plane, int stride, int width, int height, int bitDepth)
    {
        int bytes = bitDepth > 8 ? 2 : 1;
        switch (Type)
        {
            case 0:
                {
                    using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
                    byte[] row = new byte[width * bytes];
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            ushort v = plane[(y * stride) + x];
                            row[x * bytes] = (byte)v;
                            if (bytes == 2)
                            {
                                row[(x * 2) + 1] = (byte)(v >> 8);
                            }
                        }

                        md5.AppendData(row);
                    }

                    return md5.GetHashAndReset();
                }

            case 1:
                {
                    // CRC-CCITT, the polynomial 0x1021, bit by bit over the bytes of each sample, then 16 zero bits (D-27).
                    uint crc = 0xFFFF;
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            ushort v = plane[(y * stride) + x];
                            for (int k = 0; k < bytes; k++)
                            {
                                int data = (v >> (8 * k)) & 0xFF;
                                for (int bit = 0; bit < 8; bit++)
                                {
                                    uint msb = (crc >> 15) & 1;
                                    int b = (data >> (7 - bit)) & 1;
                                    crc = (((crc << 1) + (uint)b) & 0xFFFF) ^ (msb * 0x1021);
                                }
                            }
                        }
                    }

                    for (int bit = 0; bit < 16; bit++)
                    {
                        uint msb = (crc >> 15) & 1;
                        crc = ((crc << 1) & 0xFFFF) ^ (msb * 0x1021);
                    }

                    return [(byte)(crc >> 8), (byte)crc];
                }

            default:
                {
                    uint sum = 0;
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            uint mask = (uint)((x & 0xFF) ^ (y & 0xFF) ^ (x >> 8) ^ (y >> 8));
                            ushort v = plane[(y * stride) + x];
                            sum += ((v & 0xFFu) ^ mask) & 0xFF;
                            if (bytes == 2)
                            {
                                sum += ((uint)(v >> 8) ^ mask) & 0xFF;
                            }
                        }
                    }

                    return [(byte)(sum >> 24), (byte)(sum >> 16), (byte)(sum >> 8), (byte)sum];
                }
        }
    }

    /// <summary>Keeps the first decoded picture hash message of an SEI NAL unit.</summary>
    private sealed class HashReader(int components)
    {
        internal HevcPictureHash? Hash { get; private set; }

        internal void Payload(NalBitReader r, int type, int size)
        {
            if (type != 132 || Hash is not null)
            {
                return;
            }

            int hashType = (int)r.U(8, "hash_type");
            (int length, string name) = hashType switch
            {
                0 => (16, "picture_md5"),
                1 => (2, "picture_crc"),
                2 => (4, "picture_checksum"),
                _ => (0, ""),
            };
            if (length > 0 && size >= 1 + (components * length))
            {
                byte[][] values = new byte[components][];
                for (int c = 0; c < components; c++)
                {
                    values[c] = new byte[length];
                    for (int i = 0; i < length; i++)
                    {
                        values[c][i] = (byte)r.U(8, name);
                    }
                }

                Hash = new HevcPictureHash(hashType, values);
            }
        }
    }
}
