namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// Decodes a VP9 stream packet by packet, as libvpx's vp9_dx_iface does: a packet is a superframe (several frames with an index
/// at its end) or frames laid end to end, every frame is decoded, and the packet shows at most one frame, the last one decoded when
/// that one is shown. Holds a byte or ushort decoder core for the stream's bit depth and replaces it when a keyframe changes it.
/// </summary>
internal sealed class Vp9StreamDecoder : IDisposable
{
    private readonly IPixelMemorySource _memory;
    private Vp9Decoder<byte>? _core8;
    private Vp9Decoder<ushort>? _core16;

    /// <summary>Gets the most threads a frame decodes on: its tile columns, then its loop filter rows.</summary>
    public int MaxParallelism { get; init; } = 1;

    /// <summary>Gets how many frame buffers the decoder holds (both cores, when the bit depth changed).</summary>
    internal int FrameBuffers => (_core8?.FrameBuffers ?? 0) + (_core16?.FrameBuffers ?? 0);

    /// <summary>Gets the limits each frame's size is checked against (EgamiContext.Limits).</summary>
    public SafetyLimits Limits { get; init; } = SafetyLimits.Default;

    public Vp9StreamDecoder(IPixelMemorySource memory)
    {
        _memory = memory;
    }

    /// <summary>Gets the frame the last packet shows, at 8 bits, or null.</summary>
    public Vp9Frame<byte>? Shown8 { get; private set; }

    /// <summary>Gets the frame the last packet shows, above 8 bits, or null.</summary>
    public Vp9Frame<ushort>? Shown16 { get; private set; }

    /// <summary>Gets or sets a callback that sees every decoded frame (a <see cref="Vp9Frame{T}"/> of byte or ushort) and its adapted probabilities.</summary>
    public Action<object, byte[]>? FrameDecoded { get; set; }

    /// <summary>Gets the headers of the frames the last packet decoded.</summary>
    public List<Vp9FrameHeader> Headers { get; } = [];

    /// <summary>Gets whether the decoder waits for a keyframe or intra-only frame before it shows anything.</summary>
    public bool NeedsResync => (_core8?.NeedsResync ?? true) && (_core16?.NeedsResync ?? true);

    /// <summary>
    /// Splits a packet into its frames (vp9_parse_superframe_index): when the last byte is a superframe marker 0b110mmfff matched by
    /// the byte 2 + (mm+1)(fff+1) from the end, that index gives fff+1 frame sizes of mm+1 bytes each, little-endian.
    /// </summary>
    /// <returns>
    /// The number of frames in the index, or 0 when there is none: as libvpx does, a last byte that looks like a marker without
    /// its match at the index's start is not one.
    /// </returns>
    /// <exception cref="CorruptDataException">A frame size in the index is larger than the packet.</exception>
    public static int ParseSuperframeIndex(ReadOnlySpan<byte> data, Span<int> sizes)
    {
        if (data.IsEmpty)
        {
            return 0;
        }

        byte marker = data[^1];
        if ((marker & 0xe0) != 0xc0)
        {
            return 0;
        }

        int frames = (marker & 7) + 1;
        int mag = ((marker >> 3) & 3) + 1;
        int indexSize = 2 + (mag * frames);
        if (data.Length < indexSize || data[^indexSize] != marker)
        {
            return 0;
        }

        int at = data.Length - indexSize + 1;
        for (int i = 0; i < frames; i++)
        {
            uint size = 0;
            for (int j = 0; j < mag; j++)
            {
                size |= (uint)data[at++] << (j * 8);
            }

            if (size > (uint)data.Length)
            {
                throw new CorruptDataException("VP9", at - mag, "A frame size in the superframe index runs past the packet.");
            }

            sizes[i] = (int)size;
        }

        return frames;
    }

    /// <summary>Decodes one packet. Afterwards <see cref="Shown8"/> or <see cref="Shown16"/> holds the frame it shows, if any.</summary>
    /// <exception cref="CorruptDataException">A frame cannot be decoded; the decoder then waits for a keyframe or intra-only frame.</exception>
    public void Decode(ReadOnlySpan<byte> packet)
    {
        Shown8 = null;
        Shown16 = null;
        Headers.Clear();
        if (packet.IsEmpty)
        {
            return;
        }

        Span<int> sizes = stackalloc int[8];
        int count = ParseSuperframeIndex(packet, sizes);
        bool lastShown = false;
        try
        {
            if (count > 0)
            {
                int at = 0;
                for (int i = 0; i < count; i++)
                {
                    if (sizes[i] > packet.Length - at)
                    {
                        throw new CorruptDataException("VP9", at, "A frame size in the superframe index runs past the packet.");
                    }

                    DecodeOne(packet.Slice(at, sizes[i]), out lastShown);
                    at += sizes[i];
                }
            }
            else
            {
                int at = 0;
                while (at < packet.Length)
                {
                    int used = DecodeOne(packet[at..], out lastShown);
                    at += Math.Max(used, 1);

                    // Account for encoders that pad the end of a frame with zero bytes.
                    while (at < packet.Length && packet[at] == 0)
                    {
                        at++;
                    }
                }
            }
        }
        catch
        {
            Shown8 = null;
            Shown16 = null;
            _core8?.Reset();
            _core16?.Reset();
            throw;
        }

        if (!lastShown)
        {
            Shown8 = null;
            Shown16 = null;
        }
    }

    private int DecodeOne(ReadOnlySpan<byte> frame, out bool shown)
    {
        // Pick the core for the frame's bit depth: a keyframe or intra-only frame with its own color config may change it.
        bool high = _core16 is not null && _core8 is null;
        Vp9FrameHeader peek = Vp9FrameHeader.Read(frame, slot => high ? _core16!.SlotSize(slot) : _core8?.SlotSize(slot) ?? (0, 0));
        if (peek.HasColorConfig)
        {
            if (peek.SubsamplingX == 0 && peek.SubsamplingY == 1)
            {
                throw new UnsupportedFormatException("The VP9 stream is 4:4:0 (chroma at full width and half height), which Egami.Video does not decode.");
            }

            high = peek.BitDepth > 8;
        }

        if (high)
        {
            if (_core8 is not null)
            {
                _core8.Dispose();
                _core8 = null;
            }

            _core16 ??= new Vp9Decoder<ushort>(_memory) { MaxParallelism = MaxParallelism, Limits = Limits, FrameDecoded = (f, p) => FrameDecoded?.Invoke(f, p) };
            int used = _core16.DecodeFrame(frame, out Vp9FrameResult result);
            Headers.Add(result.Header);
            shown = result.Shown;
            Shown16 = _core16.FrameToShow;
            Shown8 = null;
            return used;
        }
        else
        {
            if (_core16 is not null)
            {
                _core16.Dispose();
                _core16 = null;
            }

            _core8 ??= new Vp9Decoder<byte>(_memory) { MaxParallelism = MaxParallelism, Limits = Limits, FrameDecoded = (f, p) => FrameDecoded?.Invoke(f, p) };
            int used = _core8.DecodeFrame(frame, out Vp9FrameResult result);
            Headers.Add(result.Header);
            shown = result.Shown;
            Shown8 = _core8.FrameToShow;
            Shown16 = null;
            return used;
        }
    }

    /// <summary>Drops every reference, for a seek: decoding starts again at the next keyframe or intra-only frame.</summary>
    public void Reset()
    {
        _core8?.Reset();
        _core16?.Reset();
        Shown8 = null;
        Shown16 = null;
    }

    public void Dispose()
    {
        _core8?.Dispose();
        _core16?.Dispose();
        _core8 = null;
        _core16 = null;
    }
}
