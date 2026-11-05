using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Pixels;

namespace Egami.Formats.Extended.Fits;

/// <summary>
/// Writes an uncompressed FITS primary HDU: gray as a 2-axis image, colour as a cube of three planes (red, green, blue), 8-bit,
/// unsigned 16-bit (stored with <c>BZERO</c> 32768) or float samples, the picture's bottom row first, as FITS stores it. The
/// keywords of a raster read from FITS and those of <see cref="FitsWriterOptions.Keywords"/> follow the structural ones.
/// </summary>
internal sealed class FitsWriter : ExtendedWriter
{
    /// <summary>Keywords the writer sets itself, or that would no longer be true of the data written.</summary>
    private static readonly HashSet<string> Structural = new(StringComparer.Ordinal)
    {
        "SIMPLE", "BITPIX", "NAXIS", "EXTEND", "BZERO", "BSCALE", "BLANK", "END", "XTENSION", "PCOUNT", "GCOUNT", "GROUPS",
        "CHECKSUM", "DATASUM", "CONTINUE", "EXTNAME", "EXTVER", "EXTLEVEL", "INHERIT", "ZCMPTYPE", "ZQUANTIZ", "ZDITHER0", "ZSCALE",
        "ZZERO", "ZBLANK",
    };

    protected override FormatInfo Format => FitsFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        FitsWriterOptions options = start.Options as FitsWriterOptions ?? FitsWriterOptions.Default;
        PixelLayoutInfo layout = TPixel.Layout;
        int bitpix = options.Bitpix ?? (layout.ChannelType == PixelChannelType.Float32 || layout.ChannelType == PixelChannelType.Float16 ? -32 : layout.BitsPerChannel > 8 ? 16 : 8);
        if (bitpix is not (8 or 16 or -32))
        {
            throw new ArgumentOutOfRangeException(nameof(start), bitpix, "FITS is written with BITPIX 8, 16 or -32.");
        }

        start.ReportLosses("FITS", bitpix switch
        {
            -32 => SaveKeeps.Wide | SaveKeeps.FloatingPoint,
            16 => SaveKeeps.Wide,
            _ => SaveKeeps.None,
        });
        int planes = IsGray<TPixel>() ? 1 : 3;
        FitsDetails? details = start.Metadata.FormatDetails as FitsDetails;
        List<string> cards =
        [
            Card("SIMPLE", true, "conforms to FITS standard"),
            Card("BITPIX", (long)bitpix, bitpix switch { 8 => "8-bit unsigned integers", 16 => "16-bit integers", _ => "32-bit floats" }),
            Card("NAXIS", planes == 1 ? 2L : 3L, null),
            Card("NAXIS1", (long)start.Width, "width"),
            Card("NAXIS2", (long)start.Height, "height"),
        ];
        if (planes == 3)
        {
            cards.Add(Card("NAXIS3", 3L, "red, green, blue"));
        }

        if (bitpix == 16)
        {
            cards.Add(Card("BZERO", 32768L, "unsigned 16-bit values"));
            cards.Add(Card("BSCALE", 1L, null));
        }

        IEnumerable<FitsCard> kept = details?.Header ?? (IEnumerable<FitsCard>)[];
        bool stretched = details is not null && details.Stretch != FitsStretch.None;
        foreach (FitsCard card in kept.Concat(options.Keywords))
        {
            string key = card.Keyword.ToUpperInvariant();
            if (Structural.Contains(key) || key.StartsWith("NAXIS", StringComparison.Ordinal) || (stretched && key is "DATAMIN" or "DATAMAX" or "BUNIT"))
            {
                continue;
            }

            cards.AddRange(FormatCard(card with { Keyword = key }));
        }

        return new Encoder<TPixel>(output, start.Width, start.Height, planes, bitpix, cards);
    }

    /// <summary>Formats a card as FITS fixed format: numbers and logicals right-aligned to column 30, strings from column 11.</summary>
    internal static string Card(string keyword, object? value, string? comment)
    {
        string text = value switch
        {
            bool b => (b ? "T" : "F").PadLeft(20),
            long l => l.ToString(CultureInfo.InvariantCulture).PadLeft(20),
            double d => Real(d).PadLeft(20),
            string s => ("'" + s.Replace("'", "''", StringComparison.Ordinal).PadRight(8) + "'").PadRight(20),
            _ => "".PadLeft(20),
        };
        string card = keyword.PadRight(8) + "= " + text + (comment is null ? "" : " / " + comment);
        return card.Length > 80 ? card[..80] : card;
    }

    /// <summary>Formats any card: commentary text split over cards of 72 characters, long strings continued, long keywords as HIERARCH.</summary>
    internal static IEnumerable<string> FormatCard(FitsCard card)
    {
        string keyword = Ascii(card.Keyword);
        if (card.Value is null)
        {
            string text = Ascii(card.Comment ?? "");
            if (keyword.Length > 8)
            {
                yield break;
            }

            do
            {
                yield return keyword.PadRight(8) + text[..Math.Min(72, text.Length)];
                text = text[Math.Min(72, text.Length)..];
            }
            while (text.Length > 0);
            yield break;
        }

        if (card.Value is double d && !double.IsFinite(d))
        {
            yield break;
        }

        if (keyword.Length > 8 || keyword.Contains(' ', StringComparison.Ordinal))
        {
            string hierarch = "HIERARCH " + keyword + " = " + Value(card.Value) + (card.Comment is null ? "" : " / " + Ascii(card.Comment));
            if (hierarch.Length <= 80)
            {
                yield return hierarch;
            }

            yield break;
        }

        if (card.Value is string s && s.Replace("'", "''", StringComparison.Ordinal).Length > 68)
        {
            // The CONTINUE convention: pieces ending in '&', each continued on the next card.
            string rest = Ascii(s);
            bool first = true;
            while (rest.Length > 0)
            {
                int take = Math.Min(rest.Length, 66);
                while (take > 1 && rest[..take].Replace("'", "''", StringComparison.Ordinal).Length > 66)
                {
                    take--;
                }

                string piece = rest[..take];
                rest = rest[take..];
                string quoted = "'" + piece.Replace("'", "''", StringComparison.Ordinal) + (rest.Length > 0 ? "&" : "") + "'";
                yield return first ? (keyword.PadRight(8) + "= " + quoted).PadRight(80)[..80] : ("CONTINUE  " + quoted).PadRight(80)[..80];
                first = false;
            }

            yield break;
        }

        yield return Card(keyword, card.Value is string text2 ? Ascii(text2) : card.Value, card.Comment is null ? null : Ascii(card.Comment));
    }

    private static string Value(object? value) => value switch
    {
        bool b => b ? "T" : "F",
        long l => l.ToString(CultureInfo.InvariantCulture),
        double d => Real(d),
        string s => "'" + Ascii(s).Replace("'", "''", StringComparison.Ordinal) + "'",
        _ => "",
    };

    private static string Real(double d)
    {
        string r = d.ToString("R", CultureInfo.InvariantCulture).Replace('e', 'E');
        return r.Contains('.', StringComparison.Ordinal) || r.Contains('E', StringComparison.Ordinal) ? r : r + ".0";
    }

    /// <summary>Keeps printable ASCII only, which FITS headers allow.</summary>
    private static string Ascii(string text)
    {
        StringBuilder s = new(text.Length);
        foreach (char c in text)
        {
            s.Append(c is >= ' ' and <= '~' ? c : '?');
        }

        return s.ToString();
    }

    private sealed class Encoder<TPixel> : RowEncoder<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Stream _output;
        private readonly int _width;
        private readonly int _height;
        private readonly int _planes;
        private readonly int _bitpix;
        private readonly List<string> _cards;
        private readonly byte[] _data;
        private readonly float[] _floats;
        private readonly ushort[] _words;
        private readonly byte[] _bytes;
        private int _y;

        public Encoder(Stream output, int width, int height, int planes, int bitpix, List<string> cards)
        {
            _output = output;
            _width = width;
            _height = height;
            _planes = planes;
            _bitpix = bitpix;
            _cards = cards;
            _data = new byte[checked((long)width * height * planes * (Math.Abs(bitpix) / 8))];
            _floats = bitpix == -32 ? new float[width * planes] : [];
            _words = bitpix == 16 ? new ushort[width * planes] : [];
            _bytes = bitpix == 8 ? new byte[width * planes] : [];
        }

        public override void WriteRow(ReadOnlySpan<TPixel> row)
        {
            int fileRow = _height - 1 - _y++;
            long pixels = (long)_width * _height;
            int width = Math.Abs(_bitpix) / 8;
            for (int c = 0; c < _planes; c++)
            {
                Span<byte> plane = _data.AsSpan((int)(((c * pixels) + ((long)fileRow * _width)) * width), _width * width);
                if (c == 0)
                {
                    Convert(row);
                }

                for (int x = 0; x < _width; x++)
                {
                    int i = (x * _planes) + c;
                    switch (_bitpix)
                    {
                        case 8:
                            plane[x] = _bytes[i];
                            break;
                        case 16:
                            BinaryPrimitives.WriteUInt16BigEndian(plane[(x * 2)..], (ushort)(_words[i] ^ 0x8000));
                            break;
                        default:
                            BinaryPrimitives.WriteSingleBigEndian(plane[(x * 4)..], _floats[i]);
                            break;
                    }
                }
            }
        }

        public override void Complete()
        {
            List<string> cards = [.. _cards, "END"];
            int blocks = ((cards.Count * 80) + 2879) / 2880;
            byte[] header = new byte[blocks * 2880];
            header.AsSpan().Fill((byte)' ');
            for (int i = 0; i < cards.Count; i++)
            {
                Encoding.ASCII.GetBytes(cards[i].PadRight(80).AsSpan(0, 80), header.AsSpan(i * 80, 80));
            }

            _output.Write(header);
            _output.Write(_data);
            int pad = (int)((2880 - (_data.LongLength % 2880)) % 2880);
            _output.Write(new byte[pad]);
            _output.Flush();
        }

        private void Convert(ReadOnlySpan<TPixel> row)
        {
            switch (_bitpix, _planes)
            {
                case (8, 1):
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Gray8>(_bytes.AsSpan()));
                    break;
                case (8, _):
                    PixelConvert.Convert(row, MemoryMarshal.Cast<byte, Rgb8>(_bytes.AsSpan()));
                    break;
                case (16, 1):
                    PixelConvert.Convert(row, MemoryMarshal.Cast<ushort, Gray16>(_words.AsSpan()));
                    break;
                case (16, _):
                    PixelConvert.Convert(row, MemoryMarshal.Cast<ushort, Rgb16>(_words.AsSpan()));
                    break;
                case (_, 1):
                    PixelConvert.Convert(row, MemoryMarshal.Cast<float, GrayF32>(_floats.AsSpan()));
                    break;
                default:
                    PixelConvert.Convert(row, MemoryMarshal.Cast<float, RgbF32>(_floats.AsSpan()));
                    break;
            }
        }
    }
}
