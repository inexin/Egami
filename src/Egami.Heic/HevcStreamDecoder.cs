using System.Buffers.Binary;

using Egami.Colors;
using Egami.Heic.Hevc;
using Egami.Nal;
using Egami.Pixels;

namespace Egami.Heic;

/// <summary>
/// Decodes an HEVC (H.265) video stream an access unit at a time, as MP4, Matroska and raw .h265 files carry it: intra and inter
/// pictures of the Main, Main 10, Main Still Picture, Main 12 and range extensions profiles to 12 bits, 4:0:0 to 4:4:4, with
/// tiles, wavefronts and every inter tool. The pictures equal the HEVC reference decoder's (HM) sample for sample. Containers are
/// not read here: Egami.Video reads them and uses this decoder, and any other source of HEVC access units can too.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Send"/> takes the NAL units of one access unit or more: an Annex B byte stream (start codes), or after
/// <see cref="SetConfiguration"/> with an MP4 or Matroska hvcC record, NAL units each after a size field. Pictures come out in output
/// order once the stream's reordering allows, through <see cref="TryReceiveFrame"/>; <see cref="Flush"/> lets the rest out at
/// the end of the stream.
/// </para>
/// <para>
/// Decoding starts at an IRAP picture (IDR, CRA or BLA); leading pictures of a CRA or BLA picture that starts the stream are left
/// out, as the specification says. Only the base layer of a layered stream (Dolby Vision's enhancement layer, MV-HEVC) decodes.
/// The decoder decodes a picture's tiles and wavefront rows on up to <see cref="EgamiContext.MaxParallelism"/> threads and checks
/// its size against the context's <see cref="EgamiContext.Limits"/>. An instance is not thread-safe.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Heic.cs" region="stream"/>
/// </example>
public sealed class HevcStreamDecoder : IDisposable
{
    private readonly HevcSequenceDecoder _decoder;
    private readonly HevcDecoderOptions _options;
    private readonly List<(HevcDpbPicture Picture, int Poc)> _output = [];
    private readonly Queue<(HevcDpbPicture Picture, int Poc)> _frames = new();
    private int _lengthSize;
    private bool _disposed;

    /// <summary>Starts a stream.</summary>
    /// <param name="options">How to decode; <see cref="HevcDecoderOptions.Default"/> when null.</param>
    /// <param name="context">The threads and limits to use; <see cref="EgamiContext.Default"/> when null.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Heic.cs" region="stream"/>
    /// </example>
    public HevcStreamDecoder(HevcDecoderOptions? options = null, EgamiContext? context = null)
    {
        _options = options ?? HevcDecoderOptions.Default;
        EgamiContext c = context ?? EgamiContext.Default;
        _decoder = new HevcSequenceDecoder(c.Limits, Math.Max(1, c.MaxParallelism)) { ConcealErrors = _options.ConcealErrors };
    }

    /// <summary>Gets the information of the next frame <see cref="TryReceiveFrame"/> gives, to size its raster, or null when there is none.</summary>
    public HevcFrameInfo? NextFrame => _frames.TryPeek(out (HevcDpbPicture Picture, int Poc) next) ? Info(next.Picture) : null;

    /// <summary>
    /// Takes an MP4 or Matroska codec configuration record (hvcC, ISO/IEC 14496-15 8.3.3): its parameter sets, and the size of
    /// the NAL unit size fields that <see cref="Send"/> then expects.
    /// </summary>
    /// <param name="hvcC">The record's bytes, from configurationVersion on.</param>
    /// <exception cref="CorruptDataException">The record is broken.</exception>
    public void SetConfiguration(ReadOnlySpan<byte> hvcC)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (hvcC.Length < 23 || hvcC[0] != 1)
        {
            throw HevcErrors.Corrupt("The hvcC record is cut off or not version 1.");
        }

        int lengthSize = (hvcC[21] & 3) + 1;
        if (lengthSize == 3)
        {
            throw HevcErrors.Corrupt("The hvcC record's lengthSizeMinusOne is 2; NAL unit sizes take 1, 2 or 4 bytes.");
        }

        int at = 23;
        for (int array = 0; array < hvcC[22]; array++)
        {
            if (at + 3 > hvcC.Length)
            {
                throw HevcErrors.Corrupt("The hvcC record's NAL unit arrays are cut off.");
            }

            int count = BinaryPrimitives.ReadUInt16BigEndian(hvcC[(at + 1)..]);
            at += 3;
            for (int i = 0; i < count; i++)
            {
                if (at + 2 > hvcC.Length || at + 2 + BinaryPrimitives.ReadUInt16BigEndian(hvcC[at..]) > hvcC.Length)
                {
                    throw HevcErrors.Corrupt("An hvcC NAL unit is cut off.");
                }

                int length = BinaryPrimitives.ReadUInt16BigEndian(hvcC[at..]);
                Take(HevcNal.Parse(hvcC.Slice(at + 2, length)));
                at += 2 + length;
            }
        }

        _lengthSize = lengthSize;
    }

    /// <summary>
    /// Decodes NAL units: an Annex B byte stream, or after <see cref="SetConfiguration"/> NAL units after size fields. Pictures that
    /// are ready in output order wait for <see cref="TryReceiveFrame"/>.
    /// </summary>
    /// <param name="data">Whole NAL units, of one access unit or more.</param>
    /// <exception cref="CorruptDataException">The data is broken.</exception>
    /// <exception cref="UnsupportedFormatException">The stream uses a profile the decoder does not have (16-bit, High Throughput bypass alignment, separate colour planes).</exception>
    /// <exception cref="LimitExceededException">A picture's size breaks the context's safety limits.</exception>
    public void Send(ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] copy = data.ToArray();
        List<ReadOnlyMemory<byte>> units = _lengthSize == 0 ? NalFraming.SplitAnnexB(copy) : NalFraming.SplitLengthPrefixed(copy, _lengthSize);
        foreach (ReadOnlyMemory<byte> unit in units)
        {
            Take(HevcNal.Parse(unit.Span));
        }
    }

    /// <summary>Ends the stream: the last picture completes and every picture still waiting comes out.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _decoder.Flush(_output);
        Collect();
    }

    /// <summary>Copies the next picture in output order into <paramref name="destination"/>, cropped to its conformance window.</summary>
    /// <param name="destination">
    /// A raster of the frame's size (<see cref="NextFrame"/>) and chroma format: 8-bit for an 8-bit stream, else 16-bit of the
    /// stream's bit depth. Its color is left as it is.
    /// </param>
    /// <param name="info">What the frame was.</param>
    /// <returns>True when a frame was copied; false when none is ready.</returns>
    /// <exception cref="ArgumentException"><paramref name="destination"/> does not match the frame.</exception>
    public bool TryReceiveFrame(YuvRaster destination, out HevcFrameInfo info)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_frames.TryPeek(out (HevcDpbPicture Picture, int Poc) next))
        {
            info = default;
            return false;
        }

        info = Info(next.Picture);
        if (destination.Width != info.Width || destination.Height != info.Height || destination.ChromaFormat != info.Chroma || destination.BitDepth != info.BitDepth)
        {
            throw new ArgumentException($"The destination must be {info.BitDepth}-bit {info.Chroma} at {info.Width}×{info.Height}; it is "
                + $"{destination.BitDepth}-bit {destination.ChromaFormat} at {destination.Width}×{destination.Height}.", nameof(destination));
        }

        _frames.Dequeue();
        HevcPicture picture = next.Picture.Picture;
        HevcSps sps = picture.Sps;
        for (int c = 0; c < picture.Planes.Length; c++)
        {
            int sx = c == 0 ? 1 : sps.SubWidthC, sy = c == 0 ? 1 : sps.SubHeightC;
            int left = sps.Conformance.Left / sx, top = sps.Conformance.Top / sy, width = info.Width / sx, height = info.Height / sy;
            ushort[] plane = picture.Planes[c];
            int stride = picture.Widths[c];
            if (destination is YuvRaster<byte> bytes)
            {
                YuvPlane<byte> target = c == 0 ? bytes.Y : c == 1 ? bytes.U! : bytes.V!;
                for (int y = 0; y < height; y++)
                {
                    Span<byte> row = target.Row(y);
                    ReadOnlySpan<ushort> source = plane.AsSpan(((top + y) * stride) + left, width);
                    for (int x = 0; x < width; x++)
                    {
                        row[x] = (byte)source[x];
                    }
                }
            }
            else if (destination is YuvRaster<ushort> words)
            {
                YuvPlane<ushort> target = c == 0 ? words.Y : c == 1 ? words.U! : words.V!;
                for (int y = 0; y < height; y++)
                {
                    plane.AsSpan(((top + y) * stride) + left, width).CopyTo(target.Row(y));
                }
            }
            else
            {
                throw new ArgumentException("The destination must have byte or ushort samples.", nameof(destination));
            }
        }

        return true;
    }

    /// <summary>Drops the pictures, decoded and waiting, and the parameter sets' pictures; the next data must start at an IRAP picture.</summary>
    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _frames.Clear();
        _output.Clear();
        _decoder.Reset();
    }

    /// <summary>Drops the pictures.</summary>
    public void Dispose()
    {
        if (!_disposed)
        {
            _frames.Clear();
            _decoder.Reset();
            _disposed = true;
        }
    }

    private void Take(HevcNal nal)
    {
        _decoder.Send(nal, _output);
        Collect();
    }

    private void Collect()
    {
        foreach ((HevcDpbPicture picture, int poc) in _output)
        {
            if (_options.CheckPictureHashes && picture.Hash is { } hash && !HevcDecoder.Matches(picture.Picture, hash))
            {
                throw HevcErrors.Corrupt($"The picture with order count {poc} does not match its decoded picture hash.");
            }

            _frames.Enqueue((picture, poc));
        }

        _output.Clear();
    }

    private static HevcFrameInfo Info(HevcDpbPicture p)
    {
        HevcSps sps = p.Picture.Sps;
        HevcVui? vui = sps.Vui;
        ColorCodePoints color = vui is not null && (vui.ColourPrimaries != 2 || vui.TransferCharacteristics != 2 || vui.MatrixCoefficients != 2)
            ? new ColorCodePoints((byte)vui.ColourPrimaries, (byte)vui.TransferCharacteristics, (byte)vui.MatrixCoefficients, vui.FullRange)
            : new ColorCodePoints(2, 2, (byte)(sps.OutputHeight <= 576 ? 6 : 1), vui?.FullRange ?? false);
        ChromaFormat chroma = sps.ChromaFormatIdc switch { 0 => ChromaFormat.Yuv400, 2 => ChromaFormat.Yuv422, 3 => ChromaFormat.Yuv444, _ => ChromaFormat.Yuv420 };
        return new HevcFrameInfo(p.Kind, p.Kind is HevcPictureKind.Idr or HevcPictureKind.Cra or HevcPictureKind.Bla, p.Poc, sps.OutputWidth, sps.OutputHeight, Math.Max(sps.BitDepthLuma, sps.BitDepthChroma), chroma, p.TemporalId, color);
    }
}

/// <summary>How an <see cref="HevcStreamDecoder"/> decodes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Heic.cs" region="stream"/>
/// </example>
public sealed record HevcDecoderOptions
{
    /// <summary>Gets the default options.</summary>
    public static HevcDecoderOptions Default { get; } = new();

    /// <summary>
    /// Gets a value indicating whether damaged slice data is concealed rather than thrown: the picture keeps what decoded before
    /// the damage, stays a reference and comes out; damaged parameter sets and slice headers still throw. Defaults to false.
    /// </summary>
    public bool ConcealErrors { get; init; }

    /// <summary>
    /// Gets a value indicating whether each picture is checked against its decoded picture hash SEI message, when it has one: a
    /// picture that does not match throws <see cref="CorruptDataException"/>. Defaults to false.
    /// </summary>
    public bool CheckPictureHashes { get; init; }
}

/// <summary>What an HEVC picture's coded slice NAL units are (nal_unit_type, H.265 Table 7-1).</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Heic.cs" region="stream"/>
/// </example>
public enum HevcPictureKind
{
    /// <summary>An IDR picture: decodes on its own and starts a coded video sequence.</summary>
    Idr,

    /// <summary>A CRA picture: decodes on its own; its leading pictures may refer to pictures before it.</summary>
    Cra,

    /// <summary>A BLA picture: a CRA picture where a stream was spliced, whose skipped leading pictures are left out.</summary>
    Bla,

    /// <summary>A trailing picture (TRAIL, TSA or STSA): after the IRAP picture before it in both decoding and output order.</summary>
    Trailing,

    /// <summary>A leading picture that decodes from the IRAP picture before it on (RADL).</summary>
    DecodableLeading,

    /// <summary>A leading picture that may refer to pictures before the IRAP picture (RASL).</summary>
    SkippedLeading,
}

/// <summary>What an HEVC picture was, as <see cref="HevcStreamDecoder.TryReceiveFrame"/> gives it.</summary>
/// <param name="Kind">The type of its NAL units.</param>
/// <param name="IsIrap">Whether it is an IRAP picture (IDR, CRA or BLA), where decoding can start.</param>
/// <param name="Poc">PicOrderCntVal: its place in output order within its coded video sequence.</param>
/// <param name="Width">The width of its conformance window, the picture as shown.</param>
/// <param name="Height">The height of its conformance window.</param>
/// <param name="BitDepth">The bit depth of its samples (the larger of luma's and chroma's).</param>
/// <param name="Chroma">Its chroma format.</param>
/// <param name="TemporalId">Its temporal sub-layer (TemporalId).</param>
/// <param name="Color">Its colour from the sequence's VUI, or the HD or SD default when the VUI leaves it unspecified.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Heic.cs" region="stream"/>
/// </example>
public readonly record struct HevcFrameInfo(HevcPictureKind Kind, bool IsIrap, int Poc, int Width, int Height, int BitDepth, ChromaFormat Chroma,
    int TemporalId, ColorCodePoints Color);
