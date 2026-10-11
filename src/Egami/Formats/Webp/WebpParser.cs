using System.Buffers.Binary;

using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Webp;

/// <summary>
/// Reads the RIFF container of a WebP file (RFC 9649 section 2) up to the image data: the simple form (one VP8 or VP8L chunk) or
/// the extended form (VP8X first, then ICCP, ANIM, ALPH and the image or its frames, with EXIF, XMP and unknown chunks anywhere).
/// Every chunk size is checked against the RIFF size before it is trusted. After the image data, <see cref="ReadToEnd"/> reads the
/// chunks that follow it.
/// </summary>
internal sealed class WebpParser
{
    /// <summary>The largest canvas side VP8X can hold (24 bits plus one).</summary>
    internal const int MaxCanvasSide = 1 << 24;

    private readonly EgamiInput _input;
    private readonly bool _strict;
    private readonly bool _readMetadata;
    private long _riffEnd;
    private long _metadataBytes;
    private bool _imageSeen;
    private bool _exifSeen;
    private bool _xmpSeen;
    private long _pendingSkip = -1;

    private WebpParser(EgamiInput input, ReaderOptions options)
    {
        _input = input;
        _strict = options.Recovery == RecoveryMode.Strict;
        _readMetadata = options.ReadMetadata;
        Metadata = new RasterMetadata { FormatName = WebpFormat.Info.Name, FormatDetails = Details };
    }

    /// <summary>Gets the metadata read so far.</summary>
    internal RasterMetadata Metadata { get; }

    /// <summary>Gets the format details read so far.</summary>
    internal WebpDetails Details { get; } = new();

    /// <summary>Gets the VP8X flags, or 0 for the simple form.</summary>
    internal byte Flags { get; private set; }

    /// <summary>Gets the canvas size from VP8X, or the image's size for the simple form.</summary>
    internal int CanvasWidth { get; private set; }

    /// <summary>Gets the canvas height.</summary>
    internal int CanvasHeight { get; private set; }

    /// <summary>Gets the image chunk the parser stopped at: VP8, VP8L, or ANMF for an animation's first frame.</summary>
    internal uint ImageChunk { get; private set; }

    /// <summary>Gets the image chunk's payload size.</summary>
    internal long ImageSize { get; private set; }

    /// <summary>Gets the input position of the image chunk's payload.</summary>
    internal long ImageStart { get; private set; }

    /// <summary>Gets a lossy still's ALPH payload, or <see langword="null"/>.</summary>
    internal byte[]? Alpha { get; private set; }

    /// <summary>Gets whether the file is animated (the VP8X animation flag with ANMF frames).</summary>
    internal bool Animated => ImageChunk == WebpContainer.Anmf;

    /// <summary>Gets an animation's frame count once <see cref="CountFrames"/> has run.</summary>
    internal int? FrameCount { get; private set; }

    /// <summary>Gets the image width (the canvas for animations).</summary>
    internal int Width { get; private set; }

    /// <summary>Gets the image height.</summary>
    internal int Height { get; private set; }

    /// <summary>Gets whether the natural pixel type has alpha: the VP8X flag, the VP8L hint or an ALPH chunk.</summary>
    internal bool HasAlpha { get; private set; }

    /// <summary>Gets whether the header shows metadata.</summary>
    internal bool HasMetadata { get; private set; }

    /// <summary>Returns whether a file starts with RIFF, a size and WEBP.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header.Length >= 12 && header.StartsWith("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8);
    }

    /// <summary>Reads the container up to the image data: the image chunk's header is consumed and its payload is next.</summary>
    internal static WebpParser Open(EgamiInput input, ReaderOptions options)
    {
        WebpParser parser = new(input, options);
        parser.ReadHeader();
        return parser;
    }

    private void ReadHeader()
    {
        Span<byte> riff = stackalloc byte[12];
        _input.ReadExactly(riff);
        if (!Matches(riff))
        {
            throw _input.Corrupt(0, "The WebP signature (RIFF ... WEBP) is wrong.");
        }

        uint size = BinaryPrimitives.ReadUInt32LittleEndian(riff[4..]);
        if (size < 12)
        {
            throw _input.Corrupt(4, $"The RIFF size {size} is too small for a WebP image.");
        }

        _riffEnd = 8 + (long)size;
        if (_input.Length is long length && length < _riffEnd)
        {
            Problem(4, $"The RIFF size says the file has {_riffEnd} bytes, but it has {length}.", truncation: true);
        }

        if (!NextChunk(out uint type, out long chunkSize))
        {
            throw _input.Corrupt(12, "The WebP file has no image chunk.");
        }

        if (type == WebpContainer.Vp8 || type == WebpContainer.Vp8L)
        {
            StartImage(type, chunkSize);
            CanvasWidth = Width;
            CanvasHeight = Height;
            return;
        }

        if (type != WebpContainer.Vp8X)
        {
            throw _input.Corrupt(12, $"The first chunk is \"{FourCC(type)}\", not VP8, VP8L or VP8X.");
        }

        ReadVp8X(chunkSize);
        while (true)
        {
            long at = _input.Position;
            if (!NextChunk(out type, out chunkSize))
            {
                throw _input.Corrupt(at, "The WebP file ends before its image data.");
            }

            switch (type)
            {
                case WebpContainer.Vp8:
                case WebpContainer.Vp8L:
                    if ((Flags & 0x02) != 0)
                    {
                        Problem(at, "The VP8X header says the file is animated, but it holds a still image.");
                    }

                    StartImage(type, chunkSize);
                    if (Width != CanvasWidth || Height != CanvasHeight)
                    {
                        throw _input.Corrupt(at, $"The image is {Width}×{Height} but the VP8X canvas is {CanvasWidth}×{CanvasHeight}.");
                    }

                    return;
                case WebpContainer.Alph:
                    ReadAlpha(chunkSize);
                    break;
                case WebpContainer.Anim:
                    ReadAnim(chunkSize);
                    break;
                case WebpContainer.Anmf:
                    if ((Flags & 0x02) == 0)
                    {
                        Problem(at, "The file holds ANMF frames but the VP8X header does not say it is animated.");
                    }

                    ImageChunk = WebpContainer.Anmf;
                    ImageSize = chunkSize;
                    ImageStart = _input.Position;
                    Width = CanvasWidth;
                    Height = CanvasHeight;
                    HasAlpha = (Flags & 0x10) != 0;
                    _imageSeen = true;
                    return;
                default:
                    ReadOther(type, chunkSize);
                    break;
            }
        }
    }

    /// <summary>
    /// Reads the chunks after the image data, the payload of which the decoder has consumed <paramref name="consumed"/> bytes of;
    /// the rest of it is skipped first. Data that ends early is a warning (lenient) or an error (strict).
    /// </summary>
    internal void ReadToEnd(long consumed)
    {
        // In steps an incremental decode can come back to: the rest of the image data first, then whole chunks one at a time.
        if (_pendingSkip < 0)
        {
            _pendingSkip = Math.Min(ImageSize - consumed + (ImageSize & 1), _riffEnd - _input.Position);
        }

        while (_pendingSkip > 0)
        {
            int n = _input.Peek((int)Math.Min(_pendingSkip, 64 * 1024)).Length;
            if (n == 0)
            {
                if (_input.Incremental && !_input.AtEndOfInput)
                {
                    throw new Streaming.NeedsInputException();
                }

                Problem(_input.Position, "The WebP file ends early.", truncation: true);
                _pendingSkip = 0;
                return;
            }

            _input.Skip(n);
            _pendingSkip -= n;
        }

        ReadTrailingChunks();
    }

    /// <summary>Reads the chunks from the read position to the end of the RIFF data.</summary>
    internal void ReadTrailingChunks()
    {
        while (NextChunk(out uint type, out long size, whole: true))
        {
            switch (type)
            {
                case WebpContainer.Vp8:
                case WebpContainer.Vp8L:
                case WebpContainer.Alph:
                case WebpContainer.Anmf:
                    Problem(_input.Position - 8, $"A second image chunk (\"{FourCC(type)}\") follows the image; it was ignored.");
                    SkipPayload(size);
                    break;
                default:
                    ReadOther(type, size);
                    break;
            }
        }
    }

    /// <summary>
    /// Walks the ANMF chunk headers from the read position (the first frame's payload) to count the frames, skipping their data.
    /// Only for Probe: the payloads are skipped, not decoded.
    /// </summary>
    internal void CountFrames()
    {
        int frames = 1;
        SkipPayload(ImageSize);
        while (NextChunk(out uint type, out long size))
        {
            if (type == WebpContainer.Anmf)
            {
                frames++;
                SkipPayload(size);
            }
            else
            {
                ReadOther(type, size);
            }
        }

        FrameCount = frames;
    }

    /// <summary>
    /// Reads a chunk header if the RIFF data has another chunk; returns <see langword="false"/> at its end. In an incremental decode
    /// with <paramref name="whole"/>, the header is consumed only once the whole chunk has arrived, so a decode that waits can
    /// come back to it.
    /// </summary>
    internal bool NextChunk(out uint type, out long size, bool whole = false)
    {
        type = 0;
        size = 0;
        long left = _riffEnd - _input.Position;
        if (left < 8)
        {
            if (left > 0 && _input.Peek((int)Math.Min(left, 8)).Length > 0)
            {
                Problem(_input.Position, $"{left} bytes after the last chunk are too few for another; they were ignored.");
                SkipAvailable(left);
            }

            return false;
        }

        ReadOnlySpan<byte> header = _input.Peek(8);
        if (header.Length < 8 && _input.Incremental && !_input.AtEndOfInput)
        {
            throw new Streaming.NeedsInputException();
        }

        if (header.Length < 8)
        {
            if (header.Length > 0 || !_imageSeen)
            {
                Problem(_input.Position, "The WebP file ends inside a chunk header.", truncation: true);
            }
            else
            {
                Problem(_input.Position, $"The file ends {left} bytes before the end the RIFF size gives.", truncation: true);
            }

            _input.Skip(header.Length);
            return false;
        }

        type = BinaryPrimitives.ReadUInt32LittleEndian(header);
        size = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (whole && _input.Incremental && !_input.AtEndOfInput && _input.Available < Math.Min(8 + size + (size & 1), left))
        {
            throw new Streaming.NeedsInputException();
        }

        _input.Skip(8);
        if (size + (size & 1) > left - 8)
        {
            if (size > left - 8)
            {
                Problem(_input.Position - 4, $"The \"{FourCC(type)}\" chunk's size {size} runs past the RIFF data ({left - 8} bytes left).", truncation: true);
                size = left - 8;
            }
        }

        return true;
    }

    /// <summary>Skips a chunk payload and its padding byte, stopping at the end of the data.</summary>
    internal void SkipPayload(long size)
    {
        SkipAvailable(Math.Min(size + (size & 1), _riffEnd - _input.Position));
    }

    /// <summary>Reads a whole chunk payload (and its padding byte) into a new array, for small chunks such as metadata and ALPH.</summary>
    internal byte[]? ReadPayload(long size, string what)
    {
        if (_input.Incremental && !_input.AtEndOfInput && _input.Available < size)
        {
            throw new Streaming.NeedsInputException();
        }

        byte[] data = new byte[size];
        int read = _input.Read(data);
        if (read < size)
        {
            Problem(_input.Position, $"The file ends inside the {what}.", truncation: true);
            return null;
        }

        if ((size & 1) != 0 && _riffEnd - _input.Position > 0)
        {
            SkipAvailable(1);
        }

        return data;
    }

    /// <summary>Notes a recoverable problem: a warning when lenient, <see cref="CorruptDataException"/> when strict.</summary>
    internal void Problem(long offset, string problem, bool truncation = false)
    {
        if (_strict)
        {
            throw new CorruptDataException(WebpFormat.Info.Name, offset, problem) { IsTruncation = truncation };
        }

        Metadata.WarningList.Add(problem);
    }

    internal static string FourCC(uint type)
    {
        Span<char> text = stackalloc char[4];
        for (int i = 0; i < 4; i++)
        {
            byte b = (byte)(type >> (8 * i));
            text[i] = b is >= 0x20 and < 0x7F ? (char)b : '?';
        }

        return new string(text);
    }

    private bool SkipAvailable(long count)
    {
        while (count > 0)
        {
            int n = _input.Peek((int)Math.Min(count, 64 * 1024)).Length;
            if (n == 0)
            {
                Problem(_input.Position, "The WebP file ends early.", truncation: true);
                return false;
            }

            _input.Skip(n);
            count -= n;
        }

        return true;
    }

    private void ReadVp8X(long size)
    {
        if (size < 10)
        {
            throw _input.Corrupt(_input.Position, $"The VP8X chunk has {size} bytes; it needs 10.");
        }

        Span<byte> data = stackalloc byte[10];
        _input.ReadExactly(data);
        SkipPayload(size - 10);
        Flags = data[0];
        CanvasWidth = (int)(data[4] | ((uint)data[5] << 8) | ((uint)data[6] << 16)) + 1;
        CanvasHeight = (int)(data[7] | ((uint)data[8] << 8) | ((uint)data[9] << 16)) + 1;
        if ((long)CanvasWidth * CanvasHeight > uint.MaxValue)
        {
            throw _input.Corrupt(16, $"The VP8X canvas {CanvasWidth}×{CanvasHeight} has more than 2^32 − 1 pixels.");
        }

        _input.Limits.CheckFrameSize(CanvasWidth, CanvasHeight);
        Details.Extended = true;
        Details.HasAlpha = (Flags & 0x10) != 0;
        HasMetadata = (Flags & 0x2C) != 0;
    }

    private void StartImage(uint type, long size)
    {
        ImageChunk = type;
        ImageSize = size;
        ImageStart = _input.Position;
        _imageSeen = true;
        if (type == WebpContainer.Vp8L)
        {
            ReadOnlySpan<byte> header = _input.Peek(5);
            if (header.Length < 5 || size < 5)
            {
                throw _input.Corrupt(_input.Position, "The VP8L header is cut short.");
            }

            if (header[0] != Vp8lEncoder.Signature)
            {
                throw _input.Corrupt(_input.Position, "The VP8L signature is wrong.");
            }

            uint bits = BinaryPrimitives.ReadUInt32LittleEndian(header[1..]);
            Width = (int)(bits & 0x3FFF) + 1;
            Height = (int)((bits >> 14) & 0x3FFF) + 1;
            bool hint = ((bits >> 28) & 1) != 0;
            if ((bits >> 29) != 0)
            {
                throw _input.Corrupt(_input.Position, "The VP8L version is not 0.");
            }

            HasAlpha = hint || (Flags & 0x10) != 0;
            Details.Lossless = true;
        }
        else
        {
            ReadOnlySpan<byte> header = _input.Peek(10);
            if (header.Length < 10 || size < 10)
            {
                throw _input.Corrupt(_input.Position, "The VP8 frame header is cut short.");
            }

            if ((header[0] & 1) != 0)
            {
                throw _input.Corrupt(_input.Position, "The VP8 data is not a key frame.");
            }

            if (header[3] != 0x9D || header[4] != 0x01 || header[5] != 0x2A)
            {
                throw _input.Corrupt(_input.Position + 3, "The VP8 start code is wrong.");
            }

            Width = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]) & 0x3FFF;
            Height = BinaryPrimitives.ReadUInt16LittleEndian(header[8..]) & 0x3FFF;
            if (Width == 0 || Height == 0)
            {
                throw _input.Corrupt(_input.Position + 6, $"The VP8 image size {Width}×{Height} is invalid.");
            }

            HasAlpha = Alpha is not null || (Flags & 0x10) != 0;
            Details.Lossless = false;
        }

        Details.HasAlpha |= HasAlpha;
        _input.Limits.CheckFrameSize(Width, Height);
    }

    private void ReadAlpha(long size)
    {
        if (Alpha is not null)
        {
            Problem(_input.Position - 8, "The file has a second ALPH chunk; it was ignored.");
            SkipPayload(size);
            return;
        }

        if (size < 1)
        {
            Problem(_input.Position - 8, "The ALPH chunk is empty; the image is read as opaque.");
            SkipPayload(size);
            return;
        }

        _input.CheckMemory(size);
        Alpha = ReadPayload(size, "ALPH chunk");
    }

    private void ReadAnim(long size)
    {
        if (size < 6)
        {
            Problem(_input.Position - 8, $"The ANIM chunk has {size} bytes; it needs 6. It was ignored.");
            SkipPayload(size);
            return;
        }

        Span<byte> data = stackalloc byte[6];
        _input.ReadExactly(data);
        SkipPayload(size - 6);
        Details.BackgroundColor = new Rgba8(data[2], data[1], data[0], data[3]);
        Metadata.LoopCount = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
    }

    /// <summary>Reads ICCP, EXIF and XMP into the metadata, and keeps any other chunk in <see cref="WebpDetails.OtherChunks"/>.</summary>
    private void ReadOther(uint type, long size)
    {
        long at = _input.Position - 8;
        if (!_readMetadata)
        {
            SkipPayload(size);
            return;
        }

        if (type == WebpContainer.Iccp && _imageSeen)
        {
            Problem(at, "The ICCP chunk comes after the image data; it was read anyway.");
        }

        _metadataBytes += size;
        if (_metadataBytes > _input.Limits.MaxMetadataBytes)
        {
            Metadata.WarningList.Add($"The \"{FourCC(type)}\" chunk is over the metadata limit. It was dropped.");
            SkipPayload(size);
            return;
        }

        byte[]? data = ReadPayload(size, $"\"{FourCC(type)}\" chunk");
        if (data is null)
        {
            return;
        }

        HasMetadata = true;
        switch (type)
        {
            case WebpContainer.Iccp:
                if (Metadata.IccProfile.IsEmpty)
                {
                    Metadata.IccProfile = data;
                }

                break;
            case WebpContainer.Exif:
                if (_exifSeen)
                {
                    Metadata.WarningList.Add("The file has a second EXIF chunk; it was ignored.");
                    break;
                }

                _exifSeen = true;
                ExifData exif = Jpeg.JpegSegmentReader.ParseExif(RawProfiles.WithoutExifHeader(data), _input.Limits.MaxMetadataBytes, Metadata.WarningList);
                Metadata.Exif = exif;
                Metadata.Resolution ??= ResolutionFrom(exif);
                break;
            case WebpContainer.Xmp:
                if (_xmpSeen)
                {
                    Metadata.WarningList.Add("The file has a second XMP chunk; it was ignored.");
                    break;
                }

                _xmpSeen = true;
                Metadata.Xmp = data;
                break;
            default:
                Details.OtherChunks.Add(new WebpChunk(FourCC(type), data, _imageSeen ? WebpChunkPosition.AfterImageData : WebpChunkPosition.BeforeImageData));
                break;
        }
    }

    /// <summary>Returns the resolution EXIF gives (XResolution, YResolution and ResolutionUnit), or <see langword="null"/>.</summary>
    internal static Resolution? ResolutionFrom(ExifData exif)
    {
        if (!exif.TryGet(ExifKey.XResolution, out ExifEntry? x) || !exif.TryGet(ExifKey.YResolution, out ExifEntry? y))
        {
            return null;
        }

        double rx = x.AsDouble();
        double ry = y.AsDouble();
        if (!(rx > 0) || !(ry > 0) || double.IsInfinity(rx) || double.IsInfinity(ry))
        {
            return null;
        }

        long unit = exif.TryGet(ExifKey.ResolutionUnit, out ExifEntry? u) ? u.AsInteger() : 2;
        return new Resolution(rx, ry, unit switch { 3 => ResolutionUnit.PerCentimeter, 1 => ResolutionUnit.None, _ => ResolutionUnit.PerInch });
    }
}
