using System.Buffers.Binary;
using System.Text;

namespace Egami.Tests.Formats.Psd;

/// <summary>
/// Writes action descriptors for hand-built test files: a <see cref="double"/> as doub, a <see cref="Unit"/> as UntF, an
/// <see cref="int"/> as long, a <see cref="bool"/>, a <see cref="string"/> as TEXT, an <see cref="EnumValue"/>, a <see cref="Desc"/>
/// as Objc, a list as VlLs and bytes as tdta.
/// </summary>
internal static class PsdDescriptorWriter
{
    /// <summary>Writes a block's descriptor: version 16, then the descriptor.</summary>
    public static byte[] Versioned(Desc descriptor, int prefix = 0)
    {
        using MemoryStream stream = new();
        stream.Write(new byte[prefix]);
        U32(stream, 16);
        Write(stream, descriptor);
        if (stream.Length % 2 == 1)
        {
            stream.WriteByte(0);
        }

        return stream.ToArray();
    }

    private static void Write(MemoryStream stream, Desc d)
    {
        U32(stream, 0);
        Id(stream, d.ClassId);
        U32(stream, d.Items.Length);
        foreach ((string key, object value) in d.Items)
        {
            Id(stream, key);
            Value(stream, value);
        }
    }

    private static void Value(MemoryStream stream, object value)
    {
        Span<byte> eight = stackalloc byte[8];
        switch (value)
        {
            case double v:
                stream.Write("doub"u8);
                BinaryPrimitives.WriteDoubleBigEndian(eight, v);
                stream.Write(eight);
                break;
            case Unit u:
                stream.Write("UntF"u8);
                stream.Write(Encoding.ASCII.GetBytes(u.Type));
                BinaryPrimitives.WriteDoubleBigEndian(eight, u.Value);
                stream.Write(eight);
                break;
            case int v:
                stream.Write("long"u8);
                U32(stream, v);
                break;
            case bool v:
                stream.Write("bool"u8);
                stream.WriteByte(v ? (byte)1 : (byte)0);
                break;
            case string v:
                stream.Write("TEXT"u8);
                U32(stream, v.Length + 1);
                stream.Write(Encoding.BigEndianUnicode.GetBytes(v + "\0"));
                break;
            case EnumValue e:
                stream.Write("enum"u8);
                Id(stream, e.Type);
                Id(stream, e.Value);
                break;
            case Desc d:
                stream.Write("Objc"u8);
                Write(stream, d);
                break;
            case object[] list:
                stream.Write("VlLs"u8);
                U32(stream, list.Length);
                foreach (object item in list)
                {
                    Value(stream, item);
                }

                break;
            case byte[] bytes:
                stream.Write("tdta"u8);
                U32(stream, bytes.Length);
                stream.Write(bytes);
                break;
            default:
                throw new ArgumentException($"No descriptor type for {value.GetType()}.", nameof(value));
        }
    }

    private static void Id(MemoryStream stream, string id)
    {
        U32(stream, id.Length == 4 ? 0 : id.Length);
        stream.Write(Encoding.ASCII.GetBytes(id));
    }

    private static void U32(MemoryStream stream, long v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)v);
        stream.Write(b);
    }

    /// <summary>A descriptor: its class id and items.</summary>
    internal sealed record Desc(string ClassId, params (string Key, object Value)[] Items);

    /// <summary>A unit float: its unit (#Prc, #Ang, #Pxl) and value.</summary>
    internal sealed record Unit(string Type, double Value);

    /// <summary>An enumerated value: its type and value ids.</summary>
    internal sealed record EnumValue(string Type, string Value);
}
