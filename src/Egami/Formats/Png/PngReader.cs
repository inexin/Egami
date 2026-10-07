using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Png;

/// <summary>
/// Reads static PNG images, written from the W3C PNG Specification (Third Edition) and RFC 1950/1951.
/// The reader streams: it holds two scanlines and writes each finished row straight into the raster.
/// </summary>
internal sealed class PngReader : IFormatReader
{
    internal static readonly PngReader Instance = new();

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private delegate void UnpackRow<TNatural>(PngUnpacker unpacker, ReadOnlySpan<byte> scanline, Span<TNatural> destination)
        where TNatural : unmanaged, IPixelType<TNatural>;

    /// <summary>Returns whether the data starts with the PNG signature.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.StartsWith(Signature);
    }

    public RasterInfo ReadInfo(EgamiInput input, ReaderOptions options)
    {
        State state = ReadUpToImageData(input, options);
        PngNaturalType natural = state.Unpacker.Natural;
        PixelLayoutInfo layout = PngUnpacker.Layout(natural);
        return new RasterInfo
        {
            Format = PngFormat.Info,
            Width = state.Header.Width,
            Height = state.Header.Height,
            Layout = layout,
            FrameCount = 1,
            HasMetadata = state.HasMetadata,
            EstimatedBytes = (long)state.Header.Width * state.Header.Height * layout.BytesPerPixel,
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        State state = ReadUpToImageData(input, options);
        return Finish(state, DecodeImage<TPixel>(state));
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        State state = ReadUpToImageData(input, options);
        Raster raster = state.Unpacker.Natural switch
        {
            PngNaturalType.Gray8 => DecodeImage<Gray8>(state),
            PngNaturalType.GrayAlpha8 => DecodeImage<GrayAlpha8>(state),
            PngNaturalType.Gray16 => DecodeImage<Gray16>(state),
            PngNaturalType.Rgb8 => DecodeImage<Rgb8>(state),
            PngNaturalType.Rgba8 => DecodeImage<Rgba8>(state),
            _ => DecodeImage<Rgba16>(state),
        };
        return Finish(state, raster);
    }

    /// <summary>Reads the signature, IHDR and every chunk before the first IDAT.</summary>
    private static State ReadUpToImageData(EgamiInput input, ReaderOptions options)
    {
        Span<byte> signature = stackalloc byte[8];
        input.ReadExactly(signature);
        if (!signature.SequenceEqual(Signature))
        {
            throw input.Corrupt(0, "The PNG signature is wrong.");
        }

        RasterMetadata metadata = new() { FormatName = PngFormat.Info.Name };
        PngChunkReader chunks = new(input, options.Recovery == RecoveryMode.Strict, metadata.Warnings);
        PngHeader header = ReadHeader(chunks);
        PngMetadata png = new() { ColorType = header.ColorType, BitDepth = header.BitDepth, Interlaced = header.Interlaced };
        State state = new(input, options, chunks, header, metadata, png);

        while (true)
        {
            if (!chunks.TryReadHeader(out uint length, out uint type))
            {
                throw input.Corrupt(input.Position, "The file ends before any image data (IDAT).");
            }

            if (type == PngChunkType.Idat)
            {
                state.FirstIdatLength = length;
                break;
            }

            ReadChunk(state, type, length);
            if (type == PngChunkType.Iend)
            {
                throw input.Corrupt(chunks.ChunkOffset, "IEND appears before any image data (IDAT).");
            }
        }

        if (header.ColorType == PngColorType.Palette && state.Palette.Length == 0)
        {
            throw input.Corrupt(chunks.ChunkOffset, "A palette image has no PLTE chunk before its image data.");
        }

        state.Unpacker = new PngUnpacker(header, state.Palette, state.Transparency);
        return state;
    }

    private static PngHeader ReadHeader(PngChunkReader chunks)
    {
        EgamiInput input = chunks.Input;
        if (!chunks.TryReadHeader(out uint length, out uint type) || type != PngChunkType.Ihdr)
        {
            throw input.Corrupt(8, "The first chunk is not IHDR.");
        }

        if (length != 13)
        {
            throw input.Corrupt(8, $"The IHDR chunk is {length} bytes long; it must be 13.");
        }

        byte[] data = chunks.ReadData(type, length)!;
        uint width = BinaryPrimitives.ReadUInt32BigEndian(data);
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        byte depth = data[8];
        PngColorType colorType = (PngColorType)data[9];
        if (width is 0 or > int.MaxValue || height is 0 or > int.MaxValue)
        {
            throw input.Corrupt(16, $"The image size {width}×{height} is invalid; both must be 1 to 2^31-1.");
        }

        if (!PngHeader.IsValid(colorType, depth))
        {
            throw input.Corrupt(24, $"Color type {(int)colorType} with bit depth {depth} is not allowed.");
        }

        if (data[10] != 0 || data[11] != 0)
        {
            throw input.Corrupt(26, $"Compression method {data[10]} or filter method {data[11]} is unknown; both must be 0.");
        }

        if (data[12] > 1)
        {
            throw input.Corrupt(28, $"Interlace method {data[12]} is unknown; it must be 0 or 1.");
        }

        return new PngHeader((int)width, (int)height, depth, colorType, data[12] == 1);
    }

    /// <summary>Handles one chunk other than IHDR and IDAT, enforcing the ordering rules (section 5.6).</summary>
    private static void ReadChunk(State state, uint type, uint length)
    {
        PngChunkReader chunks = state.Chunks;
        long offset = chunks.ChunkOffset;
        string name = PngChunkType.Name(type);

        switch (type)
        {
            case PngChunkType.Ihdr:
                throw state.Input.Corrupt(offset, "A second IHDR chunk appears.");

            case PngChunkType.Iend:
                if (length != 0)
                {
                    chunks.Recoverable(offset, $"The IEND chunk has {length} bytes of data; it must be empty.");
                }

                chunks.Skip(type, length);
                return;

            case PngChunkType.Plte:
                ReadPalette(state, length);
                return;

            case PngChunkType.Trns:
                if (state.SeenImageData || (state.Header.ColorType == PngColorType.Palette && state.Palette.Length == 0))
                {
                    OutOfOrder(state, type, length, "must come after PLTE and before IDAT");
                    return;
                }

                ReadTransparency(state, length);
                return;

            case PngChunkType.Gama or PngChunkType.Chrm or PngChunkType.Srgb or PngChunkType.Iccp or PngChunkType.Cicp:
                if (state.SeenImageData || state.Palette.Length > 0)
                {
                    OutOfOrder(state, type, length, "must come before PLTE and IDAT");
                    return;
                }

                if (!state.Seen.Add(type))
                {
                    OutOfOrder(state, type, length, "may appear only once");
                    return;
                }

                break;

            case PngChunkType.Phys:
                if (state.SeenImageData)
                {
                    OutOfOrder(state, type, length, "must come before IDAT");
                    return;
                }

                if (!state.Seen.Add(type))
                {
                    OutOfOrder(state, type, length, "may appear only once");
                    return;
                }

                break;

            default:
                if (PngChunkType.IsCritical(type))
                {
                    throw state.Input.Corrupt(offset, $"Unknown critical chunk {name}.");
                }

                break;
        }

        // Everything left is an ancillary metadata chunk.
        bool known = type is PngChunkType.Gama or PngChunkType.Chrm or PngChunkType.Srgb or PngChunkType.Iccp or PngChunkType.Cicp
            or PngChunkType.Phys or PngChunkType.Text or PngChunkType.Ztxt or PngChunkType.Itxt or PngChunkType.Exif;
        bool keep = known || PngChunkType.IsKeptAsIs(type) || PngChunkType.IsSafeToCopy(type);
        if (!state.Options.ReadMetadata || !keep)
        {
            chunks.Skip(type, length);
            return;
        }

        state.KeptBytes += length;
        state.Input.Limits.CheckMetadataBytes(state.KeptBytes);
        byte[]? data = chunks.ReadData(type, length);
        if (data is null)
        {
            return;
        }

        if (!state.SeenImageData)
        {
            state.HasMetadata = true;
        }

        try
        {
            ReadMetadataChunk(state, type, data);
        }
        catch (InvalidDataException e)
        {
            chunks.Recoverable(offset, $"The {name} chunk is malformed ({e.Message}). It was ignored.");
        }
    }

    private static void ReadMetadataChunk(State state, uint type, byte[] data)
    {
        RasterMetadata metadata = state.Metadata;
        PngMetadata png = state.Png;
        switch (type)
        {
            case PngChunkType.Gama:
                Expect(data, 4);
                png.Gamma = BinaryPrimitives.ReadUInt32BigEndian(data);
                break;
            case PngChunkType.Chrm:
                Expect(data, 32);
                png.Chromaticities = data;
                break;
            case PngChunkType.Srgb:
                Expect(data, 1);
                png.SrgbRenderingIntent = data[0];
                break;
            case PngChunkType.Cicp:
                Expect(data, 4);
                png.CodingIndependentCodePoints = data;
                break;
            case PngChunkType.Iccp:
                int nameEnd = Keyword(data);
                if (nameEnd + 1 >= data.Length || data[nameEnd + 1] != 0)
                {
                    throw new InvalidDataException("unknown compression method");
                }

                png.IccProfileName = Encoding.Latin1.GetString(data, 0, nameEnd);
                metadata.IccProfile = Inflate(state, data.AsSpan(nameEnd + 2));
                break;
            case PngChunkType.Phys:
                Expect(data, 9);
                bool meter = data[8] == 1;
                png.ResolutionHasUnit = meter;
                metadata.Resolution = new Resolution(
                    BinaryPrimitives.ReadUInt32BigEndian(data),
                    BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4)),
                    meter ? ResolutionUnit.PerMeter : ResolutionUnit.None);
                break;
            case PngChunkType.Text:
                int keyEnd = Keyword(data);
                AddText(state, new TextEntry(Encoding.Latin1.GetString(data, 0, keyEnd), Encoding.Latin1.GetString(data, keyEnd + 1, data.Length - keyEnd - 1)), "tEXt", false);
                break;
            case PngChunkType.Ztxt:
                keyEnd = Keyword(data);
                if (keyEnd + 1 >= data.Length || data[keyEnd + 1] != 0)
                {
                    throw new InvalidDataException("unknown compression method");
                }

                AddText(state, new TextEntry(Encoding.Latin1.GetString(data, 0, keyEnd), Encoding.Latin1.GetString(Inflate(state, data.AsSpan(keyEnd + 2)))), "zTXt", true);
                break;
            case PngChunkType.Itxt:
                ReadInternationalText(state, data);
                break;
            case PngChunkType.Exif:
                metadata.Exif = data;
                break;
            default:
                png.OtherChunks.Add(new PngChunk(PngChunkType.Name(type), data, state.Position));
                break;
        }
    }

    private static void ReadInternationalText(State state, byte[] data)
    {
        int keyEnd = Keyword(data);
        int i = keyEnd + 1;
        if (i + 2 > data.Length)
        {
            throw new InvalidDataException("the chunk is too short");
        }

        bool compressed = data[i] == 1;
        if (data[i] > 1 || (compressed && data[i + 1] != 0))
        {
            throw new InvalidDataException("unknown compression flag or method");
        }

        i += 2;
        int languageEnd = Array.IndexOf(data, (byte)0, i);
        if (languageEnd < 0)
        {
            throw new InvalidDataException("the language tag is not terminated");
        }

        string language = Encoding.ASCII.GetString(data, i, languageEnd - i);
        int translatedEnd = Array.IndexOf(data, (byte)0, languageEnd + 1);
        if (translatedEnd < 0)
        {
            throw new InvalidDataException("the translated keyword is not terminated");
        }

        ReadOnlySpan<byte> text = data.AsSpan(translatedEnd + 1);
        string value = Encoding.UTF8.GetString(compressed ? Inflate(state, text) : text);
        AddText(state, new TextEntry(Encoding.Latin1.GetString(data, 0, keyEnd), value, language.Length > 0 ? language : null), "iTXt", compressed);
    }

    private static void AddText(State state, TextEntry entry, string chunkType, bool compressed)
    {
        state.Metadata.Text.Add(entry);
        state.Png.TextSources.Add(new PngTextSource(chunkType, compressed));
    }

    /// <summary>Returns the length of the null-terminated keyword at the start of a chunk: 1 to 79 bytes (section 11.3.3).</summary>
    private static int Keyword(byte[] data)
    {
        int end = Array.IndexOf(data, (byte)0);
        return end is >= 1 and <= 79 ? end : throw new InvalidDataException("the keyword must be 1 to 79 bytes followed by a zero byte");
    }

    private static void Expect(byte[] data, int length)
    {
        if (data.Length != length)
        {
            throw new InvalidDataException($"it is {data.Length} bytes long instead of {length}");
        }
    }

    /// <summary>Inflates compressed metadata into a buffer capped at <see cref="SafetyLimits.MaxMetadataBytes"/>.</summary>
    private static byte[] Inflate(State state, ReadOnlySpan<byte> compressed)
    {
        long cap = state.Input.Limits.MaxMetadataBytes - state.KeptBytes;
        using ZLibStream zlib = new(new MemoryStream(compressed.ToArray(), writable: false), CompressionMode.Decompress);
        using MemoryStream output = new();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            int n;
            while ((n = zlib.Read(buffer)) > 0)
            {
                output.Write(buffer, 0, n);
                if (output.Length > cap)
                {
                    state.Input.Limits.CheckMetadataBytes(state.KeptBytes + output.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        state.KeptBytes += output.Length;
        return output.ToArray();
    }

    private static void ReadPalette(State state, uint length)
    {
        PngChunkReader chunks = state.Chunks;
        long offset = chunks.ChunkOffset;
        PngColorType colorType = state.Header.ColorType;
        if (state.SeenImageData)
        {
            throw state.Input.Corrupt(offset, "PLTE appears after the image data.");
        }

        if (state.Palette.Length > 0 || state.SeenSuggestedPalette)
        {
            throw state.Input.Corrupt(offset, "A second PLTE chunk appears.");
        }

        if (colorType is PngColorType.Gray or PngColorType.GrayAlpha)
        {
            throw state.Input.Corrupt(offset, "Gray images must not have a PLTE chunk.");
        }

        if (length == 0 || length > 768 || length % 3 != 0)
        {
            throw state.Input.Corrupt(offset, $"The PLTE chunk is {length} bytes long; it must be a multiple of 3 from 3 to 768.");
        }

        byte[] data = chunks.ReadData(PngChunkType.Plte, length)!;
        int count = (int)length / 3;
        if (colorType != PngColorType.Palette)
        {
            // A suggested palette for an RGB image: kept, not used for decoding.
            state.SeenSuggestedPalette = true;
            state.Png.Palette = ToPalette(data, count);
            return;
        }

        if (count > 1 << state.Header.BitDepth)
        {
            chunks.Recoverable(offset, $"The palette has {count} entries but bit depth {state.Header.BitDepth} can index only {1 << state.Header.BitDepth}.");
        }

        state.Palette = ToPalette(data, count);
        state.Png.Palette = state.Palette;
        state.Position = PngChunkPosition.BeforeImageData;
    }

    private static Rgba8[] ToPalette(byte[] data, int count)
    {
        Rgba8[] palette = new Rgba8[count];
        for (int i = 0; i < count; i++)
        {
            palette[i] = new Rgba8(data[i * 3], data[(i * 3) + 1], data[(i * 3) + 2], 255);
        }

        return palette;
    }

    private static void ReadTransparency(State state, uint length)
    {
        PngChunkReader chunks = state.Chunks;
        long offset = chunks.ChunkOffset;
        if (!state.Seen.Add(PngChunkType.Trns))
        {
            OutOfOrder(state, PngChunkType.Trns, length, "may appear only once");
            return;
        }

        PngColorType colorType = state.Header.ColorType;
        int expected = colorType switch
        {
            PngColorType.Gray => 2,
            PngColorType.Rgb => 6,
            PngColorType.Palette => state.Palette.Length,
            _ => -1,
        };

        bool valid = colorType == PngColorType.Palette ? length <= expected : length == expected;
        if (!valid)
        {
            chunks.Recoverable(offset, expected < 0
                ? $"Color type {(int)colorType} already has alpha and must not have a tRNS chunk. It was ignored."
                : $"The tRNS chunk is {length} bytes long, which does not fit color type {(int)colorType}. It was ignored.");
            chunks.Skip(PngChunkType.Trns, length);
            return;
        }

        byte[]? data = chunks.ReadData(PngChunkType.Trns, length);
        if (data is null)
        {
            return;
        }

        state.Png.Transparency = data;
        if (colorType == PngColorType.Palette)
        {
            for (int i = 0; i < data.Length; i++)
            {
                state.Palette[i].A = data[i];
            }
        }
        else
        {
            state.Transparency = data;
        }
    }

    private static void OutOfOrder(State state, uint type, uint length, string rule)
    {
        state.Chunks.Recoverable(state.Chunks.ChunkOffset, $"The {PngChunkType.Name(type)} chunk {rule}. It was ignored.");
        state.Chunks.Skip(type, length);
    }

    private static Raster<TPixel> DecodeImage<TPixel>(State state)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return state.Unpacker.Natural switch
        {
            PngNaturalType.Gray8 => Decode<Gray8, TPixel>(state, static (u, s, d) => u.Unpack(s, d)),
            PngNaturalType.GrayAlpha8 => Decode<GrayAlpha8, TPixel>(state, static (u, s, d) => u.Unpack(s, d)),
            PngNaturalType.Gray16 => Decode<Gray16, TPixel>(state, static (_, s, d) => PngUnpacker.Unpack(s, d)),
            PngNaturalType.Rgb8 => Decode<Rgb8, TPixel>(state, static (u, s, d) => u.Unpack(s, d)),
            PngNaturalType.Rgba8 => Decode<Rgba8, TPixel>(state, static (u, s, d) => u.Unpack(s, d)),
            _ => Decode<Rgba16, TPixel>(state, static (u, s, d) => u.Unpack(s, d)),
        };
    }

    /// <summary>Inflates the image data and decodes every row into a new raster.</summary>
    private static Raster<TPixel> Decode<TNatural, TPixel>(State state, UnpackRow<TNatural> unpack)
        where TNatural : unmanaged, IPixelType<TNatural>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PngHeader header = state.Header;
        EgamiInput input = state.Input;
        Raster<TPixel> raster = input.CreateRaster<TPixel>(header.Width, header.Height, header.Interlaced ? ClearMode.Zero : ClearMode.None);
        state.SeenImageData = true;
        state.Position = PngChunkPosition.AfterImageData;
        IdatStream idat = new(state.Chunks, state.FirstIdatLength);
        try
        {
            bool complete = false;
            bool checkAdler = false;
            uint adler = 1;
            if (ReadZlibHeader(state, idat))
            {
                using DeflateStream deflate = new(idat, CompressionMode.Decompress, leaveOpen: true);
                RowDecoder<TNatural, TPixel> rows = new(state, raster, deflate, unpack);
                complete = header.Interlaced ? rows.DecodeInterlaced() : rows.DecodeSequential();
                adler = rows.Adler;
                checkAdler = complete && IsEndOfImageData(state, deflate);
            }
            else
            {
                for (int y = 0; y < header.Height; y++)
                {
                    raster.Row(y).Clear();
                }
            }

            try
            {
                idat.Drain();
            }
            catch (CorruptDataException e) when (e.IsTruncation)
            {
                // Reported below.
            }

            if (idat.Truncated)
            {
                state.Chunks.Recoverable(input.Position, "The file ends inside the image data.");
            }
            else if (checkAdler && idat.Tail != adler)
            {
                state.Chunks.Recoverable(input.Position, $"The image data's Adler-32 checksum is 0x{idat.Tail:X8} but the data gives 0x{adler:X8}.");
            }

            state.Unpacker.ThrowIfBadIndices(state.Chunks, input.Position);
            state.NextChunk = idat.Ended && !idat.Truncated && idat.NextType != 0 ? (idat.NextLength, idat.NextType) : null;
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Reads and checks the two-byte zlib header (RFC 1950, section 2.2).</summary>
    /// <returns><see langword="false"/> if the image data ends first in lenient mode.</returns>
    private static bool ReadZlibHeader(State state, IdatStream idat)
    {
        Span<byte> header = stackalloc byte[2];
        int read = idat.Read(header);
        if (read == 1)
        {
            read += idat.Read(header[1..]);
        }

        if (read < 2)
        {
            state.Chunks.Recoverable(state.Input.Position, "The image data ends early, before any scanline. The image is transparent black.");
            return false;
        }

        int cmf = header[0];
        int flg = header[1];
        if ((cmf & 0x0F) != 8 || (cmf >> 4) > 7 || ((cmf << 8) | flg) % 31 != 0 || (flg & 0x20) != 0)
        {
            throw state.Input.Corrupt(state.Input.Position, $"The image data does not start with a valid zlib header (0x{cmf:X2} 0x{flg:X2}).");
        }

        return true;
    }

    /// <summary>Checks that the compressed data ends right after the last scanline.</summary>
    /// <returns><see langword="true"/> if it does, so the Adler-32 checksum can be compared.</returns>
    private static bool IsEndOfImageData(State state, DeflateStream deflate)
    {
        Span<byte> extra = stackalloc byte[1];
        try
        {
            if (deflate.Read(extra) == 0)
            {
                return true;
            }

            state.Metadata.Warnings.Add("The image data has extra bytes after the last row. They were ignored.");
        }
        catch (InvalidDataException e)
        {
            state.Chunks.Recoverable(state.Input.Position, $"The compressed image data is damaged after the last row ({e.Message}).");
        }

        return false;
    }

    /// <summary>Reads the chunks after the image data through IEND.</summary>
    private static Raster Finish(State state, Raster raster)
    {
        try
        {
            ReadTrailingChunks(state);
        }
        catch (CorruptDataException e) when (state.Options.Recovery == RecoveryMode.Lenient && e.IsTruncation)
        {
            state.Metadata.Warnings.Add("The file ends inside a chunk after the image data. That chunk was ignored.");
        }
        catch
        {
            raster.Dispose();
            throw;
        }

        CopyMetadata(state, raster.Metadata);
        return raster;
    }

    private static void ReadTrailingChunks(State state)
    {
        {
            (uint Length, uint Type)? next = state.NextChunk;
            while (true)
            {
                if (next is null)
                {
                    state.Chunks.Recoverable(state.Input.Position, "The file ends without an IEND chunk.");
                    break;
                }

                (uint length, uint type) = next.Value;
                if (type == PngChunkType.Idat)
                {
                    throw state.Input.Corrupt(state.Chunks.ChunkOffset, "IDAT chunks are not consecutive.");
                }

                ReadChunk(state, type, length);
                if (type == PngChunkType.Iend)
                {
                    break;
                }

                next = state.Chunks.TryReadHeader(out length, out type) ? (length, type) : null;
            }
        }
    }

    private static Raster<TPixel> Finish<TPixel>(State state, Raster<TPixel> raster)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Finish(state, (Raster)raster);
        return raster;
    }

    private static void CopyMetadata(State state, RasterMetadata target)
    {
        RasterMetadata source = state.Metadata;
        PngMetadata png = state.Png;
        target.FormatName = PngFormat.Info.Name;
        target.FormatDetails = png;
        target.Resolution = source.Resolution;
        target.IccProfile = source.IccProfile;
        target.Exif = source.Exif;
        foreach (TextEntry entry in source.Text)
        {
            target.Text.Add(entry);
        }

        foreach (string warning in source.Warnings)
        {
            target.Warnings.Add(warning);
        }

        // Precedence from the specification: cICP, then iCCP, then sRGB, then gAMA.
        ReadOnlySpan<byte> cicp = png.CodingIndependentCodePoints.Span;
        if (cicp.Length == 4 && cicp[0] == 1 && cicp[1] == 13)
        {
            target.ColorSpace = ColorSpaceHint.Srgb;
        }
        else if (cicp.Length == 4 && cicp[1] == 8)
        {
            target.ColorSpace = ColorSpaceHint.Linear;
        }
        else if (!source.IccProfile.IsEmpty)
        {
            target.ColorSpace = ColorSpaceHint.EmbeddedIcc;
        }
        else if (png.SrgbRenderingIntent is not null)
        {
            target.ColorSpace = ColorSpaceHint.Srgb;
        }
        else if (png.Gamma is uint gamma and > 0)
        {
            target.ColorSpace = gamma == 100_000 ? ColorSpaceHint.Linear : ColorSpaceHint.Gamma;
            target.Gamma = 100_000.0 / gamma;
        }
    }

    /// <summary>Everything the reader learns while going through the file.</summary>
    private sealed class State(EgamiInput input, ReaderOptions options, PngChunkReader chunks, PngHeader header, RasterMetadata metadata, PngMetadata png)
    {
        public EgamiInput Input { get; } = input;

        public ReaderOptions Options { get; } = options;

        public PngChunkReader Chunks { get; } = chunks;

        public PngHeader Header { get; } = header;

        public RasterMetadata Metadata { get; } = metadata;

        public PngMetadata Png { get; } = png;

        public HashSet<uint> Seen { get; } = [];

        public Rgba8[] Palette { get; set; } = [];

        public bool SeenSuggestedPalette { get; set; }

        public byte[] Transparency { get; set; } = [];

        public PngUnpacker Unpacker { get; set; } = null!;

        public uint FirstIdatLength { get; set; }

        public bool SeenImageData { get; set; }

        public bool HasMetadata { get; set; }

        public long KeptBytes { get; set; }

        public PngChunkPosition Position { get; set; } = PngChunkPosition.BeforePalette;

        public (uint Length, uint Type)? NextChunk { get; set; }
    }

    /// <summary>Reads scanlines from the inflated stream, unfilters them and writes pixels into the raster.</summary>
    private sealed class RowDecoder<TNatural, TPixel>(State state, Raster<TPixel> raster, Stream zlib, UnpackRow<TNatural> unpack)
        where TNatural : unmanaged, IPixelType<TNatural>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly PngHeader _header = state.Header;

        /// <summary>Gets the Adler-32 checksum of every byte decoded so far.</summary>
        public uint Adler { get; private set; } = 1;

        /// <returns><see langword="true"/> if every row was read; <see langword="false"/> if the data ended early in lenient mode.</returns>
        public bool DecodeSequential()
        {
            int width = _header.Width;
            int rowBytes = (int)_header.RowBytes(width);
            byte[] current = ArrayPool<byte>.Shared.Rent(rowBytes + 1);
            byte[] previous = ArrayPool<byte>.Shared.Rent(rowBytes + 1);
            TNatural[]? natural = typeof(TNatural) == typeof(TPixel) ? null : ArrayPool<TNatural>.Shared.Rent(width);
            try
            {
                previous.AsSpan(0, rowBytes + 1).Clear();
                for (int y = 0; y < _header.Height; y++)
                {
                    if (!ReadScanline(current.AsSpan(0, rowBytes + 1), previous.AsSpan(1, rowBytes), y))
                    {
                        for (int rest = y; rest < _header.Height; rest++)
                        {
                            raster.Row(rest).Clear();
                        }

                        return false;
                    }

                    ReadOnlySpan<byte> scanline = current.AsSpan(1, rowBytes);
                    Span<TPixel> row = raster.Row(y);
                    if (natural is null)
                    {
                        unpack(state.Unpacker, scanline, MemoryMarshal.Cast<TPixel, TNatural>(row));
                    }
                    else
                    {
                        unpack(state.Unpacker, scanline, natural.AsSpan(0, width));
                        PixelConvert.Convert<TNatural, TPixel>(natural.AsSpan(0, width), row);
                    }

                    (current, previous) = (previous, current);
                }

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(current);
                ArrayPool<byte>.Shared.Return(previous);
                if (natural is not null)
                {
                    ArrayPool<TNatural>.Shared.Return(natural);
                }
            }
        }

        /// <returns><see langword="true"/> if every row of every pass was read.</returns>
        public bool DecodeInterlaced()
        {
            int width = _header.Width;
            int maxRowBytes = (int)_header.RowBytes(width);
            byte[] current = ArrayPool<byte>.Shared.Rent(maxRowBytes + 1);
            byte[] previous = ArrayPool<byte>.Shared.Rent(maxRowBytes + 1);
            TNatural[] natural = ArrayPool<TNatural>.Shared.Rent(width);
            TPixel[] converted = ArrayPool<TPixel>.Shared.Rent(width);
            int line = 0;
            try
            {
                for (int pass = 0; pass < 7; pass++)
                {
                    int columns = Adam7.Columns(pass, width);
                    int rows = Adam7.Rows(pass, _header.Height);
                    if (columns == 0 || rows == 0)
                    {
                        continue;
                    }

                    int rowBytes = (int)_header.RowBytes(columns);
                    previous.AsSpan(0, rowBytes + 1).Clear();
                    int xStart = Adam7.XStart[pass];
                    int xStep = Adam7.XStep[pass];
                    for (int r = 0; r < rows; r++)
                    {
                        if (!ReadScanline(current.AsSpan(0, rowBytes + 1), previous.AsSpan(1, rowBytes), line++))
                        {
                            return false;
                        }

                        unpack(state.Unpacker, current.AsSpan(1, rowBytes), natural.AsSpan(0, columns));
                        Span<TPixel> pixels = converted.AsSpan(0, columns);
                        PixelConvert.Convert<TNatural, TPixel>(natural.AsSpan(0, columns), pixels);
                        Span<TPixel> row = raster.Row(Adam7.YStart[pass] + (r * Adam7.YStep[pass]));
                        for (int i = 0; i < columns; i++)
                        {
                            row[xStart + (i * xStep)] = pixels[i];
                        }

                        (current, previous) = (previous, current);
                    }
                }

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(current);
                ArrayPool<byte>.Shared.Return(previous);
                ArrayPool<TNatural>.Shared.Return(natural);
                ArrayPool<TPixel>.Shared.Return(converted);
            }
        }

        /// <summary>Reads one filter byte and scanline and reverses the filter.</summary>
        /// <returns><see langword="false"/> if the data ended first in lenient mode.</returns>
        private bool ReadScanline(Span<byte> line, ReadOnlySpan<byte> previous, int index)
        {
            state.Input.CancellationToken.ThrowIfCancellationRequested();
            int read = 0;
            string? problem = null;
            try
            {
                while (read < line.Length)
                {
                    int n = zlib.Read(line[read..]);
                    if (n == 0)
                    {
                        break;
                    }

                    read += n;
                }
            }
            catch (InvalidDataException e)
            {
                problem = $"The compressed image data is damaged ({e.Message}).";
            }

            if (read < line.Length)
            {
                state.Chunks.Recoverable(state.Input.Position, (problem ?? "The image data ends early.") + $" Decoded {index} of the scanlines; the rest are transparent black.");
                return false;
            }

            Adler = Adler32.Update(Adler, line);
            byte filter = line[0];
            if (filter > PngFilter.Paeth)
            {
                throw state.Input.Corrupt(state.Input.Position, $"Scanline {index} has filter type {filter}; only 0 to 4 exist.");
            }

            Span<byte> row = line[1..];
            PngFilter.Unfilter(filter, row, previous, _header.FilterStride);
            return true;
        }
    }
}
