using System.Buffers.Binary;

namespace Egami.Video.Audio.Codecs.Alac;

/// <summary>
/// ALACSpecificConfig, the 24 bytes every ALAC stream carries out of band (Apple's ALACMagicCookieDescription.txt): frame
/// length, bit depth, the adaptive Golomb tuning (pb, mb, kb), channels, maximum run, maximum frame size, average bit rate and
/// sample rate, all big-endian.
/// </summary>
internal sealed record AlacSpecificConfig(uint FrameLength, int CompatibleVersion, int BitDepth, int Pb, int Mb, int Kb, int Channels, int MaxRun, uint MaxFrameBytes, uint AvgBitRate, uint SampleRate)
{
    /// <summary>The size of the config.</summary>
    public const int Size = 24;

    /// <summary>
    /// Takes ALACSpecificConfig out of a magic cookie in any of its forms: the bare 24 bytes, or wrapped in QuickTime's frma and
    /// alac atoms (which FFmpeg writes), with a channel layout atom and terminator after it. Returns an empty array if it is too short.
    /// </summary>
    public static byte[] Extract(ReadOnlySpan<byte> cookie)
    {
        if (cookie.Length >= 12 && cookie[4..8].SequenceEqual("frma"u8))
        {
            cookie = cookie[12..];
        }

        if (cookie.Length >= 12 + Size && cookie[4..8].SequenceEqual("alac"u8))
        {
            cookie = cookie[12..];
        }

        return cookie.Length >= Size ? cookie[..Size].ToArray() : [];
    }

    /// <summary>Reads the config from a cookie, or returns <see langword="null"/> if it holds none.</summary>
    public static AlacSpecificConfig? Read(ReadOnlySpan<byte> cookie)
    {
        byte[] c = Extract(cookie);
        if (c.Length != Size)
        {
            return null;
        }

        return new AlacSpecificConfig(
            BinaryPrimitives.ReadUInt32BigEndian(c),
            c[4],
            c[5],
            c[6],
            c[7],
            c[8],
            c[9],
            BinaryPrimitives.ReadUInt16BigEndian(c.AsSpan(10)),
            BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(12)),
            BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(16)),
            BinaryPrimitives.ReadUInt32BigEndian(c.AsSpan(20)));
    }

    /// <summary>Writes the 24 bytes.</summary>
    public byte[] Write()
    {
        byte[] c = new byte[Size];
        BinaryPrimitives.WriteUInt32BigEndian(c, FrameLength);
        c[4] = (byte)CompatibleVersion;
        c[5] = (byte)BitDepth;
        c[6] = (byte)Pb;
        c[7] = (byte)Mb;
        c[8] = (byte)Kb;
        c[9] = (byte)Channels;
        BinaryPrimitives.WriteUInt16BigEndian(c.AsSpan(10), (ushort)MaxRun);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(12), MaxFrameBytes);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(16), AvgBitRate);
        BinaryPrimitives.WriteUInt32BigEndian(c.AsSpan(20), SampleRate);
        return c;
    }
}
