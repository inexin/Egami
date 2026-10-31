using System.Buffers.Binary;
using System.Text;

namespace Egami.Formats.Psd;

/// <summary>
/// An action descriptor, the key-value structure Photoshop keeps settings in (fills, effects, adjustments), read from the
/// specification's "Descriptor structure": values are <see cref="double"/> (doub, and UntF as <see cref="PsdUnitFloat"/>),
/// <see cref="int"/> (long), <see cref="long"/> (comp), <see cref="bool"/>, <see cref="string"/> (TEXT, and an enum's value),
/// nested descriptors, lists, and raw bytes for the rest. Reading stops at damage; what was read stands.
/// </summary>
internal sealed class PsdDescriptor
{
    private const int MaxDepth = 32;

    public string ClassId { get; private init; } = "";

    public Dictionary<string, object> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>Reads the descriptor that follows a 4-byte descriptor version in a block; <see langword="null"/> when damaged.</summary>
    public static PsdDescriptor? ReadVersioned(ReadOnlySpan<byte> data, int skip = 0)
    {
        if (data.Length < skip + 4 || BinaryPrimitives.ReadUInt32BigEndian(data[skip..]) != 16)
        {
            return null;
        }

        int at = skip + 4;
        try
        {
            return Read(data, ref at, 0);
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or InvalidDataException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    public T? Get<T>(string key)
        where T : class => Items.TryGetValue(key, out object? value) ? value as T : null;

    public double? Number(string key) => Items.TryGetValue(key, out object? value) ? value switch
    {
        double d => d,
        int i => i,
        long l => l,
        PsdUnitFloat u => u.Value,
        _ => null,
    } : null;

    public bool? Flag(string key) => Items.TryGetValue(key, out object? value) && value is bool b ? b : null;

    private static PsdDescriptor Read(ReadOnlySpan<byte> data, ref int at, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException("Descriptors nest too deep.");
        }

        _ = UnicodeString(data, ref at);
        PsdDescriptor descriptor = new() { ClassId = Id(data, ref at) };
        int count = Int(data, ref at);
        for (int i = 0; i < count; i++)
        {
            string key = Id(data, ref at);
            descriptor.Items[key] = Value(data, ref at, Type(data, ref at), depth);
        }

        return descriptor;
    }

    private static object Value(ReadOnlySpan<byte> data, ref int at, string type, int depth)
    {
        switch (type)
        {
            case "Objc" or "GlbO":
                return Read(data, ref at, depth + 1);
            case "VlLs":
                {
                    int count = Int(data, ref at);
                    List<object> list = new(Math.Min(count, 1024));
                    for (int i = 0; i < count; i++)
                    {
                        list.Add(Value(data, ref at, Type(data, ref at), depth + 1));
                    }

                    return list;
                }

            case "doub":
                return Double(data, ref at);
            case "UntF":
                {
                    string unit = Type(data, ref at);
                    return new PsdUnitFloat(unit, Double(data, ref at));
                }

            case "UnFl":
                {
                    string unit = Type(data, ref at);
                    int count = Int(data, ref at);
                    List<object> values = [];
                    for (int i = 0; i < count; i++)
                    {
                        values.Add(new PsdUnitFloat(unit, Double(data, ref at)));
                    }

                    return values;
                }

            case "TEXT":
                return UnicodeString(data, ref at);
            case "enum":
                _ = Id(data, ref at);
                return Id(data, ref at);
            case "long":
                return Int(data, ref at);
            case "comp":
                {
                    long v = BinaryPrimitives.ReadInt64BigEndian(data.Slice(at, 8));
                    at += 8;
                    return v;
                }

            case "bool":
                return data[at++] != 0;
            case "type" or "GlbC":
                _ = UnicodeString(data, ref at);
                return Id(data, ref at);
            case "alis" or "tdta" or "Pth ":
                {
                    int length = Int(data, ref at);
                    byte[] bytes = data.Slice(at, length).ToArray();
                    at += length;
                    return bytes;
                }

            case "obj ":
                {
                    int count = Int(data, ref at);
                    for (int i = 0; i < count; i++)
                    {
                        string kind = Type(data, ref at);
                        switch (kind)
                        {
                            case "prop":
                                _ = UnicodeString(data, ref at);
                                _ = Id(data, ref at);
                                _ = Id(data, ref at);
                                break;
                            case "Clss":
                                _ = UnicodeString(data, ref at);
                                _ = Id(data, ref at);
                                break;
                            case "Enmr":
                                _ = UnicodeString(data, ref at);
                                _ = Id(data, ref at);
                                _ = Id(data, ref at);
                                _ = Id(data, ref at);
                                break;
                            case "rele":
                                _ = UnicodeString(data, ref at);
                                _ = Id(data, ref at);
                                at += 4;
                                break;
                            case "Idnt" or "indx":
                                at += 4;
                                break;
                            case "name":
                                _ = UnicodeString(data, ref at);
                                _ = Id(data, ref at);
                                _ = UnicodeString(data, ref at);
                                break;
                            default:
                                throw new InvalidDataException($"Unknown reference kind {kind}.");
                        }
                    }

                    return Array.Empty<byte>();
                }

            default:
                throw new InvalidDataException($"Unknown descriptor type {type}.");
        }
    }

    private static int Int(ReadOnlySpan<byte> data, ref int at)
    {
        int v = BinaryPrimitives.ReadInt32BigEndian(data.Slice(at, 4));
        at += 4;
        return v;
    }

    private static double Double(ReadOnlySpan<byte> data, ref int at)
    {
        double v = BinaryPrimitives.ReadDoubleBigEndian(data.Slice(at, 8));
        at += 8;
        return v;
    }

    private static string Type(ReadOnlySpan<byte> data, ref int at)
    {
        string v = Encoding.Latin1.GetString(data.Slice(at, 4));
        at += 4;
        return v;
    }

    /// <summary>Reads a class or key id: a length, then that many bytes, or 4 bytes when the length is 0.</summary>
    private static string Id(ReadOnlySpan<byte> data, ref int at)
    {
        int length = Int(data, ref at);
        if (length < 0 || length > data.Length - at)
        {
            throw new InvalidDataException("An id runs past the data.");
        }

        int n = length == 0 ? 4 : length;
        string v = Encoding.Latin1.GetString(data.Slice(at, n));
        at += n;
        return v;
    }

    private static string UnicodeString(ReadOnlySpan<byte> data, ref int at)
    {
        int count = Int(data, ref at);
        if (count < 0 || count > (data.Length - at) / 2)
        {
            throw new InvalidDataException("A string runs past the data.");
        }

        string v = Encoding.BigEndianUnicode.GetString(data.Slice(at, count * 2)).TrimEnd('\0');
        at += count * 2;
        return v;
    }
}

/// <summary>A descriptor number with its unit, such as "#Prc" (percent), "#Ang" (degrees) or "#Pxl" (pixels).</summary>
internal sealed record PsdUnitFloat(string Unit, double Value);
