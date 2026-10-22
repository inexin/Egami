using Egami.Video.Codecs.Av1;

namespace Egami.Video.Codecs;

/// <summary>
/// Helpers for AV1 packets, which hold a temporal unit of low-overhead OBUs (AV1 specification section 5, each OBU with its size
/// field): split a unit into its frames and merge them back, and build the av1C configuration record MP4 and Matroska store
/// for a track.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1-packets"/>
/// </example>
public static class Av1Packets
{
    /// <summary>
    /// Splits a temporal unit into its frames, as FFmpeg's av1_frame_split filter does: each part holds one frame header (or frame
    /// OBU) and its tile groups. A temporal delimiter, sequence header, metadata or padding OBU goes with the frame after it, and
    /// any after the last frame with that frame. A frame shown again with show_existing_frame is a part of its own. The parts are
    /// slices of <paramref name="temporalUnit"/>, in order, so <see cref="MergeFrames"/> gives the unit back.
    /// </summary>
    /// <param name="temporalUnit">The temporal unit.</param>
    /// <returns>The frames; one part holding everything when the unit has no frame.</returns>
    /// <exception cref="CorruptDataException">The OBUs are broken: a forbidden bit, or a size that runs past the data.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1-packets"/>
    /// </example>
    public static IReadOnlyList<ReadOnlyMemory<byte>> SplitFrames(ReadOnlyMemory<byte> temporalUnit) =>
        [.. FrameRanges(temporalUnit.Span).Select(r => temporalUnit[r.Start..r.End])];

    /// <summary>Joins frames into one temporal unit: the inverse of <see cref="SplitFrames"/>.</summary>
    /// <param name="frames">The frames, in decoding order.</param>
    /// <returns>The temporal unit.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1-packets"/>
    /// </example>
    public static byte[] MergeFrames(IEnumerable<ReadOnlyMemory<byte>> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        using MemoryStream unit = new();
        foreach (ReadOnlyMemory<byte> frame in frames)
        {
            unit.Write(frame.Span);
        }

        return unit.ToArray();
    }

    /// <summary>
    /// Builds the av1C configuration record (AV1 Codec ISO Media File Format Binding section 2.3) from the first sequence header
    /// OBU in <paramref name="obus"/>: version 1, seq_profile, seq_level_idx_0, seq_tier_0, high_bitdepth, twelve_bit,
    /// monochrome, chroma_subsampling_x and _y and chroma_sample_position from the header, no initial_presentation_delay, then the
    /// sequence header OBU itself (with its size field). This is the <see cref="TrackInfo.CodecPrivate"/> of an AV1 track in
    /// MP4, Matroska and WebM.
    /// </summary>
    /// <param name="obus">Low-overhead OBUs, such as a temporal unit that starts with a key frame, or a sequence header OBU alone.</param>
    /// <returns>The record, or <see langword="null"/> when there is no sequence header.</returns>
    /// <exception cref="CorruptDataException">The OBUs or the sequence header are broken.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1-packets"/>
    /// </example>
    public static byte[]? ExtractConfig(ReadOnlySpan<byte> obus) => Av1Config.FromObus(obus);

    /// <summary>The byte ranges of a temporal unit's frames, as <see cref="SplitFrames"/> gives them, with the OBUs of each.</summary>
    internal static List<(int Start, int End, List<Av1Obu> Obus)> FrameRanges(ReadOnlySpan<byte> unit)
    {
        List<(int Start, int End, List<Av1Obu> Obus)> frames = [];
        List<Av1Obu> current = [], prefix = [];
        int frameStart = -1, prefixStart = 0;
        foreach (Av1Obu obu in Av1Obus.Split(unit))
        {
            if (obu.StartsFrame)
            {
                if (frameStart < 0)
                {
                    // Everything before the first frame goes with it.
                    frameStart = 0;
                }
                else
                {
                    int end = prefix.Count > 0 ? prefixStart : obu.Start;
                    frames.Add((frameStart, end, current));
                    current = [];
                    frameStart = end;
                }

                current.AddRange(prefix);
                prefix.Clear();
                current.Add(obu);
            }
            else if (obu.Type is Av1Obus.TileGroup or Av1Obus.RedundantFrameHeader or Av1Obus.TileList)
            {
                // A tile group belongs to its frame, with anything before it.
                current.AddRange(prefix);
                prefix.Clear();
                current.Add(obu);
            }
            else
            {
                if (prefix.Count == 0)
                {
                    prefixStart = obu.Start;
                }

                prefix.Add(obu);
            }
        }

        current.AddRange(prefix);
        frames.Add((Math.Max(0, frameStart), unit.Length, current));
        return frames;
    }

    /// <summary>
    /// Fills an AV1 track's bit depth, chroma format, chroma siting and av1C from the first sequence header in the start of a
    /// temporal unit; the track as it was when there is none or it cannot be read.
    /// </summary>
    internal static VideoTrackInfo FillTrack(VideoTrackInfo track, ReadOnlySpan<byte> unitStart)
    {
        int at = 0;
        try
        {
            while (at < unitStart.Length)
            {
                Av1Obu obu = Av1Obus.ReadHeader(unitStart, at);
                if (obu.End > unitStart.Length)
                {
                    break;
                }

                if (obu.Type == Av1Obus.SequenceHeader)
                {
                    Av1SequenceInfo s = Av1SequenceInfo.Parse(unitStart.Slice(obu.PayloadStart, obu.PayloadLength));
                    return track with
                    {
                        BitDepth = s.BitDepth,
                        ChromaFormat = s.Chroma,
                        ChromaSiting = track.ChromaSiting ?? s.Siting,
                        CodecPrivate = track.CodecPrivate.IsEmpty ? Av1Config.Build(unitStart[obu.Start..obu.End], s) : track.CodecPrivate,
                    };
                }

                at = obu.End;
            }
        }
        catch (CorruptDataException)
        {
        }

        return track;
    }
}
