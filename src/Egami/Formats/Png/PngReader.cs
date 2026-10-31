using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;

using Egami.Metadata;
using Egami.Operations.Compose;
using Egami.Pixels;

namespace Egami.Formats.Png;

/// <summary>
/// Reads static PNG images, written from the W3C PNG Specification (Third Edition) and RFC 1950/1951.
/// The reader streams: it holds two scanlines and writes each finished row straight into the raster.
/// </summary>
internal sealed partial class PngReader : IFormatReader, Streaming.IRowSourceReader, Streaming.IIncrementalFormatReader
{
    internal static readonly PngReader Instance = new();

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static ReadOnlySpan<byte> XmpKeyword => "XML:com.adobe.xmp"u8;

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
        bool animated = state.Animated;
        PixelLayoutInfo layout = animated ? (state.Header.BitDepth == 16 ? Rgba16.Layout : Rgba8.Layout) : PngUnpacker.Layout(natural);
        long frameBytes = (long)state.Header.Width * state.Header.Height * layout.BytesPerPixel;
        int frames = animated ? state.AnimationFrames : 1;
        return new RasterInfo
        {
            Format = PngFormat.Info,
            Width = state.Header.Width,
            Height = state.Header.Height,
            Layout = layout,
            FrameCount = frames,
            HasMetadata = state.HasMetadata,
            EstimatedBytes = animated ? (frameBytes * frames) + frameBytes : frameBytes,
            Orientation = state.Metadata.Exif?.Orientation ?? ImageOrientation.Normal,
            LoopCount = state.Png.Animated ? state.Plays : null,
            Access = Streams(options) ? RasterAccess.Streamed : RasterAccess.Buffered,
            WorkingBytes = Streams(options) ? WorkingBytes(state) : 0,
        };
    }

    public Raster<TPixel> Read<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        State state = ReadUpToImageData(input, options);
        if (state.Animated)
        {
            return state.Header.BitDepth == 16 ? ReadAnimation<Rgba16, TPixel>(state) : ReadAnimation<Rgba8, TPixel>(state);
        }

        return Finish(state, DecodeImage<TPixel>(state));
    }

    public Raster Read(EgamiInput input, ReaderOptions options)
    {
        State state = ReadUpToImageData(input, options);
        if (state.Animated)
        {
            return state.Header.BitDepth == 16 ? ReadAnimation<Rgba16, Rgba16>(state) : ReadAnimation<Rgba8, Rgba8>(state);
        }

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
        PngChunkReader chunks = new(input, options.Recovery == RecoveryMode.Strict, metadata.WarningList);
        PngHeader header = ReadHeader(chunks);
        PngDetails png = new() { ColorType = header.ColorType, BitDepth = header.BitDepth, Interlaced = header.Interlaced };
        State state = new(input, options, chunks, header, metadata, png);

        while (true)
        {
            if (!chunks.TryReadHeader(out uint length, out uint type))
            {
                throw new CorruptDataException(PngFormat.Info.Name, input.Position, "The file ends before any image data (IDAT).") { IsTruncation = true };
            }

            if (type == PngChunkType.Idat)
            {
                state.FirstIdatLength = length;
                break;
            }

            if (type == PngChunkType.Actl)
            {
                ReadAnimationControl(state, length);
                continue;
            }

            if (type == PngChunkType.Fctl && state.Png.Animated && state.FirstControl is null)
            {
                state.FirstControl = ReadFrameControl(state, length);
                continue;
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
        state.Png.DefaultImageIsFrame = !state.Png.Animated || state.FirstControl is not null;
        return state;
    }

    /// <summary>Reads acTL: the number of frames and plays. A frame count of 0 leaves the file a plain PNG, with a warning.</summary>
    private static void ReadAnimationControl(State state, uint length)
    {
        PngChunkReader chunks = state.Chunks;
        long offset = chunks.ChunkOffset;
        byte[]? data = length == 8 ? chunks.ReadData(PngChunkType.Actl, length) : null;
        if (data is null)
        {
            if (length != 8)
            {
                chunks.Recoverable("Png.ActlLength", offset, $"The acTL chunk is {length} bytes long; it must be 8. The file is read as a still PNG.");
                chunks.Skip(PngChunkType.Actl, length);
            }

            return;
        }

        if (state.Png.Animated)
        {
            chunks.Recoverable("Png.ActlRepeated", offset, "A second acTL chunk appears. It was ignored.");
            return;
        }

        uint frames = BinaryPrimitives.ReadUInt32BigEndian(data);
        uint plays = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        if (frames == 0 || frames > int.MaxValue || plays > int.MaxValue)
        {
            state.Metadata.WarningList.Add("Png.ActlFrameCount", $"The acTL chunk says {frames} frames; the file is read as a still PNG.");
            return;
        }

        state.Input.Limits.CheckFrameCount(frames);
        state.Png.Animated = true;
        state.AnimationFrames = (int)frames;
        state.Plays = (int)plays;
    }

    /// <summary>Reads fcTL, checking its sequence number and that the frame lies inside the image.</summary>
    private static FrameControl? ReadFrameControl(State state, uint length)
    {
        PngChunkReader chunks = state.Chunks;
        long offset = chunks.ChunkOffset;
        if (length != 26)
        {
            throw state.Input.Corrupt(offset, $"The fcTL chunk is {length} bytes long; it must be 26.");
        }

        byte[]? data = chunks.ReadData(PngChunkType.Fctl, length);
        if (data is null || !state.CheckSequence(BinaryPrimitives.ReadUInt32BigEndian(data)))
        {
            return null;
        }

        uint width = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(8));
        uint x = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(12));
        uint y = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16));
        if (width == 0 || height == 0 || (ulong)x + width > (ulong)state.Header.Width || (ulong)y + height > (ulong)state.Header.Height)
        {
            throw state.Input.Corrupt(offset, $"The frame ({x}, {y}, {width}×{height}) does not fit inside the {state.Header.Width}×{state.Header.Height} image.");
        }

        int numerator = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(20));
        int denominator = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(22));
        if (data[24] > 2 || data[25] > 1)
        {
            chunks.Recoverable("Png.FctlUnknownOps", offset, $"The fcTL chunk has disposal {data[24]} and blend {data[25]}; unknown values are read as 0.");
        }

        FrameDisposal disposal = data[24] switch
        {
            1 => FrameDisposal.ClearToBackground,
            2 => FrameDisposal.RestorePrevious,
            _ => FrameDisposal.None,
        };
        return new FrameControl(
            new Rect((int)x, (int)y, (int)width, (int)height),
            TimeSpan.FromTicks((long)Math.Round(numerator * (double)TimeSpan.TicksPerSecond / (denominator == 0 ? 100 : denominator))),
            disposal,
            data[25] == 1 ? FrameBlend.Over : FrameBlend.Source);
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
                    chunks.Recoverable("Png.IendData", offset, $"The IEND chunk has {length} bytes of data; it must be empty.");
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

            case PngChunkType.Actl or PngChunkType.Fctl or PngChunkType.Fdat:
                if (type == PngChunkType.Actl && state.SeenImageData)
                {
                    chunks.Recoverable("Png.ActlAfterData", offset, "The acTL chunk comes after the image data; the file is read as a still PNG.");
                }

                chunks.Skip(type, length);
                return;

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
            chunks.Recoverable("Png.ChunkMalformed", offset, $"The {name} chunk is malformed ({e.Message}). It was ignored.");
        }
    }

    private static void ReadMetadataChunk(State state, uint type, byte[] data)
    {
        RasterMetadata metadata = state.Metadata;
        PngDetails png = state.Png;
        switch (type)
        {
            case PngChunkType.Gama:
                Expect(data, 4);
                png.FileGamma = BinaryPrimitives.ReadUInt32BigEndian(data);
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
                if (state.ExifFromRawProfile is string kind)
                {
                    metadata.WarningList.Add("Png.ExifDuplicate", $"The file has both an eXIf chunk and a \"Raw profile type {kind}\" chunk; the eXIf chunk is used.");
                    state.ExifFromRawProfile = null;
                }

                metadata.Exif = Jpeg.JpegSegmentReader.ParseExif(data, state.Input.Limits.MaxMetadataBytes, metadata.WarningList);
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
        byte[] bytes = compressed ? Inflate(state, text) : text.ToArray();

        // The XMP packet (XMP specification part 3) is metadata, not text; a second packet stays text, as 1.0 kept it.
        if (data.AsSpan(0, keyEnd).SequenceEqual(XmpKeyword) && (state.Metadata.Xmp.IsEmpty || state.XmpFromRawProfile))
        {
            if (state.XmpFromRawProfile)
            {
                state.Metadata.WarningList.Add("Png.XmpDuplicate", "The file has both an XMP iTXt chunk and a \"Raw profile type xmp\" chunk; the iTXt chunk is used.");
                state.XmpFromRawProfile = false;
            }

            state.Metadata.Xmp = bytes;
            return;
        }

        string value = Encoding.UTF8.GetString(bytes);
        AddText(state, new TextEntry(Encoding.Latin1.GetString(data, 0, keyEnd), value, language.Length > 0 ? language : null), "iTXt", compressed);
    }

    // A legacy profile fills a block the file has not given yet; the real chunk wins whichever comes first (see the eXIf and
    // iTXt cases).
    private static void ApplyRawProfile(State state, string kind, byte[] profile)
    {
        RasterMetadata metadata = state.Metadata;
        switch (kind)
        {
            case "exif" or "APP1" when metadata.Exif is null:
                metadata.Exif = Jpeg.JpegSegmentReader.ParseExif(RawProfiles.WithoutExifHeader(profile), state.Input.Limits.MaxMetadataBytes, metadata.WarningList);
                state.ExifFromRawProfile = kind;
                break;
            case "exif" or "APP1":
                metadata.WarningList.Add("Png.ExifDuplicate", $"The file has both EXIF and a \"Raw profile type {kind}\" chunk; the first EXIF is used.");
                break;
            case "iptc" when !metadata.HasIptc:
                metadata.SetIptcSource(RawProfiles.Iim(profile), state.Input.Limits.MaxMetadataBytes);
                break;
            case "xmp" when metadata.Xmp.IsEmpty:
                metadata.Xmp = profile;
                state.XmpFromRawProfile = true;
                break;
            case "xmp":
                metadata.WarningList.Add("Png.XmpDuplicate", "The file has both an XMP iTXt chunk and a \"Raw profile type xmp\" chunk; the iTXt chunk is used.");
                break;
        }
    }

    private static void AddText(State state, TextEntry entry, string chunkType, bool compressed)
    {
        // Legacy "Raw profile type" chunks become metadata blocks; a malformed one stays text, as 1.0 kept it.
        if (RawProfiles.Kind(entry.Key) is string kind && !state.RawProfiles.ContainsKey(kind))
        {
            if (RawProfiles.Decode(entry.Value, state.Input.Limits.MaxMetadataBytes, out string? problem) is byte[] profile)
            {
                state.RawProfiles[kind] = profile;
                ApplyRawProfile(state, kind, profile);
                return;
            }

            state.Metadata.WarningList.Add("Png.RawProfileInvalid", $"The \"{entry.Key}\" text chunk cannot be decoded: {problem}. It is kept as text.");
        }

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
            chunks.Recoverable("Png.PaletteTooLong", offset, $"The palette has {count} entries but bit depth {state.Header.BitDepth} can index only {1 << state.Header.BitDepth}.");
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
            chunks.Recoverable("Png.TrnsInvalid", offset, expected < 0
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
        state.Chunks.Recoverable("Png.ChunkOutOfOrder", state.Chunks.ChunkOffset, $"The {PngChunkType.Name(type)} chunk {rule}. It was ignored.");
        state.Chunks.Skip(type, length);
    }

    private static Raster<TPixel> DecodeImage<TPixel>(State state)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        // 16-bit RGB and gray-alpha files decode straight into the matching 16-bit types, with no Rgba16 row in between.
        if (state.Header.BitDepth == 16 && typeof(TPixel) == typeof(Rgb16) && state.Header.ColorType == PngColorType.Rgb)
        {
            return Decode<Rgb16, TPixel>(state, static (_, s, d) => PngUnpacker.Unpack(s, d));
        }

        if (state.Header.BitDepth == 16 && typeof(TPixel) == typeof(GrayAlpha16) && state.Header.ColorType == PngColorType.GrayAlpha)
        {
            return Decode<GrayAlpha16, TPixel>(state, static (_, s, d) => PngUnpacker.Unpack(s, d));
        }

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
            DecodeStream(state, raster, idat, unpack);
            state.NextChunk = idat.Ended && !idat.Truncated && idat.NextType != 0 ? (idat.NextLength, idat.NextType) : null;
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Inflates one image's data from <paramref name="idat"/> into <paramref name="raster"/>, which is the image's size.</summary>
    private static void DecodeStream<TNatural, TPixel>(State state, Raster<TPixel> raster, IdatStream idat, UnpackRow<TNatural> unpack)
        where TNatural : unmanaged, IPixelType<TNatural>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PngHeader header = state.Header;
        EgamiInput input = state.Input;
        {
            bool complete = false;
            bool checkAdler = false;
            uint adler = 1;
            if (ReadZlibHeader(state, idat))
            {
                using DeflateStream deflate = new(idat, CompressionMode.Decompress, leaveOpen: true);

                // The worker owns the input until disposed, which waits for it: before the IDAT run is drained below.
                long imageBytes = (header.RowBytes(header.Width) + 1) * header.Height;
                using ReadAheadInflater? ahead = ReadAheadInflater.Worth(input, imageBytes) ? new(deflate, input.CancellationToken) : null;
                using InflatedReader inflated = new((Stream?)ahead ?? deflate, (int)header.RowBytes(header.Width) + 1);
                RowDecoder<TNatural, TPixel> rows = new(state, raster, inflated, unpack);
                complete = header.Interlaced ? rows.DecodeInterlaced() : rows.DecodeSequential();
                adler = rows.Adler;
                checkAdler = complete && IsEndOfImageData(state, inflated);
                ahead?.Dispose();
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
                state.Chunks.Recoverable("Png.DataTruncated", input.Position, "The file ends inside the image data.");
            }
            else if (checkAdler && idat.Tail != adler)
            {
                state.Chunks.Recoverable("Png.AdlerMismatch", input.Position, $"The image data's Adler-32 checksum is 0x{idat.Tail:X8} but the data gives 0x{adler:X8}.");
            }

            state.Unpacker.ThrowIfBadIndices(state.Chunks, input.Position);
        }
    }

    /// <summary>
    /// Reads an APNG: every frame is decoded at its own size through the same inflate, unfilter and unpack path as IDAT, drawn on
    /// the shared canvas with its blend and disposal, and copied out at full size. The default image is frame 0 when an fcTL came
    /// before it; otherwise it is a fallback for old viewers and is skipped.
    /// </summary>
    private static Raster<TPixel> ReadAnimation<TCanvas, TPixel>(State state)
        where TCanvas : unmanaged, IPixelType<TCanvas>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        EgamiInput input = state.Input;
        PngChunkReader chunks = state.Chunks;
        PngHeader header = state.Header;
        Range range = state.Options.Frames;
        bool fromEnd = range.Start.IsFromEnd || range.End.IsFromEnd;
        int first = fromEnd ? 0 : range.Start.Value;
        int end = fromEnd ? int.MaxValue : range.End.Value;
        List<Frame<TPixel>> frames = [];
        using AnimationCanvas<TCanvas> canvas = new(header.Width, header.Height, input.Memory);
        int index = 0;
        bool stopped = false;
        try
        {
            state.SeenImageData = true;
            state.Position = PngChunkPosition.AfterImageData;
            IdatStream idat = new(chunks, state.FirstIdatLength);
            if (state.FirstControl is FrameControl control)
            {
                if (control.Bounds != new Rect(0, 0, header.Width, header.Height))
                {
                    throw input.Corrupt(chunks.ChunkOffset, "The first frame is the default image but its fcTL does not cover the whole image.");
                }

                stopped = !DrawFrame(state, canvas, control, idat, index++, first, end, frames);
            }
            else
            {
                idat.Drain();
            }

            (uint Length, uint Type)? next = idat.Ended && !idat.Truncated && idat.NextType != 0 ? (idat.NextLength, idat.NextType) : null;
            while (!stopped && index < end)
            {
                if (next is null)
                {
                    chunks.Recoverable("Png.IendMissing", input.Position, "The file ends before IEND.");
                    break;
                }

                (uint length, uint type) = next.Value;
                if (type == PngChunkType.Iend)
                {
                    ReadChunk(state, type, length);
                    break;
                }

                if (type == PngChunkType.Fctl)
                {
                    FrameControl? frame = ReadFrameControl(state, length);
                    if (frame is null || !chunks.TryReadHeader(out length, out type))
                    {
                        break;
                    }

                    if (type != PngChunkType.Fdat)
                    {
                        chunks.Recoverable("Png.FdatMissing", chunks.ChunkOffset, "An fcTL chunk is not followed by fdAT; the animation ends there.");
                        break;
                    }

                    if (index >= state.AnimationFrames)
                    {
                        chunks.Recoverable("Png.FramesExtra", chunks.ChunkOffset, $"There are more frames than the {state.AnimationFrames} acTL gives; the rest were ignored.");
                        break;
                    }

                    IdatStream fdat = new(chunks, length, PngChunkType.Fdat, state.CheckSequence);
                    stopped = !DrawFrame(state, canvas, frame.Value, fdat, index++, first, end, frames);
                    next = fdat.Ended && !fdat.Truncated && fdat.NextType != 0 ? (fdat.NextLength, fdat.NextType) : null;
                    continue;
                }

                if (type is PngChunkType.Idat or PngChunkType.Fdat)
                {
                    chunks.Recoverable("Png.ChunkOutOfPlace", chunks.ChunkOffset, $"A {PngChunkType.Name(type)} chunk is out of place. It was ignored.");
                    chunks.Skip(type, length);
                }
                else
                {
                    ReadChunk(state, type, length);
                }

                next = chunks.TryReadHeader(out length, out type) ? (length, type) : null;
            }

            if (!stopped && index < state.AnimationFrames && index < end)
            {
                chunks.Recoverable("Png.FramesMissing", input.Position, $"acTL gives {state.AnimationFrames} frames but the file has {index}.");
            }
        }
        catch (CorruptDataException e) when (state.Options.Recovery == RecoveryMode.Lenient && e.IsTruncation && frames.Count > 0)
        {
            state.Metadata.WarningList.Add("Png.AnimationTruncated", "The file ends inside the animation; the frames before that are kept.");
        }
        catch
        {
            foreach (Frame<TPixel> frame in frames)
            {
                frame.Plane.Dispose();
            }

            throw;
        }

        if (fromEnd)
        {
            (int offset, int count) = AnimationCanvasRange.Clamp(range, frames.Count);
            for (int i = 0; i < frames.Count; i++)
            {
                if (i < offset || i >= offset + count)
                {
                    frames[i].Plane.Dispose();
                }
            }

            frames = frames.GetRange(offset, count);
        }

        if (frames.Count == 0)
        {
            throw input.Corrupt(input.Position, "No frame of the animation is in the requested range or could be read.");
        }

        RasterMetadata metadata = new() { LoopCount = state.Plays };
        CopyMetadata(state, metadata);
        return new Raster<TPixel>(frames, metadata, input.Context);
    }

    /// <summary>Decodes one frame, draws it on the canvas and keeps a full-size copy when it is in the range.</summary>
    /// <returns><see langword="false"/> if the frame was cut short and the animation ends there.</returns>
    private static bool DrawFrame<TCanvas, TPixel>(State state, AnimationCanvas<TCanvas> canvas, FrameControl control, IdatStream data, int index, int first, int end, List<Frame<TPixel>> frames)
        where TCanvas : unmanaged, IPixelType<TCanvas>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (ComposeFrame(state, canvas, control, data, index) is not bool complete)
        {
            return false;
        }

        if (index >= first && index < end)
        {
            EgamiInput input = state.Input;
            input.Memory.Check((long)canvas.Width * canvas.Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
            PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(canvas.Width, canvas.Height, input.Memory);
            canvas.CopyTo(plane);
            frames.Add(new Frame<TPixel>(plane, FrameMetadataOf(control)));
        }

        canvas.End();
        return complete;
    }

    /// <summary>Returns the metadata Load gives a frame with this fcTL.</summary>
    private static FrameMetadata FrameMetadataOf(FrameControl control)
    {
        return new FrameMetadata { Delay = control.Delay, Disposal = control.Disposal, Blend = control.Blend, Bounds = control.Bounds };
    }

    /// <summary>Decodes one frame and draws it on the canvas with its blend, after <see cref="AnimationCanvas{TCanvas}.Begin"/>; the caller ends it.</summary>
    /// <returns>Whether the frame's data was complete, or <see langword="null"/> if it was out of sequence and was not drawn.</returns>
    private static bool? ComposeFrame<TCanvas>(State state, AnimationCanvas<TCanvas> canvas, FrameControl control, IdatStream data, int index)
        where TCanvas : unmanaged, IPixelType<TCanvas>
    {
        EgamiInput input = state.Input;
        input.Limits.CheckFrameCount(index + 1);
        input.CancellationToken.ThrowIfCancellationRequested();
        Rect bounds = control.Bounds;
        State frameState = state.ForFrame(state.Header with { Width = bounds.Width, Height = bounds.Height });
        using Raster<TCanvas> pixels = input.CreateRaster<TCanvas>(bounds.Width, bounds.Height, ClearMode.Zero);
        DecodeFrame(frameState, pixels, data);
        bool complete = !data.Truncated && !data.OutOfSequence;
        if (data.OutOfSequence)
        {
            state.Metadata.WarningList.Add("Png.SequenceError", $"Frame {index}'s data is out of sequence; the animation ends before it.");
            return null;
        }

        // A first frame that restores the previous canvas restores the empty one (APNG specification, fcTL).
        FrameDisposal disposal = index == 0 && control.Disposal == FrameDisposal.RestorePrevious ? FrameDisposal.ClearToBackground : control.Disposal;
        canvas.Begin(bounds, disposal);
        for (int y = 0; y < bounds.Height; y++)
        {
            Span<TCanvas> target = canvas.Row(bounds.Y + y).Slice(bounds.X, bounds.Width);
            Span<TCanvas> source = pixels.Row(y);
            if (control.Blend == FrameBlend.Source)
            {
                source.CopyTo(target);
            }
            else if (typeof(TCanvas) == typeof(Rgba8))
            {
                ComposeKernels.SourceOver8(MemoryMarshal.Cast<TCanvas, uint>(source), MemoryMarshal.Cast<TCanvas, uint>(target), 255);
            }
            else
            {
                AnimationBlend.SourceOver16(MemoryMarshal.Cast<TCanvas, Rgba16>(source), MemoryMarshal.Cast<TCanvas, Rgba16>(target));
            }
        }

        return complete;
    }

    private static void DecodeFrame<TCanvas>(State state, Raster<TCanvas> raster, IdatStream data)
        where TCanvas : unmanaged, IPixelType<TCanvas>
    {
        switch (state.Unpacker.Natural)
        {
            case PngNaturalType.Gray8:
                DecodeStream<Gray8, TCanvas>(state, raster, data, static (u, s, d) => u.Unpack(s, d));
                break;
            case PngNaturalType.GrayAlpha8:
                DecodeStream<GrayAlpha8, TCanvas>(state, raster, data, static (u, s, d) => u.Unpack(s, d));
                break;
            case PngNaturalType.Gray16:
                DecodeStream<Gray16, TCanvas>(state, raster, data, static (_, s, d) => PngUnpacker.Unpack(s, d));
                break;
            case PngNaturalType.Rgb8:
                DecodeStream<Rgb8, TCanvas>(state, raster, data, static (u, s, d) => u.Unpack(s, d));
                break;
            case PngNaturalType.Rgba8:
                DecodeStream<Rgba8, TCanvas>(state, raster, data, static (u, s, d) => u.Unpack(s, d));
                break;
            default:
                DecodeStream<Rgba16, TCanvas>(state, raster, data, static (u, s, d) => u.Unpack(s, d));
                break;
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

        return CheckZlibHeader(state, header, read);
    }

    /// <summary>Checks the two zlib header bytes, of which <paramref name="read"/> arrived (RFC 1950, section 2.2).</summary>
    /// <returns><see langword="false"/> if the image data ended first in lenient mode.</returns>
    private static bool CheckZlibHeader(State state, ReadOnlySpan<byte> header, int read)
    {
        if (read < 2)
        {
            state.Chunks.Recoverable("Png.DataEmpty", state.Input.Position, "The image data ends early, before any scanline. The image is transparent black.");
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
    private static bool IsEndOfImageData(State state, InflatedReader inflated)
    {
        if (inflated.AtEnd())
        {
            return true;
        }

        if (inflated.Problem is string problem)
        {
            state.Chunks.Recoverable("Png.DamageAfterLastRow", state.Input.Position, problem + " The damage is after the last row.");
        }
        else
        {
            state.Metadata.WarningList.Add("Png.DataAfterLastRow", "The image data has extra bytes after the last row. They were ignored.");
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
            state.Metadata.WarningList.Add("Png.ChunkTruncated", "The file ends inside a chunk after the image data. That chunk was ignored.");
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
                    state.Chunks.Recoverable("Png.IendMissing", state.Input.Position, "The file ends without an IEND chunk.");
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
        PngDetails png = state.Png;
        target.FormatName = PngFormat.Info.Name;
        target.FormatDetails = png;
        target.Resolution = source.Resolution;
        target.IccProfile = source.IccProfile;
        target.Exif = source.Exif;
        target.ShareIptc(source);
        target.Xmp = source.Xmp;
        foreach (TextEntry entry in source.Text)
        {
            target.Text.Add(entry);
        }

        target.WarningList.AddRange(source.WarningList);

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
        else if (png.FileGamma is uint gamma and > 0)
        {
            target.ColorSpace = gamma == 100_000 ? ColorSpaceHint.Linear : ColorSpaceHint.Gamma;
            target.Gamma = 100_000.0 / gamma;
        }

        ColorTags(png, target);
    }

    /// <summary>
    /// The 2.1.2 color fields: cICP into CodePoints, cHRM into Primaries, mDCV and cLLI (PNG third edition, either spelling of the
    /// last letter) into Hdr. The chunks stay in the details, so the 1.0 values and round trips are unchanged.
    /// </summary>
    private static void ColorTags(PngDetails png, RasterMetadata target)
    {
        ReadOnlySpan<byte> cicp = png.CodingIndependentCodePoints.Span;
        if (cicp.Length == 4)
        {
            target.CodePoints = new Colors.ColorCodePoints(cicp[0], cicp[1], cicp[2], cicp[3] != 0);
        }

        ReadOnlySpan<byte> chrm = png.Chromaticities.Span;
        if (chrm.Length == 32)
        {
            Colors.ColorPrimaries primaries = new(Xy(chrm[8..], 100_000), Xy(chrm[16..], 100_000), Xy(chrm[24..], 100_000), Xy(chrm, 100_000));
            target.Primaries = primaries.IsValid ? primaries : null;
        }

        Colors.HdrMetadata? hdr = null;
        foreach (PngChunk chunk in png.OtherChunks)
        {
            ReadOnlySpan<byte> d = chunk.Data.Span;
            if (string.Equals(chunk.Type, "mDCV", StringComparison.OrdinalIgnoreCase) && d.Length == 24)
            {
                Colors.ColorPrimaries display = new(Xy16(d), Xy16(d[4..]), Xy16(d[8..]), Xy16(d[12..]));
                hdr = (hdr ?? new Colors.HdrMetadata()) with
                {
                    MasteringPrimaries = display.IsValid ? display : null,
                    MasteringMaxLuminance = BinaryPrimitives.ReadUInt32BigEndian(d[16..]) / 10_000.0,
                    MasteringMinLuminance = BinaryPrimitives.ReadUInt32BigEndian(d[20..]) / 10_000.0,
                };
            }
            else if (string.Equals(chunk.Type, "cLLI", StringComparison.OrdinalIgnoreCase) && d.Length == 8)
            {
                hdr = (hdr ?? new Colors.HdrMetadata()) with
                {
                    MaxContentLightLevel = BinaryPrimitives.ReadUInt32BigEndian(d) / 10_000.0,
                    MaxFrameAverageLightLevel = BinaryPrimitives.ReadUInt32BigEndian(d[4..]) / 10_000.0,
                };
            }
        }

        target.Hdr = hdr;

        static Colors.Chromaticity Xy(ReadOnlySpan<byte> d, double scale) => new(BinaryPrimitives.ReadUInt32BigEndian(d) / scale, BinaryPrimitives.ReadUInt32BigEndian(d[4..]) / scale);

        static Colors.Chromaticity Xy16(ReadOnlySpan<byte> d) => new(BinaryPrimitives.ReadUInt16BigEndian(d) * 0.00002, BinaryPrimitives.ReadUInt16BigEndian(d[2..]) * 0.00002);
    }

    /// <summary>Everything the reader learns while going through the file.</summary>
    private sealed class State(EgamiInput input, ReaderOptions options, PngChunkReader chunks, PngHeader header, RasterMetadata metadata, PngDetails png)
    {
        public EgamiInput Input { get; } = input;

        public ReaderOptions Options { get; } = options;

        public PngChunkReader Chunks { get; } = chunks;

        public PngHeader Header { get; } = header;

        public RasterMetadata Metadata { get; } = metadata;

        public PngDetails Png { get; } = png;

        public HashSet<uint> Seen { get; } = [];

        public Rgba8[] Palette { get; set; } = [];

        public bool SeenSuggestedPalette { get; set; }

        public byte[] Transparency { get; set; } = [];

        public PngUnpacker Unpacker { get; set; } = null!;

        public uint FirstIdatLength { get; set; }

        public bool SeenImageData { get; set; }

        public bool HasMetadata { get; set; }

        public long KeptBytes { get; set; }

        /// <summary>Legacy "Raw profile type" chunks decoded, by kind (exif, APP1, iptc, xmp).</summary>
        public Dictionary<string, byte[]> RawProfiles { get; } = [];

        /// <summary>The legacy profile EXIF came from, which a later eXIf chunk replaces; null when none did.</summary>
        public string? ExifFromRawProfile { get; set; }

        /// <summary>Whether XMP came from a legacy profile, which a later iTXt packet replaces.</summary>
        public bool XmpFromRawProfile { get; set; }

        public PngChunkPosition Position { get; set; } = PngChunkPosition.BeforePalette;

        public (uint Length, uint Type)? NextChunk { get; set; }

        /// <summary>Gets whether the file is read as an animation: an APNG of more than one frame, or whose frame is not IDAT.</summary>
        public bool Animated => Png.Animated && (AnimationFrames > 1 || FirstControl is null);

        public int AnimationFrames { get; set; }

        public int Plays { get; set; }

        public FrameControl? FirstControl { get; set; }

        private uint ExpectedSequence { get; set; }

        /// <summary>Checks an fcTL or fdAT sequence number: a gap or repeat is corrupt (Strict), or ends the animation (Lenient).</summary>
        public bool CheckSequence(uint number)
        {
            if (number != ExpectedSequence)
            {
                Chunks.Recoverable("Png.SequenceError", Chunks.ChunkOffset, $"Sequence number {number} comes where {ExpectedSequence} was expected.");
                return false;
            }

            ExpectedSequence++;
            return true;
        }

        /// <summary>Returns a state for decoding one frame: the same file, palette and transparency, with the frame's size.</summary>
        public State ForFrame(PngHeader header)
        {
            return new State(Input, Options, Chunks, header, Metadata, Png)
            {
                Palette = Palette,
                Transparency = Transparency,
                Unpacker = new PngUnpacker(header, Palette, Transparency),
                KeptBytes = KeptBytes,
            };
        }
    }

    /// <summary>One frame's fcTL: where it goes, how long it shows, and how it is drawn and disposed of.</summary>
    private readonly record struct FrameControl(Rect Bounds, TimeSpan Delay, FrameDisposal Disposal, FrameBlend Blend);

    /// <summary>Reads scanlines from the inflated stream, unfilters them and writes pixels into the raster.</summary>
    private sealed class RowDecoder<TNatural, TPixel>(State state, Raster<TPixel> raster, InflatedReader inflated, UnpackRow<TNatural> unpack)
        where TNatural : unmanaged, IPixelType<TNatural>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly PngHeader _header = state.Header;

        /// <summary>Gets the Adler-32 checksum of every byte decoded so far.</summary>
        public uint Adler { get; private set; } = 1;

        /// <summary>Decodes the rows top to bottom, unfiltering each into the raster as it arrives.</summary>
        /// <returns><see langword="true"/> if every row was read; <see langword="false"/> if the data ended early in lenient mode.</returns>
        public bool DecodeSequential()
        {
            return typeof(TNatural) == typeof(TPixel) && state.Unpacker.ScanlineIsNatural ? DecodeInPlace() : DecodeThroughBuffers();
        }

        /// <summary>
        /// When the file's bytes are exactly the requested pixels, each scanline is inflated and unfiltered straight in its raster
        /// row, using the row above as the previous scanline, so no pixel byte is copied after decompression.
        /// </summary>
        private bool DecodeInPlace()
        {
            int rowBytes = (int)_header.RowBytes(_header.Width);
            byte[] zero = ArrayPool<byte>.Shared.Rent(rowBytes);
            Span<byte> filter = stackalloc byte[1];
            try
            {
                zero.AsSpan(0, rowBytes).Clear();
                for (int y = 0; y < _header.Height; y++)
                {
                    Span<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
                    ReadOnlySpan<byte> above = y == 0 ? zero.AsSpan(0, rowBytes) : MemoryMarshal.AsBytes(raster.Row(y - 1));
                    if (!ReadScanline(filter, row, above, y))
                    {
                        for (int rest = y; rest < _header.Height; rest++)
                        {
                            raster.Row(rest).Clear();
                        }

                        return false;
                    }
                }

                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(zero);
            }
        }

        private bool DecodeThroughBuffers()
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
                    if (state.Unpacker.TryUnpackPalette(scanline, row))
                    {
                        // Palette rows go straight to the target type.
                    }
                    else if (natural is null)
                    {
                        unpack(state.Unpacker, scanline, MemoryMarshal.Cast<TPixel, TNatural>(row));
                    }
                    else if (state.Unpacker.ScanlineIsNatural)
                    {
                        // The scanline already holds natural pixels: convert from it directly.
                        PixelConvert.Convert<TNatural, TPixel>(MemoryMarshal.Cast<byte, TNatural>(scanline)[..width], row);
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

        /// <summary>Decodes the seven Adam7 passes, scattering each pass row into the raster.</summary>
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

                        Span<TPixel> pixels = converted.AsSpan(0, columns);
                        if (!state.Unpacker.TryUnpackPalette(current.AsSpan(1, rowBytes), pixels))
                        {
                            unpack(state.Unpacker, current.AsSpan(1, rowBytes), natural.AsSpan(0, columns));
                            PixelConvert.Convert<TNatural, TPixel>(natural.AsSpan(0, columns), pixels);
                        }
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

        /// <summary>Reads one filter byte and scanline into <paramref name="line"/> and reverses the filter.</summary>
        /// <returns><see langword="false"/> if the data ended first in lenient mode.</returns>
        private bool ReadScanline(Span<byte> line, ReadOnlySpan<byte> previous, int index)
        {
            return ReadScanline(line[..1], line[1..], previous, index);
        }

        /// <summary>Reads the filter byte into <paramref name="filter"/> and the scanline into <paramref name="row"/>, then reverses the filter.</summary>
        /// <returns><see langword="false"/> if the data ended first in lenient mode.</returns>
        private bool ReadScanline(Span<byte> filter, Span<byte> row, ReadOnlySpan<byte> previous, int index)
        {
            state.Input.CancellationToken.ThrowIfCancellationRequested();
            int read = inflated.Read(filter);
            read += read == 1 ? inflated.Read(row) : 0;
            if (read < 1 + row.Length)
            {
                string? problem = inflated.Problem;
                state.Chunks.Recoverable("Png.RowsMissing", state.Input.Position, (problem ?? "The image data ends early.") + $" Decoded {index} of the scanlines; the rest are transparent black.");
                return false;
            }

            Adler = Adler32.Update(Adler32.Update(Adler, filter), row);
            if (filter[0] > PngFilter.Paeth)
            {
                throw state.Input.Corrupt(state.Input.Position, $"Scanline {index} has filter type {filter[0]}; only 0 to 4 exist.");
            }

            PngFilterSimd.Unfilter(filter[0], row, previous, _header.FilterStride);
            return true;
        }
    }
}
