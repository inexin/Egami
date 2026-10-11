using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Egami.Metadata;

/// <summary>
/// Photoshop image resource blocks (the Adobe Photoshop file format specification, "Image Resource Blocks"), as JPEG APP13, TIFF
/// and PSD carry them: a run of "8BIM" resources, each an id, a padded Pascal name and padded data. Egami parses only the IPTC
/// resource (0x0404) and keeps the rest as bytes; the IPTC digest (0x0425) is the MD5 of the IPTC bytes.
/// </summary>
internal static class PhotoshopResources
{
    public const ushort IptcId = 0x0404;
    public const ushort IptcDigestId = 0x0425;

    private static ReadOnlySpan<byte> Signature => "8BIM"u8;

    /// <summary>One resource: where it starts and ends in the block (padding included), its id and where its data is.</summary>
    public readonly record struct Resource(int Start, int End, ushort Id, int DataStart, int DataLength);

    /// <summary>Walks the block's resources. Returns false, with what came before, when a resource is damaged.</summary>
    public static bool TryWalk(ReadOnlySpan<byte> block, List<Resource> resources)
    {
        int at = 0;
        while (at < block.Length)
        {
            // Some writers pad the block's end with zeros.
            if (!block[at..].ContainsAnyExcept((byte)0))
            {
                return true;
            }

            if (block.Length - at < 12 || !block.Slice(at, 4).SequenceEqual(Signature))
            {
                return false;
            }

            ushort id = BinaryPrimitives.ReadUInt16BigEndian(block[(at + 4)..]);
            int name = 6 + ((block[at + 6] + 2) & ~1);
            if (block.Length - at < name + 4)
            {
                return false;
            }

            uint size = BinaryPrimitives.ReadUInt32BigEndian(block[(at + name)..]);
            int data = at + name + 4;
            if (size > block.Length - data)
            {
                return false;
            }

            int end = Math.Min(block.Length, data + (int)((size + 1) & ~1u));
            resources.Add(new Resource(at, end, id, data, (int)size));
            at = end;
        }

        return true;
    }

    /// <summary>Returns the IPTC resource's bytes, or empty.</summary>
    public static ReadOnlyMemory<byte> FindIptc(ReadOnlyMemory<byte> block, IList<string>? warnings)
    {
        List<Resource> resources = [];
        if (!TryWalk(block.Span, resources))
        {
            warnings?.Add("The Photoshop resource block is damaged; it is kept as it was.");
        }

        return resources.Find(r => r.Id == IptcId) is { DataLength: > 0 } iptc ? block.Slice(iptc.DataStart, iptc.DataLength) : default;
    }

    /// <summary>
    /// Returns the block with its IPTC resource replaced by <paramref name="iim"/> (removed when empty, added at the end when the
    /// block had none) and an IPTC digest that matches (added when the block had none). Every other resource keeps its bytes. A damaged block is returned as it
    /// was, since its resources cannot be told apart.
    /// </summary>
    public static byte[] WithIptc(ReadOnlySpan<byte> block, ReadOnlySpan<byte> iim)
    {
        List<Resource> resources = [];
        if (!TryWalk(block, resources))
        {
            return block.ToArray();
        }

        using MemoryStream stream = new();
        bool wroteIptc = false, wroteDigest = false;
        foreach (Resource r in resources)
        {
            if (r.Id == IptcId)
            {
                if (!iim.IsEmpty && !wroteIptc)
                {
                    Write(stream, IptcId, iim);
                    wroteIptc = true;
                }
            }
            else if (r.Id == IptcDigestId)
            {
                if (!iim.IsEmpty && !wroteDigest)
                {
                    Write(stream, IptcDigestId, Digest(iim));
                    wroteDigest = true;
                }
            }
            else
            {
                stream.Write(block[r.Start..r.End]);

                // A last resource whose padding byte was cut off gets it back, so what follows starts where readers look.
                int padded = r.DataStart + ((r.DataLength + 1) & ~1);
                for (int i = r.End; i < padded; i++)
                {
                    stream.WriteByte(0);
                }
            }
        }

        if (!wroteIptc && !iim.IsEmpty)
        {
            Write(stream, IptcId, iim);
        }

        // A digest that matches, so Photoshop and Lightroom trust the new IPTC; added when the block had none.
        if (!wroteDigest && !iim.IsEmpty)
        {
            Write(stream, IptcDigestId, Digest(iim));
        }

        return stream.ToArray();
    }

    /// <summary>A new block holding the IPTC and its digest, as Photoshop writes them.</summary>
    public static byte[] Create(ReadOnlySpan<byte> iim)
    {
        using MemoryStream stream = new();
        Write(stream, IptcId, iim);
        Write(stream, IptcDigestId, Digest(iim));
        return stream.ToArray();
    }

#pragma warning disable CA5351 // Photoshop names MD5 for the IPTC digest; it is not used for security.
    private static byte[] Digest(ReadOnlySpan<byte> iim) => MD5.HashData(iim);
#pragma warning restore CA5351

    private static void Write(MemoryStream stream, ushort id, ReadOnlySpan<byte> data)
    {
        Span<byte> header = stackalloc byte[12];
        Signature.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], id);
        header[6] = 0;   // an empty name, padded to two bytes
        header[7] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(header[8..], (uint)data.Length);
        stream.Write(header);
        stream.Write(data);
        if ((data.Length & 1) != 0)
        {
            stream.WriteByte(0);
        }
    }
}
