namespace Egami.Video.Containers;

/// <summary>
/// How <see cref="Mp4Format"/> writes fragmented MP4 (ISO/IEC 14496-12 8.8, V4.3 Design Q1 and Q3): moov holds only the track
/// setup, and the media follows as moof and mdat pairs, so nothing is written back and nothing waits in a temporary file. A
/// fragment starts at the first video keyframe after <see cref="Duration"/>; audio-only files cut every <see cref="Duration"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-fragments"/>
/// </example>
public sealed record Mp4FragmentOptions
{
    /// <summary>Gets how long a fragment lasts at least, up to its next video keyframe. Default 2 seconds.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets how many bytes of media a fragment holds at most before it is closed, even between keyframes. The writer holds one
    /// fragment in memory, so this bounds its memory however long a group of pictures is. Default 32 MB.
    /// </summary>
    public long MaxBytes { get; init; } = 32 << 20;
}

/// <summary>What <see cref="Mp4Format"/> writes to a stream that cannot seek and read back (V4.3 Design Q2).</summary>
public enum Mp4NonSeekableLayout
{
    /// <summary>Fragmented MP4 with the default <see cref="Mp4FragmentOptions"/>, written as it goes, with an mp4-fragmented warning.</summary>
    Fragment,

    /// <summary>Flat MP4 with moov first: the media waits in a temporary file until the end, as before V4.3.</summary>
    TempFile,
}
