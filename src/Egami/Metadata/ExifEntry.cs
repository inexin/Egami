using System.Buffers.Binary;
using System.Text;

namespace Egami.Metadata;

/// <summary>The image file directories (IFDs) an EXIF block holds its tags in.</summary>
public enum ExifIfd
{
    /// <summary>IFD0: the main image (camera make and model, orientation, resolution, date).</summary>
    Primary,

    /// <summary>The EXIF sub-IFD: exposure, lens and capture details.</summary>
    Exif,

    /// <summary>The GPS sub-IFD: where the photo was taken.</summary>
    Gps,

    /// <summary>The interoperability sub-IFD.</summary>
    Interoperability,

    /// <summary>IFD1: the embedded thumbnail and its tags.</summary>
    Thumbnail,
}

/// <summary>The TIFF data types EXIF values are stored as.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "These are the TIFF specification's type names.")]
public enum ExifType
{
    /// <summary>8-bit unsigned integers.</summary>
    Byte = 1,

    /// <summary>Text, ending with a zero byte.</summary>
    Ascii = 2,

    /// <summary>16-bit unsigned integers.</summary>
    Short = 3,

    /// <summary>32-bit unsigned integers.</summary>
    Long = 4,

    /// <summary>Pairs of 32-bit unsigned integers: numerator and denominator.</summary>
    Rational = 5,

    /// <summary>8-bit signed integers.</summary>
    SByte = 6,

    /// <summary>Bytes whose meaning depends on the tag.</summary>
    Undefined = 7,

    /// <summary>16-bit signed integers.</summary>
    SShort = 8,

    /// <summary>32-bit signed integers.</summary>
    SLong = 9,

    /// <summary>Pairs of 32-bit signed integers: numerator and denominator.</summary>
    SRational = 10,

    /// <summary>32-bit floating point numbers.</summary>
    Float = 11,

    /// <summary>64-bit floating point numbers.</summary>
    Double = 12,
}

/// <summary>A rational EXIF value, such as an exposure time of 1/250 or an aperture of 28/10.</summary>
/// <param name="Numerator">The numerator.</param>
/// <param name="Denominator">The denominator; 0 in damaged or "unknown" values.</param>
public readonly record struct ExifFraction(long Numerator, long Denominator)
{
    /// <summary>Returns the value as a number, or NaN when the denominator is 0.</summary>
    /// <returns>The quotient.</returns>
    public double ToDouble() => Denominator == 0 ? double.NaN : (double)Numerator / Denominator;

    /// <inheritdoc/>
    public override string ToString() => $"{Numerator}/{Denominator}";
}

/// <summary>A position from the GPS tags of an EXIF block.</summary>
/// <param name="Latitude">Degrees north, negative for south.</param>
/// <param name="Longitude">Degrees east, negative for west.</param>
/// <param name="Altitude">Meters above sea level, negative below it, or <see langword="null"/> when the block has none.</param>
public readonly record struct GeoPosition(double Latitude, double Longitude, double? Altitude);

/// <summary>One EXIF value: its tag, TIFF type and raw bytes, with accessors that read them as text, integers or fractions.</summary>
public sealed class ExifEntry
{
    private readonly byte[] _value;

    internal ExifEntry(ExifKey key, ExifType type, int count, byte[] value, bool bigEndian, int offset = -1)
    {
        Key = key;
        Type = type;
        Count = count;
        _value = value;
        IsBigEndian = bigEndian;
        Offset = offset;
    }

    /// <summary>Gets the tag and the IFD it is in.</summary>
    public ExifKey Key { get; }

    /// <summary>Gets the TIFF type.</summary>
    public ExifType Type { get; }

    /// <summary>Gets the number of values (characters, for text).</summary>
    public int Count { get; }

    /// <summary>Gets the value bytes as stored, in the block's byte order.</summary>
    public ReadOnlyMemory<byte> RawValue => _value;

    /// <summary>Gets whether <see cref="RawValue"/> is big-endian (Motorola order) rather than little-endian (Intel order).</summary>
    public bool IsBigEndian { get; }

    /// <summary>Gets where the value was in the block it was read from, or -1 (kept for the maker note, whose own offsets refer to it).</summary>
    internal int Offset { get; }

    /// <summary>Returns the value as text: ASCII up to the first zero byte, read as UTF-8 when it is valid UTF-8 and as Latin-1 otherwise.</summary>
    /// <returns>The text.</returns>
    public string AsString()
    {
        ReadOnlySpan<byte> bytes = _value;
        int end = bytes.IndexOf((byte)0);
        if (end >= 0)
        {
            bytes = bytes[..end];
        }

        return System.Text.Unicode.Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : Encoding.Latin1.GetString(bytes);
    }

    /// <summary>Returns value <paramref name="index"/> of an integer type.</summary>
    /// <param name="index">Which value, from 0.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The type is not an integer type.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not below <see cref="Count"/>.</exception>
    public long AsInteger(int index = 0)
    {
        CheckIndex(index);
        return Type switch
        {
            ExifType.Byte or ExifType.Undefined => _value[index],
            ExifType.SByte => (sbyte)_value[index],
            ExifType.Short => UInt16(index * 2),
            ExifType.SShort => (short)UInt16(index * 2),
            ExifType.Long => UInt32(index * 4),
            ExifType.SLong => (int)UInt32(index * 4),
            _ => throw new InvalidOperationException($"Tag 0x{Key.Tag:X4} holds {Type} values, not integers."),
        };
    }

    /// <summary>Returns value <paramref name="index"/> as a fraction: rationals as stored, integers over 1.</summary>
    /// <param name="index">Which value, from 0.</param>
    /// <returns>The fraction.</returns>
    /// <exception cref="InvalidOperationException">The type is neither rational nor integer.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not below <see cref="Count"/>.</exception>
    public ExifFraction AsFraction(int index = 0)
    {
        CheckIndex(index);
        return Type switch
        {
            ExifType.Rational => new ExifFraction(UInt32(index * 8), UInt32((index * 8) + 4)),
            ExifType.SRational => new ExifFraction((int)UInt32(index * 8), (int)UInt32((index * 8) + 4)),
            _ => new ExifFraction(AsInteger(index), 1),
        };
    }

    /// <summary>Returns value <paramref name="index"/> of any numeric type as a double.</summary>
    /// <param name="index">Which value, from 0.</param>
    /// <returns>The value.</returns>
    /// <exception cref="InvalidOperationException">The type is text.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is not below <see cref="Count"/>.</exception>
    public double AsDouble(int index = 0)
    {
        CheckIndex(index);
        return Type switch
        {
            ExifType.Float => BitConverter.Int32BitsToSingle((int)UInt32(index * 4)),
            ExifType.Double => BitConverter.Int64BitsToDouble((long)UInt64(index * 8)),
            ExifType.Rational or ExifType.SRational => AsFraction(index).ToDouble(),
            _ => AsInteger(index),
        };
    }

    /// <inheritdoc/>
    public override string ToString() => Type == ExifType.Ascii ? $"{Key}: \"{AsString()}\"" : $"{Key}: {Type}[{Count}]";

    /// <summary>Returns the number of bytes one value of <paramref name="type"/> takes, or 0 for an unknown type.</summary>
    internal static int Size(ExifType type) => type switch
    {
        ExifType.Byte or ExifType.Ascii or ExifType.SByte or ExifType.Undefined => 1,
        ExifType.Short or ExifType.SShort => 2,
        ExifType.Long or ExifType.SLong or ExifType.Float => 4,
        ExifType.Rational or ExifType.SRational or ExifType.Double => 8,
        _ => 0,
    };

    private void CheckIndex(int index)
    {
        if (Type == ExifType.Ascii)
        {
            throw new InvalidOperationException($"Tag 0x{Key.Tag:X4} holds text; use AsString.");
        }

        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
    }

    private ushort UInt16(int at) => IsBigEndian ? BinaryPrimitives.ReadUInt16BigEndian(_value.AsSpan(at)) : BinaryPrimitives.ReadUInt16LittleEndian(_value.AsSpan(at));

    private uint UInt32(int at) => IsBigEndian ? BinaryPrimitives.ReadUInt32BigEndian(_value.AsSpan(at)) : BinaryPrimitives.ReadUInt32LittleEndian(_value.AsSpan(at));

    private ulong UInt64(int at) => IsBigEndian ? BinaryPrimitives.ReadUInt64BigEndian(_value.AsSpan(at)) : BinaryPrimitives.ReadUInt64LittleEndian(_value.AsSpan(at));
}
