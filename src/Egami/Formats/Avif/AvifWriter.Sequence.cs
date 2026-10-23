using System.Text;

using Egami.Colors;
using Egami.Formats.Av1;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Avif;

/// <content>
/// Animated AVIF (the avis brand), as libavif writes it: the frames coded as one AV1 stream by <see cref="Av1StreamEncoder"/>, a
/// key frame first and inter frames after it, in a color track (handler pict) with each frame's duration, and the alpha, when
/// any frame has transparency, as a monochrome stream in an auxiliary track (handler auxv, an auxl reference to the color track).
/// The loop count is the edit list's: repeated forever, or the tracks' duration a whole number of plays. The meta box keeps the
/// first frame as the primary item, with the alpha item and the metadata, for readers of stills; its items share the first
/// samples' bytes.
/// </content>
internal sealed partial class AvifWriter
{
    /// <summary>The timescale of the tracks: milliseconds, which every frame delay Egami reads is a whole number of.</summary>
    private const int Timescale = 1000;

    /// <summary>The frames between golden frame refreshes.</summary>
    private const int GoldenInterval = 8;

    /// <summary>Encodes the frames of an animation and writes its file.</summary>
    internal static void WriteSequence<TPixel>(Raster<TPixel> raster, RasterMetadata metadata, AvifWriterOptions options, EgamiContext context, Stream output)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelLayoutInfo layout = raster.Layout;
        bool gray = layout.ChannelOrder is PixelChannelOrder.Gray or PixelChannelOrder.GrayAlpha;
        bool deep = layout.NeedsWideSamples;
        AvifDetails? details = metadata.FormatDetails as AvifDetails;
        bool lossless = options.Lossless ?? details?.Lossless ?? false;
        int bitDepth = options.BitDepth ?? (!deep ? 8 : lossless ? (details is { BitDepth: > 8 } ? details.BitDepth : 12) : 10);
        ChromaFormat chroma = gray ? ChromaFormat.Yuv400
            : lossless ? ChromaFormat.Yuv444
            : options.Chroma is ChromaFormat chosen and (ChromaFormat.Yuv420 or ChromaFormat.Yuv422 or ChromaFormat.Yuv444) ? chosen
            : options.Quality >= 90 ? ChromaFormat.Yuv444 : ChromaFormat.Yuv420;
        FileMetadata extras = FileMetadata.From(metadata, options);
        (int primaries, int transfer) = metadata.CodePoints is ColorCodePoints points ? (points.Primaries, points.TransferCharacteristics)
            : !extras.Icc.IsEmpty ? (2, 2) : (1, 13);
        int matrix = gray ? 2 : lossless ? 0 : 6;
        int width = raster.Width, height = raster.Height, frames = raster.FrameCount;

        bool alpha = false;
        for (int i = 0; i < frames && layout.HasAlpha && !alpha; i++)
        {
            using Raster<Rgba16> rgba = Single(raster, i).As<Rgba16>();
            alpha = !Opaque(rgba);
        }

        Av1StreamEncoder? colorEncoder = null, alphaEncoder = null;
        List<byte[]> colorSamples = [], alphaSamples = [];
        long[] durations = new long[frames];
        try
        {
            for (int i = 0; i < frames; i++)
            {
                durations[i] = Math.Max(1, (long)Math.Round(raster.Frames[i].Metadata.Delay.TotalMilliseconds));
                using Raster<TPixel> frame = Single(raster, i);
                using Raster<Rgba16> rgba = frame.As<Rgba16>();
                Av1EncoderInput color = lossless || gray
                    ? DirectPlanes(rgba, chroma == ChromaFormat.Yuv400, bitDepth, primaries, transfer, matrix)
                    : chroma == ChromaFormat.Yuv420 && bitDepth == 8 && options.Effort >= 5 ? SharpPlanes(frame, primaries, transfer, matrix)
                    : ConvertedPlanes(frame, chroma, bitDepth, primaries, transfer, matrix);
                colorEncoder ??= new Av1StreamEncoder(SequenceOptions(color), context);
                colorSamples.Add(colorEncoder.EncodePlanes(color, Plan(i, lossless ? 0 : QIndexFor(options.Quality), options.Effort)).Data.ToArray());
                if (alpha)
                {
                    Av1EncoderInput plane = AlphaPlane(rgba, bitDepth);
                    alphaEncoder ??= new Av1StreamEncoder(SequenceOptions(plane), context);
                    int q = options.AlphaQuality == 100 ? 0 : QIndexFor(options.AlphaQuality);
                    alphaSamples.Add(alphaEncoder.EncodePlanes(plane, Plan(i, q, options.Effort)).Data.ToArray());
                }
            }

            Track colorTrack = new(1, "pict", colorEncoder!.Sequence, colorEncoder.SequenceHeader.ToArray(), colorSamples, durations,
                HeifWriter.ColorNclx(primaries, transfer, matrix, colorEncoder.Sequence.FullRange), extras.Icc);
            Track? alphaTrack = alphaEncoder is null ? null
                : new Track(2, "auxv", alphaEncoder.Sequence, alphaEncoder.SequenceHeader.ToArray(), alphaSamples, durations, null, default);
            WriteSequenceFile(output, width, height, colorTrack, alphaTrack, primaries, transfer, matrix, extras, metadata.LoopCount);
        }
        finally
        {
            colorEncoder?.Dispose();
            alphaEncoder?.Dispose();
        }

        // Each frame after the first predicts from the one before (LAST) and the last golden frame, refreshed every eighth frame.
        static Av1FramePlan Plan(int index, int qindex, int effort) => new()
        {
            Kind = index == 0 ? Av1FrameKind.Key : Av1FrameKind.Inter,
            QIndex = qindex,
            Effort = effort,
            RefreshSlots = index % GoldenInterval == 0 ? 1 | 8 : 1,
        };

        static Av1SequenceOptions SequenceOptions(Av1EncoderInput input) => new()
        {
            Width = input.Width,
            Height = input.Height,
            BitDepth = input.BitDepth,
            Chroma = input.Chroma,
            CodePoints = new ColorCodePoints((byte)input.ColorPrimaries, (byte)input.TransferCharacteristics, (byte)input.MatrixCoefficients, input.FullRange),
            ChromaSiting = input.ChromaSamplePosition switch { 1 => ChromaSiting.Left, 2 => ChromaSiting.TopLeft, _ => null },
        };
    }

    /// <summary>A frame of a raster as a raster of its own.</summary>
    private static Raster<TPixel> Single<TPixel>(Raster<TPixel> raster, int index)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> one = Raster.Create<TPixel>(raster.Width, raster.Height);
        Frame<TPixel> frame = raster.Frames[index];
        for (int y = 0; y < raster.Height; y++)
        {
            frame.Row(y).CopyTo(one.Row(y));
        }

        return one;
    }

    /// <summary>A track of an image sequence: its id, handler, stream, samples (temporal units, the first a key frame) and their durations, and its colr.</summary>
    private sealed record Track(uint Id, string Handler, Av1SequenceHeader Sequence, byte[] SequenceObu, List<byte[]> Samples, long[] Durations,
        byte[]? Nclx, ReadOnlyMemory<byte> Icc);

    /// <summary>Writes the file of an image sequence: ftyp, meta (the first frame as items), moov (the tracks), then mdat.</summary>
    private static void WriteSequenceFile(Stream output, int width, int height, Track color, Track? alpha, int primaries, int transfer, int matrix,
        FileMetadata metadata, int loopCount)
    {
        Av1SequenceHeader sequence = color.Sequence;
        List<string> brands = ["avif", "avis", "msf1", "iso8", "mif1", "miaf"];
        if (sequence.BitDepth <= 10 && sequence.Chroma == ChromaFormat.Yuv420)
        {
            brands.Add("MA1B");
        }
        else if (sequence.BitDepth <= 10 && sequence.Chroma == ChromaFormat.Yuv444)
        {
            brands.Add("MA1A");
        }

        HeifWriter heif = new("avis", [.. brands]);
        int ispe = heif.AddProperty(HeifWriter.SpatialExtents(width, height));
        uint colorItem = heif.AddItem("av01", color.Samples[0], "Color");
        heif.Associate(colorItem, ispe);
        heif.AddProperty(colorItem, HeifWriter.PixelInformation(sequence.BitDepth, sequence.Monochrome ? 1 : 3));
        heif.AddProperty(colorItem, ConfigurationBox(sequence, color.SequenceObu), essential: true);
        heif.PrimaryItemId = colorItem;
        if (!metadata.Icc.IsEmpty)
        {
            heif.AddProperty(colorItem, HeifWriter.ColorIcc(metadata.Icc.Span));
        }

        heif.AddProperty(colorItem, HeifWriter.ColorNclx(primaries, transfer, matrix, sequence.FullRange));
        AddHdrProperties(heif, colorItem, metadata.Hdr);
        uint alphaItem = 0;
        if (alpha is not null)
        {
            byte[] auxC = HeifWriter.AuxiliaryType(AvifProperties.AlphaUrn);
            alphaItem = heif.AddItem("av01", alpha.Samples[0], "Alpha", hidden: true);
            heif.Associate(alphaItem, ispe);
            heif.AddProperty(alphaItem, HeifWriter.PixelInformation(alpha.Sequence.BitDepth, 1));
            heif.AddProperty(alphaItem, ConfigurationBox(alpha.Sequence, alpha.SequenceObu), essential: true);
            heif.AddProperty(alphaItem, auxC);
            heif.Reference("auxl", alphaItem, colorItem);
        }

        if (!metadata.Exif.IsEmpty)
        {
            uint exifItem = heif.AddItem("Exif", (byte[])[0, 0, 0, 0, .. metadata.Exif.Span], "Exif");
            heif.Reference("cdsc", exifItem, colorItem);
        }

        if (!metadata.Xmp.IsEmpty)
        {
            uint xmpItem = heif.AddItem("mime", metadata.Xmp, "XMP", contentType: "application/rdf+xml");
            heif.Reference("cdsc", xmpItem, colorItem);
        }

        // The first samples are the items' data; the others follow in the mdat, color first.
        int colorBlock = heif.MediaData.Count;
        heif.MediaData.AddRange(color.Samples.Skip(1).Select(s => (ReadOnlyMemory<byte>)s));
        int alphaBlock = heif.MediaData.Count;
        if (alpha is not null)
        {
            heif.MediaData.AddRange(alpha.Samples.Skip(1).Select(s => (ReadOnlyMemory<byte>)s));
        }

        // 64-bit chunk offsets when the file may pass 4 GiB, decided before the offsets are known so the box's size is fixed.
        long data = color.Samples.Sum(s => (long)s.Length) + (alpha?.Samples.Sum(s => (long)s.Length) ?? 0);
        bool wide = data > uint.MaxValue - (16L << 20);
        heif.Movie = (items, media) =>
        {
            long[] colorOffsets = [items[colorItem - 1], .. media.AsSpan(colorBlock, color.Samples.Count - 1)];
            List<byte[]> tracks = [TrackBox(color, colorOffsets, width, height, loopCount, null, wide)];
            if (alpha is not null)
            {
                long[] alphaOffsets = [items[alphaItem - 1], .. media.AsSpan(alphaBlock, alpha.Samples.Count - 1)];
                tracks.Add(TrackBox(alpha, alphaOffsets, width, height, loopCount, color.Id, wide));
            }

            long duration = MovieDuration(color, loopCount);
            byte[] mvhd = HeifWriter.FullBox("mvhd", 1, 0, HeifWriter.U64(0), HeifWriter.U64(0), HeifWriter.U32(Timescale), HeifWriter.U64((ulong)duration),
                HeifWriter.U32(0x00010000), HeifWriter.U16(0x0100), new byte[10], Matrix(), new byte[24], HeifWriter.U32((uint)tracks.Count + 1));
            return HeifWriter.Box("moov", [mvhd, .. tracks]);
        };
        heif.Write(output);
    }

    /// <summary>Gets a track's duration over all its plays, in the timescale: all ones for forever.</summary>
    private static long MovieDuration(Track track, int loopCount)
    {
        long once = track.Durations.Sum();
        return loopCount == 0 ? -1 : once * Math.Max(1, loopCount);
    }

    /// <summary>The unity transformation matrix of mvhd and tkhd.</summary>
    private static byte[] Matrix() =>
        [.. HeifWriter.U32(0x00010000), .. new byte[12], .. HeifWriter.U32(0x00010000), .. new byte[12], .. HeifWriter.U32(0x40000000)];

    /// <summary>
    /// A trak box: tkhd, the auxl reference of an auxiliary track, the edit list that plays the track
    /// <paramref name="loopCount"/> times (0 for forever), and the media with its sample table.
    /// </summary>
    private static byte[] TrackBox(Track track, long[] offsets, int width, int height, int loopCount, uint? describes, bool wide)
    {
        long once = track.Durations.Sum();
        long total = MovieDuration(track, loopCount);
        List<byte[]> children =
        [
            // Enabled and in the movie; the auxiliary track as libavif marks it, enabled too.
            HeifWriter.FullBox("tkhd", 1, 3, HeifWriter.U64(0), HeifWriter.U64(0), HeifWriter.U32(track.Id), new byte[4], HeifWriter.U64((ulong)total),
                new byte[8], HeifWriter.U16(0), HeifWriter.U16(0), HeifWriter.U16(0), new byte[2], Matrix(),
                HeifWriter.U32((uint)width << 16), HeifWriter.U32((uint)height << 16)),
        ];
        if (describes is uint target)
        {
            children.Add(HeifWriter.Box("tref", HeifWriter.Box("auxl", HeifWriter.U32(target))));
        }

        if (loopCount != 1)
        {
            // An edit list whose flag 1 repeats it: the readers derive the plays from the track's duration over the segment's.
            children.Add(HeifWriter.Box("edts", HeifWriter.FullBox("elst", 1, 1, HeifWriter.U32(1), HeifWriter.U64((ulong)once), HeifWriter.U64(0),
                HeifWriter.U16(1), HeifWriter.U16(0))));
        }

        byte[] mdhd = HeifWriter.FullBox("mdhd", 1, 0, HeifWriter.U64(0), HeifWriter.U64(0), HeifWriter.U32(Timescale), HeifWriter.U64((ulong)once),
            HeifWriter.U16(0x55C4), HeifWriter.U16(0));
        byte[] hdlr = HeifWriter.FullBox("hdlr", 0, 0, HeifWriter.U32(0), Encoding.ASCII.GetBytes(track.Handler), new byte[12], [0]);
        byte[] vmhd = HeifWriter.FullBox("vmhd", 0, 1, new byte[8]);
        byte[] dinf = HeifWriter.Box("dinf", HeifWriter.FullBox("dref", 0, 0, HeifWriter.U32(1), HeifWriter.FullBox("url ", 0, 1)));
        byte[] minf = HeifWriter.Box("minf", vmhd, dinf, SampleTable(track, offsets, width, height, wide));
        children.Add(HeifWriter.Box("mdia", mdhd, hdlr, minf));
        return HeifWriter.Box("trak", [.. children]);
    }

    /// <summary>The stbl box: the av01 sample entry, then the samples' durations, chunks (one sample each), sizes, offsets and sync samples.</summary>
    private static byte[] SampleTable(Track track, long[] offsets, int width, int height, bool wide)
    {
        // CodingConstraintsBox (HEIF 7.3.? ccst): references may be inter, intra prediction used, up to 15 references a picture.
        byte[] ccst = HeifWriter.FullBox("ccst", 0, 0, HeifWriter.U32((0u << 31) | (1u << 30) | (15u << 26)));
        List<byte[]> entryBoxes = [ConfigurationBox(track.Sequence, track.SequenceObu)];
        if (track.Nclx is byte[] nclx)
        {
            entryBoxes.Add(nclx);
        }

        if (!track.Icc.IsEmpty)
        {
            entryBoxes.Add(HeifWriter.ColorIcc(track.Icc.Span));
        }

        entryBoxes.Add(ccst);
        if (track.Handler == "auxv")
        {
            entryBoxes.Add(HeifWriter.FullBox("auxi", 0, 0, [.. Encoding.UTF8.GetBytes(AvifProperties.AlphaUrn), 0]));
        }

        // VisualSampleEntry: reserved, data_reference_index 1, pre-defined and reserved, the size, 72 dpi, one frame a sample, the
        // compressor's name as libavif gives it, depth 24 and pre_defined -1.
        byte[] name = new byte[32];
        byte[] coder = Encoding.ASCII.GetBytes("AOM Coding");
        name[0] = (byte)coder.Length;
        coder.CopyTo(name, 1);
        byte[] entry = HeifWriter.Box("av01", [new byte[6], HeifWriter.U16(1), new byte[16], HeifWriter.U16(width), HeifWriter.U16(height),
            HeifWriter.U32(0x00480000), HeifWriter.U32(0x00480000), new byte[4], HeifWriter.U16(1), name, HeifWriter.U16(0x0018), HeifWriter.U16(0xFFFF),
            .. entryBoxes]);
        byte[] stsd = HeifWriter.FullBox("stsd", 0, 0, HeifWriter.U32(1), entry);

        List<byte[]> times = [];
        int runs = 0;
        for (int i = 0; i < track.Durations.Length;)
        {
            int j = i;
            while (j < track.Durations.Length && track.Durations[j] == track.Durations[i])
            {
                j++;
            }

            times.Add(HeifWriter.U32((uint)(j - i)));
            times.Add(HeifWriter.U32((uint)track.Durations[i]));
            runs++;
            i = j;
        }

        byte[] stts = HeifWriter.FullBox("stts", 0, 0, [HeifWriter.U32((uint)runs), .. times]);
        byte[] stsc = HeifWriter.FullBox("stsc", 0, 0, HeifWriter.U32(1), HeifWriter.U32(1), HeifWriter.U32(1), HeifWriter.U32(1));
        byte[] stsz = HeifWriter.FullBox("stsz", 0, 0, [HeifWriter.U32(0), HeifWriter.U32((uint)track.Samples.Count), .. track.Samples.Select(s => HeifWriter.U32((uint)s.Length))]);
        byte[] chunks = wide
            ? HeifWriter.FullBox("co64", 0, 0, [HeifWriter.U32((uint)offsets.Length), .. offsets.Select(o => HeifWriter.U64((ulong)o))])
            : HeifWriter.FullBox("stco", 0, 0, [HeifWriter.U32((uint)offsets.Length), .. offsets.Select(o => HeifWriter.U32((uint)o))]);

        // Only the first sample is a key frame.
        byte[] stss = HeifWriter.FullBox("stss", 0, 0, HeifWriter.U32(1), HeifWriter.U32(1));
        return HeifWriter.Box("stbl", stsd, stts, stsc, stsz, chunks, stss);
    }
}
