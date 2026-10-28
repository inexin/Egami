using System.Runtime.InteropServices;
using System.Text;

namespace Egami.SystemDrawing.Imaging;

/// <summary>An encoder setting, by the GUID GDI+ gives it: quality, compression, color depth, save flags and the rest.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="codecs"/>
/// </example>
public sealed class Encoder
{
    /// <summary>TIFF compression: one of the Compression <see cref="EncoderValue"/> values.</summary>
    public static readonly Encoder Compression = new(new Guid("{e09d739d-ccd4-44ee-8eba-3fbf8be4fc58}"));

    /// <summary>TIFF bits a pixel: 1, 4, 8, 24 or 32.</summary>
    public static readonly Encoder ColorDepth = new(new Guid("{66087055-ad66-4c7c-9a18-38a2310b8337}"));

    /// <summary>Interlacing; GDI+ writes no format with it.</summary>
    public static readonly Encoder ScanMethod = new(new Guid("{3a4e2661-3109-4e56-8536-42c156e7dcfa}"));

    /// <summary>The GIF version; GDI+ writes no format with it.</summary>
    public static readonly Encoder Version = new(new Guid("{24d18c76-814a-41a4-bf53-1c219cccf797}"));

    /// <summary>Progressive JPEG; GDI+ writes no format with it.</summary>
    public static readonly Encoder RenderMethod = new(new Guid("{6d42c53a-229a-4825-8bb7-5c99e2b9a8b8}"));

    /// <summary>JPEG quality, 0 to 100.</summary>
    public static readonly Encoder Quality = new(new Guid("{1d5be4b5-fa4a-452d-9cdd-5db35105e7eb}"));

    /// <summary>A lossless JPEG rotation or flip.</summary>
    public static readonly Encoder Transformation = new(new Guid("{8d0eb2d1-a58e-4ea8-aa14-108074b7b6f9}"));

    /// <summary>The JPEG luminance quantization table.</summary>
    public static readonly Encoder LuminanceTable = new(new Guid("{edb33bce-0266-4a77-b904-27216099e717}"));

    /// <summary>The JPEG chrominance quantization table.</summary>
    public static readonly Encoder ChrominanceTable = new(new Guid("{f2e455dc-09b3-4316-8260-676ada32481c}"));

    /// <summary>Multi-frame saving: <see cref="EncoderValue.MultiFrame"/>, <see cref="EncoderValue.FrameDimensionPage"/> or <see cref="EncoderValue.Flush"/>.</summary>
    public static readonly Encoder SaveFlag = new(new Guid("{292266fc-ac40-47bf-8cfc-a85b89a655de}"));

    /// <summary>The color space to write.</summary>
    public static readonly Encoder ColorSpace = new(new Guid("{ae7a62a0-ee2c-49d8-9d07-1ba8a927596e}"));

    /// <summary>The image items to write.</summary>
    public static readonly Encoder ImageItems = new(new Guid("{63875e13-1f1d-45ab-9195-a29b6066a650}"));

    /// <summary>Whether to write CMYK.</summary>
    public static readonly Encoder SaveAsCmyk = new(new Guid("{a219bbc9-0a9d-4005-a3ee-3a421b8bb06c}"));

    /// <summary>Initializes a new instance of the <see cref="Encoder"/> class from its GUID.</summary>
    /// <param name="guid">The GUID.</param>
    public Encoder(Guid guid)
    {
        Guid = guid;
    }

    /// <summary>Gets the GUID.</summary>
    public Guid Guid { get; }
}

/// <summary>One encoder setting and its values, passed to a save in <see cref="EncoderParameters"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="codecs"/>
/// </example>
public sealed class EncoderParameter : IDisposable
{
    private Encoder _encoder;

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with one byte.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="value">The value.</param>
    public EncoderParameter(Encoder encoder, byte value)
        : this(encoder, EncoderParameterValueType.ValueTypeByte, 1, new[] { value })
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with one byte of the byte or undefined type.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="value">The value.</param>
    /// <param name="undefined">Whether the type is <see cref="EncoderParameterValueType.ValueTypeUndefined"/>.</param>
    public EncoderParameter(Encoder encoder, byte value, bool undefined)
        : this(encoder, undefined ? EncoderParameterValueType.ValueTypeUndefined : EncoderParameterValueType.ValueTypeByte, 1, new[] { value })
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with one 16-bit value.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="value">The value.</param>
    public EncoderParameter(Encoder encoder, short value)
        : this(encoder, EncoderParameterValueType.ValueTypeShort, 1, new[] { value })
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with one 32-bit value.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="value">The value; GDI+ keeps the low 32 bits.</param>
    public EncoderParameter(Encoder encoder, long value)
        : this(encoder, EncoderParameterValueType.ValueTypeLong, 1, new[] { (int)value })
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with a rational.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="numerator">The numerator.</param>
    /// <param name="denominator">The denominator.</param>
    public EncoderParameter(Encoder encoder, int numerator, int denominator)
        : this(encoder, EncoderParameterValueType.ValueTypeRational, 1, new[] { numerator, denominator })
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with a range of 32-bit values.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="rangebegin">The start of the range.</param>
    /// <param name="rangeend">The end of the range.</param>
    public EncoderParameter(Encoder encoder, long rangebegin, long rangeend)
        : this(encoder, EncoderParameterValueType.ValueTypeLongRange, 1, new[] { (int)rangebegin, (int)rangeend })
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with a range of rationals.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="numerator1">The start's numerator.</param>
    /// <param name="demoninator1">The start's denominator.</param>
    /// <param name="numerator2">The end's numerator.</param>
    /// <param name="demoninator2">The end's denominator.</param>
    public EncoderParameter(Encoder encoder, int numerator1, int demoninator1, int numerator2, int demoninator2)
        : this(encoder, EncoderParameterValueType.ValueTypeRationalRange, 1, new[] { numerator1, demoninator1, numerator2, demoninator2 })
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with an ASCII string.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="value">The string.</param>
    public EncoderParameter(Encoder encoder, string value)
        : this(encoder, EncoderParameterValueType.ValueTypeAscii, (value ?? throw new ArgumentNullException(nameof(value))).Length, Encoding.ASCII.GetBytes(value + "\0"))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with bytes.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="value">The bytes.</param>
    public EncoderParameter(Encoder encoder, byte[] value)
        : this(encoder, EncoderParameterValueType.ValueTypeByte, value.Length, (byte[])value.Clone())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with bytes of the byte or undefined type.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="value">The bytes.</param>
    /// <param name="undefined">Whether the type is <see cref="EncoderParameterValueType.ValueTypeUndefined"/>.</param>
    public EncoderParameter(Encoder encoder, byte[] value, bool undefined)
        : this(encoder, undefined ? EncoderParameterValueType.ValueTypeUndefined : EncoderParameterValueType.ValueTypeByte, value.Length, (byte[])value.Clone())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with 16-bit values.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="value">The values.</param>
    public EncoderParameter(Encoder encoder, short[] value)
        : this(encoder, EncoderParameterValueType.ValueTypeShort, value.Length, (short[])value.Clone())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with 32-bit values.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="value">The values; GDI+ keeps the low 32 bits of each.</param>
    public EncoderParameter(Encoder encoder, long[] value)
        : this(encoder, EncoderParameterValueType.ValueTypeLong, value.Length, value.Select(v => (int)v).ToArray())
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with rationals.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="numerator">The numerators.</param>
    /// <param name="denominator">The denominators, as many as numerators.</param>
    public EncoderParameter(Encoder encoder, int[] numerator, int[] denominator)
        : this(encoder, EncoderParameterValueType.ValueTypeRational, Same(numerator, denominator), Interleave(numerator, denominator))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with ranges of 32-bit values.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="rangebegin">The starts.</param>
    /// <param name="rangeend">The ends, as many as starts.</param>
    public EncoderParameter(Encoder encoder, long[] rangebegin, long[] rangeend)
        : this(encoder, EncoderParameterValueType.ValueTypeLongRange, Same(rangebegin, rangeend), Interleave(rangebegin.Select(v => (int)v).ToArray(), rangeend.Select(v => (int)v).ToArray()))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class with ranges of rationals.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="numerator1">The starts' numerators.</param>
    /// <param name="denominator1">The starts' denominators.</param>
    /// <param name="numerator2">The ends' numerators.</param>
    /// <param name="denominator2">The ends' denominators.</param>
    public EncoderParameter(Encoder encoder, int[] numerator1, int[] denominator1, int[] numerator2, int[] denominator2)
        : this(encoder, EncoderParameterValueType.ValueTypeRationalRange, Same(numerator1, denominator1, numerator2, denominator2), Interleave(numerator1, denominator1, numerator2, denominator2))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class from values in memory, which are copied.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="numberValues">The number of values.</param>
    /// <param name="type">The type of the values.</param>
    /// <param name="value">The address of the values.</param>
    public EncoderParameter(Encoder encoder, int numberValues, EncoderParameterValueType type, IntPtr value)
        : this(encoder, type, numberValues, Copy(numberValues, type, value))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameter"/> class from values in memory, which are copied.</summary>
    /// <param name="encoder">The setting.</param>
    /// <param name="NumberOfValues">The number of values.</param>
    /// <param name="Type">The type of the values, an <see cref="EncoderParameterValueType"/>.</param>
    /// <param name="Value">The address of the values as a 32-bit integer.</param>
    [Obsolete("This constructor has been deprecated. Use EncoderParameter(Encoder encoder, int numberValues, EncoderParameterValueType type, IntPtr value) instead.")]
#pragma warning disable CA1707, SA1313 // The parameter names are System.Drawing's.
    public EncoderParameter(Encoder encoder, int NumberOfValues, int Type, int Value)
#pragma warning restore CA1707, SA1313
        : this(encoder, NumberOfValues, (EncoderParameterValueType)Type, (IntPtr)Value)
    {
    }

    private EncoderParameter(Encoder encoder, EncoderParameterValueType type, int count, Array values)
    {
        _encoder = encoder ?? throw new ArgumentNullException(nameof(encoder));
        ValueType = type;
        NumberOfValues = count;
        Values = values;
    }

    /// <summary>Gets or sets the setting.</summary>
    public Encoder Encoder
    {
        get => _encoder;
        set => _encoder = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Gets the type of the values.</summary>
    public EncoderParameterValueType Type => ValueType;

    /// <summary>Gets the type of the values.</summary>
    public EncoderParameterValueType ValueType { get; }

    /// <summary>Gets the number of values.</summary>
    public int NumberOfValues { get; }

    /// <summary>Gets the values: bytes, 16-bit or 32-bit integers, with rationals and ranges as consecutive pairs.</summary>
    internal Array Values { get; }

    /// <summary>The first value as a 32-bit integer, for the settings that take one number.</summary>
    internal long? Number => Values switch
    {
        int[] { Length: > 0 } v => v[0],
        short[] { Length: > 0 } v => v[0],
        byte[] { Length: > 0 } v when ValueType != EncoderParameterValueType.ValueTypeAscii => v[0],
        _ => null,
    };

    /// <summary>Does nothing; the values are managed memory.</summary>
    public void Dispose()
    {
    }

    private static int Same(params Array[] arrays)
    {
        if (arrays.Any(a => a is null))
        {
            throw new ArgumentNullException(nameof(arrays));
        }

        if (arrays.Any(a => a.Length != arrays[0].Length))
        {
            throw new ArgumentException("The arrays must be the same length.");
        }

        return arrays[0].Length;
    }

    private static int[] Interleave(params int[][] arrays)
    {
        Same(arrays);
        int[] all = new int[arrays[0].Length * arrays.Length];
        for (int i = 0; i < arrays[0].Length; i++)
        {
            for (int a = 0; a < arrays.Length; a++)
            {
                all[(i * arrays.Length) + a] = arrays[a][i];
            }
        }

        return all;
    }

    private static Array Copy(int count, EncoderParameterValueType type, IntPtr value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (type == EncoderParameterValueType.ValueTypePointer)
        {
            // A pointer GDI+ keeps as it is; nothing reads it here.
            return Array.Empty<byte>();
        }

        if (value == IntPtr.Zero)
        {
            throw new ArgumentNullException(nameof(value));
        }

        switch (type)
        {
            case EncoderParameterValueType.ValueTypeByte or EncoderParameterValueType.ValueTypeUndefined:
                byte[] bytes = new byte[count];
                Marshal.Copy(value, bytes, 0, count);
                return bytes;
            case EncoderParameterValueType.ValueTypeAscii:
                byte[] text = new byte[count];
                Marshal.Copy(value, text, 0, count);
                return text;
            case EncoderParameterValueType.ValueTypeShort:
                short[] shorts = new short[count];
                Marshal.Copy(value, shorts, 0, count);
                return shorts;
            case EncoderParameterValueType.ValueTypeLong:
                int[] ints = new int[count];
                Marshal.Copy(value, ints, 0, count);
                return ints;
            case EncoderParameterValueType.ValueTypeRational or EncoderParameterValueType.ValueTypeLongRange:
                int[] pairs = new int[count * 2];
                Marshal.Copy(value, pairs, 0, pairs.Length);
                return pairs;
            case EncoderParameterValueType.ValueTypeRationalRange:
                int[] quads = new int[count * 4];
                Marshal.Copy(value, quads, 0, quads.Length);
                return quads;
            default:
                throw new ArgumentException("The value type is not one EncoderParameterValueType defines.", nameof(type));
        }
    }
}

/// <summary>The encoder settings of one save.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="codecs"/>
/// </example>
public sealed class EncoderParameters : IDisposable
{
    /// <summary>Initializes a new instance of the <see cref="EncoderParameters"/> class with room for one setting.</summary>
    public EncoderParameters()
        : this(1)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="EncoderParameters"/> class with room for some settings.</summary>
    /// <param name="count">The number of settings.</param>
    public EncoderParameters(int count)
    {
        Param = new EncoderParameter[count];
    }

    /// <summary>Gets or sets the settings.</summary>
    public EncoderParameter[] Param { get; set; }

    /// <summary>Disposes of each setting.</summary>
    public void Dispose()
    {
        foreach (EncoderParameter? p in Param)
        {
            p?.Dispose();
        }
    }

    /// <summary>The value of the first setting for an encoder, or null.</summary>
    internal long? Number(Encoder encoder) => Param?.FirstOrDefault(p => p?.Encoder.Guid == encoder.Guid)?.Number;
}
