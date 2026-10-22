namespace Egami.Video.Codecs;

/// <summary>Reads whether a compressed frame is a keyframe from the frame's own bits, for containers that do not flag it.</summary>
internal static class FrameKind
{
    /// <summary>Gets how many bytes from a frame's start <see cref="IsKeyframe"/> needs to decide for a codec.</summary>
    public static int BytesNeeded(string codecId) => codecId == CodecIds.Av1 ? 2048 : 4;

    /// <summary>
    /// Returns whether <paramref name="frame"/> is a keyframe, or <see langword="null"/> when the codec is not one this knows or
    /// the frame is too short to say. VP8: the frame tag's first bit (RFC 6386 section 9.1). VP9: frame_type in the uncompressed
    /// header, a shown existing frame counting as not a keyframe (VP9 bitstream specification section 6.2).
    /// AV1: a temporal unit whose first frame is a key frame shown now; a forward key frame, hidden and shown later with show_existing_frame, is not a point to
    /// start decoding from, and neither is the unit that shows it.
    /// </summary>
    public static bool? IsKeyframe(string codecId, ReadOnlySpan<byte> frame) => codecId switch
    {
        CodecIds.Vp8 => frame.Length >= 3 ? (frame[0] & 1) == 0 : null,
        CodecIds.Vp9 => Vp9IsKeyframe(frame),
        CodecIds.Av1 => Av1.Av1UnitInfo.StartsWithShownKeyFrame(frame),
        _ => null,
    };

    private static bool? Vp9IsKeyframe(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2)
        {
            return null;
        }

        // Bits from the top: frame_marker (2), profile_low_bit, profile_high_bit, reserved_zero when the profile is 3,
        // show_existing_frame, frame_type (0 is KEY_FRAME).
        int bits = (frame[0] << 8) | frame[1];
        if (bits >> 14 != 2)
        {
            return null;
        }

        int profile = ((bits >> 13) & 1) | (((bits >> 12) & 1) << 1);
        int at = profile == 3 ? 10 : 11;
        if (((bits >> at) & 1) != 0)
        {
            return false;
        }

        return ((bits >> (at - 1)) & 1) == 0;
    }
}
