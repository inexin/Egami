using System.Buffers.Binary;

using Egami.Video.Codecs.H264;

namespace Egami.Video.Codecs;

/// <summary>
/// H.264 (AVC), as MP4, MOV, Matroska, AVI and raw .h264 files carry it, decoded in the progressive Constrained Baseline, Main,
/// High, High 10 and High 4:2:2 profiles and their Intra profiles: 4:2:0, 4:2:2 and monochrome, 8 to 10 bits. Frames come out in
/// presentation order at the stream's cropped size, as 8-bit planes, or 16-bit planes holding 9 or 10 bits; monochrome streams
/// give a luma plane only (<see cref="Egami.Pixels.ChromaFormat.Yuv400"/>). Register it with <see cref="H264MediaContextExtensions.WithH264"/>. Decode only.
/// </summary>
/// <remarks>
/// <para>
/// Interlaced streams (field pictures and MBAFF), 4:4:4, lossless coding, slice groups, arbitrary slice order, redundant slices
/// and Extended profile data partitioning and SP/SI slices are not decoded; SVC and MVC streams decode as their base layer.
/// Decoding starts at an IDR picture or at a recovery point SEI.
/// </para>
/// <para>
/// The frame's color comes from the stream's VUI, else from the container (<see cref="VideoTrackInfo.Color"/>); mastering
/// display, content light level and HDR10+ SEI messages fill <see cref="VideoFrame.Hdr"/> and <see cref="VideoFrame.Hdr10Plus"/>.
/// Broken data follows <see cref="VideoDecoderOptions.Errors"/> and is reported through the Egami context's diagnostics as
/// <c>H264.CorruptFrame</c> (a slice cannot be decoded) or <c>H264.MissingReference</c> (a picture refers to one that is not
/// there). In <see cref="DecodeErrorMode.Conceal"/> mode the macroblocks a broken slice leaves are copied from the previous
/// picture.
/// </para>
/// <para>
/// H.264 is covered by patents. Egami's authors grant no patent rights and are not lawyers. Whether you need a patent licence
/// depends on your country and on how you use and distribute this package; commercial products may need one from the AVC patent
/// pools or holders.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoH264.cs" region="decode"/>
/// </example>
public sealed class H264Codec : VideoCodec
{
    private H264Codec()
    {
    }

    /// <summary>Gets the H.264 codec.</summary>
    public static H264Codec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "H.264";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Avc;

    /// <inheritdoc/>
    /// <remarks>
    /// Also decodes AVI tracks with the Video for Windows id (<see cref="CodecIds.VfwFourCC"/>) whose compression FourCC is
    /// H264, X264, AVC1 or DAVC in either case, which carry Annex B packets.
    /// </remarks>
    public override bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (string.Equals(track.CodecId, CodecIds.Avc, StringComparison.Ordinal))
        {
            return true;
        }

        // A BITMAPINFOHEADER: biCompression is the FourCC at offset 16.
        ReadOnlySpan<byte> header = track.CodecPrivate.Span;
        if (!string.Equals(track.CodecId, CodecIds.VfwFourCC, StringComparison.Ordinal) || header.Length < 20)
        {
            return false;
        }

        uint fourCC = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]) | 0x20202020;
        return fourCC is 0x34363268 or 0x34363278 or 0x31637661 or 0x63766164; // h264, x264, avc1, davc
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoH264.cs" region="decode"/>
    /// </example>
    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        options ??= VideoDecoderOptions.Default;
        if (options.ScaleDenominator != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ScaleDenominator, "H.264 decodes at full size only; the scale denominator must be 1.");
        }

        return new H264VideoDecoder(track, options, context);
    }
}
