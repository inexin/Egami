using System.Buffers;
using System.Buffers.Binary;
using System.IO.Compression;

namespace Egami.Formats.Jxl;

/// <summary>A top-level box of the container, as listed by <see cref="JxlContainer"/>.</summary>
/// <param name="Type">The four-character type.</param>
/// <param name="Offset">Where the box starts in the file.</param>
/// <param name="Size">The box's size with its header.</param>
/// <param name="ContentSize">The size of its contents.</param>
/// <param name="CompressedType">For a brob box, the type of the box it compresses; otherwise <see langword="null"/>.</param>
internal sealed record JxlBox(string Type, long Offset, long Size, long ContentSize, string? CompressedType = null);

/// <summary>A problem a lenient read recovered from, such as a broken metadata box that was dropped.</summary>
internal readonly record struct JxlProblem(string Id, string Message, long Offset);

/// <summary>
/// Walks the box format of ISO/IEC 18181-2 and supplies the codestream from its jxlc box or jxlp boxes in order, so the
/// codestream reader never sees boxes. Metadata boxes met on the way are kept (Exif, xml, jumb, jhgm, jbrd, jxli, and brob
/// ones opened with the BCL Brotli decoder); a broken one is dropped with a <see cref="JxlProblem"/>. A bare codestream (FF 0A)
/// passes straight through.
/// </summary>
internal sealed class JxlContainer : IJxlByteSource
{
    /// <summary>The 12-byte signature box that starts the container.</summary>
    internal static ReadOnlySpan<byte> Signature => [0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 13, 10, 0x87, 10];

    private readonly EgamiInput _input;
    private long _remaining;
    private bool _ended;
    private int _nextPart;
    private bool _sawJxlc;
    private bool _lastPart;
    private long _metadataBytes;

    private JxlContainer(EgamiInput input, bool container)
    {
        _input = input;
        IsContainer = container;
    }

    internal bool IsContainer { get; }

    /// <summary>Gets the boxes walked so far, in file order.</summary>
    internal List<JxlBox> Boxes { get; } = [];

    /// <summary>Gets the problems recovered from so far.</summary>
    internal List<JxlProblem> Problems { get; } = [];

    /// <summary>Gets the jxll box's level (5 or 10), or <see langword="null"/>.</summary>
    internal int? Level { get; private set; }

    /// <summary>Gets the Exif block of the first valid Exif box: the TIFF header and what follows, without the 4-byte offset.</summary>
    internal byte[]? Exif { get; private set; }

    /// <summary>Gets the first xml box's contents (XMP).</summary>
    internal byte[]? Xmp { get; private set; }

    /// <summary>Gets each jumb box's contents (JUMBF, such as C2PA manifests).</summary>
    internal List<byte[]> Jumbf { get; } = [];

    /// <summary>Gets the jbrd box's contents (JPEG reconstruction data).</summary>
    internal byte[]? JpegReconstruction { get; private set; }

    /// <summary>Gets the jhgm box's contents (a gain map bundle).</summary>
    internal byte[]? GainMap { get; private set; }

    /// <summary>Gets the jxli box's contents (the frame index).</summary>
    internal byte[]? FrameIndex { get; private set; }

    /// <summary>Opens the input, which must start with FF 0A or the signature box.</summary>
    internal static JxlContainer Open(EgamiInput input)
    {
        ReadOnlySpan<byte> head = input.Peek(12);
        if (head is [0xFF, 0x0A, ..])
        {
            JxlContainer bare = new(input, container: false) { _remaining = long.MaxValue };
            return bare;
        }

        if (!head.SequenceEqual(Signature))
        {
            throw input.Corrupt(0, "The data is neither a JPEG XL codestream nor a JPEG XL container.");
        }

        input.Skip(12);
        JxlContainer box = new(input, container: true);
        box.Boxes.Add(new JxlBox("JXL ", 0, 12, 4));
        box.ReadFileType();
        return box;
    }

    /// <summary>Gets whether the data starts like JPEG XL: a bare codestream or the container's signature box.</summary>
    internal static bool Matches(ReadOnlySpan<byte> header)
    {
        return header is [0xFF, 0x0A, ..] || (header.Length >= 12 && header[..12].SequenceEqual(Signature));
    }

    public int Read(Span<byte> destination)
    {
        if (!Advance())
        {
            return 0;
        }

        int n = _input.Read(destination[..(int)Math.Min(destination.Length, _remaining)]);
        if (n == 0)
        {
            if (_remaining != long.MaxValue)
            {
                throw _input.Corrupt(_input.Position, "The file ends inside a codestream box.");
            }

            _ended = true;
            return 0;
        }

        if (_remaining != long.MaxValue)
        {
            _remaining -= n;
        }

        return n;
    }

    public long Skip(long count)
    {
        long skipped = 0;
        Span<byte> scratch = stackalloc byte[4096];
        while (count > 0 && Advance())
        {
            if (_remaining == long.MaxValue)
            {
                // A bare codestream or a box to the end of the file: skip by reading.
                int n = _input.Read(scratch[..(int)Math.Min(count, scratch.Length)]);
                if (n == 0)
                {
                    _ended = true;
                    break;
                }

                skipped += n;
                count -= n;
                continue;
            }

            long step = Math.Min(count, _remaining);
            _input.Skip(step);
            _remaining -= step;
            skipped += step;
            count -= step;
        }

        return skipped;
    }

    /// <summary>Walks the rest of the file, skipping the codestream and reading every box after it.</summary>
    internal void Finish()
    {
        while (Skip(long.MaxValue / 2) > 0)
        {
        }

        if (IsContainer)
        {
            while (NextBox())
            {
            }
        }
    }

    /// <summary>Moves to codestream bytes; returns false at the end of the codestream.</summary>
    private bool Advance()
    {
        while (_remaining == 0 && !_ended)
        {
            if (!IsContainer || _lastPart || !NextBox())
            {
                _ended = true;
            }
        }

        return !_ended && _remaining > 0;
    }

    private void ReadFileType()
    {
        long offset = _input.Position;
        Span<byte> ftyp = stackalloc byte[20];
        if (_input.Read(ftyp) != 20 || BinaryPrimitives.ReadUInt32BigEndian(ftyp) != 20 || !ftyp[4..8].SequenceEqual("ftyp"u8)
            || !ftyp[8..12].SequenceEqual("jxl "u8) || BinaryPrimitives.ReadUInt32BigEndian(ftyp[12..]) != 0 || !ftyp[16..20].SequenceEqual("jxl "u8))
        {
            throw _input.Corrupt(offset, "The signature box is not followed by an ftyp box with brand jxl.");
        }

        Boxes.Add(new JxlBox("ftyp", offset, 20, 12));
    }

    /// <summary>Reads the next box header and handles it; returns false at the end of the file.</summary>
    private bool NextBox()
    {
        long offset = _input.Position;
        Span<byte> header = stackalloc byte[16];
        int got = _input.Read(header[..8]);
        if (got == 0)
        {
            return false;
        }

        if (got < 8 && _input.Read(header[got..8]) + got < 8)
        {
            throw _input.Corrupt(offset, "The file ends inside a box header.");
        }

        long size = BinaryPrimitives.ReadUInt32BigEndian(header);
        string type = System.Text.Encoding.Latin1.GetString(header[4..8]);
        int headerSize = 8;
        if (size == 1)
        {
            _input.ReadExactly(header[8..16]);
            ulong large = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            if (large > long.MaxValue)
            {
                throw _input.Corrupt(offset, $"The {type} box claims {large} bytes.");
            }

            size = (long)large;
            headerSize = 16;
        }

        long content;
        if (size == 0)
        {
            content = _input.Length is long length ? length - (offset + headerSize) : long.MaxValue;
        }
        else if (size < headerSize)
        {
            throw _input.Corrupt(offset, $"The {type} box is {size} bytes, smaller than its header.");
        }
        else
        {
            content = size - headerSize;
        }

        switch (type)
        {
            case "jxlc":
            case "jxlp":
                Codestream(type, offset, size, headerSize, content);
                return true;
            case "JXL ":
            case "ftyp":
                throw _input.Corrupt(offset, $"A second {type} box appears.");
        }

        if (content > _input.Limits.MaxMetadataBytes || content == long.MaxValue)
        {
            if (type is "Exif" or "xml " or "jumb" or "jhgm" or "jbrd" or "brob" or "jxli")
            {
                Problems.Add(new JxlProblem("Jxl.BoxDropped", $"The {type} box of {content} bytes is over the metadata limit; it was skipped.", offset));
            }

            Boxes.Add(new JxlBox(type, offset, size, size == 0 ? 0 : content));
            SkipContent(content);
            return true;
        }

        switch (type)
        {
            case "jxll":
            case "Exif":
            case "xml ":
            case "jumb":
            case "jhgm":
            case "jbrd":
            case "jxli":
            case "brob":
                byte[] data = ReadContent(offset, (int)content);
                Boxes.Add(new JxlBox(type, offset, size, content, type == "brob" && data.Length >= 4 ? System.Text.Encoding.Latin1.GetString(data, 0, 4) : null));
                Keep(type, data, offset);
                break;
            default:
                Boxes.Add(new JxlBox(type, offset, size, content));
                SkipContent(content);
                break;
        }

        return true;
    }

    private void Codestream(string type, long offset, long size, int headerSize, long content)
    {
        // A box to the end of the file lists its contents as 0 bytes, as libjxl does.
        Boxes.Add(new JxlBox(type, offset, size, size == 0 ? 0 : content));
        if (_lastPart || (_sawJxlc && type == "jxlc") || (_sawJxlc && type == "jxlp") || (type == "jxlc" && _nextPart > 0))
        {
            throw _input.Corrupt(offset, type == "jxlc" ? "A jxlc box appears beside another codestream box." : "A jxlp box appears after the last codestream part.");
        }

        if (type == "jxlc")
        {
            _sawJxlc = true;
            _lastPart = true;
            _remaining = content;
            return;
        }

        if (content < 4)
        {
            throw _input.Corrupt(offset, "A jxlp box is too small to hold its index.");
        }

        uint index = _input.ReadUInt32BigEndian();
        if ((index & 0x7FFFFFFF) != _nextPart)
        {
            throw _input.Corrupt(offset, $"The jxlp box with index {index & 0x7FFFFFFF} comes where part {_nextPart} belongs.");
        }

        _nextPart++;
        _lastPart = (index & 0x80000000) != 0;
        _remaining = content == long.MaxValue ? long.MaxValue : content - 4;
        _ = headerSize;
    }

    private byte[] ReadContent(long offset, int length)
    {
        _metadataBytes += length;
        _input.CheckMetadataBytes(_metadataBytes);
        byte[] data = new byte[length];
        _input.ReadExactly(data);
        _ = offset;
        return data;
    }

    private void SkipContent(long content)
    {
        if (content == long.MaxValue)
        {
            Span<byte> scratch = stackalloc byte[4096];
            while (_input.Read(scratch) > 0)
            {
            }

            return;
        }

        _input.Skip(content);
    }

    private void Keep(string type, byte[] data, long offset)
    {
        switch (type)
        {
            case "jxll":
                if (data.Length == 1 && data[0] is 5 or 10)
                {
                    Level ??= data[0];
                }
                else
                {
                    Problems.Add(new JxlProblem("Jxl.LevelInvalid", $"The jxll box holds {(data.Length == 1 ? $"level {data[0]}" : $"{data.Length} bytes")}; it is ignored.", offset));
                }

                break;
            case "brob":
                if (data.Length < 4)
                {
                    Problems.Add(new JxlProblem("Jxl.BrobDropped", "A brob box is too small to name the box it compresses; it was dropped.", offset));
                    break;
                }

                string inner = System.Text.Encoding.Latin1.GetString(data, 0, 4);
                if (inner is "JXL " or "ftyp" or "jxll" or "jxli" or "jxlc" or "jxlp" or "jbrd" or "brob")
                {
                    Problems.Add(new JxlProblem("Jxl.BrobDropped", $"A brob box compresses a {inner} box, which must not be compressed; it was dropped.", offset));
                    break;
                }

                if (Decompress(data.AsSpan(4), offset) is byte[] opened)
                {
                    Keep(inner, opened, offset);
                }

                break;
            case "Exif":
                uint start = data.Length >= 4 ? BinaryPrimitives.ReadUInt32BigEndian(data) : uint.MaxValue;
                if (start > (uint)Math.Max(data.Length - 4, 0) || data.Length < 4)
                {
                    Problems.Add(new JxlProblem("Jxl.ExifDropped", $"The Exif box's TIFF header offset {(data.Length < 4 ? "is missing" : $"{start} points past its {data.Length} bytes")}; it was dropped.", offset));
                }
                else
                {
                    Exif ??= data[(4 + (int)start)..];
                }

                break;
            case "xml ":
                Xmp ??= data;
                break;
            case "jumb":
                Jumbf.Add(data);
                break;
            case "jhgm":
                GainMap ??= data;
                break;
            case "jbrd":
                JpegReconstruction ??= data;
                break;
            case "jxli":
                FrameIndex ??= data;
                break;
        }
    }

    /// <summary>Opens Brotli data with the BCL decoder, at most the metadata limit; null (and a problem noted) if it is broken.</summary>
    private byte[]? Decompress(ReadOnlySpan<byte> data, long offset)
    {
        long limit = Math.Min(_input.Limits.MaxMetadataBytes, Array.MaxLength);
        byte[] output = ArrayPool<byte>.Shared.Rent((int)Math.Min(Math.Max(data.Length * 4L, 4096), limit));
        int written = 0;
        using BrotliDecoder decoder = new();
        try
        {
            while (true)
            {
                OperationStatus status = decoder.Decompress(data, output.AsSpan(written), out int consumed, out int produced);
                data = data[consumed..];
                written += produced;
                if (status == OperationStatus.Done)
                {
                    _metadataBytes += written;
                    _input.CheckMetadataBytes(_metadataBytes);
                    return output.AsSpan(0, written).ToArray();
                }

                if (status != OperationStatus.DestinationTooSmall)
                {
                    Problems.Add(new JxlProblem("Jxl.BrobDropped", status == OperationStatus.NeedMoreData ? "A brob box's Brotli data ends early; it was dropped." : "A brob box's Brotli data is invalid; it was dropped.", offset));
                    return null;
                }

                if (output.Length >= limit)
                {
                    Problems.Add(new JxlProblem("Jxl.BoxDropped", $"A brob box opens to more than the metadata limit of {_input.Limits.MaxMetadataBytes} bytes; it was dropped.", offset));
                    return null;
                }

                byte[] larger = ArrayPool<byte>.Shared.Rent((int)Math.Min((long)output.Length * 2, limit));
                output.AsSpan(0, written).CopyTo(larger);
                ArrayPool<byte>.Shared.Return(output);
                output = larger;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(output);
        }
    }
}
