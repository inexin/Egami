namespace Egami.Video.Audio.Codecs;

/// <summary>
/// The Vorbis channel order (Vorbis I specification section 4.3.9), which Vorbis and Opus mapping family 1 use: center second for
/// three or more channels, LFE last. Decoders remap it to the canonical order.
/// </summary>
internal static class VorbisChannelOrder
{
    /// <summary>The layout of <paramref name="channels"/> Vorbis-order channels and, for each, its index in that layout.</summary>
    public static (ChannelLayout Layout, int[] Order) Layout(int channels)
    {
        if (channels > 8)
        {
            // Above eight channels the order is application-defined: they keep their indices, unpositioned.
            return (ChannelLayout.Unpositioned(channels), [.. Enumerable.Range(0, channels)]);
        }

        Speakers[] order = channels switch
        {
            1 => [Speakers.FrontCenter],
            2 => [Speakers.FrontLeft, Speakers.FrontRight],
            3 => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight],
            4 => [Speakers.FrontLeft, Speakers.FrontRight, Speakers.BackLeft, Speakers.BackRight],
            5 => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight, Speakers.BackLeft, Speakers.BackRight],
            6 => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight, Speakers.BackLeft, Speakers.BackRight, Speakers.LowFrequency],
            7 => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight, Speakers.SideLeft, Speakers.SideRight, Speakers.BackCenter, Speakers.LowFrequency],
            _ => [Speakers.FrontLeft, Speakers.FrontCenter, Speakers.FrontRight, Speakers.SideLeft, Speakers.SideRight, Speakers.BackLeft, Speakers.BackRight, Speakers.LowFrequency],
        };
        Speakers all = Speakers.None;
        foreach (Speakers s in order)
        {
            all |= s;
        }

        ChannelLayout layout = new(all);
        return (layout, [.. order.Select(layout.IndexOf)]);
    }
}
