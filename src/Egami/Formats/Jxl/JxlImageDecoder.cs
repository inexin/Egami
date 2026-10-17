using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Formats.Jxl;

/// <summary>
/// Decodes a JPEG XL image into a raster: the whole file read into memory, its frames decoded in order (LF frames kept for the
/// frames that use them, reference frames kept in their four slots), each rendered (restoration filters, patches, splines,
/// upsampling, noise), converted from XYB to the file's color space and blended onto the canvas; the frames shown are put into
/// the natural pixel type (design question 5) and from there the type asked for.
/// </summary>
internal static class JxlImageDecoder
{

    /// <summary>Decodes into the natural pixel type: one frame for a still, the coalesced frames for an animation.</summary>
    internal static Raster Decode(EgamiInput input, ReaderOptions options)
    {
        if (options.Hdr is HdrDecode hdr)
        {
            // As Ultra HDR JPEGs: the SDR base, then its gain map applied for the display's headroom (design question 12).
            Raster sdr = Decode(input, options with { Hdr = null });
            if (sdr.Metadata.GainMap is not GainMap map)
            {
                return sdr;
            }

            using (sdr)
            {
                Raster<RgbaF16> result = map.Apply(sdr, hdr.Headroom);
                RasterMetadata metadata = result.Metadata;
                metadata.FormatName = sdr.Metadata.FormatName;
                metadata.FormatDetails = sdr.Metadata.FormatDetails;
                metadata.Exif = sdr.Metadata.Exif;
                metadata.Xmp = sdr.Metadata.Xmp;
                metadata.WarningList.Prepend(sdr.Metadata.WarningList);
                return result;
            }
        }

        (JxlFile file, byte[] codestream) = Open(input, options);
        return Natural(input, file, layout => Shown(layout, input, options, file, codestream, forceFloat: false));
    }

    /// <summary>Decodes into <typeparamref name="TPixel"/>, through the natural type.</summary>
    internal static Raster<TPixel> Decode<TPixel>(EgamiInput input, ReaderOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster natural = Decode(input, options);
        return Convert<TPixel>(input, natural);
    }

    /// <summary>
    /// Decodes the frames shown as raw float planes, as the conformance references are: the color channels (one for gray) then
    /// every extra channel, unclamped, with no spot color mixed in, alpha as coded and CMYK as stored (0 is full ink).
    /// </summary>
    internal static List<JxlPlanes> DecodeChannels(EgamiInput input)
    {
        (JxlFile file, byte[] codestream) = Open(input, ReaderOptions.Default, likeDjxl: true);
        List<JxlPlanes> frames = [];
        int color = file.Header.ColorChannels == 1 ? 1 : 3;
        Run(file, codestream, input, Sequence(file), lfOnly: false, layers: false, (decoded, f) =>
        {
            decoded.ForceFloat();
            frames.Add(new JxlPlanes(decoded.Width, decoded.Height, [.. decoded.Color![..color], .. decoded.Extras!]));
            return true;
        });
        return frames;
    }

    /// <summary>Decodes one extra channel of each frame shown into gray floats.</summary>
    internal static Raster<GrayF32> DecodeExtraChannel(EgamiInput input, ReaderOptions options, int index)
    {
        (JxlFile file, byte[] codestream) = Open(input, options);
        if (index >= file.Header.ExtraChannels.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"The image has {file.Header.ExtraChannels.Count} extra channels.");
        }

        return Shown<GrayF32>(input, options, file, codestream, forceFloat: true, extra: index);
    }

    /// <summary>Decodes every regular frame as it is before blending, in the natural pixel type, with where and how it blends.</summary>
    internal static List<JxlLayer> DecodeLayers(EgamiInput input, ReaderOptions options)
    {
        (JxlFile file, byte[] codestream) = Open(input, options);
        List<JxlLayer> layers = [];
        try
        {
            Run(file, codestream, input, Sequence(file), lfOnly: false, layers: true, (decoded, f) =>
            {
                Raster raster = Natural(input, file, layout => Single(layout, input, file, decoded, f));
                layers.Add(new JxlLayer(raster, f.X0, f.Y0, (JxlLayerBlend)f.Blending.Mode, Delay(f, file.Header), f.Name));
                return true;
            });
            return layers;
        }
        catch
        {
            foreach (JxlLayer layer in layers)
            {
                layer.Dispose();
            }

            throw;
        }
    }

    /// <summary>Decodes the preview frame, or returns <see langword="null"/> when the file has none.</summary>
    internal static Raster? DecodePreview(EgamiInput input, ReaderOptions options)
    {
        (JxlFile file, byte[] codestream) = Open(input, options);
        if (file.PreviewFrame is not JxlFrameHeader preview || file.PreviewSections is not (JxlToc toc, long offset))
        {
            return null;
        }

        Raster? result = null;
        Run(file, codestream, input, [(preview, toc, offset)], lfOnly: false, layers: false, (decoded, f) =>
        {
            result = Natural(input, file, layout => Single(layout, input, file, decoded, f));
            return false;
        });
        return result;
    }

    /// <summary>
    /// Writes the JPEG a recompressed JPEG XL file was made from (design question 8): the jbrd box's segments around the
    /// frame's quantized coefficients, the ICC profile, Exif and XMP from the file.
    /// </summary>
    internal static void ReconstructJpeg(EgamiInput input, Stream output)
    {
        (JxlFile file, byte[] codestream) = Open(input, ReaderOptions.Default);
        if (file.Container.JpegReconstruction is not byte[] box)
        {
            throw new UnsupportedFormatException("The JPEG XL file has no JPEG reconstruction data (jbrd box); it was not recompressed from a JPEG.");
        }

        List<(JxlFrameHeader Header, JxlToc Toc, long Offset)> sequence = Sequence(file);
        if (sequence.Count != 1 || sequence[0].Header is not { Modular: false, Type: JxlFrameType.Regular } f || file.Header.XybEncoded
            || (f.Flags & 0x20) != 0 || (f.Flags & 0x80) == 0)
        {
            throw new UnsupportedFormatException("The JPEG XL file's frames are not one recompressed JPEG frame, so the JPEG cannot be rebuilt.");
        }

        JxlJbrd jbrd = JxlJbrd.Parse(box, input.Limits);
        JxlFrameDecoder decoder = new(codestream, file.Header, f, sequence[0].Toc, sequence[0].Offset, input.Limits) { Parallel = new JxlParallel(Math.Max(1, input.Context.MaxParallelism), input.CancellationToken), CoefficientsOnly = true };
        JxlFrameChannels frame = decoder.Decode(null);
        jbrd.Write(output, f, frame.VarDct!, file.Icc, file.Container.Exif, file.Container.Xmp);
    }

    /// <summary>
    /// Decodes the last frame of a bare codestream the lossy encoder wrote, a VarDCT frame, to its XYB planes before the
    /// restoration filters and patches, cropped to the frame, for the encoder's own reconstruction (Phase 2.9.7's EPF search).
    /// </summary>
    internal static float[][] DecodeXyb(byte[] codestream, int parallelism)
    {
        EgamiInput input = new(new MemoryStream(codestream, writable: false), EgamiContext.Default, CancellationToken.None);
        try
        {
            (JxlFile file, byte[] data) = Open(input, ReaderOptions.Default);
            List<(JxlFrameHeader Header, JxlToc Toc, long Offset)> sequence = Sequence(file);
            JxlFrameHeader f = sequence[^1].Header;
            JxlFrameDecoder decoder = new(data, file.Header, f, sequence[^1].Toc, sequence[^1].Offset, input.Limits) { Parallel = new JxlParallel(Math.Max(1, parallelism), CancellationToken.None) };
            JxlFrameChannels frame = decoder.Decode(null);
            return [.. frame.ColorPlanes!.Select((plane, c) => Crop(plane, frame.ColorStrides[c], frame.Width, frame.Height))];
        }
        finally
        {
            input.Dispose();
        }
    }

    /// <summary>Converts a decoded raster, every frame, to <typeparamref name="TPixel"/>.</summary>
    internal static Raster<TPixel> Convert<TPixel>(EgamiInput input, Raster natural)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (natural is Raster<TPixel> same)
        {
            return same;
        }

        using (natural)
        {
            return natural.Accept(new Converter<TPixel>(input));
        }
    }

    /// <summary>Gets a sample as a float: integers divided by their maximum, float bit patterns as they are (H.7).</summary>
    internal static float Float(int v, JxlBitDepth depth)
    {
        if (!depth.IsFloat)
        {
            return v / (float)((1L << depth.BitsPerSample) - 1);
        }

        int bits = depth.BitsPerSample;
        int exponentBits = depth.ExponentBits;
        int mantissaBits = bits - exponentBits - 1;
        uint sample = (uint)v;
        if (bits == 32 && exponentBits == 8)
        {
            return BitConverter.UInt32BitsToSingle(sample);
        }

        bool negative = (sample & (1u << (bits - 1))) != 0;
        uint mantissa = sample & ((1u << mantissaBits) - 1);
        int exponent = (int)((sample >> mantissaBits) & ((1u << exponentBits) - 1));
        int bias = (1 << (exponentBits - 1)) - 1;
        if (exponent == 0)
        {
            float sub = mantissa * MathF.Pow(2, 1 - bias - mantissaBits);
            return negative ? -sub : sub;
        }

        if (exponent == (1 << exponentBits) - 1)
        {
            return mantissa == 0 ? (negative ? float.NegativeInfinity : float.PositiveInfinity) : float.NaN;
        }

        float value = (1 + (mantissa / (float)(1u << mantissaBits))) * MathF.Pow(2, exponent - bias);
        return negative ? -value : value;
    }

    /// <summary>Reads the whole file and its headers, joins the codestream, and sets the output color space.</summary>
    private static (JxlFile File, byte[] Codestream) Open(EgamiInput input, ReaderOptions options, bool likeDjxl = false)
    {
        (byte[] data, int length, bool pooled) = InputBytes.ReadAll(input);
        try
        {
            using MemoryStream stream = new(data, 0, length, writable: false);
            EgamiInput copy = new(stream, input.Context, input.CancellationToken);
            try
            {
                JxlFile file = JxlFile.Read(copy, allBoxes: true);
                file.Output = JxlOutput.For(file, options is JxlReaderOptions { LinearOutput: true }, input.Context, likeDjxl);
                file.Parallel = new JxlParallel(Math.Max(1, input.Context.MaxParallelism), input.CancellationToken);
                file.Region = options.Region;
                return (file, file.Container.Codestream(data.AsSpan(0, length)));
            }
            finally
            {
                copy.Dispose();
            }
        }
        finally
        {
            if (pooled)
            {
                System.Buffers.ArrayPool<byte>.Shared.Return(data);
            }
        }
    }

    /// <summary>Calls <paramref name="decode"/> with the natural pixel type of the file.</summary>
    private static Raster Natural(EgamiInput input, JxlFile file, Func<PixelLayoutInfo, Raster> decode) => decode(JxlReader.NaturalLayout(file.Header, file.Output!.Linear));

    /// <summary>Gets the frames of the codestream with their TOCs and section offsets.</summary>
    private static List<(JxlFrameHeader Header, JxlToc Toc, long Offset)> Sequence(JxlFile file)
    {
        List<(JxlFrameHeader, JxlToc, long)> frames = [];
        for (int i = 0; i < file.Frames.Count; i++)
        {
            frames.Add((file.Frames[i], file.Tocs[i], file.SectionOffsets[i]));
        }

        return frames;
    }

    private static Raster Shown(PixelLayoutInfo layout, EgamiInput input, ReaderOptions options, JxlFile file, byte[] codestream, bool forceFloat) => layout switch
    {
        _ when layout == Gray8.Layout => Shown<Gray8>(input, options, file, codestream, forceFloat),
        _ when layout == GrayAlpha8.Layout => Shown<GrayAlpha8>(input, options, file, codestream, forceFloat),
        _ when layout == Rgb8.Layout => Shown<Rgb8>(input, options, file, codestream, forceFloat),
        _ when layout == Rgba8.Layout => Shown<Rgba8>(input, options, file, codestream, forceFloat),
        _ when layout == Gray16.Layout => Shown<Gray16>(input, options, file, codestream, forceFloat),
        _ when layout == GrayAlpha16.Layout => Shown<GrayAlpha16>(input, options, file, codestream, forceFloat),
        _ when layout == Rgb16.Layout => Shown<Rgb16>(input, options, file, codestream, forceFloat),
        _ when layout == Rgba16.Layout => Shown<Rgba16>(input, options, file, codestream, forceFloat),
        _ when layout == RgbaF16.Layout => Shown<RgbaF16>(input, options, file, codestream, forceFloat),
        _ when layout == GrayF32.Layout => Shown<GrayF32>(input, options, file, codestream, forceFloat),
        _ when layout == RgbF32.Layout => Shown<RgbF32>(input, options, file, codestream, forceFloat),
        _ when layout == Cmyk8.Layout => Shown<Cmyk8>(input, options, file, codestream, forceFloat),
        _ when layout == Cmyk16.Layout => Shown<Cmyk16>(input, options, file, codestream, forceFloat),
        _ => Shown<RgbaF32>(input, options, file, codestream, forceFloat),
    };

    private static Raster Single(PixelLayoutInfo layout, EgamiInput input, JxlFile file, JxlDecoded decoded, JxlFrameHeader f) => layout switch
    {
        _ when layout == Gray8.Layout => Single<Gray8>(input, file, decoded, f),
        _ when layout == GrayAlpha8.Layout => Single<GrayAlpha8>(input, file, decoded, f),
        _ when layout == Rgb8.Layout => Single<Rgb8>(input, file, decoded, f),
        _ when layout == Rgba8.Layout => Single<Rgba8>(input, file, decoded, f),
        _ when layout == Gray16.Layout => Single<Gray16>(input, file, decoded, f),
        _ when layout == GrayAlpha16.Layout => Single<GrayAlpha16>(input, file, decoded, f),
        _ when layout == Rgb16.Layout => Single<Rgb16>(input, file, decoded, f),
        _ when layout == Rgba16.Layout => Single<Rgba16>(input, file, decoded, f),
        _ when layout == RgbaF16.Layout => Single<RgbaF16>(input, file, decoded, f),
        _ when layout == GrayF32.Layout => Single<GrayF32>(input, file, decoded, f),
        _ when layout == RgbF32.Layout => Single<RgbF32>(input, file, decoded, f),
        _ when layout == Cmyk8.Layout => Single<Cmyk8>(input, file, decoded, f),
        _ when layout == Cmyk16.Layout => Single<Cmyk16>(input, file, decoded, f),
        _ => Single<RgbaF32>(input, file, decoded, f),
    };

    private static Raster<TPixel> Single<TPixel>(EgamiInput input, JxlFile file, JxlDecoded decoded, JxlFrameHeader f)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> plane = Plane<TPixel>(input, decoded, spots: true, extra: -1);
        return new Raster<TPixel>([new Frame<TPixel>(plane, new FrameMetadata { Delay = Delay(f, file.Header) })], Metadata(input, file), input.Context);
    }

    /// <summary>
    /// Decodes the frames shown: the coalesced canvas after each key frame, those in <see cref="ReaderOptions.Frames"/> kept.
    /// Frames after the range are not decoded.
    /// </summary>
    private static Raster<TPixel> Shown<TPixel>(EgamiInput input, ReaderOptions options, JxlFile file, byte[] codestream, bool forceFloat, int extra = -1)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        bool spots = options is not JxlReaderOptions { RenderSpotColors: false };
        Range range = options.Frames;
        bool fromEnd = range.Start.IsFromEnd || range.End.IsFromEnd;
        int first = fromEnd ? 0 : range.Start.Value;
        int end = fromEnd ? int.MaxValue : range.End.Value;
        bool lfOnly = options is JxlReaderOptions { MaxDownscale: >= 8 };
        List<Frame<TPixel>> frames = [];
        int seen = 0;
        try
        {
            Run(file, codestream, input, Sequence(file), lfOnly, layers: false, (decoded, f) =>
            {
                int index = seen++;
                if (index >= first && index < end)
                {
                    if (forceFloat)
                    {
                        decoded.ForceFloat();
                    }

                    frames.Add(new Frame<TPixel>(Plane<TPixel>(input, decoded, spots, extra), new FrameMetadata { Delay = Delay(f, file.Header) }));
                }

                return seen < end;
            });

            if (fromEnd)
            {
                // A range counted from the end is only known once every frame is read.
                (int offset, int length) = AnimationCanvasRange.Clamp(range, frames.Count);
                for (int i = 0; i < frames.Count; i++)
                {
                    if (i < offset || i >= offset + length)
                    {
                        frames[i].Plane.Dispose();
                    }
                }

                frames = frames.GetRange(offset, length);
            }

            if (frames.Count == 0)
            {
                throw input.Corrupt(-1, "No frame is in the requested range.");
            }

            return new Raster<TPixel>(frames, Metadata(input, file), input.Context);
        }
        catch
        {
            foreach (Frame<TPixel> frame in frames)
            {
                frame.Plane.Dispose();
            }

            throw;
        }
    }

    /// <summary>Gets how long a frame shows, from its duration in ticks.</summary>
    private static TimeSpan Delay(JxlFrameHeader f, JxlImageHeader h)
    {
        return h.Animation is JxlAnimation a && a.TicksNumerator != 0 ? TimeSpan.FromSeconds((double)f.Duration * a.TicksDenominator / a.TicksNumerator) : TimeSpan.Zero;
    }

    /// <summary>
    /// Decodes the frames in order and calls <paramref name="emit"/> with each frame shown (the canvas after each key frame) or,
    /// with <paramref name="layers"/>, with each regular frame before blending; stops when it returns <see langword="false"/>.
    /// </summary>
    private static void Run(JxlFile file, byte[] codestream, EgamiInput input, List<(JxlFrameHeader Header, JxlToc Toc, long Offset)> sequence, bool lfOnly, bool layers, Func<JxlDecoded, JxlFrameHeader, bool> emit)
    {
        JxlImageHeader h = file.Header;
        float[][]?[] lfFrames = new float[4][][];
        JxlReference?[] references = new JxlReference?[4];
        int visible = 0;
        int invisible = 0;

        // The LF image alone (1:8): for a single VarDCT frame with nothing drawn on it and no extra channels.
        List<JxlFrameHeader> regular = [.. sequence.Select(s => s.Header).Where(f => f.Type != JxlFrameType.Lf)];
        lfOnly = lfOnly && !layers && regular.Count == 1 && regular[0] is { Modular: false, YCbCr: false, HaveCrop: false, Upsampling: 1 } only
            && (only.Flags & 0x13) == 0 && h.ExtraChannels.Count == 0;
        for (int i = 0; i < sequence.Count; i++)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            (JxlFrameHeader f, JxlToc toc, long offset) = sequence[i];

            // The frame's place in the sequence seeds its noise.
            if (f.IsKeyFrame)
            {
                visible++;
                invisible = 0;
            }
            else
            {
                invisible++;
            }

            bool referenced = !f.IsLast && f.Type != JxlFrameType.Lf && (f.Duration == 0 || f.SaveAsReference != 0);
            JxlFrameDecoder decoder = new(codestream, h, f, toc, offset, input.Limits) { Parallel = file.Parallel, Needed = f.IsLast && file.DisplayedFrames == 1 && !lfOnly && !layers ? file.Needed(f) : null };
            if (lfOnly && f.Type != JxlFrameType.Lf)
            {
                // From the LF frame when the frame has one, from its own LF groups otherwise.
                float[][] lf;
                int lw = (int)((f.Width + 7L) / 8);
                int lh = (int)((f.Height + 7L) / 8);
                if ((f.Flags & 0x20) != 0 && lfFrames[0] is float[][] fromFrame)
                {
                    lf = [.. fromFrame.Select(p => (float[])p.Clone())];
                }
                else
                {
                    JxlFrameChannels channels = decoder.Decode(lfFrames[f.LfLevel], lfOnly: true);
                    lf = channels.ColorPlanes!;
                }

                ToColorSpace(lf, lw * lh, file);
                emit(new JxlDecoded(file, lw, lh, lf, []), f);
                return;
            }

            JxlFrameChannels frame = decoder.Decode(lfFrames[f.LfLevel]);
            if (decoder.Needed is not null)
            {
                (int decoded, int total) = decoder.PassGroups;
                input.Emit(DiagnosticLevel.Debug, "Jxl.RegionGroups", $"ReaderOptions.Region needed {decoded} of {total} pass groups; the others were not decoded.");
            }
            if (sequence.Count == 1 && frame.ColorPlanes is null && !h.XybEncoded && file.Output!.Transform is null && !f.Gaborish && f.EpfIterations == 0 && (f.Flags & 0x13) == 0
                && f.Upsampling == 1 && f.ExtraChannelUpsampling.All(u => u == 1) && !f.HaveCrop
                && !h.ExtraChannels.Any(e => e.Type is JxlExtraChannelType.SpotColor or JxlExtraChannelType.Black || e.AlphaAssociated))
            {
                // Lossless Modular: keep the integers, so 8 and 16-bit output is exact.
                emit(new JxlDecoded(file, f.Width, f.Height, frame), f);
                return;
            }

            JxlPlanes planes = Render(f, frame, h, references, visible, invisible, file.Parallel);
            if (f.Type == JxlFrameType.Lf)
            {
                lfFrames[f.LfLevel - 1] = planes.Planes[..3];
                continue;
            }

            if (referenced && f.SaveBeforeColorTransform)
            {
                references[f.SaveAsReference] = new JxlReference(f.IsNormal ? planes.Copy() : planes, true, f.X0, f.Y0);
                if (!f.IsNormal)
                {
                    continue;
                }
            }

            if (f.YCbCr)
            {
                // A recompressed JPEG: its color transform is YCbCr to RGB, in the file's own color space.
                JxlColor.YCbCrToRgb(planes.Planes, planes.Width * planes.Height, file.Parallel);
            }

            ToColorSpace(planes.Planes, planes.Width * planes.Height, file);
            JxlPlanes canvas = planes;
            if (f.IsNormal)
            {
                if (layers && !emit(new JxlDecoded(file, planes.Width, planes.Height, planes.Planes[..3], planes.Planes[3..]), f))
                {
                    return;
                }

                bool replaceAll = f.Blending.Mode == JxlBlendMode.Replace && f.ExtraChannelBlending.All(b => b.Mode == JxlBlendMode.Replace);
                if (f.HaveCrop || !replaceAll)
                {
                    canvas = JxlBlend.Frame(f, planes, references, h);
                }
            }

            if (referenced && !f.SaveBeforeColorTransform)
            {
                references[f.SaveAsReference] = new JxlReference(canvas, false, f.IsNormal ? 0 : f.X0, f.IsNormal ? 0 : f.Y0);
            }

            if (!layers && f.IsNormal && f.IsKeyFrame && !emit(new JxlDecoded(file, canvas.Width, canvas.Height, canvas.Planes[..3], canvas.Planes[3..]), f))
            {
                return;
            }
        }

        if (!layers && visible == 0)
        {
            throw new CorruptDataException(JxlFormat.Info.Name, -1, "The codestream has no frame to show.");
        }
    }

    /// <summary>Converts a Modular XYB frame's integer Y, X and B into X, Y and B floats (B coded as B - Y).</summary>
    private static float[][] ModularXyb(JxlFrameChannels frame, int count)
    {
        int[] y = frame.Channels[0].Data;
        int[] x = frame.Channels[1].Data;
        int[] b = frame.Channels[2].Data;
        float mx = frame.LfDequant[0] / 128;
        float my = frame.LfDequant[1] / 128;
        float mb = frame.LfDequant[2] / 128;
        float[][] planes = [new float[count], new float[count], new float[count]];
        for (int i = 0; i < count; i++)
        {
            planes[0][i] = x[i] * mx;
            planes[1][i] = y[i] * my;
            planes[2][i] = (int)Math.Clamp((long)b[i] + y[i], int.MinValue, int.MaxValue) * mb;
        }

        return planes;
    }

    /// <summary>
    /// Brings a subsampled JPEG chroma plane to full size as libjxl does: each step doubles it with weights of 3/4 for the
    /// sample and 1/4 for its neighbor on that side, mirrored at the edges; horizontally first, then vertically.
    /// </summary>
    private static float[] ChromaUpsample(float[] plane, int stride, int hs, int vs, int width, int height)
    {
        int w = (width + (1 << hs) - 1) >> hs;
        int h = (height + (1 << vs) - 1) >> vs;
        float[] p = Crop(plane, stride, w, h);
        if (hs == 1)
        {
            float[] wide = new float[w * 2 * h];
            for (int y = 0; y < h; y++)
            {
                int row = y * w;
                for (int x = 0; x < w; x++)
                {
                    float current = p[row + x] * 0.75f;
                    wide[(row * 2) + (2 * x)] = MathF.FusedMultiplyAdd(0.25f, p[row + JxlFilters.Mirror(x - 1, w)], current);
                    wide[(row * 2) + (2 * x) + 1] = MathF.FusedMultiplyAdd(0.25f, p[row + JxlFilters.Mirror(x + 1, w)], current);
                }
            }

            (p, w) = (wide, w * 2);
        }

        if (vs == 1)
        {
            float[] tall = new float[w * h * 2];
            for (int y = 0; y < h; y++)
            {
                int top = JxlFilters.Mirror(y - 1, h) * w;
                int mid = y * w;
                int bottom = JxlFilters.Mirror(y + 1, h) * w;
                for (int x = 0; x < w; x++)
                {
                    float scaled = p[mid + x] * 0.75f;
                    tall[(2 * y * w) + x] = MathF.FusedMultiplyAdd(p[top + x], 0.25f, scaled);
                    tall[(((2 * y) + 1) * w) + x] = MathF.FusedMultiplyAdd(p[bottom + x], 0.25f, scaled);
                }
            }

            (p, h) = (tall, h * 2);
        }

        return Crop(p, w, width, height);
    }

    private static float[] Crop(float[] plane, int stride, int width, int height)
    {
        if (stride == width && plane.Length == width * height)
        {
            return plane;
        }

        float[] output = new float[width * height];
        for (int y = 0; y < height; y++)
        {
            plane.AsSpan(y * stride, width).CopyTo(output.AsSpan(y * width));
        }

        return output;
    }

    /// <summary>
    /// Renders a decoded frame up to its color transform, in libjxl's order: the restoration filters, extra channel upsampling,
    /// patches, splines, color upsampling, then noise. LF frames stay at their coded size.
    /// </summary>
    private static JxlPlanes Render(JxlFrameHeader f, JxlFrameChannels frame, JxlImageHeader h, JxlReference?[] references, int visible, int invisible, JxlParallel parallel)
    {
        int width = frame.Width;
        int height = frame.Height;
        bool xyb = h.XybEncoded;
        float[][] color;
        int count = width * height;
        if (frame.ColorPlanes is float[][] planes)
        {
            color = frame.VarDct is { Subsampled: true } v
                ? [.. planes.Select((p, c) => ChromaUpsample(p, frame.ColorStrides[c], v.HShift(c), v.VShift(c), width, height))]
                : [.. planes.Select((p, c) => Crop(p, frame.ColorStrides[c], width, height))];
        }
        else if (xyb)
        {
            color = ModularXyb(frame, count);
        }
        else
        {
            color = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                int[] data = frame.Channels[Math.Min(c, frame.ColorChannels - 1)].Data;
                float[] plane = new float[count];
                for (int i = 0; i < count; i++)
                {
                    plane[i] = Float(data[i], h.BitDepth);
                }

                color[c] = plane;
            }
        }

        if (f.Gaborish)
        {
            JxlFilters.Gaborish(color, width, height, width, f.LoopFilter.GaborishWeights, parallel);
        }

        if (f.EpfIterations > 0)
        {
            float[]? sigma = frame.VarDct?.EpfSigma;
            int blocksX = frame.VarDct?.BlocksX ?? 0;
            float modularSigma = f.LoopFilter.SigmaForModular;
            JxlFilters.Epf(color, width, height, width, f.LoopFilter, sigma is null ? (_, _) => modularSigma : (bx, by) => sigma[(by * blocksX) + bx], parallel);
        }

        (int outWidth, int outHeight) = f.Type == JxlFrameType.Lf ? (width, height) : (f.Width, f.Height);

        // Extra channels: as floats, upsampled to the frame's full size where they were coded smaller; with the color channels when
        // they all share its factor, before the patches otherwise.
        bool late = f.Upsampling != 1 && f.ExtraChannelUpsampling.All(u => u == f.Upsampling);
        float[][] extras = new float[h.ExtraChannels.Count][];
        for (int e = 0; e < extras.Length; e++)
        {
            JxlChannel c = frame.Channels[frame.ExtraStart + e];
            float[] plane = new float[c.Width * c.Height];
            for (int i = 0; i < plane.Length; i++)
            {
                plane[i] = Float(c.Data[i], h.ExtraChannels[e].BitDepth);
            }

            int factor = f.ExtraChannelUpsampling[e];
            if (factor > 1 && !late)
            {
                plane = Crop(JxlFilters.Upsample(plane, c.Width, c.Height, c.Width, factor, h, parallel), c.Width * factor, outWidth, outHeight);
            }

            extras[e] = plane;
        }

        bool colorFeatures = xyb || h.ColorChannels == 3;
        if (frame.Patches is not null || frame.Splines is not null)
        {
            if (f.Upsampling != 1 && !late)
            {
                throw new CorruptDataException(JxlFormat.Info.Name, -1, "A frame with patches or splines upsamples its extra channels differently from its color.");
            }

            JxlPlanes coded = new(width, height, [.. color, .. extras]);
            if (frame.Patches is JxlPatch[] patches)
            {
                JxlFeatures.ApplyPatches(coded, patches, references, h);
            }

            if (frame.Splines is JxlSplines splines && colorFeatures)
            {
                JxlFeatures.RenderSplines(coded, splines, frame.Correlation.X, frame.Correlation.B, parallel);
            }
        }

        if (f.Upsampling > 1)
        {
            for (int c = 0; c < 3; c++)
            {
                color[c] = Crop(JxlFilters.Upsample(color[c], width, height, width, f.Upsampling, h, parallel), width * f.Upsampling, outWidth, outHeight);
            }

            for (int e = 0; e < extras.Length && late; e++)
            {
                extras[e] = Crop(JxlFilters.Upsample(extras[e], width, height, width, f.Upsampling, h, parallel), width * f.Upsampling, outWidth, outHeight);
            }
        }

        JxlPlanes result = new(outWidth, outHeight, [.. color, .. extras]);
        if (frame.Noise is float[] noise && colorFeatures)
        {
            JxlFeatures.RenderNoise(result, noise, f.GroupDim, visible, invisible, frame.Correlation.X, frame.Correlation.B);
        }

        return result;
    }

    /// <summary>
    /// Converts decoded color in place into the output space: XYB through linear sRGB, by the transfer function alone when the
    /// space has sRGB primaries and a D65 white, else through the 2.1.2 color pipeline (design question 4); other color only
    /// for linear output.
    /// </summary>
    private static void ToColorSpace(float[][] color, int count, JxlFile file)
    {
        const int Chunk = 1 << 15;
        file.Parallel.For((count + Chunk - 1) / Chunk, i => ToColorSpace(color, i * Chunk, Math.Min(count, (i + 1) * Chunk), file));
        JxlOutput output = file.Output!;
        if (!file.Header.XybEncoded && output.Transform is null)
        {
            return;
        }

        // A gray result lands in one plane (1 from XYB, 0 from a transform); all three planes then share it.
        if (output.Transform?.Target.Model == ColorModel.Gray && color.Length >= 3)
        {
            color[1] = color[0];
            color[2] = color[0];
        }
        else if (output.Transform is null && output.Gray)
        {
            color[0] = color[1];
            color[2] = color[1];
        }
    }

    /// <summary>Converts samples <paramref name="start"/> to <paramref name="end"/> of each plane.</summary>
    private static void ToColorSpace(float[][] color, int start, int end, JxlFile file)
    {
        JxlImageHeader h = file.Header;
        JxlOutput output = file.Output!;
        int count = end - start;
        if (h.XybEncoded)
        {
            JxlColor.XybToLinear(color, start, end, h, output.Opsin);
        }

        if (output.Transform is ColorTransform transform)
        {
            JxlColor.Apply(transform, color, start, end);
            return;
        }

        if (!h.XybEncoded)
        {
            return;
        }

        if (output.Matrix is float[] m && output.Opsin is null)
        {
            float[] r = color[0];
            float[] g = color[1];
            float[] b = color[2];
            for (int i = start; i < end; i++)
            {
                (r[i], g[i], b[i]) = ((m[0] * r[i]) + (m[1] * g[i]) + (m[2] * b[i]), (m[3] * r[i]) + (m[4] * g[i]) + (m[5] * b[i]), (m[6] * r[i]) + (m[7] * g[i]) + (m[8] * b[i]));
            }
        }

        if (output.Gray)
        {
            // Gray: the luminance of linear sRGB, in plane 1.
            for (int i = start; i < end; i++)
            {
                color[1][i] = (0.2126f * color[0][i]) + (0.7152f * color[1][i]) + (0.0722f * color[2][i]);
            }
        }

        if (output.Curve is JxlColorEncoding curve)
        {
            float target = h.ToneMapping.IntensityTarget;
            if (curve is { Gamma: 0, TransferFunction: 18 })
            {
                Span<float> y = color[1].AsSpan(start, count);
                JxlColor.HlgToSceneLight(output.Gray ? y : color[0].AsSpan(start, count), y, output.Gray ? y : color[2].AsSpan(start, count), output.Gray ? [0, 1, 0] : output.Luminances, target);
            }

            foreach (int c in output.Gray ? (ReadOnlySpan<int>)[1] : [0, 1, 2])
            {
                JxlColor.Encode(color[c].AsSpan(start, count), curve, target);
            }
        }
    }

    /// <summary>Gets the raster metadata: ICC, Exif, XMP, orientation and the container's warnings.</summary>
    private static RasterMetadata Metadata(EgamiInput input, JxlFile file)
    {
        JxlImageHeader h = file.Header;
        RasterMetadata metadata = new()
        {
            FormatName = JxlFormat.Info.Name,
            FormatDetails = JxlDetails.From(file),
        };
        if (h.Animation is JxlAnimation animation)
        {
            metadata.LoopCount = (int)Math.Min(animation.Loops, int.MaxValue);
        }

        // The space the pixels are in (design question 4): the file's ICC profile, sRGB or linear sRGB by hint, else a profile written for it.
        JxlOutput output = file.Output!;
        if (file.Icc is byte[] icc && !output.Linear)
        {
            metadata.IccProfile = icc;
            metadata.ColorSpace = ColorSpaceHint.EmbeddedIcc;
        }
        else if (output.Profile == ColorProfile.Srgb)
        {
            metadata.ColorSpace = ColorSpaceHint.Srgb;
        }
        else if (output.Profile == ColorProfile.LinearSrgb)
        {
            metadata.ColorSpace = ColorSpaceHint.Linear;
        }
        else if (output.Profile.IsEquivalentTo(ColorProfile.Rec2100Pq) || output.Profile.IsEquivalentTo(ColorProfile.Rec2100Hlg))
        {
            // PQ and HLG as the file says (design question 12), named by their H.273 code points.
            metadata.CodePoints = output.Profile.IsEquivalentTo(ColorProfile.Rec2100Pq) ? ColorCodePoints.Rec2100Pq : ColorCodePoints.Rec2100Hlg;
        }
        else
        {
            metadata.IccProfile = output.Profile.ToIcc();
            metadata.ColorSpace = ColorSpaceHint.EmbeddedIcc;
        }

        // intensity_target is the brightest light in the image; min_nits the darkest.
        JxlToneMapping tone = h.ToneMapping;
        if (output.Profile.Transfer?.IsHdr == true || tone.IntensityTarget != 255 || tone.MinNits > 0)
        {
            metadata.Hdr = new HdrMetadata
            {
                MaxContentLightLevel = tone.IntensityTarget,
                MasteringMinLuminance = tone.MinNits > 0 ? tone.MinNits : null,
            };
        }

        if (file.Container.GainMap is byte[] bundle && input.Context is EgamiContext context)
        {
            JxlGainMap.Read(context, bundle, metadata);
        }

        if (file.Container.Exif is byte[] exif)
        {
            metadata.Exif = Jpeg.JpegSegmentReader.ParseExif(exif, input.Limits.MaxMetadataBytes, metadata.WarningList);
        }

        if (file.Container.Xmp is byte[] xmp)
        {
            metadata.Xmp = xmp;
        }

        // The codestream's orientation is the one that counts (design question 11); AutoOrient reads it from EXIF.
        if (h.Orientation != 1 || metadata.Exif is not null)
        {
            metadata.Exif ??= new ExifData();
            metadata.Exif.Orientation = (ImageOrientation)h.Orientation;
        }

        foreach (JxlProblem problem in file.Container.Problems)
        {
            metadata.WarningList.Add(problem.Id, problem.Message, problem.Offset);
        }

        return metadata;
    }

    /// <summary>
    /// Gets the color as Load gives it: each spot color mixed in by its coverage and solidity (as libjxl renders them, in the
    /// output color space after blending), then divided by a premultiplied alpha. Copies only when something changes.
    /// </summary>
    private static float[][] Straight(JxlImageHeader h, JxlDecoded d, bool spots)
    {
        float[][] color = d.Color!;
        int alpha = h.AlphaIndex;
        bool premultiplied = alpha >= 0 && h.ExtraChannels[alpha].AlphaAssociated;
        List<int> spotChannels = spots ? [.. Enumerable.Range(0, h.ExtraChannels.Count).Where(e => h.ExtraChannels[e].Type == JxlExtraChannelType.SpotColor)] : [];
        if (!premultiplied && spotChannels.Count == 0)
        {
            return color;
        }

        color = [.. color.Select(p => (float[])p.Clone())];
        int count = d.Width * d.Height;
        foreach (int e in spotChannels)
        {
            float[] spot = h.ExtraChannels[e].SpotColor!;
            float[] coverage = d.Extras![e];
            for (int c = 0; c < 3; c++)
            {
                float[] plane = color[c];
                for (int i = 0; i < count; i++)
                {
                    float mix = spot[3] * coverage[i];
                    plane[i] = (mix * spot[c]) + ((1 - mix) * plane[i]);
                }
            }
        }

        if (premultiplied)
        {
            // As libjxl: alpha below 2^-26 counts as 2^-26.
            float[] a = d.Extras![alpha];
            for (int c = 0; c < 3; c++)
            {
                float[] plane = color[c];
                for (int i = 0; i < count; i++)
                {
                    plane[i] /= MathF.Max(1f / (1 << 26), a[i]);
                }
            }
        }

        return color;
    }

    /// <summary>
    /// Puts a decoded frame's samples into a plane of <typeparamref name="TPixel"/>: spot colors mixed in (with
    /// <paramref name="spots"/>), premultiplied color made straight, CMYK as ink amounts; or, with <paramref name="extra"/> 0 or
    /// more, that extra channel alone.
    /// </summary>
    private static PixelPlane<TPixel> Plane<TPixel>(EgamiInput input, JxlDecoded d, bool spots, int extra)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        JxlImageHeader h = d.File.Header;
        if (extra >= 0 || TPixel.Layout == Cmyk8.Layout || TPixel.Layout == Cmyk16.Layout)
        {
            d.ForceFloat();
        }

        float[][]? color = d.Color is null ? null : Straight(h, d, spots);
        input.CheckMemory((long)d.Width * d.Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
        PixelPlane<TPixel> raster = PixelPlane.Allocate<TPixel>(d.Width, d.Height, input.Memory);
        try
        {
            PixelLayoutInfo layout = TPixel.Layout;
            int samples = layout.ChannelCount;
            bool alpha = layout.HasAlpha;
            int colorSamples = alpha ? samples - 1 : samples;
            int alphaIndex = h.AlphaIndex;
            bool cmyk = layout == Cmyk8.Layout || layout == Cmyk16.Layout;
            int blackIndex = h.ExtraChannels.ToList().FindIndex(e => e.Type == JxlExtraChannelType.Black);
            float[]? black = cmyk ? d.Extras![blackIndex] : null;
            if (d.Ints is null && extra < 0 && !cmyk && layout.ChannelType is PixelChannelType.Unorm8 or PixelChannelType.Unorm16)
            {
                // Each sample's plane picked once; rows on several threads.
                float[]?[] planes = new float[samples][];
                for (int s = 0; s < samples; s++)
                {
                    bool isAlpha = alpha && s == samples - 1;
                    planes[s] = isAlpha ? (alphaIndex >= 0 ? d.Extras![alphaIndex] : null) : color![Math.Min(s, colorSamples - 1) % 3];
                }

                bool wide = layout.ChannelType == PixelChannelType.Unorm16;
                d.File.Parallel.For(d.Height, y =>
                {
                    Span<int> rounded = stackalloc int[8];
                    Span<byte> bytes = MemoryMarshal.AsBytes(raster.Row(y));
                    Span<ushort> words = MemoryMarshal.Cast<byte, ushort>(bytes);
                    int at = y * d.Width;
                    for (int s = 0; s < samples; s++)
                    {
                        float[]? plane = planes[s];
                        int x0 = 0;
                        if (!wide && plane is not null && System.Runtime.Intrinsics.X86.Avx.IsSupported)
                        {
                            // Eight samples at a time: clamped, scaled and rounded to nearest even, as MathF.Round does.
                            System.Runtime.Intrinsics.Vector256<float> zero = System.Runtime.Intrinsics.Vector256<float>.Zero;
                            System.Runtime.Intrinsics.Vector256<float> one = System.Runtime.Intrinsics.Vector256<float>.One;
                            System.Runtime.Intrinsics.Vector256<float> max = System.Runtime.Intrinsics.Vector256.Create(255f);
                            for (; x0 + 8 <= d.Width; x0 += 8)
                            {
                                System.Runtime.Intrinsics.Vector256<float> f = System.Runtime.Intrinsics.Vector256.Create(plane.AsSpan(at + x0, 8));
                                f = System.Runtime.Intrinsics.Vector256.Min(System.Runtime.Intrinsics.Vector256.Max(f, zero), one) * max;
                                System.Runtime.Intrinsics.X86.Avx.ConvertToVector256Int32(System.Runtime.Intrinsics.X86.Avx.RoundToNearestInteger(f)).CopyTo(rounded);
                                for (int k = 0; k < 8; k++)
                                {
                                    bytes[((x0 + k) * samples) + s] = (byte)rounded[k];
                                }
                            }
                        }

                        for (int x = x0; x < d.Width; x++)
                        {
                            float f = plane is null ? 1f : plane[at + x];
                            int i = (x * samples) + s;
                            if (wide)
                            {
                                words[i] = (ushort)MathF.Round(Math.Clamp(f, 0, 1) * 65535);
                            }
                            else
                            {
                                bytes[i] = (byte)MathF.Round(Math.Clamp(f, 0, 1) * 255);
                            }
                        }
                    }
                }, minimumPerThread: 16);
                return raster;
            }

            for (int y = 0; y < d.Height; y++)
            {
                Span<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
                int at = y * d.Width;
                for (int x = 0; x < d.Width; x++)
                {
                    for (int s = 0; s < samples; s++)
                    {
                        bool isAlpha = alpha && s == samples - 1;
                        int i = (x * samples) + s;
                        if (d.Ints is JxlFrameChannels ints)
                        {
                            int[]? plane = isAlpha ? (alphaIndex >= 0 ? ints.Channels[ints.ExtraStart + alphaIndex].Data : null) : ints.Channels[Math.Min(s, ints.ColorChannels - 1)].Data;
                            JxlBitDepth depth = isAlpha && alphaIndex >= 0 ? h.ExtraChannels[alphaIndex].BitDepth : h.BitDepth;
                            int v = plane?[at + x] ?? 0;
                            switch (layout.ChannelType)
                            {
                                case PixelChannelType.Unorm8:
                                    row[i] = plane is null ? (byte)255 : (byte)Scale(v, depth, 255);
                                    break;
                                case PixelChannelType.Unorm16:
                                    MemoryMarshal.Cast<byte, ushort>(row)[i] = plane is null ? (ushort)65535 : (ushort)Scale(v, depth, 65535);
                                    break;
                                case PixelChannelType.Float16:
                                    MemoryMarshal.Cast<byte, Half>(row)[i] = (Half)(plane is null ? 1f : Float(v, depth));
                                    break;
                                default:
                                    MemoryMarshal.Cast<byte, float>(row)[i] = plane is null ? 1f : Float(v, depth);
                                    break;
                            }

                            continue;
                        }

                        float f = extra >= 0 ? d.Extras![extra][at + x]
                            : cmyk ? 1 - (s < 3 ? color![s][at + x] : black![at + x])
                            : isAlpha ? (alphaIndex >= 0 ? d.Extras![alphaIndex][at + x] : 1f) : color![Math.Min(s, colorSamples - 1) % 3][at + x];
                        switch (layout.ChannelType)
                        {
                            case PixelChannelType.Unorm8:
                                row[i] = (byte)MathF.Round(Math.Clamp(f, 0, 1) * 255);
                                break;
                            case PixelChannelType.Unorm16:
                                MemoryMarshal.Cast<byte, ushort>(row)[i] = (ushort)MathF.Round(Math.Clamp(f, 0, 1) * 65535);
                                break;
                            case PixelChannelType.Float16:
                                MemoryMarshal.Cast<byte, Half>(row)[i] = (Half)f;
                                break;
                            default:
                                MemoryMarshal.Cast<byte, float>(row)[i] = f;
                                break;
                        }
                    }
                }
            }

            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>Scales an integer sample of <paramref name="depth"/> bits to 0 to <paramref name="max"/>, rounding.</summary>
    private static int Scale(int v, JxlBitDepth depth, int max)
    {
        if (depth.IsFloat)
        {
            return (int)Math.Round(Math.Clamp(Float(v, depth), 0f, 1f) * max);
        }

        long top = (1L << depth.BitsPerSample) - 1;
        long clamped = Math.Clamp(v, 0, top);
        return top == max ? (int)clamped : (int)(((clamped * max * 2) + top) / (2 * top));
    }

    /// <summary>A decoded image: either the Modular integers as coded (lossless), or float planes after filters and color.</summary>
    private sealed class JxlDecoded
    {
        internal JxlDecoded(JxlFile file, int width, int height, JxlFrameChannels ints)
        {
            File = file;
            Width = width;
            Height = height;
            Ints = ints;
        }

        internal JxlDecoded(JxlFile file, int width, int height, float[][] color, float[][] extras)
        {
            File = file;
            Width = width;
            Height = height;
            Color = color;
            Extras = extras;
        }

        internal JxlFile File { get; }

        internal int Width { get; }

        internal int Height { get; }

        internal JxlFrameChannels? Ints { get; private set; }

        internal float[][]? Color { get; private set; }

        internal float[][]? Extras { get; private set; }

        /// <summary>Turns the integer form into floats, unclamped.</summary>
        internal void ForceFloat()
        {
            if (Ints is not JxlFrameChannels ints)
            {
                return;
            }

            JxlImageHeader h = File.Header;
            int count = Width * Height;
            Color = new float[3][];
            for (int c = 0; c < 3; c++)
            {
                int[] data = ints.Channels[Math.Min(c, ints.ColorChannels - 1)].Data;
                float[] plane = new float[count];
                for (int i = 0; i < count; i++)
                {
                    plane[i] = Float(data[i], h.BitDepth);
                }

                Color[c] = plane;
            }

            Extras = new float[h.ExtraChannels.Count][];
            for (int e = 0; e < h.ExtraChannels.Count; e++)
            {
                int[] data = ints.Channels[ints.ExtraStart + e].Data;
                float[] plane = new float[count];
                for (int i = 0; i < count; i++)
                {
                    plane[i] = Float(data[i], h.ExtraChannels[e].BitDepth);
                }

                Extras[e] = plane;
            }

            Ints = null;
        }
    }

    /// <summary>Converts every frame of a raster.</summary>
    private sealed class Converter<TPixel>(EgamiInput input) : IRasterVisitor<Raster<TPixel>>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        public Raster<TPixel> Visit<TFrom>(Raster<TFrom> source)
            where TFrom : unmanaged, IPixelType<TFrom>
        {
            List<Frame<TPixel>> frames = [];
            try
            {
                foreach (Frame<TFrom> frame in source.Frames)
                {
                    input.CheckMemory((long)source.Width * source.Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
                    PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(source.Width, source.Height, input.Memory);
                    frames.Add(new Frame<TPixel>(plane, frame.Metadata.Copy()));
                    for (int y = 0; y < source.Height; y++)
                    {
                        PixelConvert.Convert<TFrom, TPixel>(frame.Row(y), plane.Row(y));
                    }
                }

                return new Raster<TPixel>(frames, source.Metadata.Copy(), input.Context);
            }
            catch
            {
                foreach (Frame<TPixel> frame in frames)
                {
                    frame.Plane.Dispose();
                }

                throw;
            }
        }
    }
}
