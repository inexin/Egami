using System.Diagnostics.CodeAnalysis;

namespace Egami.Pixels;

/// <summary>The order in which a pixel type stores its channels.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-layout"/>
/// </example>
public enum PixelChannelOrder
{
    /// <summary>
    /// A layout Egami does not know, such as packed YUV or 5-6-5. Such pixels are converted only through their
    /// <see cref="IPixelType{TSelf}"/> methods, never by reinterpreting their bytes.
    /// </summary>
    Other,

    /// <summary>Red, green, blue, alpha.</summary>
    Rgba,

    /// <summary>Blue, green, red, alpha.</summary>
    Bgra,

    /// <summary>Red, green, blue.</summary>
    Rgb,

    /// <summary>Blue, green, red.</summary>
    Bgr,

    /// <summary>Luminance only.</summary>
    Gray,

    /// <summary>Luminance, then alpha.</summary>
    GrayAlpha,

    /// <summary>Alpha, red, green, blue.</summary>
    Argb,

    /// <summary>Alpha, blue, green, red.</summary>
    Abgr,

    /// <summary>Alpha only: a mask. Converts to and from colors as black with that alpha.</summary>
    Alpha,

    /// <summary>Cyan, magenta, yellow, black, as ink amounts: 0 is no ink.</summary>
    Cmyk,
}

/// <summary>The storage type of every channel in a pixel type.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-layout"/>
/// </example>
[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Float32 names the channel encoding, as in GPU format names.")]
public enum PixelChannelType
{
    /// <summary>A storage type Egami does not know, or channels of different types. See <see cref="PixelChannelOrder.Other"/>.</summary>
    Other,

    /// <summary>An 8-bit unsigned integer, 0 to 255, meaning 0 to 1.</summary>
    Unorm8,

    /// <summary>A 16-bit unsigned integer, 0 to 65535, meaning 0 to 1.</summary>
    Unorm16,

    /// <summary>A 32-bit IEEE float, nominally 0 to 1.</summary>
    Float32,

    /// <summary>A 16-bit IEEE float (<see cref="Half"/>), nominally 0 to 1.</summary>
    Float16,

    /// <summary>
    /// Channels packed into a 16 or 32-bit integer rather than whole bytes each, such as 5-6-5.
    /// <see cref="PixelLayoutInfo.ChannelBits"/> gives their widths from the lowest bit up.
    /// </summary>
    Packed,
}

/// <summary>How a pixel type stores alpha.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-layout"/>
/// </example>
public enum AlphaKind
{
    /// <summary>The type has no alpha channel. Every pixel is opaque.</summary>
    None,

    /// <summary>Straight (not premultiplied) alpha.</summary>
    Straight,

    /// <summary>
    /// Premultiplied alpha: each color channel is stored already multiplied by alpha. The conversions of
    /// <see cref="IPixelType{TSelf}"/> still take and return straight values, so only code that reads the memory directly sees it.
    /// </summary>
    Premultiplied,
}

/// <summary>Describes the memory layout of a pixel type.</summary>
/// <param name="ChannelOrder">The order of the channels in memory.</param>
/// <param name="ChannelType">The storage type shared by every channel.</param>
/// <param name="Alpha">How alpha is stored, if at all.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="pixel-layout"/>
/// </example>
public readonly record struct PixelLayoutInfo(PixelChannelOrder ChannelOrder, PixelChannelType ChannelType, AlphaKind Alpha)
{
    private readonly int _bitsPerPixel;
    private readonly int[]? _channelBits;

    /// <summary>Initializes a new instance of the <see cref="PixelLayoutInfo"/> struct for a packed layout, such as 5-6-5.</summary>
    /// <param name="channelOrder">The order of the channels, from the lowest bit up.</param>
    /// <param name="channelType">The storage type, usually <see cref="PixelChannelType.Packed"/>.</param>
    /// <param name="alpha">How alpha is stored, if at all.</param>
    /// <param name="bitsPerPixel">The size of one pixel in bits.</param>
    /// <param name="channelBits">The width of each channel in bits, in the order of <paramref name="channelOrder"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="bitsPerPixel"/> is not positive, or a channel width is negative.</exception>
    /// <exception cref="ArgumentException">The channel widths add up to more than <paramref name="bitsPerPixel"/>.</exception>
    public PixelLayoutInfo(PixelChannelOrder channelOrder, PixelChannelType channelType, AlphaKind alpha, int bitsPerPixel, params ReadOnlySpan<int> channelBits)
        : this(channelOrder, channelType, alpha)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bitsPerPixel);
        int total = 0;
        foreach (int bits in channelBits)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(bits, nameof(channelBits));
            total += bits;
        }

        if (total > bitsPerPixel)
        {
            throw new ArgumentException($"The channels take {total} bits, more than the {bitsPerPixel} in a pixel.", nameof(channelBits));
        }

        _bitsPerPixel = bitsPerPixel;
        _channelBits = channelBits.IsEmpty ? null : channelBits.ToArray();
    }

    /// <summary>
    /// Gets the number of channels, including alpha; for <see cref="PixelChannelOrder.Other"/>, the number of channel widths
    /// given, else 0.
    /// </summary>
    public int ChannelCount => ChannelOrder switch
    {
        PixelChannelOrder.Rgba or PixelChannelOrder.Bgra or PixelChannelOrder.Argb or PixelChannelOrder.Abgr or PixelChannelOrder.Cmyk => 4,
        PixelChannelOrder.Rgb or PixelChannelOrder.Bgr => 3,
        PixelChannelOrder.GrayAlpha => 2,
        PixelChannelOrder.Gray or PixelChannelOrder.Alpha => 1,
        _ => _channelBits?.Length ?? 0,
    };

    /// <summary>
    /// Gets the number of bits in each channel; for a packed layout, the width its channels share, or 0 when they differ;
    /// 0 for <see cref="PixelChannelType.Other"/>.
    /// </summary>
    public int BitsPerChannel => ChannelType switch
    {
        PixelChannelType.Unorm8 => 8,
        PixelChannelType.Unorm16 or PixelChannelType.Float16 => 16,
        PixelChannelType.Float32 => 32,
        _ => SharedChannelBits(),
    };

    /// <summary>
    /// Gets the size of one pixel in bits: the size given for a packed layout, otherwise the channel count times the channel
    /// size; 0 when the order or type is <c>Other</c> and no size was given.
    /// </summary>
    public int BitsPerPixel => _bitsPerPixel != 0 ? _bitsPerPixel : ChannelCount * BitsPerChannel;

    /// <summary>
    /// Gets the width of each channel in bits, in channel order, for a layout created with them (packed layouts); empty
    /// otherwise, when every channel is <see cref="BitsPerChannel"/> wide.
    /// </summary>
    public ReadOnlySpan<int> ChannelBits => _channelBits;

    /// <summary>
    /// Gets the size of one pixel in bytes as the layout describes it, rounded up to whole bytes; 0 when the order or type is
    /// <c>Other</c> and no size was given. Egami itself always uses the struct's size.
    /// </summary>
    public int BytesPerPixel => (BitsPerPixel + 7) / 8;

    /// <summary>Gets a value indicating whether the type has an alpha channel.</summary>
    public bool HasAlpha => Alpha != AlphaKind.None;

    /// <summary>
    /// Gets a value indicating whether a writer needs 16-bit samples to keep every value: 16-bit, float and unknown layouts,
    /// and packed layouts with a channel wider than 8 bits.
    /// </summary>
    internal bool NeedsWideSamples => ChannelType switch
    {
        PixelChannelType.Unorm8 => false,
        PixelChannelType.Packed => MaxChannelBits > 8,
        _ => true,
    };

    /// <summary>Gets the widest channel in bits: <see cref="BitsPerChannel"/>, or the widest of <see cref="ChannelBits"/>.</summary>
    internal int MaxChannelBits
    {
        get
        {
            int max = BitsPerChannel;
            foreach (int bits in ChannelBits)
            {
                max = Math.Max(max, bits);
            }

            return max;
        }
    }

    /// <summary>Returns whether two layouts are the same, including the size and channel widths of packed layouts.</summary>
    /// <param name="other">The other layout.</param>
    /// <returns><see langword="true"/> if they describe the same memory.</returns>
    public bool Equals(PixelLayoutInfo other)
    {
        return ChannelOrder == other.ChannelOrder && ChannelType == other.ChannelType && Alpha == other.Alpha
            && _bitsPerPixel == other._bitsPerPixel && ChannelBits.SequenceEqual(other.ChannelBits);
    }

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        hash.Add(ChannelOrder);
        hash.Add(ChannelType);
        hash.Add(Alpha);
        hash.Add(_bitsPerPixel);
        foreach (int bits in ChannelBits)
        {
            hash.Add(bits);
        }

        return hash.ToHashCode();
    }

    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append(System.Globalization.CultureInfo.InvariantCulture, $"ChannelOrder = {ChannelOrder}, ChannelType = {ChannelType}, Alpha = {Alpha}");
        if (_bitsPerPixel != 0)
        {
            builder.Append(System.Globalization.CultureInfo.InvariantCulture, $", BitsPerPixel = {_bitsPerPixel}, ChannelBits = [{string.Join(", ", _channelBits ?? [])}]");
        }

        return true;
    }

    private int SharedChannelBits()
    {
        ReadOnlySpan<int> bits = ChannelBits;
        if (bits.IsEmpty)
        {
            return 0;
        }

        foreach (int b in bits)
        {
            if (b != bits[0])
            {
                return 0;
            }
        }

        return bits[0];
    }
}
