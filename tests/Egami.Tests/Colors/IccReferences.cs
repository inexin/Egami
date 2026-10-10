using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Egami.Tests.Colors;

/// <summary>
/// The ICC references from tools/make-icc-references.py: the profiles in tests/images/icc, and for each, what Little CMS 2 reads
/// from it and its transforms of fixed inputs.
/// </summary>
internal static class IccReferences
{
    public static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "icc");
    public static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "icc");

    private static readonly Lazy<JsonElement> Manifest = new(() => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json"))).RootElement);

    /// <summary>Gets the profile paths relative to tests/images/icc, such as "compact/sRGB-v4.icc".</summary>
    public static IEnumerable<string> Files => Profiles.Select(p => p.GetProperty("file").GetString()!);

    public static IEnumerable<JsonElement> Profiles => Manifest.Value.GetProperty("profiles").EnumerateArray();

    public static JsonElement Profile(string file) => Profiles.Single(p => p.GetProperty("file").GetString() == file);

    public static byte[] Bytes(string file) => File.ReadAllBytes(Path.Combine(Images, file));

    /// <summary>Returns the inputs for a model ("rgb", "gray" or "cmyk"): float samples, 0..1.</summary>
    public static float[] Inputs(string model) => MemoryMarshal.Cast<byte, float>(File.ReadAllBytes(Path.Combine(References, $"inputs-{model}.f32"))).ToArray();

    /// <summary>Returns each case of a profile with its outputs.</summary>
    public static IEnumerable<(JsonElement Case, float[] Outputs)> Cases(string file)
    {
        JsonElement profile = Profile(file);
        using MemoryStream all = new();
        using (GZipStream gz = new(File.OpenRead(Path.Combine(References, profile.GetProperty("outputs").GetString()!)), CompressionMode.Decompress))
        {
            gz.CopyTo(all);
        }

        float[] floats = MemoryMarshal.Cast<byte, float>(all.ToArray()).ToArray();
        int at = 0;
        int deviceChannels = profile.GetProperty("lcms").GetProperty("colorSpace").GetString() switch
        {
            "RGB " => 3,
            "GRAY" => 1,
            _ => 4,
        };
        foreach (JsonElement c in profile.GetProperty("cases").EnumerateArray())
        {
            string target = c.GetProperty("target").GetString()!;
            int channels = target is "lab" or "srgb" ? 3 : deviceChannels;
            int n = c.GetProperty("count").GetInt32() * channels;
            yield return (c, floats[at..(at + n)]);
            at += n;
        }
    }
}

/// <summary>Writes small ICC profiles for tests: a header and the tags given, each 4-byte aligned.</summary>
internal static class IccBuilder
{
    public static byte[] Build(string profileClass, string dataSpace, string pcs, uint version, params (string Signature, byte[] Data)[] tags)
    {
        int offset = 128 + 4 + (12 * tags.Length);
        List<byte> body = [];
        byte[] table = new byte[4 + (12 * tags.Length)];
        BinaryPrimitives.WriteUInt32BigEndian(table, (uint)tags.Length);
        for (int i = 0; i < tags.Length; i++)
        {
            Sig(table.AsSpan(4 + (i * 12)), tags[i].Signature);
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(8 + (i * 12)), (uint)(offset + body.Count));
            BinaryPrimitives.WriteUInt32BigEndian(table.AsSpan(12 + (i * 12)), (uint)tags[i].Data.Length);
            body.AddRange(tags[i].Data);
            while (body.Count % 4 != 0)
            {
                body.Add(0);
            }
        }

        byte[] header = new byte[128];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(offset + body.Count));
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), version);
        Sig(header.AsSpan(12), profileClass);
        Sig(header.AsSpan(16), dataSpace);
        Sig(header.AsSpan(20), pcs);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(24), 2026);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(26), 10);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(28), 10);
        Sig(header.AsSpan(36), "acsp");
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(68), S15(0.9642));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(72), S15(1.0));
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(76), S15(0.8249));
        return [.. header, .. table, .. body];
    }

    public static byte[] Xyz(double x, double y, double z) => [.. Type("XYZ "), .. Be(S15(x)), .. Be(S15(y)), .. Be(S15(z))];

    public static byte[] Gamma(double g) => [.. Type("curv"), 0, 0, 0, 1, (byte)((int)(g * 256) >> 8), (byte)(int)(g * 256), 0, 0];

    public static byte[] Para(ushort function, params double[] values)
    {
        List<byte> d = [.. Type("para"), (byte)(function >> 8), (byte)function, 0, 0];
        foreach (double v in values)
        {
            d.AddRange(Be(S15(v)));
        }

        return [.. d];
    }

    public static byte[] Cicp(byte primaries, byte transfer) => [.. Type("cicp"), primaries, transfer, 0, 1];

    public static byte[] Mluc(params (string Language, string Country, string Text)[] records)
    {
        List<byte> d = [.. Type("mluc"), 0, 0, 0, (byte)records.Length, 0, 0, 0, 12];
        int at = 16 + (12 * records.Length);
        List<byte> strings = [];
        foreach ((string language, string country, string text) in records)
        {
            byte[] u = System.Text.Encoding.BigEndianUnicode.GetBytes(text);
            d.AddRange(System.Text.Encoding.ASCII.GetBytes(language + country));
            d.AddRange(Be(u.Length));
            d.AddRange(Be(at + strings.Count));
            strings.AddRange(u);
        }

        return [.. d, .. strings];
    }

    /// <summary>The sRGB colorants adapted to D50 and its curve, as tags of a version 4 display profile.</summary>
    public static (string, byte[])[] SrgbTags() =>
    [
        ("wtpt", Xyz(0.9642, 1.0, 0.8249)),
        ("rXYZ", Xyz(0.4360747, 0.2225045, 0.0139322)),
        ("gXYZ", Xyz(0.3850649, 0.7168786, 0.0971045)),
        ("bXYZ", Xyz(0.1430804, 0.0606169, 0.7141733)),
        ("rTRC", Para(3, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045)),
        ("gTRC", Para(3, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045)),
        ("bTRC", Para(3, 2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045)),
    ];

    private static byte[] Type(string signature) => [.. System.Text.Encoding.ASCII.GetBytes(signature), 0, 0, 0, 0];

    private static int S15(double v) => (int)Math.Round(v * 65536);

    private static byte[] Be(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    private static void Sig(Span<byte> d, string s) => System.Text.Encoding.ASCII.GetBytes(s.PadRight(4), d);
}
