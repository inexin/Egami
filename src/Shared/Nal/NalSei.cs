namespace Egami.Nal;

/// <summary>
/// Walks the SEI messages of an SEI RBSP (H.264 7.3.2.3, H.265 7.3.5): each message's payloadType and payloadSize, coded in
/// bytes of 255 plus a last byte, then its payload. The codec reads the payloads it knows; the walk skips to the next message
/// whatever the codec read, and reads rbsp_trailing_bits after the last.
/// </summary>
internal static class NalSei
{
    /// <summary>Reads one SEI message's payload, the reader at its first bit. Reading past the payload is not checked here.</summary>
    internal delegate void PayloadReader<TState>(NalBitReader reader, int payloadType, int payloadSize, TState state);

    /// <summary>Reads every SEI message of the RBSP, handing each payload to <paramref name="payload"/>.</summary>
    /// <exception cref="CorruptDataException">A message runs past the end of its NAL unit, or the trailing bits are wrong.</exception>
    internal static void Read<TState>(NalBitReader r, TState state, PayloadReader<TState> payload)
    {
        do
        {
            int type = 0, size = 0, b;
            do
            {
                b = (int)r.U(8, "payload_type");
                type += b;
            }
            while (b == 0xFF);

            do
            {
                b = (int)r.U(8, "payload_size");
                size += b;
            }
            while (b == 0xFF);

            long end = r.BitPosition + (8L * size);
            if (size * 8L > r.BitsLeft)
            {
                throw NalErrors.Corrupt("An SEI message runs past the end of its NAL unit.");
            }

            payload(r, type, size, state);
            if (r.BitPosition > end)
            {
                throw NalErrors.Corrupt($"SEI message {type} reads past its {size} bytes.");
            }

            r.Skip(end - r.BitPosition);
        }
        while (r.MoreRbspData());

        r.TrailingBits();
    }
}
