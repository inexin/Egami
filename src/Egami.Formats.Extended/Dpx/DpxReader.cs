using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Extended.Dpx;

/// <summary>
/// Reads DPX, written from SMPTE ST 268: big and little-endian files of 1 to 8 image elements, each 1, 8, 10, 12 or 16-bit integer
/// or 32 or 64-bit float samples, packed (method 0) or filled to 32-bit words (methods A and B), plain or run-length coded, as RGB,
/// RGBA, ABGR, one channel (luma, alpha, depth or a single colour) or CbYCr 4:2:2 or 4:4:4 with or without alpha. Rows of a plain
/// element are read as they are asked for; a run-length coded or bottom-up element is read whole first.
/// </summary>
internal sealed class DpxReader : ExtendedReader, IMultiImageReader
{
    public override RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        DpxHeader header = DpxHeader.ReadFrom(input);
        DpxElement element = Choose(header, options);
        Plan plan = Plan.For(header, element, options);
        return Describe(DpxFormat.Info, header.Width, header.Height, plan.Layout, plan.Buffered ? RasterAccess.Buffered : RasterAccess.Streamed, plan.WorkingBytes(header.Width, header.Height)) with
        {
            ImageCount = Supported(header).Count,
            HasMetadata = true,
        };
    }

    public override RowReader Open(EgamiInput input, ReaderOptions options)
    {
        DpxHeader header = DpxHeader.ReadFrom(input);
        DpxElement element = Choose(header, options);
        Plan plan = Plan.For(header, element, options);
        input.CheckFrameSize(header.Width, header.Height);
        if (element.Offset < input.Position)
        {
            throw input.Corrupt(780 + (element.Index * 72) + 28, $"Image element {element.Index} starts at {element.Offset}, inside the header or before the element read last.");
        }

        input.Skip(element.Offset - input.Position);
        DpxSamples probe = new(header.Width, plan.Color, plan.Map, plan.PixelSamples, element.Bits, plan.Storage, header.BigEndian, plan.Layout, false, false);
        long unpadded = probe.ByteLength(probe.RowSamples);
        long padded = (unpadded + 3) & ~3L;

        // ST 268 starts every line on a 32-bit word; some writers leave rows of bytes or 16-bit words unpadded, which the file
        // size gives away.
        long stride = padded != unpadded && header.Elements.Length == 1 && header.FileSize == element.Offset + (unpadded * header.Height) ? unpadded : padded;
        FilmSource source = new()
        {
            Format = DpxFormat.Info,
            Source = "Dpx",
            Width = header.Width,
            Height = header.Height,
            Offset = element.Offset,
            BigEndian = header.BigEndian,
            Bits = element.Bits,
            Plan = plan,
            Stride = stride + element.EndOfLinePadding,
            LinePadding = element.EndOfLinePadding,
            Details = header.Details(element),
            Linear = plan.Linearize || element.Transfer == DpxTransfer.Linear,
            Orientation = header.Orientation,
        };
        return FilmRows.Open(input, options, source);
    }

    public IReadOnlyList<ReaderOptions> Images(EgamiInput input, ReaderOptions options)
    {
        DpxHeader header = DpxHeader.ReadFrom(input);
        DpxReaderOptions dpx = options as DpxReaderOptions ?? new DpxReaderOptions
        {
            Recovery = options.Recovery,
            ReadMetadata = options.ReadMetadata,
            ConvertColorTo = options.ConvertColorTo,
            Frames = options.Frames,
            MinimumSize = options.MinimumSize,
            Region = options.Region,
        };
        return [.. Supported(header).Select(e => dpx with { Element = e.Index })];
    }

    /// <summary>Returns the elements Egami can read, in the file's order.</summary>
    private static List<DpxElement> Supported(DpxHeader header)
    {
        return [.. header.Elements.Where(e => Plan.Describe(e) is not null && e.Offset >= header.Read)];
    }

    private static DpxElement Choose(DpxHeader header, ReaderOptions options)
    {
        int index = (options as DpxReaderOptions)?.Element ?? 0;
        if (index >= header.Elements.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(options), $"Image element {index} was asked for; the file has {header.Elements.Length}.");
        }

        return header.Elements[index];
    }

    /// <summary>How an element's samples become pixels.</summary>
    internal sealed class Plan
    {
        public required PixelLayoutInfo Layout { get; init; }

        public required DpxColor Color { get; init; }

        public required int[] Map { get; init; }

        public required int PixelSamples { get; init; }

        public required DpxStorage Storage { get; init; }

        public required bool Linearize { get; init; }

        public required bool Rec601 { get; init; }

        public bool RunLength { get; init; }

        public bool BottomUp { get; init; }

        public bool FlipX { get; init; }

        public bool Buffered => RunLength || BottomUp;

        public long WorkingBytes(int width, int height)
        {
            long row = (long)width * Layout.ChannelCount * 4;
            return Buffered ? row + ((long)width * height * PixelSamples * 2) : row * 2;
        }

        /// <summary>Returns what an element's descriptor, bits and packing make, or <see langword="null"/> when Egami cannot read it.</summary>
        public static (DpxColor Color, int[] Map, int PixelSamples, int Channels, DpxStorage Storage)? Describe(DpxElement element)
        {
            DpxStorage? storage = (element.Bits, element.Packing) switch
            {
                (1, _) => DpxStorage.PackedLsb,
                (8, _) => DpxStorage.Bytes,
                (10, 1) => DpxStorage.Filled10A,
                (10, 2) => DpxStorage.Filled10B,
                (10, _) => DpxStorage.PackedLsb,
                (12, 1) => DpxStorage.Words16,
                (12, 2) => DpxStorage.Words16Low,
                (12, _) => DpxStorage.PackedLsb,
                (16, _) => DpxStorage.Words16Low,
                (32, _) => DpxStorage.Float32,
                (64, _) => DpxStorage.Float64,
                _ => null,
            };
            if (storage is not DpxStorage s || (element.Encoding == 1 && s is DpxStorage.Float32 or DpxStorage.Float64))
            {
                return null;
            }

            bool video = s is not (DpxStorage.Float32 or DpxStorage.Float64);
            return element.Descriptor switch
            {
                1 or 2 or 3 or 4 or 6 or 8 => (DpxColor.Direct, [0], 1, 1, s),
                50 => (DpxColor.Direct, [0, 1, 2], 3, 3, s),
                51 => (DpxColor.Direct, [0, 1, 2, 3], 4, 4, s),
                52 => (DpxColor.Direct, [3, 2, 1, 0], 4, 4, s),
                100 when video => (DpxColor.CbYCrY, [], 2, 3, s),
                101 when video => (DpxColor.CbYACrYA, [], 3, 4, s),
                102 when video => (DpxColor.CbYCr, [], 3, 3, s),
                103 when video => (DpxColor.CbYCrA, [], 4, 4, s),
                _ => null,
            };
        }

        public static Plan For(DpxHeader header, DpxElement element, ReaderOptions options)
        {
            if (Describe(element) is not { } d)
            {
                throw new UnsupportedFormatException($"DPX image element {element.Index} has descriptor {element.Descriptor}, {element.Bits} bits, packing {element.Packing} and encoding {element.Encoding}, which Egami does not read.");
            }

            bool isFloat = d.Storage is DpxStorage.Float32 or DpxStorage.Float64;
            bool linearize = options is DpxReaderOptions { Linearize: true } && !isFloat && d.Color == DpxColor.Direct
                && element.Descriptor != 4 && element.Transfer is DpxTransfer.PrintingDensity or DpxTransfer.Logarithmic;
            bool floatOut = isFloat || linearize;
            bool wide = element.Bits > 8;
            PixelLayoutInfo layout = d.Channels switch
            {
                1 => floatOut ? GrayF32.Layout : wide ? Gray16.Layout : Gray8.Layout,
                3 => floatOut ? RgbF32.Layout : wide ? Rgb16.Layout : Rgb8.Layout,
                _ => floatOut ? RgbaF32.Layout : wide ? Rgba16.Layout : Rgba8.Layout,
            };
            int orientation = header.Orientation;
            return new Plan
            {
                Layout = layout,
                Color = d.Color,
                Map = d.Map,
                PixelSamples = d.PixelSamples,
                Storage = d.Storage,
                Linearize = linearize,
                Rec601 = element.Colorimetric is DpxTransfer.Rec601Bg or DpxTransfer.Rec601M,
                RunLength = element.Encoding == 1,
                BottomUp = (orientation & 2) != 0,
                FlipX = (orientation & 1) != 0,
            };
        }
    }
}
