using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Bmp;

/// <summary>
/// Reads BMP, written from Microsoft's documentation of the file header and the core, info, V4 and V5 headers, and the OS/2 2.x
/// header layout. Rows decode one at a time into <see cref="Bgr8"/> or <see cref="Bgra8"/> and convert to the target type;
/// embedded PNG and JPEG data goes to Egami's own readers.
/// </summary>
internal sealed partial class BmpReader : IFormatReader
{
    internal static readonly BmpReader Instance = new();

    /// <summary>"sRGB", "Win ", "LINK" and "MBED" as V4 and V5 color space types.</summary>
    internal const uint SpaceCalibrated = 0;
    internal const uint SpaceSrgb = 0x7352_4742;
    internal const uint SpaceWindows = 0x5769_6E20;
    internal const uint SpaceLinked = 0x4C49_4E4B;
    internal const uint SpaceEmbedded = 0x4D42_4544;

    private const int FileHeaderLength = 14;

    /// <summary>Returns whether the data starts with "BM" and a known info header size.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= 18 && header[0] == 'B' && header[1] == 'M' && IsHeaderSize(BinaryPrimitives.ReadUInt32LittleEndian(header[14..]));
    }

    internal static bool IsHeaderSize(uint size)
    {
        return size is 12 or 16 or 40 or 52 or 56 or 64 or 108 or 124;
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        bool streams = Streams(input, options);
        Header header = ReadHeader(input, options);
        PixelLayoutInfo layout = header.Natural;
        if (header.Embedded)
        {
            SkipToPixels(input, header);
            layout = Inner(header).ReadInfo(input, options).Layout;
        }

        return new RasterInfo
        {
            Format = BmpFormat.Info,
            Width = header.Width,
            Height = header.Height,
            Layout = layout,
            FrameCount = 1,
            HasMetadata = header.ProfileSize > 0,
            EstimatedBytes = (long)header.Width * header.Height * layout.BytesPerPixel,
            Access = streams ? RasterAccess.Streamed : RasterAccess.Buffered,
            WorkingBytes = streams ? WorkingBytes(header) : 0,
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Header header = ReadHeader(input, options);
        if (header.Embedded)
        {
            SkipToPixels(input, header);
            return Finish(input, header, Inner(header).Read<TPixel>(input, options), options);
        }

        return Decode<TPixel>(input, header, options);
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        Header header = ReadHeader(input, options);
        if (header.Embedded)
        {
            SkipToPixels(input, header);
            return Finish(input, header, Inner(header).Read(input, options), options);
        }

        return header.Natural.HasAlpha ? Decode<Bgra8>(input, header, options) : Decode<Bgr8>(input, header, options);
    }

    private static IFormatReader Inner(Header header)
    {
        return header.Compression == BmpCompression.Png ? Png.PngFormat.Info.Reader! : Jpeg.JpegFormat.Info.Reader!;
    }

    private static TRaster Finish<TRaster>(EgamiInput input, Header header, TRaster raster, ReaderOptions options)
        where TRaster : Raster
    {
        try
        {
            if (raster.Width != header.Width || raster.Height != header.Height)
            {
                raster.Metadata.WarningList.Add("Bmp.EmbeddedSizeMismatch", $"The embedded image is {raster.Width}×{raster.Height}; the BMP header says {header.Width}×{header.Height}.");
            }

            raster.Metadata.FormatName = BmpFormat.Info.Name;
            ApplyMetadata(input, header, raster.Metadata, options);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    private static Header ReadHeader(EgamiInput input, ReaderOptions options)
    {
        Span<byte> file = stackalloc byte[FileHeaderLength];
        input.ReadExactly(file);
        if (file[0] != 'B' || file[1] != 'M')
        {
            throw input.Corrupt(0, "The BMP signature is wrong.");
        }

        uint size = input.ReadUInt32LittleEndian();
        if (!IsHeaderSize(size))
        {
            throw input.Corrupt(FileHeaderLength, $"The info header size {size} is unknown.");
        }

        byte[] info = new byte[size];
        BinaryPrimitives.WriteUInt32LittleEndian(info, size);
        input.ReadExactly(info.AsSpan(4));
        Header header = new() { HeaderSize = (int)size, DataOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[10..]) };
        bool os2 = size is 16 or 64;
        int height;
        int planes;
        uint compression = 0;
        uint colorsUsed = 0;
        if (size == 12)
        {
            header.Width = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(4));
            height = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(6));
            planes = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(8));
            header.Bits = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(10));
        }
        else
        {
            header.Width = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(4));
            height = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(8));
            planes = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(12));
            header.Bits = BinaryPrimitives.ReadUInt16LittleEndian(info.AsSpan(14));
            if (size >= 40)
            {
                compression = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(16));
                int xppm = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(24));
                int yppm = BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(28));
                header.Resolution = xppm > 0 && yppm > 0 ? new Resolution(xppm, yppm, ResolutionUnit.PerMeter) : null;
                colorsUsed = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(32));
            }
        }

        if (header.Width < 1 || height == 0 || height == int.MinValue)
        {
            throw input.Corrupt(18, $"The image size {header.Width}×{height} is invalid.");
        }

        header.TopDown = height < 0;
        header.Height = Math.Abs(height);
        if (planes != 1)
        {
            header.Warnings.Add("Bmp.PlanesInvalid", $"The header says {planes} planes; BMP has 1. The value was ignored.");
        }

        header.Compression = (os2, compression) switch
        {
            (_, 0) => BmpCompression.None,
            (_, 1) => BmpCompression.Rle8,
            (_, 2) => BmpCompression.Rle4,
            (true, 3) => throw new UnsupportedFormatException("OS/2 Huffman 1D compressed BMP files are not supported."),
            (true, 4) => BmpCompression.Rle24,
            (false, 3) => BmpCompression.Bitfields,
            (false, 4) => BmpCompression.Jpeg,
            (false, 5) => BmpCompression.Png,
            (false, 6) => BmpCompression.AlphaBitfields,
            _ => throw new UnsupportedFormatException($"BMP compression {compression} is not supported."),
        };

        bool bitsFit = header.Compression switch
        {
            BmpCompression.Rle8 => header.Bits == 8,
            BmpCompression.Rle4 => header.Bits == 4,
            BmpCompression.Rle24 => header.Bits == 24,
            BmpCompression.Bitfields or BmpCompression.AlphaBitfields => header.Bits is 16 or 32,
            BmpCompression.Jpeg or BmpCompression.Png => true,
            _ => header.Bits is 1 or 2 or 4 or 8 or 16 or 24 or 32,
        };
        if (!bitsFit)
        {
            throw input.Corrupt(28, $"{header.Bits} bits a pixel does not fit {header.Compression} compression.");
        }

        if (header.TopDown && header.Compression is BmpCompression.Rle4 or BmpCompression.Rle8 or BmpCompression.Rle24)
        {
            throw input.Corrupt(22, "Run-length encoded rows cannot be top-down.");
        }

        ReadMasks(input, header, info);
        if (size >= 108)
        {
            header.ColorSpaceType = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(56));
            header.Calibration = info.AsSpan(60, 48).ToArray();
        }

        if (size >= 124)
        {
            header.RenderingIntent = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(108));
            if (header.ColorSpaceType == SpaceEmbedded)
            {
                header.ProfileOffset = FileHeaderLength + (long)BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(112));
                header.ProfileSize = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(116)), int.MaxValue);
                if (header.ProfileSize > 0 && options.ReadMetadata)
                {
                    input.Limits.CheckMetadataBytes(header.ProfileSize);
                }
            }
        }

        if (header.Bits <= 8 && !header.Embedded)
        {
            ReadPalette(input, header, colorsUsed, size == 12 ? 3 : 4);
        }

        return header;
    }

    private static void ReadMasks(EgamiInput input, Header header, byte[] info)
    {
        if (header.Compression is BmpCompression.Bitfields or BmpCompression.AlphaBitfields)
        {
            int count = header.Compression == BmpCompression.AlphaBitfields ? 4 : 3;
            Span<uint> masks = stackalloc uint[4];
            masks.Clear();
            if (header.HeaderSize >= 52)
            {
                for (int i = 0; i < Math.Min(header.HeaderSize >= 56 ? 4 : 3, 4); i++)
                {
                    masks[i] = BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(40 + (4 * i)));
                }
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    masks[i] = input.ReadUInt32LittleEndian();
                }
            }

            if (header.Bits == 16)
            {
                for (int i = 0; i < 4; i++)
                {
                    masks[i] &= 0xFFFF;
                }
            }

            header.Masks = (masks[0], masks[1], masks[2], masks[3]);
            for (int i = 0; i < 4; i++)
            {
                uint mask = masks[i];
                if (mask != 0 && !BitOperations.IsPow2((mask >> BitOperations.TrailingZeroCount(mask)) + 1UL))
                {
                    throw input.Corrupt(54, $"The bit mask 0x{mask:X8} is not contiguous.");
                }

                for (int j = i + 1; j < 4; j++)
                {
                    if ((mask & masks[j]) != 0)
                    {
                        throw input.Corrupt(54, "The bit masks overlap.");
                    }
                }
            }
        }
        else if (header.Bits == 16)
        {
            header.Masks = (0x7C00, 0x03E0, 0x001F, 0);
        }
        else if (header.Bits == 32)
        {
            // The fourth byte is alpha unless it is 0 everywhere, or the V3 to V5 header declares an alpha mask.
            uint alpha = header.HeaderSize >= 56 ? BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(52)) : 0;
            header.Masks = (0x00FF_0000, 0x0000_FF00, 0x0000_00FF, 0xFF00_0000);
            header.AlphaUnlessAllZero = alpha != 0xFF00_0000;
        }
    }

    private static void ReadPalette(EgamiInput input, Header header, uint colorsUsed, int entrySize)
    {
        int count = colorsUsed == 0 || colorsUsed > 1u << header.Bits ? 1 << header.Bits : (int)colorsUsed;
        long room = (header.DataOffset - input.Position) / entrySize;
        if (room > 0 && room < count)
        {
            count = (int)room;
        }

        Span<byte> bytes = stackalloc byte[count * entrySize];
        input.ReadExactly(bytes);
        header.Palette = new Rgb8[count];
        for (int i = 0; i < count; i++)
        {
            header.Palette[i] = new Rgb8(bytes[(i * entrySize) + 2], bytes[(i * entrySize) + 1], bytes[i * entrySize]);
        }
    }

    /// <summary>Moves to the pixel data, reading an embedded V5 profile on the way when it lies before the pixels.</summary>
    private static void SkipToPixels(EgamiInput input, Header header)
    {
        if (header.DataOffset < input.Position)
        {
            header.Warnings.Add("Bmp.DataOffsetInHeaders", $"The pixel data offset {header.DataOffset} points into the headers; the pixels are read from byte {input.Position}.", header.DataOffset);
            return;
        }

        if (header.ProfileSize > 0 && header.ProfileOffset >= input.Position && header.ProfileOffset + header.ProfileSize <= header.DataOffset)
        {
            input.Skip(header.ProfileOffset - input.Position);
            header.Profile = new byte[header.ProfileSize];
            input.ReadExactly(header.Profile);
        }

        input.Skip(header.DataOffset - input.Position);
    }

    private static void ApplyMetadata(EgamiInput input, Header header, RasterMetadata metadata, ReaderOptions options)
    {
        metadata.FormatDetails = new BmpDetails
        {
            HeaderSize = header.HeaderSize,
            BitsPerPixel = header.Bits,
            Compression = header.Compression,
            TopDown = header.TopDown,
            Palette = header.Palette,
            Masks = header.Masks,
            ColorSpaceType = header.ColorSpaceType,
            Calibration = header.Calibration,
            RenderingIntent = header.RenderingIntent,
        };
        metadata.WarningList.AddRange(header.Warnings);

        if (header.Resolution is Resolution resolution)
        {
            metadata.Resolution = resolution;
        }

        if (!options.ReadMetadata)
        {
            return;
        }

        if (header.ProfileSize > 0 && header.Profile is null && header.ProfileOffset >= input.Position)
        {
            // The profile follows the pixels, as most writers put it.
            try
            {
                input.Skip(header.ProfileOffset - input.Position);
                header.Profile = new byte[header.ProfileSize];
                input.ReadExactly(header.Profile);
            }
            catch (CorruptDataException)
            {
                header.Profile = null;
                metadata.WarningList.Add("Bmp.ProfileTruncated", "The file ends before the embedded ICC profile; it was dropped.", header.ProfileOffset);
            }
        }

        if (header.Profile is not null)
        {
            metadata.IccProfile = header.Profile;
            metadata.ColorSpace = ColorSpaceHint.EmbeddedIcc;
        }
        else if (header.ColorSpaceType == SpaceLinked)
        {
            metadata.WarningList.Add("Bmp.ProfileLinked", "The color profile is linked by file name; it was not opened.");
        }
        else if (header.ColorSpaceType is SpaceSrgb or SpaceWindows)
        {
            metadata.ColorSpace = ColorSpaceHint.Srgb;
        }
        else if (header.ColorSpaceType == SpaceCalibrated && header.Calibration.Length == 48)
        {
            metadata.Primaries = BmpDetails.Endpoints(header.Calibration);
        }
    }

    private static Raster<TPixel> Decode<TPixel>(EgamiInput input, Header header, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        SkipToPixels(input, header);
        Raster<TPixel> raster = input.CreateRaster<TPixel>(header.Width, header.Height);
        try
        {
            bool strict = options.Recovery == RecoveryMode.Strict;
            if (header.Compression is BmpCompression.Rle4 or BmpCompression.Rle8 or BmpCompression.Rle24)
            {
                new RleDecoder<TPixel>(input, header, raster, strict).Run();
            }
            else
            {
                // On a seekable stream, whether a 32-bit file's alpha counts is checked first, so no pixel goes through a lossy
                // round trip to drop its alpha afterwards.
                bool? opaque = header.Bits == 32 && header.AlphaUnlessAllZero ? KnownOpaque(input, header, input.Position) : null;
                new RowDecoder<TPixel>(input, header, raster, strict, opaque).Run();
            }

            ApplyMetadata(input, header, raster.Metadata, options);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>
    /// For a 32-bit file whose alpha counts only if some pixel uses it: reads the stored rows ahead, on a seekable stream holding
    /// them all, and returns whether every pixel is opaque; <see langword="null"/> when that cannot be known before decoding. Only
    /// standard BGRA rows are looked at, as the decoder does; others count as having no alpha.
    /// </summary>
    private static bool? KnownOpaque(EgamiInput input, Header header, long pixels)
    {
        long stride = Stride(header);
        if (input.Length is not long length || pixels + (stride * header.Height) > length)
        {
            return null;
        }

        if (header.Masks is not (0x00FF_0000, 0x0000_FF00, 0x0000_00FF, 0xFF00_0000))
        {
            return true;
        }

        int used = header.Width * 4;
        byte[] row = ArrayPool<byte>.Shared.Rent(used);
        try
        {
            for (int y = 0; y < header.Height; y++)
            {
                if (!input.TryReadAt(pixels + (y * stride), row.AsSpan(0, used)))
                {
                    return null;
                }

                if (MemoryMarshal.Cast<byte, uint>(row.AsSpan(0, used)).IndexOfAnyInRange(0x0100_0000u, uint.MaxValue) >= 0)
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(row);
        }
    }

    /// <summary>Clears the rows not yet decoded after the data ended, or throws in strict mode.</summary>
    private static void Damaged<TPixel>(Header header, Raster<TPixel> raster, int fileRow, CorruptDataException e, bool strict)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (strict)
        {
            throw e;
        }

        for (int i = fileRow; i < header.Height; i++)
        {
            raster.Row(header.RowOf(i)).Clear();
        }

        raster.Metadata.WarningList.Add("Bmp.RowsMissing", $"{e.Message} The {header.Height - fileRow} rows not decoded are left black.", e);
    }

    /// <summary>What the headers say.</summary>
    private sealed class Header
    {
        public int HeaderSize { get; init; }

        public long DataOffset { get; init; }

        public int Width { get; set; }

        public int Height { get; set; }

        public bool TopDown { get; set; }

        public int Bits { get; set; }

        public BmpCompression Compression { get; set; }

        public (uint Red, uint Green, uint Blue, uint Alpha) Masks { get; set; }

        /// <summary>Gets or sets whether a 32-bit file's alpha byte is used only when it is not 0 in every pixel.</summary>
        public bool AlphaUnlessAllZero { get; set; }

        public Rgb8[] Palette { get; set; } = [];

        public Resolution? Resolution { get; set; }

        public uint ColorSpaceType { get; set; }

        public byte[] Calibration { get; set; } = [];

        public uint RenderingIntent { get; set; }

        public long ProfileOffset { get; set; }

        public int ProfileSize { get; set; }

        public byte[]? Profile { get; set; }

        public WarningList Warnings { get; } = new();

        public bool Embedded => Compression is BmpCompression.Jpeg or BmpCompression.Png;

        public PixelLayoutInfo Natural => Bits == 32 || (Bits == 16 && Masks.Alpha != 0) ? Bgra8.Layout : Bgr8.Layout;

        /// <summary>Returns the raster row of the <paramref name="fileRow"/>th row stored.</summary>
        public int RowOf(int fileRow)
        {
            return TopDown ? fileRow : Height - 1 - fileRow;
        }
    }

    /// <summary>One channel of a bit-field layout, scaled exactly to 8 bits.</summary>
    private readonly struct Channel
    {
        private readonly uint _mask;
        private readonly int _shift;
        private readonly ulong _max;
        private readonly byte[]? _lut;

        public Channel(uint mask)
        {
            _mask = mask;
            _shift = mask == 0 ? 0 : BitOperations.TrailingZeroCount(mask);
            int bits = BitOperations.PopCount(mask);
            _max = (1UL << bits) - 1;
            if (bits is > 0 and < 8)
            {
                _lut = new byte[1 << bits];
                for (int v = 0; v < _lut.Length; v++)
                {
                    _lut[v] = (byte)(((ulong)v * 510 + _max) / (2 * _max));
                }
            }
        }

        public bool Present => _mask != 0;

        public byte Read(uint pixel, byte absent)
        {
            if (_mask == 0)
            {
                return absent;
            }

            uint v = (pixel & _mask) >> _shift;
            return _lut is not null ? _lut[v] : _max == 255 ? (byte)v : (byte)((((ulong)v * 510) + _max) / (2 * _max));
        }
    }

    /// <summary>Decodes uncompressed rows: palette indices, 16 and 32-bit bit fields, or 24-bit BGR.</summary>
    private sealed class RowDecoder<TPixel>(EgamiInput input, Header header, Raster<TPixel> raster, bool strict, bool? opaque)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private uint[]? _table;

        public void Run()
        {
            int width = header.Width;
            int stride = checked((int)((((long)width * header.Bits) + 31) / 32 * 4));
            int used = checked((int)((((long)width * header.Bits) + 7) / 8));
            byte[] row = ArrayPool<byte>.Shared.Rent(stride);
            Bgra8[] natural = ArrayPool<Bgra8>.Shared.Rent(width);
            Channel red = new(header.Masks.Red), green = new(header.Masks.Green), blue = new(header.Masks.Blue), alpha = new(header.Masks.Alpha);
            bool standard32 = header.Bits == 32 && header.Masks is (0x00FF_0000, 0x0000_FF00, 0x0000_00FF, 0xFF00_0000 or 0);

            // 16-bit 5-6-5 and 4-4-4-4 bit fields are the memory of Bgr565 and Bgra4444: rows read straight into them.
            bool packed16 = header.Bits == 16
                && ((typeof(TPixel) == typeof(Bgr565) && header.Masks is (0xF800, 0x07E0, 0x001F, 0))
                    || (typeof(TPixel) == typeof(Bgra4444) && header.Masks is (0x0F00, 0x00F0, 0x000F, 0xF000)));
            bool anyAlpha = false;
            bool pastPalette = false;
            int fileRow = 0;
            try
            {
                for (; fileRow < header.Height; fileRow++)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    Span<TPixel> target = raster.Row(header.RowOf(fileRow));
                    if (header.Bits == 24 && typeof(TPixel) == typeof(Bgr8))
                    {
                        input.ReadExactly(MemoryMarshal.AsBytes(target));
                        input.Skip(stride - used);
                        continue;
                    }

                    if (packed16)
                    {
                        input.ReadExactly(MemoryMarshal.AsBytes(target));
                        input.Skip(stride - used);
                        continue;
                    }

                    if (standard32 && typeof(TPixel) == typeof(Bgra8))
                    {
                        Span<uint> words = MemoryMarshal.Cast<TPixel, uint>(target);
                        input.ReadExactly(MemoryMarshal.AsBytes(words));
                        if (header.Masks.Alpha == 0 || opaque == true)
                        {
                            SetOpaque(words);
                        }
                        else if (!anyAlpha)
                        {
                            anyAlpha = words.IndexOfAnyInRange(0x0100_0000u, uint.MaxValue) >= 0;
                        }

                        continue;
                    }

                    Span<byte> bytes = row.AsSpan(0, stride);
                    input.ReadExactly(bytes);
                    Span<Bgra8> pixels = natural.AsSpan(0, width);
                    switch (header.Bits)
                    {
                        case 24:
                            PixelConvert.Convert<Bgr8, TPixel>(MemoryMarshal.Cast<byte, Bgr8>(bytes[..used]), target);
                            continue;
                        case 32 when standard32:
                            MemoryMarshal.Cast<byte, Bgra8>(bytes[..used]).CopyTo(pixels);
                            if (header.Masks.Alpha == 0 || opaque == true)
                            {
                                SetOpaque(MemoryMarshal.Cast<Bgra8, uint>(pixels));
                            }
                            else if (!anyAlpha)
                            {
                                anyAlpha = MemoryMarshal.Cast<Bgra8, uint>(pixels).IndexOfAnyInRange(0x0100_0000u, uint.MaxValue) >= 0;
                            }

                            break;
                        case 32:
                            for (int x = 0; x < width; x++)
                            {
                                uint p = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(4 * x)..]);
                                pixels[x] = new Bgra8(blue.Read(p, 0), green.Read(p, 0), red.Read(p, 0), opaque == true ? (byte)255 : alpha.Read(p, 255));
                            }

                            break;
                        case 16:
                            for (int x = 0; x < width; x++)
                            {
                                uint p = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(2 * x)..]);
                                pixels[x] = new Bgra8(blue.Read(p, 0), green.Read(p, 0), red.Read(p, 0), alpha.Read(p, 255));
                            }

                            break;
                        default:
                            pastPalette |= Indices(bytes, pixels);
                            break;
                    }

                    PixelConvert.Convert<Bgra8, TPixel>(pixels, target);
                }

                if (pastPalette)
                {
                    raster.Metadata.WarningList.Add("Bmp.IndexOutOfRange", $"Some pixels use indices past the {header.Palette.Length}-color palette; they are drawn black.");
                }

                if (header.Bits == 32 && header.AlphaUnlessAllZero && opaque is null && !anyAlpha && TPixel.Layout.HasAlpha)
                {
                    MakeOpaque(natural);
                }
            }
            catch (CorruptDataException e)
            {
                Damaged(header, raster, fileRow, e, strict);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(row);
                ArrayPool<Bgra8>.Shared.Return(natural);
                if (_table is not null)
                {
                    ArrayPool<uint>.Shared.Return(_table);
                }
            }
        }

        /// <summary>Looks up 1, 2, 4 or 8-bit indices in the palette; returns whether any was past its end.</summary>
        private bool Indices(ReadOnlySpan<byte> bytes, Span<Bgra8> pixels)
        {
            ReadOnlySpan<uint> table = Table();
            Span<uint> output = MemoryMarshal.Cast<Bgra8, uint>(pixels);
            int bits = header.Bits;
            int count = header.Palette.Length;
            bool past = false;
            if (bits == 8)
            {
                for (int x = 0; x < output.Length; x++)
                {
                    output[x] = table[bytes[x]];
                }

                return count < 256 && bytes[..output.Length].IndexOfAnyInRange((byte)count, (byte)255) >= 0;
            }

            int perByte = 8 / bits;
            int mask = (1 << bits) - 1;
            int x0 = 0;
            for (int i = 0; x0 < output.Length; i++)
            {
                int b = bytes[i];
                int n = Math.Min(perByte, output.Length - x0);
                for (int k = 0; k < n; k++)
                {
                    int index = (b >> (8 - (bits * (k + 1)))) & mask;
                    past |= index >= count;
                    output[x0 + k] = table[index];
                }

                x0 += n;
            }

            return past;
        }

        /// <summary>Sets the top byte of every packed pixel to 255.</summary>
        private static void SetOpaque(Span<uint> pixels)
        {
            int x = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                Span<Vector256<uint>> vectors = MemoryMarshal.Cast<uint, Vector256<uint>>(pixels);
                Vector256<uint> alpha = Vector256.Create(0xFF00_0000u);
                foreach (ref Vector256<uint> v in vectors)
                {
                    v |= alpha;
                }

                x = vectors.Length * Vector256<uint>.Count;
            }

            for (; x < pixels.Length; x++)
            {
                pixels[x] |= 0xFF00_0000;
            }
        }

        /// <summary>Gets the palette as 256 packed BGRA values, opaque, black past its end.</summary>
        private uint[] Table()
        {
            if (_table is null)
            {
                _table = ArrayPool<uint>.Shared.Rent(256);
                for (int i = 0; i < 256; i++)
                {
                    Rgb8 c = i < header.Palette.Length ? header.Palette[i] : default;
                    _table[i] = c.B | ((uint)c.G << 8) | ((uint)c.R << 16) | 0xFF00_0000;
                }
            }

            return _table;
        }

        /// <summary>A 32-bit file whose fourth byte is 0 everywhere loads opaque.</summary>
        private void MakeOpaque(Bgra8[] buffer)
        {
            for (int y = 0; y < raster.Height; y++)
            {
                Span<Bgra8> pixels = buffer.AsSpan(0, raster.Width);
                PixelConvert.Convert<TPixel, Bgra8>(raster.Row(y), pixels);
                foreach (ref Bgra8 p in pixels)
                {
                    p.A = 255;
                }

                PixelConvert.Convert<Bgra8, TPixel>(pixels, raster.Row(y));
            }
        }
    }

    /// <summary>
    /// Decodes RLE8, RLE4 and RLE24 rows: runs, literal runs, end of line, end of bitmap and delta escapes. Pixels a delta or an
    /// early end skips are black; runs that would cross the end of a row stop there.
    /// </summary>
    private sealed class RleDecoder<TPixel>(EgamiInput input, Header header, Raster<TPixel> raster, bool strict)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly Bgr8[] _row = ArrayPool<Bgr8>.Shared.Rent(header.Width);
        private readonly ByteReader _bytes = new(input);
        private int _fileRow;
        private int _x;
        private bool _clipped;
        private bool _pastPalette;

        public void Run()
        {
            Span<Bgr8> row = _row.AsSpan(0, header.Width);
            row.Clear();
            try
            {
                while (_fileRow < header.Height)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    int count = _bytes.Next();
                    int value = _bytes.Next();
                    if (count > 0)
                    {
                        Run(count, value);
                        continue;
                    }

                    switch (value)
                    {
                        case 0:
                            NextRow();
                            break;
                        case 1:
                            while (_fileRow < header.Height)
                            {
                                NextRow();
                            }

                            break;
                        case 2:
                            int dx = _bytes.Next();
                            int dy = _bytes.Next();
                            for (int i = 0; i < dy && _fileRow < header.Height; i++)
                            {
                                int x = _x;
                                NextRow();
                                _x = x;
                            }

                            _x += dx;
                            break;
                        default:
                            Literal(value);
                            break;
                    }
                }

                if (_clipped)
                {
                    raster.Metadata.WarningList.Add("Bmp.RunPastRow", "Some runs go past the end of a row; the extra pixels were dropped.");
                }

                if (_pastPalette)
                {
                    raster.Metadata.WarningList.Add("Bmp.IndexOutOfRange", $"Some pixels use indices past the {header.Palette.Length}-color palette; they are drawn black.");
                }
            }
            catch (CorruptDataException e)
            {
                if (!strict && _fileRow < header.Height)
                {
                    // Keep the part of the row decoded before the data ended.
                    Emit();
                    _fileRow++;
                }

                Damaged(header, raster, Math.Min(_fileRow, header.Height), e, strict);
            }
            finally
            {
                _bytes.Finish();
                _bytes.Release();
                ArrayPool<Bgr8>.Shared.Return(_row);
            }
        }

        private void Run(int count, int value)
        {
            switch (header.Compression)
            {
                case BmpCompression.Rle8:
                    Bgr8 color = Color(value);
                    Fill(count, color, color);
                    break;
                case BmpCompression.Rle4:
                    Fill(count, Color(value >> 4), Color(value & 0x0F));
                    break;
                default:
                    Bgr8 bgr = new((byte)value, _bytes.Next(), _bytes.Next());
                    Fill(count, bgr, bgr);
                    break;
            }
        }

        private void Literal(int count)
        {
            int bytes;
            switch (header.Compression)
            {
                case BmpCompression.Rle8:
                    ReadOnlySpan<byte> indices = _bytes.Take(count + (count & 1));
                    for (int i = 0; i < count; i++)
                    {
                        Put(Color(indices[i]));
                    }

                    return;
                case BmpCompression.Rle4:
                    bytes = (count + 1) / 2;
                    for (int i = 0; i < bytes; i++)
                    {
                        int pair = _bytes.Next();
                        Put(Color(pair >> 4));
                        if ((2 * i) + 1 < count)
                        {
                            Put(Color(pair & 0x0F));
                        }
                    }

                    break;
                default:
                    for (int i = 0; i < count; i++)
                    {
                        byte b = _bytes.Next(), g = _bytes.Next(), r = _bytes.Next();
                        Put(new Bgr8(b, g, r));
                    }

                    bytes = 3 * count;
                    break;
            }

            if ((bytes & 1) != 0)
            {
                _bytes.Next();
            }
        }

        private Bgr8 Color(int index)
        {
            if (index < header.Palette.Length)
            {
                Rgb8 c = header.Palette[index];
                return new Bgr8(c.B, c.G, c.R);
            }

            _pastPalette = true;
            return default;
        }

        private void Fill(int count, Bgr8 even, Bgr8 odd)
        {
            if (even.Equals(odd) && _x + count <= header.Width)
            {
                _row.AsSpan(_x, count).Fill(even);
                _x += count;
                return;
            }

            for (int i = 0; i < count; i++)
            {
                Put((i & 1) == 0 ? even : odd);
            }
        }

        private void Put(Bgr8 color)
        {
            if (_x < header.Width)
            {
                _row[_x] = color;
            }
            else
            {
                _clipped = true;
            }

            _x++;
        }

        private void NextRow()
        {
            Emit();
            _row.AsSpan(0, header.Width).Clear();
            _fileRow++;
            _x = 0;
        }

        private void Emit()
        {
            PixelConvert.Convert<Bgr8, TPixel>(_row.AsSpan(0, header.Width), raster.Row(header.RowOf(_fileRow)));
        }
    }
}
