using System.Buffers.Binary;
using System.Text;

using Egami.Pixels;

namespace Egami.Video;

/// <summary>
/// One mov_text subtitle (3GPP timed text, TS 26.245; codec <see cref="CodecIds.MovText"/>): its text and the style runs that
/// make parts of it bold, italic, underlined, larger or colored. Parse a packet's bytes to read it, and write one with
/// <see cref="ToBytes"/>. Modifier boxes other than styles (highlight, karaoke, scroll delay, links, text box, blink, wrap) are
/// kept as stored, so a sample read and written again keeps its bytes.
/// </summary>
/// <param name="Text">The text, with line breaks as \n. Empty for a gap between subtitles.</param>
/// <example>
/// <code language="csharp">
/// using IDemuxer demuxer = MediaFile.OpenDemuxer("movie.mp4");
/// TrackInfo subtitles = demuxer.Tracks.First(t => t.CodecId == CodecIds.MovText);
/// while (demuxer.ReadPacket() is { } packet)
/// {
///     using (packet)
///     {
///         if (packet.TrackNumber == subtitles.Number)
///             Console.WriteLine($"{packet.Timestamp}: {MovTextSample.Parse(packet.Data.Span).Text}");
///     }
/// }
/// </code>
/// </example>
public sealed record MovTextSample(string Text)
{
    /// <summary>Gets the style runs, in order.</summary>
    public IReadOnlyList<MovTextStyle> Styles { get; init; } = [];

    /// <summary>Gets the modifier boxes after the text other than the style box (styl), as stored.</summary>
    public ReadOnlyMemory<byte> OtherBoxes { get; init; }

    /// <summary>Reads a mov_text sample: a 16-bit length, the text in UTF-8 (or UTF-16 after a byte order mark), then modifier boxes.</summary>
    /// <param name="sample">The packet's bytes.</param>
    /// <returns>The subtitle.</returns>
    /// <exception cref="CorruptDataException">The text runs past the sample, or a modifier box is cut off.</exception>
    public static MovTextSample Parse(ReadOnlySpan<byte> sample)
    {
        if (sample.Length < 2)
        {
            throw new CorruptDataException("mov_text", 0, "A sample is shorter than its 2-byte text length.");
        }

        int length = BinaryPrimitives.ReadUInt16BigEndian(sample);
        if (2 + length > sample.Length)
        {
            throw new CorruptDataException("mov_text", 0, $"The text is {length} bytes, more than the sample's {sample.Length - 2}.");
        }

        ReadOnlySpan<byte> bytes = sample.Slice(2, length);
        string text = bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]) ? Encoding.BigEndianUnicode.GetString(bytes[2..]) : Encoding.UTF8.GetString(bytes);
        List<MovTextStyle> styles = [];
        List<byte> other = [];
        int at = 2 + length;
        while (at < sample.Length)
        {
            if (sample.Length - at < 8)
            {
                throw new CorruptDataException("mov_text", at, "A modifier box header is cut off.");
            }

            uint size = BinaryPrimitives.ReadUInt32BigEndian(sample[at..]);
            if (size < 8 || size > sample.Length - at)
            {
                throw new CorruptDataException("mov_text", at, $"A modifier box of {size} bytes does not fit the sample.");
            }

            ReadOnlySpan<byte> box = sample.Slice(at, (int)size);
            if (box[4..8].SequenceEqual("styl"u8) && box.Length >= 10)
            {
                int count = BinaryPrimitives.ReadUInt16BigEndian(box[8..]);
                for (int i = 0; i < count && 10 + (12 * (i + 1)) <= box.Length; i++)
                {
                    ReadOnlySpan<byte> s = box.Slice(10 + (12 * i), 12);
                    styles.Add(new MovTextStyle(BinaryPrimitives.ReadUInt16BigEndian(s), BinaryPrimitives.ReadUInt16BigEndian(s[2..]), (MovTextFace)s[6], s[7],
                        new Rgba8(s[8], s[9], s[10], s[11]), BinaryPrimitives.ReadUInt16BigEndian(s[4..])));
                }
            }
            else
            {
                other.AddRange(box);
            }

            at += (int)size;
        }

        return new MovTextSample(text) { Styles = styles, OtherBoxes = other.ToArray() };
    }

    /// <summary>Writes the sample: the text in UTF-8, a style box when there are styles, and the other boxes.</summary>
    /// <returns>The packet's bytes.</returns>
    /// <exception cref="InvalidOperationException">The text is longer than 65,535 bytes in UTF-8, or there are more than 65,535 styles.</exception>
    public byte[] ToBytes()
    {
        byte[] text = Encoding.UTF8.GetBytes(Text);
        if (text.Length > ushort.MaxValue || Styles.Count > ushort.MaxValue)
        {
            throw new InvalidOperationException("A mov_text sample holds at most 65,535 bytes of text and 65,535 styles.");
        }

        int styl = Styles.Count > 0 ? 10 + (12 * Styles.Count) : 0;
        byte[] sample = new byte[2 + text.Length + styl + OtherBoxes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(sample, (ushort)text.Length);
        text.CopyTo(sample, 2);
        int at = 2 + text.Length;
        if (styl > 0)
        {
            BinaryPrimitives.WriteUInt32BigEndian(sample.AsSpan(at), (uint)styl);
            "styl"u8.CopyTo(sample.AsSpan(at + 4));
            BinaryPrimitives.WriteUInt16BigEndian(sample.AsSpan(at + 8), (ushort)Styles.Count);
            for (int i = 0; i < Styles.Count; i++)
            {
                MovTextStyle style = Styles[i];
                Span<byte> s = sample.AsSpan(at + 10 + (12 * i), 12);
                BinaryPrimitives.WriteUInt16BigEndian(s, (ushort)style.Start);
                BinaryPrimitives.WriteUInt16BigEndian(s[2..], (ushort)style.End);
                BinaryPrimitives.WriteUInt16BigEndian(s[4..], (ushort)style.FontId);
                s[6] = (byte)style.Face;
                s[7] = (byte)style.FontSize;
                (s[8], s[9], s[10], s[11]) = (style.Color.R, style.Color.G, style.Color.B, style.Color.A);
            }

            at += styl;
        }

        OtherBoxes.Span.CopyTo(sample.AsSpan(at));
        return sample;
    }
}

/// <summary>A style run of a mov_text subtitle (a 3GPP StyleRecord): which characters it covers and how they look.</summary>
/// <param name="Start">The first character it covers, counted in Unicode characters from 0, as 3GPP counts them.</param>
/// <param name="End">The character after the last it covers.</param>
/// <param name="Face">Bold, italic and underline.</param>
/// <param name="FontSize">The font size in pixels.</param>
/// <param name="Color">The text color.</param>
/// <param name="FontId">The font, an id from the sample entry's font table (usually 1).</param>
/// <example>
/// <code language="csharp">
/// // "Hello" in bold red, then " world" plain, at 18 pixels.
/// MovTextSample sample = new("Hello world")
/// {
///     Styles = [new MovTextStyle(0, 5, MovTextFace.Bold, 18, new Rgba8(255, 0, 0, 255))],
/// };
/// byte[] packet = sample.ToBytes();
/// </code>
/// </example>
public readonly record struct MovTextStyle(int Start, int End, MovTextFace Face, int FontSize, Rgba8 Color, int FontId = 1);

/// <summary>How the characters of a <see cref="MovTextStyle"/> are drawn.</summary>
/// <example>
/// <code language="csharp">
/// // "Hello" in bold red, then " world" plain, at 18 pixels.
/// MovTextSample sample = new("Hello world")
/// {
///     Styles = [new MovTextStyle(0, 5, MovTextFace.Bold, 18, new Rgba8(255, 0, 0, 255))],
/// };
/// byte[] packet = sample.ToBytes();
/// </code>
/// </example>
[Flags]
public enum MovTextFace
{
    /// <summary>Plain.</summary>
    None = 0,

    /// <summary>Bold.</summary>
    Bold = 1,

    /// <summary>Italic.</summary>
    Italic = 2,

    /// <summary>Underlined.</summary>
    Underline = 4,
}
