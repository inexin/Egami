using System.Buffers.Binary;
using System.Text;

using Egami.Formats;
using Egami.Formats.Ico;
using Egami.Pixels;

namespace Egami.Tests.Formats.Ani;

/// <summary>
/// Builds animated cursors byte by byte for the tests, so every chunk, count and flag can be set, or broken, on purpose. Frames are
/// cursors Egami's own CUR writer makes, or raw bytes.
/// </summary>
internal sealed class AniBuilder
{
    public List<byte[]> Frames { get; } = [];

    public int[]? Rates { get; set; }

    public int[]? Sequence { get; set; }

    public int DefaultRate { get; set; } = 10;

    /// <summary>Gets or sets the header flags; null sets the icon flag, and the sequence flag when there is a sequence.</summary>
    public uint? Flags { get; set; }

    public string? Title { get; set; }

    public string? Artist { get; set; }

    /// <summary>Gets or sets how many bytes the anih chunk has; extra bytes are zeros.</summary>
    public int HeaderChunkSize { get; set; } = 36;

    public int? DeclaredFrames { get; set; }

    public int? DeclaredSteps { get; set; }

    public (int Width, int Height, int Bits) RawHeader { get; set; }

    /// <summary>Gets or sets whether odd-sized chunks get the pad byte RIFF asks for.</summary>
    public bool Pad { get; set; } = true;

    /// <summary>Gets or sets whether the INFO list comes first, as most writers put it.</summary>
    public bool InfoFirst { get; set; } = true;

    /// <summary>Makes a cursor of one or more square sizes, each drawn by <see cref="Pattern"/> with <paramref name="seed"/>; the hotspot is at the largest size.</summary>
    public static byte[] Cursor(int seed, Point hotspot, params int[] sizes)
    {
        List<Raster> rasters = [.. sizes.Select(s => (Raster)Pattern(s, s, seed))];
        try
        {
            using MemoryStream stream = new();
            Raster.SaveAll(rasters, stream, CurFormat.Info, new IcoWriterOptions { Hotspot = hotspot });
            return stream.ToArray();
        }
        finally
        {
            foreach (Raster raster in rasters)
            {
                raster.Dispose();
            }
        }
    }

    /// <summary>Draws a frame that differs for every seed and size, with transparent and half-transparent pixels.</summary>
    public static Raster<Rgba8> Pattern(int width, int height, int seed)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte a = (byte)((x + y + seed) % 3 == 0 ? 0 : 255);
                row[x] = new Rgba8((byte)(x * 7 + seed * 40), (byte)(y * 5 + seed), (byte)(seed * 31), a);
            }
        }

        return raster;
    }

    public byte[] Build()
    {
        using MemoryStream body = new();
        body.Write("ACON"u8);
        if (InfoFirst)
        {
            WriteInfo(body);
        }

        byte[] header = new byte[Math.Max(HeaderChunkSize, 36)];
        int steps = Sequence?.Length ?? Frames.Count;
        uint flags = Flags ?? (1u | (Sequence is not null ? 2u : 0u));
        BinaryPrimitives.WriteUInt32LittleEndian(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), (uint)(DeclaredFrames ?? Frames.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)(DeclaredSteps ?? steps));
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)RawHeader.Width);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), (uint)RawHeader.Height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), (uint)RawHeader.Bits);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), RawHeader.Bits > 0 ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)DefaultRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(32), flags);
        Chunk(body, "anih", header[..HeaderChunkSize]);
        if (Rates is not null)
        {
            Chunk(body, "rate", Words(Rates));
        }

        if (Sequence is not null)
        {
            Chunk(body, "seq ", Words(Sequence));
        }

        using MemoryStream frames = new();
        frames.Write("fram"u8);
        foreach (byte[] frame in Frames)
        {
            Chunk(frames, "icon", frame);
        }

        Chunk(body, "LIST", frames.ToArray());
        if (!InfoFirst)
        {
            WriteInfo(body);
        }

        using MemoryStream file = new();
        file.Write("RIFF"u8);
        Span<byte> size = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)body.Length);
        file.Write(size);
        body.Position = 0;
        body.CopyTo(file);
        return file.ToArray();
    }

    private void WriteInfo(MemoryStream body)
    {
        if (Title is null && Artist is null)
        {
            return;
        }

        using MemoryStream info = new();
        info.Write("INFO"u8);
        if (Title is not null)
        {
            Chunk(info, "INAM", [.. Encoding.UTF8.GetBytes(Title), 0]);
        }

        if (Artist is not null)
        {
            Chunk(info, "IART", [.. Encoding.UTF8.GetBytes(Artist), 0]);
        }

        Chunk(body, "LIST", info.ToArray());
    }

    private void Chunk(Stream output, string id, byte[] data)
    {
        Span<byte> head = stackalloc byte[8];
        Encoding.ASCII.GetBytes(id, head);
        BinaryPrimitives.WriteUInt32LittleEndian(head[4..], (uint)data.Length);
        output.Write(head);
        output.Write(data);
        if (Pad && (data.Length & 1) != 0)
        {
            output.WriteByte(0);
        }
    }

    private static byte[] Words(int[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), (uint)values[i]);
        }

        return bytes;
    }
}
