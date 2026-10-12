using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Egami.Tests.Formats.Exr;

/// <summary>
/// A reference written by tools/hdrref/exrref.cpp: OpenEXR 3.5.2's decode of every channel of every part, and RgbaInputFile's
/// RGBA for each layer of the first part. The file is an int32 JSON length, the JSON, then the samples it points into.
/// </summary>
internal sealed class ExrReference
{
    private readonly byte[] _blob;

    private ExrReference(JsonElement root, byte[] blob)
    {
        Root = root;
        _blob = blob;
    }

    public JsonElement Root { get; }

    public IEnumerable<JsonElement> Parts => Root.GetProperty("parts").EnumerateArray();

    public static ExrReference Load(string path)
    {
        byte[] data;
        if (path.EndsWith(".gz", StringComparison.Ordinal))
        {
            using FileStream file = File.OpenRead(path);
            using GZipStream gzip = new(file, CompressionMode.Decompress);
            using MemoryStream bytes = new();
            gzip.CopyTo(bytes);
            data = bytes.ToArray();
        }
        else
        {
            data = File.ReadAllBytes(path);
        }

        int length = BitConverter.ToInt32(data, 0);
        using JsonDocument json = JsonDocument.Parse(data.AsMemory(4, length));
        return new ExrReference(json.RootElement.Clone(), data[(4 + length)..]);
    }

    /// <summary>Returns a channel's samples as floats: half and uint converted.</summary>
    public float[] Channel(JsonElement channel)
    {
        int count = channel.GetProperty("width").GetInt32() * channel.GetProperty("height").GetInt32();
        int offset = (int)channel.GetProperty("offset").GetInt64();
        int type = channel.GetProperty("type").GetInt32();
        float[] values = new float[count];
        ReadOnlySpan<byte> bytes = _blob.AsSpan(offset);
        for (int i = 0; i < count; i++)
        {
            values[i] = type switch
            {
                1 => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(bytes.Slice(i * 2, 2))),
                2 => BitConverter.ToSingle(bytes.Slice(i * 4, 4)),
                _ => BitConverter.ToUInt32(bytes.Slice(i * 4, 4)),
            };
        }

        return values;
    }

    /// <summary>Returns an RGBA entry's pixels as half bit patterns, four a pixel.</summary>
    public ushort[] Rgba(JsonElement entry, int pixels)
    {
        int offset = (int)entry.GetProperty("offset").GetInt64();
        return MemoryMarshal.Cast<byte, ushort>(_blob.AsSpan(offset, pixels * 8)).ToArray();
    }
}
