using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Pixels;

namespace Egami.Formats.Extended.Dpx;

/// <summary>
/// Writes DPX (SMPTE ST 268 version 2.0): one image element of luma, RGB or RGBA at 8, 10, 12 or 16 bits, 10 and 12-bit samples
/// filled to 32-bit words with method A, as Nuke, Resolve and ffmpeg write them; big endian unless
/// <see cref="DpxWriterOptions.BigEndian"/> is off. The header comes first, then the rows as they come.
/// </summary>
internal sealed class DpxWriter : ExtendedWriter
{
    protected override FormatInfo Format => DpxFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        DpxWriterOptions options = start.Options as DpxWriterOptions ?? DpxWriterOptions.Default;
        DpxDetails? dpx = start.Metadata.FormatDetails as DpxDetails;
        CineonDetails? cineon = start.Metadata.FormatDetails as CineonDetails;
        int bits = options.BitsPerSample ?? (dpx?.BitsPerSample ?? cineon?.BitsPerSample) switch
        {
            int b and (8 or 10 or 12 or 16) => b,
            _ => 10,
        };
        if (bits is not (8 or 10 or 12 or 16))
        {
            throw new ArgumentOutOfRangeException(nameof(start), $"DPX is written at 8, 10, 12 or 16 bits a sample; {bits} was asked for.");
        }

        start.ReportLosses("DPX", SaveKeeps.Alpha | (bits > 8 ? SaveKeeps.Wide : SaveKeeps.None));
        PixelLayoutInfo layout = TPixel.Layout;
        int channels = layout.ChannelOrder == PixelChannelOrder.Gray ? 1 : layout.HasAlpha ? 4 : 3;
        DpxTransfer transfer = options.Transfer ?? dpx?.Transfer ?? (cineon is not null ? DpxTransfer.PrintingDensity : DpxTransfer.UserDefined);
        DpxStorage storage = bits switch
        {
            8 => DpxStorage.Bytes,
            10 => DpxStorage.Filled10A,
            12 => DpxStorage.Words16,
            _ => DpxStorage.Words16Low,
        };
        long rowBytes = FilmEncoder.RowBytes(storage, (long)start.Width * channels);
        long size = DpxHeader.Length + (rowBytes * start.Height);
        if (size > uint.MaxValue)
        {
            throw new NotSupportedException($"A DPX file holds up to 4 GB; this image needs {size} bytes.");
        }

        byte[] header = Header(start.Width, start.Height, channels, bits, storage, transfer, options.BigEndian, (uint)size, dpx);
        return (channels, bits > 8) switch
        {
            (1, false) => new FilmEncoder<TPixel, Gray8>(output, header, start.Width, bits, storage, options.BigEndian),
            (3, false) => new FilmEncoder<TPixel, Rgb8>(output, header, start.Width, bits, storage, options.BigEndian),
            (4, false) => new FilmEncoder<TPixel, Rgba8>(output, header, start.Width, bits, storage, options.BigEndian),
            (1, true) => new FilmEncoder<TPixel, Gray16>(output, header, start.Width, bits, storage, options.BigEndian),
            (3, true) => new FilmEncoder<TPixel, Rgb16>(output, header, start.Width, bits, storage, options.BigEndian),
            _ => new FilmEncoder<TPixel, Rgba16>(output, header, start.Width, bits, storage, options.BigEndian),
        };
    }

    private static byte[] Header(int width, int height, int channels, int bits, DpxStorage storage, DpxTransfer transfer, bool big, uint size, DpxDetails? details)
    {
        byte[] h = new byte[DpxHeader.Length];
        Span<byte> s = h;

        // Numbers ST 268 leaves undefined are all ones; text is NUL-padded.
        s[1408..1432].Fill(0xFF);
        s[1620..1644].Fill(0xFF);
        Encoding.ASCII.GetBytes(big ? "SDPX" : "XPDS", s);
        U32(s, 4, DpxHeader.Length, big);
        Text(s, 8, 8, "V2.0");
        U32(s, 16, size, big);
        U32(s, 20, 1, big);
        U32(s, 24, DpxHeader.GenericLength, big);
        U32(s, 28, DpxHeader.Length - DpxHeader.GenericLength, big);
        U32(s, 32, 0, big);
        Text(s, 36, 100, details?.FileName ?? "");
        Text(s, 136, 24, details?.CreationTime ?? "");
        Text(s, 160, 100, details is { Creator.Length: > 0 } ? details.Creator : "Egami");
        Text(s, 260, 200, details?.Project ?? "");
        Text(s, 460, 200, details?.Copyright ?? "");
        U32(s, 660, 0xFFFFFFFF, big);
        U16(s, 768, 0, big);
        U16(s, 770, 1, big);
        U32(s, 772, (uint)width, big);
        U32(s, 776, (uint)height, big);

        // Image element 0; the other seven are undefined.
        s[852..1356].Fill(0xFF);
        U32(s, 780, 0, big);
        U32(s, 784, 0, big);
        F32(s, 788, 0, big);
        U32(s, 792, (uint)((1 << bits) - 1), big);
        F32(s, 796, transfer == DpxTransfer.PrintingDensity ? 2.047f : BitConverter.UInt32BitsToSingle(0xFFFFFFFF), big);
        s[800] = channels switch
        {
            1 => 6,
            3 => 50,
            _ => 51,
        };
        s[801] = (byte)transfer;
        s[802] = (byte)transfer;
        s[803] = (byte)bits;
        U16(s, 804, (ushort)(storage is DpxStorage.Filled10A or DpxStorage.Words16 ? 1 : 0), big);
        U16(s, 806, 0, big);
        U32(s, 808, DpxHeader.Length, big);
        U32(s, 812, 0, big);
        U32(s, 816, 0, big);
        Text(s, 820, 32, details?.Description ?? "");
        Text(s, 1556, 32, details?.InputDevice ?? "");

        // Film header.
        Text(s, 1664, 2, details?.FilmManufacturer ?? "", terminated: false);
        Text(s, 1666, 2, details?.FilmType ?? "", terminated: false);
        Text(s, 1668, 2, details?.FilmOffset ?? "", terminated: false);
        Text(s, 1670, 6, details?.FilmPrefix ?? "", terminated: false);
        Text(s, 1676, 4, details?.FilmCount ?? "", terminated: false);
        Text(s, 1680, 32, details?.FilmFormat ?? "");
        U32(s, 1712, details?.FramePosition ?? 0xFFFFFFFF, big);
        U32(s, 1716, details?.SequenceLength ?? 0xFFFFFFFF, big);
        U32(s, 1720, 0xFFFFFFFF, big);
        U32(s, 1724, details?.FrameRate is float rate ? BitConverter.SingleToUInt32Bits(rate) : 0xFFFFFFFF, big);
        U32(s, 1728, 0xFFFFFFFF, big);
        Text(s, 1732, 32, details?.FrameId ?? "");
        Text(s, 1764, 100, details?.SlateInfo ?? "");

        // Television header: the timecode, user bits and frame rate when known, the rest undefined.
        s[1920..1972].Fill(0xFF);
        U32(s, 1920, DpxHeader.ParseTimecode(details?.Timecode) ?? 0xFFFFFFFF, big);
        U32(s, 1924, details?.UserBits ?? 0xFFFFFFFF, big);
        return h;
    }

    internal static void U32(Span<byte> s, int at, uint value, bool big)
    {
        if (big)
        {
            BinaryPrimitives.WriteUInt32BigEndian(s[at..], value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(s[at..], value);
        }
    }

    internal static void U16(Span<byte> s, int at, ushort value, bool big)
    {
        if (big)
        {
            BinaryPrimitives.WriteUInt16BigEndian(s[at..], value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(s[at..], value);
        }
    }

    internal static void F32(Span<byte> s, int at, float value, bool big) => U32(s, at, BitConverter.SingleToUInt32Bits(value), big);

    /// <summary>Writes up to <paramref name="length"/> − 1 Latin-1 characters, NUL-padded.</summary>
    /// <summary>
    /// Writes <paramref name="text"/> into a fixed-length field, NUL-filled. A terminated field keeps room for a NUL; a field
    /// ST 268 fills to its full width, such as the film edge code, does not.
    /// </summary>
    internal static void Text(Span<byte> s, int at, int length, string text, bool terminated = true)
    {
        Span<byte> field = s.Slice(at, length);
        field.Clear();
        int n = Math.Min(text.Length, terminated ? length - 1 : length);
        for (int i = 0; i < n; i++)
        {
            field[i] = text[i] < 256 ? (byte)text[i] : (byte)'?';
        }
    }
}

/// <summary>
/// Writes Cineon 4.5: 10-bit RGB, three samples to a big-endian 32-bit word from the top (packing 5), with Kodak's printing-density
/// defaults (code values 0 to 1023 for densities 0 to 2.048) and the text and film fields of a raster read from Cineon.
/// </summary>
internal sealed class CineonWriter : ExtendedWriter
{
    protected override FormatInfo Format => CineonFormat.Info;

    public override RowEncoder<TPixel> Start<TPixel>(Stream output, EncodeStart<TPixel> start)
    {
        start.ReportLosses("Cineon", SaveKeeps.Wide);
        CineonDetails? details = start.Metadata.FormatDetails as CineonDetails;
        long rowBytes = FilmEncoder.RowBytes(DpxStorage.Filled10A, (long)start.Width * 3);
        long size = CineonReader.HeaderLength + CineonReader.FilmLength + (rowBytes * start.Height);
        if (size > uint.MaxValue)
        {
            throw new NotSupportedException($"A Cineon file holds up to 4 GB; this image needs {size} bytes.");
        }

        byte[] h = new byte[CineonReader.HeaderLength + CineonReader.FilmLength];
        Span<byte> s = h;
        const bool Big = true;
        DpxWriter.U32(s, 0, CineonReader.Magic, Big);
        DpxWriter.U32(s, 4, (uint)h.Length, Big);
        DpxWriter.U32(s, 8, CineonReader.HeaderLength, Big);
        DpxWriter.U32(s, 12, CineonReader.FilmLength, Big);
        DpxWriter.U32(s, 16, 0, Big);
        DpxWriter.U32(s, 20, (uint)size, Big);
        DpxWriter.Text(s, 24, 8, "V4.5");
        DpxWriter.Text(s, 32, 100, details?.FileName ?? "");
        DpxWriter.Text(s, 132, 12, details?.CreationDate ?? "");
        DpxWriter.Text(s, 144, 12, details?.CreationTime ?? "");
        s[192] = 0;
        s[193] = 3;
        for (int c = 0; c < 8; c++)
        {
            int at = 196 + (c * 28);
            if (c >= 3)
            {
                s.Slice(at, 28).Fill(0xFF);
                continue;
            }

            s[at] = 0;
            s[at + 1] = (byte)(c + 1);
            s[at + 2] = 10;
            DpxWriter.U32(s, at + 4, (uint)start.Width, Big);
            DpxWriter.U32(s, at + 8, (uint)start.Height, Big);
            DpxWriter.F32(s, at + 12, 0, Big);
            DpxWriter.F32(s, at + 16, 0, Big);
            DpxWriter.F32(s, at + 20, 1023, Big);
            DpxWriter.F32(s, at + 24, 2.048f, Big);
        }

        s[420..452].Fill(0xFF);
        DpxWriter.Text(s, 452, 200, details?.Label ?? "");
        s[680] = 0;
        s[681] = 5;
        s[682] = 0;
        s[683] = 0;
        DpxWriter.U32(s, 684, 0, Big);
        DpxWriter.U32(s, 688, 0, Big);
        s[712..720].Fill(0xFF);
        DpxWriter.Text(s, 720, 100, details?.FileName ?? "");
        DpxWriter.Text(s, 820, 12, details?.CreationDate ?? "");
        DpxWriter.Text(s, 832, 12, details?.CreationTime ?? "");
        DpxWriter.Text(s, 844, 64, details?.InputDevice ?? "");
        DpxWriter.Text(s, 908, 32, details?.DeviceModel ?? "");
        DpxWriter.Text(s, 940, 32, details?.DeviceSerial ?? "");
        s[972..984].Fill(0xFF);

        // Film information.
        s[1024..1028].Fill(0xFF);
        DpxWriter.U32(s, 1028, details?.FilmPrefix ?? 0xFFFFFFFF, Big);
        DpxWriter.U32(s, 1032, details?.FilmCount ?? 0xFFFFFFFF, Big);
        DpxWriter.Text(s, 1036, 32, details?.FilmFormat ?? "");
        DpxWriter.U32(s, 1068, details?.FramePosition ?? 0xFFFFFFFF, Big);
        DpxWriter.U32(s, 1072, details?.FrameRate is float rate ? BitConverter.SingleToUInt32Bits(rate) : 0xFFFFFFFF, Big);
        DpxWriter.Text(s, 1076, 32, details?.FrameId ?? "");
        DpxWriter.Text(s, 1108, 200, details?.SlateInfo ?? "");
        return new FilmEncoder<TPixel, Rgb16>(output, h, start.Width, 10, DpxStorage.Filled10A, Big);
    }
}

/// <summary>Sizes of the rows the writers write.</summary>
internal static class FilmEncoder
{
    /// <summary>Returns the bytes of a row of <paramref name="samples"/> samples, padded to a 32-bit word.</summary>
    public static long RowBytes(DpxStorage storage, long samples)
    {
        long bytes = storage switch
        {
            DpxStorage.Bytes => samples,
            DpxStorage.Filled10A => (samples + 2) / 3 * 4,
            _ => samples * 2,
        };
        return (bytes + 3) & ~3L;
    }
}

/// <summary>Writes the header, then each row converted to <typeparamref name="TFile"/>, quantized and packed.</summary>
internal sealed class FilmEncoder<TPixel, TFile> : RowEncoder<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
    where TFile : unmanaged, IPixelType<TFile>
{
    private readonly OutputBuffer _output;
    private readonly TFile[] _row;
    private readonly int _bits;
    private readonly DpxStorage _storage;
    private readonly bool _big;
    private readonly int _rowBytes;
    private readonly ushort[]? _quantize;
    private readonly bool _lowFirst;

    public FilmEncoder(Stream output, byte[] header, int width, int bits, DpxStorage storage, bool big)
    {
        _output = new OutputBuffer(output);
        _row = new TFile[width];
        _bits = bits;
        _storage = storage;
        _big = big;
        _lowFirst = TFile.Layout.ChannelCount == 1;
        _rowBytes = checked((int)FilmEncoder.RowBytes(storage, (long)width * TFile.Layout.ChannelCount));
        if (bits is 10 or 12)
        {
            // 16-bit values to code values: round(v × max / 65535).
            int max = (1 << bits) - 1;
            _quantize = new ushort[65536];
            for (int v = 0; v < 65536; v++)
            {
                _quantize[v] = (ushort)((((long)v * max) + 32767) / 65535);
            }
        }

        _output.Write(header);
    }

    public override void WriteRow(ReadOnlySpan<TPixel> row)
    {
        PixelConvert.Convert<TPixel, TFile>(row, _row);
        Span<byte> dst = _output.GetSpan(_rowBytes)[.._rowBytes];
        dst.Clear();
        if (_storage == DpxStorage.Bytes)
        {
            MemoryMarshal.AsBytes(_row.AsSpan()).CopyTo(dst);
        }
        else
        {
            ReadOnlySpan<ushort> samples = MemoryMarshal.Cast<TFile, ushort>(_row);
            switch (_storage)
            {
                case DpxStorage.Filled10A:
                    {
                        ushort[] q = _quantize!;
                        int n = samples.Length;
                        int w = 0;
                        for (int i = 0; i < n; i += 3, w += 4)
                        {
                            uint a = q[samples[i]];
                            uint b = i + 1 < n ? q[samples[i + 1]] : 0u;
                            uint c = i + 2 < n ? q[samples[i + 2]] : 0u;
                            DpxWriter.U32(dst, w, _lowFirst ? (c << 22) | (b << 12) | (a << 2) : (a << 22) | (b << 12) | (c << 2), _big);
                        }

                        break;
                    }

                case DpxStorage.Words16:
                    {
                        ushort[] q = _quantize!;
                        int shift = 16 - _bits;
                        for (int i = 0; i < samples.Length; i++)
                        {
                            DpxWriter.U16(dst, i * 2, (ushort)(q[samples[i]] << shift), _big);
                        }

                        break;
                    }

                default:
                    for (int i = 0; i < samples.Length; i++)
                    {
                        DpxWriter.U16(dst, i * 2, samples[i], _big);
                    }

                    break;
            }
        }

        _output.Advance(_rowBytes);
    }

    public override void Complete()
    {
        _output.Flush();
    }

    protected override void Dispose(bool disposing)
    {
        _output.Dispose();
        base.Dispose(disposing);
    }
}
