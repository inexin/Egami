using System.Buffers.Binary;

using Egami.Pixels;

namespace Egami.Formats.Extended.Dpx;

/// <summary>
/// Reads Cineon, written from Kodak's "4.5 DRAFT - Image File Format Proposal for Digital Pictures": one or three channels of 8,
/// 10, 12 or 16-bit printing-density samples, pixel-interleaved, packed bit to bit or filled to 8, 16 or 32-bit units; the usual
/// file holds 10-bit RGB three samples to a 32-bit word. Files are big endian; little-endian ones, which some programs write,
/// are read too.
/// </summary>
internal sealed class CineonReader : ExtendedReader
{
    internal const uint Magic = 0x802A5FD7;
    internal const int HeaderLength = 1024;
    internal const int FilmLength = 1024;

    /// <summary>Returns whether the data starts with the Cineon magic number, in either byte order.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= 4 && (BinaryPrimitives.ReadUInt32BigEndian(header) == Magic || BinaryPrimitives.ReadUInt32LittleEndian(header) == Magic);
    }

    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input, options);
        return Describe(CineonFormat.Info, header.Width, header.Height, header.Plan.Layout, header.Plan.Buffered ? RasterAccess.Buffered : RasterAccess.Streamed, header.Plan.WorkingBytes(header.Width, header.Height)) with
        {
            HasMetadata = true,
        };
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input, options);
        input.CheckFrameSize(header.Width, header.Height);
        if (header.Offset < input.Position)
        {
            throw input.Corrupt(4, $"The image data offset {header.Offset} is inside the header.");
        }

        input.Skip(header.Offset - input.Position);
        DpxSamples probe = new(header.Width, header.Plan.Color, header.Plan.Map, header.Plan.PixelSamples, header.Bits, header.Plan.Storage, header.BigEndian, header.Plan.Layout, false, false);
        long unpadded = probe.ByteLength(probe.RowSamples);
        long stride = header.Plan.Storage is DpxStorage.Filled10A or DpxStorage.Filled10B or DpxStorage.PackedMsb ? (unpadded + 3) & ~3L : unpadded;
        FilmSource source = new()
        {
            Format = CineonFormat.Info,
            Source = "Cineon",
            Width = header.Width,
            Height = header.Height,
            Offset = header.Offset,
            BigEndian = header.BigEndian,
            Bits = header.Bits,
            Plan = header.Plan,
            Stride = stride + header.LinePadding,
            LinePadding = header.LinePadding,
            Details = header.Details,
            Linear = header.Plan.Linearize,
            Orientation = header.Details.Orientation,
        };
        return FilmRows.Open(input, options, source);
    }

    private static Header ReadHeader(EgamiInput input, ReaderOptions options)
    {
        byte[] bytes = new byte[HeaderLength + FilmLength];
        input.ReadExactly(bytes.AsSpan(0, 4));
        if (!Matches(bytes))
        {
            throw input.Corrupt(0, "The Cineon signature is wrong.");
        }

        bool big = BinaryPrimitives.ReadUInt32BigEndian(bytes) == Magic;
        input.ReadExactly(bytes.AsSpan(4, 4));
        uint offset = DpxHeader.U32(bytes, 4, big);
        if (offset < HeaderLength)
        {
            throw input.Corrupt(4, $"The image data offset {offset} is inside the header.");
        }

        int read = (int)Math.Min(offset, bytes.Length);
        input.ReadExactly(bytes.AsSpan(8, read - 8));
        int orientation = bytes[192];
        int channels = bytes[193];
        if (channels is not (1 or 3))
        {
            throw new UnsupportedFormatException($"The Cineon file has {channels} channels; Egami reads 1 or 3.");
        }

        int bits = bytes[198];
        uint width = DpxHeader.U32(bytes, 200, big);
        uint height = DpxHeader.U32(bytes, 204, big);
        for (int c = 1; c < channels; c++)
        {
            int at = 196 + (c * 28);
            if (bytes[at + 2] != bits || DpxHeader.U32(bytes, at + 4, big) != width || DpxHeader.U32(bytes, at + 8, big) != height)
            {
                throw new UnsupportedFormatException("The Cineon file's channels differ in depth or size, which Egami does not read.");
            }
        }

        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            throw input.Corrupt(200, $"The image size {width}×{height} is invalid.");
        }

        int interleave = bytes[680];
        int packing = bytes[681];
        if (interleave != 0)
        {
            throw new UnsupportedFormatException($"The Cineon file is interleaved by {(interleave == 1 ? "line" : "channel")}; Egami reads pixel-interleaved files.");
        }

        DpxStorage? storage = (packing, bits) switch
        {
            (0, >= 1 and <= 16) => DpxStorage.PackedMsb,
            (1 or 2, 8) => DpxStorage.Bytes,
            (3, > 8 and <= 16) => DpxStorage.Words16,
            (4, > 8 and <= 16) => DpxStorage.Words16Low,
            (5, 10) => DpxStorage.Filled10A,
            (6, 10) => DpxStorage.Filled10B,
            _ => null,
        };
        if (storage is not DpxStorage s)
        {
            throw new UnsupportedFormatException($"Cineon packing {packing} of {bits}-bit samples is not one Egami reads.");
        }

        // The channel designators say which colour each channel is: 1 red, 2 green, 3 blue.
        int[] map = [0];
        if (channels == 3)
        {
            map = [0, 1, 2];
            int[] designators = [bytes[197], bytes[197 + 28], bytes[197 + 56]];
            if (designators.Order().SequenceEqual([1, 2, 3]))
            {
                map = [Array.IndexOf(designators, 1), Array.IndexOf(designators, 2), Array.IndexOf(designators, 3)];
            }
        }

        bool linearize = options is DpxReaderOptions { Linearize: true };
        PixelLayoutInfo layout = (channels, linearize, bits > 8) switch
        {
            (1, true, _) => GrayF32.Layout,
            (1, false, true) => Gray16.Layout,
            (1, false, false) => Gray8.Layout,
            (_, true, _) => RgbF32.Layout,
            (_, false, true) => Rgb16.Layout,
            _ => Rgb8.Layout,
        };
        CineonDetails details = new()
        {
            Channels = channels,
            BitsPerSample = bits,
            Packing = packing,
            Orientation = orientation,
            FileName = DpxHeader.Text(bytes, 32, 100),
            CreationDate = DpxHeader.Text(bytes, 132, 12),
            CreationTime = DpxHeader.Text(bytes, 144, 12),
            Label = DpxHeader.Text(bytes, 452, 200),
            InputDevice = DpxHeader.Text(bytes, 844, 64),
            DeviceModel = DpxHeader.Text(bytes, 908, 32),
            DeviceSerial = DpxHeader.Text(bytes, 940, 32),
        };
        if (read >= HeaderLength + 300)
        {
            details.FilmPrefix = Defined(DpxHeader.U32(bytes, 1028, big));
            details.FilmCount = Defined(DpxHeader.U32(bytes, 1032, big));
            details.FilmFormat = DpxHeader.Text(bytes, 1036, 32);
            details.FramePosition = Defined(DpxHeader.U32(bytes, 1068, big));
            float rate = DpxHeader.F32(bytes, 1072, big);
            details.FrameRate = float.IsFinite(rate) && BitConverter.SingleToUInt32Bits(rate) != 0xFFFFFFFF ? rate : null;
            details.FrameId = DpxHeader.Text(bytes, 1076, 32);
            details.SlateInfo = DpxHeader.Text(bytes, 1108, 200);
        }

        // Cineon orientation: 0 left to right, top to bottom; 1 bottom to top; 2 right to left; 3 both.
        DpxReader.Plan plan = new()
        {
            Layout = layout,
            Color = DpxColor.Direct,
            Map = map,
            PixelSamples = channels,
            Storage = s,
            Linearize = linearize,
            Rec601 = false,
            BottomUp = (orientation & 1) != 0,
            FlipX = (orientation & 2) != 0,
        };
        return new Header(big, offset, (int)width, (int)height, bits, DpxHeader.U32(bytes, 684, big) is var pad && pad != 0xFFFFFFFF ? pad : 0, plan, details);
    }

    private static uint? Defined(uint value) => value == 0xFFFFFFFF ? null : value;

    private sealed record Header(bool BigEndian, long Offset, int Width, int Height, int Bits, uint LinePadding, DpxReader.Plan Plan, CineonDetails Details);
}
