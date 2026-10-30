namespace Egami.Video.Codecs;

/// <summary>
/// HEVC (H.265), as MP4, MOV, Matroska and raw .h265 files carry it, decoded by Egami.Heic's <see cref="Egami.Heic.HevcStreamDecoder"/>:
/// the Main, Main 10, Main 12 and format range extensions profiles to 12 bits, 4:0:0, 4:2:0, 4:2:2 and 4:4:4, and the HEVC base
/// layer of Dolby Vision profile 8 files. Frames come out in presentation order at the stream's conformance window, as 8-bit
/// planes, or 16-bit planes holding 9 to 12 bits. Register it with <see cref="HevcMediaContextExtensions.WithHevc"/>. Decode only.
/// </summary>
/// <remarks>
/// <para>
/// Layers above the base (multi-layer HEVC, Dolby Vision enhancement layers and RPU) are skipped, and so are the screen content
/// coding extensions. Decoding starts at an IRAP picture; the leading pictures a CRA picture skips at the start of decoding or
/// after a seek are not output.
/// </para>
/// <para>
/// The frame's color comes from the stream's VUI, else from the container (<see cref="VideoTrackInfo.Color"/>); mastering
/// display, content light level and HDR10+ SEI messages fill <see cref="VideoFrame.Hdr"/> and <see cref="VideoFrame.Hdr10Plus"/>.
/// Broken data follows <see cref="VideoDecoderOptions.Errors"/> and is reported through the Egami context's diagnostics as
/// <c>Hevc.CorruptFrame</c>. In <see cref="DecodeErrorMode.Conceal"/> mode a picture keeps what decoded before the damage; in
/// <see cref="DecodeErrorMode.Skip"/> mode, and where concealing cannot help, frames up to the next IRAP picture are dropped.
/// </para>
/// <para>
/// HEVC is covered by patents. Egami's authors grant no patent rights and are not lawyers. Whether you need a patent licence
/// depends on your country and on how you use and distribute this package; commercial products may need one from the HEVC patent
/// pools or holders.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHevc.cs" region="decode"/>
/// </example>
public sealed class HevcCodec : VideoCodec
{
    private HevcCodec()
    {
    }

    /// <summary>Gets the HEVC codec.</summary>
    public static HevcCodec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "HEVC";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Hevc;

    /// <inheritdoc/>
    /// <remarks>
    /// Also decodes the Dolby Vision MP4 tracks Egami.Video passes through as <see cref="CodecIds.QuickTimeVideo"/>, whose dvh1 or
    /// dvhe sample entry holds an hvcC record: their HEVC base layer.
    /// </remarks>
    public override bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return string.Equals(track.CodecId, CodecIds.Hevc, StringComparison.Ordinal)
            || (string.Equals(track.CodecId, CodecIds.QuickTimeVideo, StringComparison.Ordinal) && !DolbyVisionConfiguration(track.CodecPrivate).IsEmpty);
    }

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHevc.cs" region="decode"/>
    /// </example>
    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        options ??= VideoDecoderOptions.Default;
        if (options.ScaleDenominator != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ScaleDenominator, "HEVC decodes at full size only; the scale denominator must be 1.");
        }

        return new HevcVideoDecoder(track, options, context);
    }

    /// <summary>
    /// The hvcC record of a Dolby Vision sample entry (dvh1 or dvhe, ETSI TS 103 572), the whole stsd entry with its size and type
    /// first, or empty for any other entry. Its child boxes follow the 78 bytes of a visual sample entry's fields.
    /// </summary>
    internal static ReadOnlyMemory<byte> DolbyVisionConfiguration(ReadOnlyMemory<byte> entry)
    {
        ReadOnlySpan<byte> box = entry.Span;
        if (box.Length < 86 || !(box[4..8].SequenceEqual("dvh1"u8) || box[4..8].SequenceEqual("dvhe"u8)))
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        for (int at = 86; at + 8 <= box.Length;)
        {
            int size = (int)Math.Min(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(box[at..]), int.MaxValue);
            if (size < 8 || size > box.Length - at)
            {
                break;
            }

            if (box.Slice(at + 4, 4).SequenceEqual("hvcC"u8))
            {
                return entry.Slice(at + 8, size - 8);
            }

            at += size;
        }

        return ReadOnlyMemory<byte>.Empty;
    }
}
