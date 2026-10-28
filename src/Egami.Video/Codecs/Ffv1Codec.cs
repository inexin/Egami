using System.Buffers.Binary;

using Egami.Video.Codecs.Ffv1;

namespace Egami.Video.Codecs;

/// <summary>
/// FFV1, the lossless video codec archives use (RFC 9043), as Matroska and AVI carry it: versions 0, 1 and 3, Golomb-Rice and
/// the range coder with the default or a custom state table, YCbCr 4:2:0, 4:2:2, 4:4:4, 4:1:1, 4:1:0 and gray with or without
/// alpha, and RGB with or without alpha, at 8 to 16 bits, every frame equal to FFmpeg's sample for sample. Frames come out as
/// their own planes: 8-bit planes, or 16-bit planes holding 10, 12 or 16 bits, where depths 9, 11, 13, 14 and 15 sit in the
/// next of those with their samples shifted up. RGB comes out as G, B and R planes tagged <see cref="Pixels.YuvMatrix.Identity"/>.
/// Version 2, which was experimental, and 4:4:0 streams are not decoded.
/// </summary>
/// <remarks>
/// FFV1 carries no color information, so the frame's color comes from the container (<see cref="VideoTrackInfo.Color"/>), or is
/// BT.601 limited range when it says nothing. Broken data follows <see cref="VideoDecoderOptions.Errors"/> and is reported through
/// the Egami context's diagnostics as <c>Ffv1.SliceCrc</c> (a slice's CRC does not match), <c>Ffv1.ConfigCrc</c> (the
/// configuration record's CRC does not match), <c>Ffv1.CorruptFrame</c> (a slice or frame cannot be read) or
/// <c>Ffv1.MissingReference</c> (a non-keyframe with no keyframe before it). In <see cref="DecodeErrorMode.Conceal"/> mode a
/// damaged slice shows the last good frame there.
/// </remarks>
/// <example>
/// <code language="csharp">
/// using var reader = VideoReader.Open("master.mkv");
/// foreach (YuvVideoFrame frame in reader.ReadFrames())
///     using (frame) Console.WriteLine($"{frame.Timestamp}: {frame.Raster.BitDepth} bit {frame.Raster.ChromaFormat}");
/// </code>
/// </example>
public sealed class Ffv1Codec : VideoCodec
{
    private Ffv1Codec()
    {
    }

    /// <summary>Gets the FFV1 codec.</summary>
    public static Ffv1Codec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "FFV1";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Ffv1;

    /// <summary>Decodes <see cref="CodecIds.Ffv1"/> tracks, and Matroska's <see cref="CodecIds.VfwFourCC"/> tracks whose BITMAPINFOHEADER names FFV1.</summary>
    /// <param name="track">The track.</param>
    /// <returns>Whether this codec decodes it.</returns>
    public override bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.CodecId == CodecIds.Ffv1 || IsVfwFfv1(track);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-codecs"/>
    /// </example>
    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        options ??= VideoDecoderOptions.Default;
        if (options.ScaleDenominator != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ScaleDenominator, "FFV1 decodes at full size only; the scale denominator must be 1.");
        }

        return new Ffv1VideoDecoder(track, options, context);
    }

    /// <summary>
    /// Checks an FFV1 file's fixity without decoding pictures: the configuration record's CRC, and in every frame of the first FFV1
    /// track each slice's footer and CRC (RFC 9043 section 4.9). This is far faster than decoding, so archives can run it
    /// routinely. Versions 0 and 1, and version 3 written without slice CRCs, have no slice CRCs to check; the result says so in
    /// <see cref="Ffv1VerifyResult.HasSliceCrcs"/>.
    /// </summary>
    /// <param name="path">The Matroska or AVI file.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="cancellationToken">Stops the check between frames.</param>
    /// <returns>What was checked and every problem found.</returns>
    /// <exception cref="UnsupportedFormatException">The file has no FFV1 track.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-ffv1-verify"/>
    /// </example>
    public static Ffv1VerifyResult Verify(string path, MediaContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        using Containers.IDemuxer demuxer = MediaFile.OpenDemuxer(path, context);
        return Verify(demuxer, cancellationToken);
    }

    /// <summary>Checks an FFV1 stream's fixity without decoding pictures. See <see cref="Verify(string, MediaContext?, CancellationToken)"/>.</summary>
    /// <param name="stream">The Matroska or AVI file, from its start.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether to leave <paramref name="stream"/> open afterwards.</param>
    /// <param name="cancellationToken">Stops the check between frames.</param>
    /// <returns>What was checked and every problem found.</returns>
    /// <exception cref="UnsupportedFormatException">The file has no FFV1 track.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-ffv1-verify"/>
    /// </example>
    public static Ffv1VerifyResult Verify(Stream stream, MediaContext? context = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using Containers.IDemuxer demuxer = MediaFile.OpenDemuxer(stream, context, leaveOpen);
        return Verify(demuxer, cancellationToken);
    }

    private static Ffv1VerifyResult Verify(Containers.IDemuxer demuxer, CancellationToken cancellationToken)
    {
        TrackInfo track = demuxer.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video && Instance.CanDecode(t))
            ?? throw new UnsupportedFormatException("The file has no FFV1 track.");
        ReadOnlySpan<byte> record = ConfigurationRecord(track).Span;
        Ffv1Parameters? config = null;
        bool? configOk = null;
        List<Ffv1VerifyError> errors = [];
        if (Ffv1Decoder.HasConfigurationRecord(record))
        {
            try
            {
                config = Ffv1Parameters.ReadConfigurationRecord(record, out bool ok);
                configOk = ok;
            }
            catch (EgamiException)
            {
                configOk = false;
            }
        }

        int frames = 0;
        long slices = 0;
        byte[] buffer = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber != track.Number)
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                int frame = frames++;
                if (config is null)
                {
                    continue;
                }

                ReadOnlySpan<byte> data = packet.Data.Span;
                if (buffer.Length < data.Length)
                {
                    buffer = new byte[data.Length];
                }

                data.CopyTo(buffer);
                List<(int Start, int End, int FooterEnd)> spans;
                try
                {
                    spans = Ffv1Decoder.FindSlices(buffer, data.Length, config);
                }
                catch (CorruptDataException e)
                {
                    errors.Add(new Ffv1VerifyError(frame, packet.Timestamp, -1, $"The frame's slices cannot be found: {e.Message}"));
                    continue;
                }

                if (!config.SliceCrc)
                {
                    continue;
                }

                for (int s = 0; s < spans.Count; s++)
                {
                    (int start, int end, int footerEnd) = spans[s];
                    slices++;
                    if (Ffv1Crc.Compute(buffer.AsSpan(start, footerEnd - start)) != 0)
                    {
                        errors.Add(new Ffv1VerifyError(frame, packet.Timestamp, s, "The slice's CRC does not match."));
                    }
                    else if (buffer[end + 3] != 0)
                    {
                        errors.Add(new Ffv1VerifyError(frame, packet.Timestamp, s, $"The slice's footer reports error status {buffer[end + 3]}."));
                    }
                }
            }
        }

        return new Ffv1VerifyResult
        {
            Frames = frames,
            Slices = slices,
            HasSliceCrcs = config?.SliceCrc ?? false,
            ConfigurationCrcOk = configOk,
            Errors = errors,
        };
    }

    /// <summary>Returns a track's FFV1 configuration record: its CodecPrivate, or what follows the BITMAPINFOHEADER of a VfW track.</summary>
    internal static ReadOnlyMemory<byte> ConfigurationRecord(TrackInfo track) =>
        IsVfwFfv1(track) ? Containers.Avi.AviCodecs.ExtraData(track.CodecPrivate.Span).ToArray() : track.CodecPrivate;

    /// <summary>Returns the source bit depth and chroma format a version 3 configuration record gives, or null.</summary>
    internal static (int BitDepth, Pixels.ChromaFormat? ChromaFormat)? Layout(ReadOnlySpan<byte> record)
    {
        if (!Ffv1Decoder.HasConfigurationRecord(record))
        {
            return null;
        }

        try
        {
            Ffv1Parameters p = Ffv1Parameters.ReadConfigurationRecord(record, out _);
            return (p.BitsPerRawSample, Ffv1Decoder.ChromaFormatOf(p));
        }
        catch (EgamiException)
        {
            return null;
        }
    }

    private static bool IsVfwFfv1(TrackInfo track) =>
        track.CodecId == CodecIds.VfwFourCC && track.CodecPrivate.Length >= Containers.Avi.AviCodecs.BitmapInfoHeaderSize
        && BinaryPrimitives.ReadUInt32LittleEndian(track.CodecPrivate.Span[16..]) == Containers.Avi.AviCodecs.Ffv1;
}
