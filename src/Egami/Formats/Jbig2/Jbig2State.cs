namespace Egami.Formats.Jbig2;

/// <summary>
/// Counts the bytes of every bitmap a decode makes (regions, symbols, patterns, auxiliary buffers) against
/// <see cref="SafetyLimits.MaxTotalBytes"/>, and checks each bitmap's size against the frame limits, before allocating. The
/// pixels decoded in all are bounded too, at <see cref="WorkFactor"/> times <see cref="SafetyLimits.MaxPixels"/>: a few hundred
/// bytes of arithmetic-coded data can describe millions of symbols, so memory alone does not bound the time a decode takes.
/// </summary>
internal sealed class Jbig2Budget(SafetyLimits limits)
{
    /// <summary>How many times <see cref="SafetyLimits.MaxPixels"/> one decode may decode in all; real pages stay far below.</summary>
    internal const int WorkFactor = 8;

    private long _used;
    private long _pixels;

    internal SafetyLimits Limits => limits;

    /// <summary>Creates a bitmap after checking its size and the running total.</summary>
    internal Jbig2Bitmap Bitmap(long width, long height)
    {
        Reserve(width, height);
        return new Jbig2Bitmap((int)width, (int)height);
    }

    /// <summary>Checks a bitmap's size and counts its bytes, for a bitmap another procedure allocates.</summary>
    internal void Reserve(long width, long height)
    {
        if (width < 0 || height < 0)
        {
            throw new InvalidDataException("A bitmap has a negative size.");
        }

        if (width > limits.MaxWidth || height > limits.MaxHeight || width * height > limits.MaxPixels)
        {
            limits.CheckFrameSize(Math.Max(width, 1), Math.Max(height, 1));
        }

        Charge(((width + 63) >> 6) * 8 * height);
        _pixels += width * height;
        if (_pixels > limits.MaxPixels * WorkFactor)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxPixels), limits.MaxPixels * WorkFactor, _pixels);
        }
    }

    /// <summary>Counts bytes about to be allocated.</summary>
    internal void Charge(long bytes)
    {
        _used += bytes;
        if (_used > limits.MaxTotalBytes)
        {
            throw new LimitExceededException(nameof(SafetyLimits.MaxTotalBytes), limits.MaxTotalBytes, _used);
        }
    }
}

/// <summary>
/// The arithmetic decoding state a segment works in: the MQ decoder, the generic and refinement contexts (GB and GR), and the
/// integer decoders of Annex A. A symbol dictionary's text regions for aggregate symbols share it, as T.88 6.5.8.2 implies.
/// </summary>
internal sealed class Jbig2ArithState
{
    internal MqDecoder Mq { get; } = new();

    internal byte[] Gb { get; set; } = [];

    internal byte[] Gr { get; set; } = [];

    internal Jbig2IntegerDecoder Iaai { get; } = new();

    internal Jbig2IntegerDecoder Iadh { get; } = new();

    internal Jbig2IntegerDecoder Iads { get; } = new();

    internal Jbig2IntegerDecoder Iadt { get; } = new();

    internal Jbig2IntegerDecoder Iadw { get; } = new();

    internal Jbig2IntegerDecoder Iaex { get; } = new();

    internal Jbig2IntegerDecoder Iafs { get; } = new();

    internal Jbig2IntegerDecoder Iait { get; } = new();

    internal Jbig2IntegerDecoder Iardh { get; } = new();

    internal Jbig2IntegerDecoder Iardw { get; } = new();

    internal Jbig2IntegerDecoder Iardx { get; } = new();

    internal Jbig2IntegerDecoder Iardy { get; } = new();

    internal Jbig2IntegerDecoder Iari { get; } = new();

    internal Jbig2SymbolIdDecoder? Iaid { get; set; }

    /// <summary>
    /// Throws when the data has run out: the MQ decoder has read far past its end (it feeds 1-bits forever), or the Huffman bits
    /// have. A count read from a header (instances, symbols) can then not drive an endless decode.
    /// </summary>
    internal void CheckExhausted(Jbig2BitReader? bits)
    {
        if (Mq.Overrun > Jbig2Generic.MqDecoderExhausted || (bits is not null && bits.Overrun))
        {
            throw new EndOfStreamException("The data ends before everything its header counts was decoded.");
        }
    }
}
