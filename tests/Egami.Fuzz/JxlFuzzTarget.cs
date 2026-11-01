using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Fuzz;

/// <summary>
/// The JPEG XL fuzz targets (Phase 2.9.3). jxl loads files every way: Load and LoadAll, as Rgba8 from a stream that cannot
/// seek, the layers, the preview, the LF image alone, a region, each extra channel and the JPEG rebuilt from a jbrd box.
/// jxlbox puts its input after a JXL signature and ftyp box and before a small codestream, so mutations reach the box reader
/// (brob, Exif, xml, jbrd, jhgm, jxlp) directly. jxlentropy feeds raw bits to the entropy decoder, the MA tree and Modular
/// stream reader, and the ICC profile decoder, its first byte picking which. jxl-write runs the lossless encoder: the input picks a
/// pixel type, size, effort and pixels, and the file must load back to exactly those pixels; jxl-jpeg recompresses its input as
/// a JPEG, and a file it makes must load and rebuild the input byte for byte. jxl-lossy (Phase 2.9.7) runs the lossy encoder
/// with options the input picks (effort, distance, progressive, photon noise, alpha distance, transparent colors) on pixels it
/// gives, as small images or, now and then, one of several groups: the file must load at the raster's size with finite
/// samples, and exact alpha (below distance 10) must come back exactly. Only EgamiExceptions may escape the readers.
/// </summary>
internal static class JxlFuzzTarget
{
    private static readonly byte[] Signature = [0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 0x0D, 0x0A, 0x87, 0x0A];
    private static readonly byte[] Ftyp = [0, 0, 0, 20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', (byte)'j', (byte)'x', (byte)'l', (byte)' ', 0, 0, 0, 0, (byte)'j', (byte)'x', (byte)'l', (byte)' '];

    // A 1×1 lossless codestream, as the jxlc box closing every jxlbox input.
    private static readonly Lazy<byte[]> Tiny = new(() =>
    {
        using Raster<Rgb8> pixel = Raster.Create<Rgb8>(1, 1, new Rgb8(10, 20, 30));
        using MemoryStream s = new();
        pixel.Save(s, JxlFormat.Info, new JxlWriterOptions { Lossless = true, Effort = 1 });
        return s.ToArray();
    });

    public static void Load(byte[] data, EgamiContext context)
    {
        context = context with { Formats = [JxlFormat.Info], Limits = context.Limits with { MaxFrames = 32 } };
        ReaderOptions[] all =
        [
            new() { Recovery = RecoveryMode.Lenient },
            new() { Recovery = RecoveryMode.Strict },
            new JxlReaderOptions { MaxDownscale = 8 },
            new JxlReaderOptions { LinearOutput = true, RenderSpotColors = false },
            new() { Frames = ^1.. },
        ];

        // One extra reading per input, picked by its length, keeps an execution within AFL's time limit.
        foreach (ReaderOptions options in new[] { all[0], all[1 + (data.Length % 4)] })
        {
            try
            {
                RasterInfo info = Raster.Probe(data, context, options);
                using Raster raster = Raster.Load(data, context, options);
                using Raster<Rgba8> converted = Raster.Load<Rgba8>(new ForwardOnlyStream(data), context, options);
                if (converted.Width != raster.Width || converted.Height != raster.Height)
                {
                    throw new InvalidOperationException("A JPEG XL file loads at two sizes.");
                }

                if (options == all[0])
                {
                    Extras(data, context, raster, info);
                }
            }
            catch (EgamiException)
            {
            }
        }
    }

    private static void Extras(byte[] data, EgamiContext context, Raster raster, RasterInfo info)
    {
        try
        {
            using RasterList frames = Raster.LoadAll(data, context);
        }
        catch (EgamiException)
        {
        }

        try
        {
            foreach (JxlLayer layer in JxlImage.ReadLayers(new MemoryStream(data), context: context))
            {
                layer.Dispose();
            }

            using Raster? preview = JxlImage.ReadPreview(new MemoryStream(data), context: context);
        }
        catch (EgamiException)
        {
        }

        if (raster.Width > 2 && raster.Height > 2)
        {
            try
            {
                Rect region = new(raster.Width / 3, raster.Height / 3, Math.Max(1, raster.Width / 3), Math.Max(1, raster.Height / 3));
                using Raster part = Raster.Load(data, context, new ReaderOptions { Region = region });
            }
            catch (EgamiException)
            {
            }
        }

        if (raster.Metadata.FormatDetails is JxlDetails details)
        {
            for (int i = 0; i < Math.Min(details.ExtraChannels.Count, 3); i++)
            {
                try
                {
                    using Raster<GrayF32> channel = JxlImage.ReadExtraChannel(new MemoryStream(data), i, context: context);
                }
                catch (EgamiException)
                {
                }
            }

            if (details.HasJpegReconstruction)
            {
                try
                {
                    JxlImage.ReconstructJpeg(new MemoryStream(data), Stream.Null, context);
                }
                catch (EgamiException)
                {
                }
            }
        }

        _ = info;
    }

    public static void LoadBoxes(byte[] data, EgamiContext context)
    {
        byte[] tiny = Tiny.Value;
        byte[] file = new byte[Signature.Length + Ftyp.Length + data.Length + 8 + tiny.Length];
        Signature.CopyTo(file, 0);
        Ftyp.CopyTo(file, Signature.Length);
        data.CopyTo(file, Signature.Length + Ftyp.Length);
        int at = Signature.Length + Ftyp.Length + data.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(at), (uint)(8 + tiny.Length));
        "jxlc"u8.CopyTo(file.AsSpan(at + 4));
        tiny.CopyTo(file, at + 8);
        context = context with { Formats = [JxlFormat.Info] };
        foreach (RecoveryMode recovery in new[] { RecoveryMode.Lenient, RecoveryMode.Strict })
        {
            try
            {
                using Raster raster = Raster.Load(file, context, new ReaderOptions { Recovery = recovery });
                _ = raster.Metadata.Exif;
                _ = raster.Metadata.Xmp;
                if (raster.Metadata.FormatDetails is JxlDetails { HasJpegReconstruction: true })
                {
                    JxlImage.ReconstructJpeg(new MemoryStream(file), Stream.Null, context);
                }
            }
            catch (EgamiException)
            {
            }
        }
    }

    public static void DecodeEntropy(byte[] data, EgamiContext context)
    {
        if (data.Length < 2)
        {
            return;
        }

        JxlBitReader r = new(data, 2, data.Length - 2);
        try
        {
            switch (data[0] % 3)
            {
                case 0:
                    {
                        // A distribution set for up to 64 contexts, then symbols until the bits run out (at most 4096).
                        int contexts = 1 + (data[1] % 64);
                        JxlEntropyDecoder decoder = JxlEntropyDecoder.Read(r, contexts);
                        decoder.Begin(r);
                        for (int i = 0; i < 4096 && r.BitPosition < (long)(data.Length - 2) * 8; i++)
                        {
                            _ = decoder.ReadUint(r, i % contexts);
                        }

                        break;
                    }

                case 1:
                    {
                        // A Modular image: an MA tree, then a stream of up to three channels of up to 64 × 64 using it.
                        JxlTree tree = JxlTree.Read(r, 1024);
                        int w = 1 + (data[1] % 64), h = 1 + ((data[1] >> 2) % 64);
                        List<JxlChannel> channels = [.. Enumerable.Range(0, 1 + (data[1] % 3)).Select(_ => new JxlChannel(w, h, 0, 0))];
                        JxlModularImage image = JxlModularImage.Read(r, channels, tree, 8 + (data[1] % 9), context.Limits);
                        image.Decode(r, 0, 0, image.Channels.Count);
                        image.Inverse(r);
                        break;
                    }

                default:
                    _ = JxlIcc.Read(r, context.Limits);
                    break;
            }
        }
        catch (EgamiException)
        {
        }
    }

    public static void Write(byte[] data, EgamiContext context)
    {
        if (data.Length < 4)
        {
            return;
        }

        int width = 1 + (data[1] % 48);
        int effort = 1 + (data[2] % 7);
        JxlWriterOptions options = new() { Lossless = true, Effort = effort };
        switch (data[0] % 8)
        {
            case 0:
                RoundTrip<Gray8>(data, width, options, context);
                break;
            case 1:
                RoundTrip<GrayAlpha8>(data, width, options, context);
                break;
            case 2:
                RoundTrip<Rgb8>(data, width, options, context);
                break;
            case 3:
                RoundTrip<Rgba8>(data, width, options, context);
                break;
            case 4:
                RoundTrip<Gray16>(data, width, options, context);
                break;
            case 5:
                RoundTrip<Rgb16>(data, width, options, context);
                break;
            case 6:
                RoundTrip<Rgba16>(data, width, options, context);
                break;
            default:
                RoundTrip<RgbF32>(data, width, options, context);
                break;
        }
    }

    private static void RoundTrip<T>(byte[] data, int width, JxlWriterOptions options, EgamiContext context)
        where T : unmanaged, IPixelType<T>
    {
        int pixelBytes = T.Layout.BytesPerPixel;
        int height = Math.Clamp((data.Length - 4) / pixelBytes / width, 1, 48);
        using Raster<T> raster = Raster.Create<T>(width, height, context);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
            int at = 4 + (y * width * pixelBytes);
            if (at < data.Length)
            {
                data.AsSpan(at, Math.Min(row.Length, data.Length - at)).CopyTo(row);
            }
        }

        // The writer keeps every bit pattern, NaNs included, so the file must load back to the same bytes.
        using MemoryStream file = new();
        raster.Save(file, JxlFormat.Info, options);
        using Raster<T> again = Raster.Load<T>(file.ToArray(), context);
        for (int y = 0; y < height; y++)
        {
            if (!MemoryMarshal.AsBytes(raster.Row(y)).SequenceEqual(MemoryMarshal.AsBytes(again.Row(y))))
            {
                throw new InvalidOperationException($"The lossless {typeof(T).Name} round trip at effort {options.Effort} differs in row {y}.");
            }
        }
    }

    public static void Lossy(byte[] data, EgamiContext context)
    {
        if (data.Length < 9)
        {
            return;
        }

        // Byte 1 picks the size: mostly up to 48 pixels a side, sometimes past one group (the input's pixels repeat over it).
        bool large = data[1] >= 248;
        int width = large ? 200 + (data[1] * 7 % 200) : 1 + (data[1] % 48);
        float distance = data[3] < 128 ? 0.1f + (data[3] / 128f * 4) : 0.1f + ((data[3] - 128) / 127f * 24.9f);
        JxlWriterOptions options = new()
        {
            Lossless = false,
            // Efforts 9 and 10 parse LZ77 optimally, which on the input's repeating pixels takes as long as cjxl's (seconds),
            // so large frames stop at 8.
            Effort = 1 + (data[2] % (large ? 8 : 10)),
            Distance = distance,
            Progressive = (data[4] & 1) != 0,
            PhotonNoiseIso = (data[4] & 2) != 0 ? 100 + (data[5] * 100) : null,
            AlphaDistance = (data[4] & 4) != 0 ? data[6] / 255f * 25 : 0,
            KeepTransparentColors = (data[4] & 8) == 0,
        };
        switch (data[0] % 6)
        {
            case 0:
                LossyTrip<Gray8>(data, width, large, options, context);
                break;
            case 1:
                LossyTrip<Rgb8>(data, width, large, options, context);
                break;
            case 2:
                LossyTrip<Rgba8>(data, width, large, options, context);
                break;
            case 3:
                LossyTrip<Rgba16>(data, width, large, options, context);
                break;
            case 4:
                LossyTrip<RgbaF32>(data, width, large, options, context);
                break;
            default:
                LossyTrip<Cmyk16>(data, width, large, options, context);
                break;
        }
    }

    private static void LossyTrip<T>(byte[] data, int width, bool large, JxlWriterOptions options, EgamiContext context)
        where T : unmanaged, IPixelType<T>
    {
        const int Header = 8;
        int pixelBytes = T.Layout.BytesPerPixel;
        int height = large ? 200 + (data[7] * 3 % 200) : Math.Clamp((data.Length - Header) / pixelBytes / width, 1, 48);
        using Raster<T> raster = Raster.Create<T>(width, height, context);
        int length = data.Length - Header;
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = data[Header + (((y * row.Length) + i) % length)];
            }
        }

        if (typeof(T) == typeof(RgbaF32))
        {
            // Finite samples in a range the encoder takes; NaN and infinities have no lossy meaning.
            for (int y = 0; y < height; y++)
            {
                Span<float> s = MemoryMarshal.Cast<T, float>(raster.Row(y));
                for (int i = 0; i < s.Length; i++)
                {
                    s[i] = float.IsFinite(s[i]) ? Math.Clamp(s[i], -4, 4) : 0;
                }
            }
        }

        using MemoryStream file = new();
        raster.Save(file, JxlFormat.Info, options);
        using Raster<T> again = Raster.Load<T>(file.ToArray(), context);
        if ((again.Width, again.Height) != (width, height))
        {
            throw new InvalidOperationException($"The lossy {typeof(T).Name} file loads at {again.Width}×{again.Height}, not {width}×{height}.");
        }

        for (int y = 0; y < height; y++)
        {
            if (typeof(T) == typeof(RgbaF32) && !AllFinite(MemoryMarshal.Cast<T, float>(again.Row(y))))
            {
                throw new InvalidOperationException($"The lossy {typeof(T).Name} file decodes to samples that are not finite in row {y}.");
            }

            // From distance 10 the image, alpha too, is coded at half size (as libjxl does), so alpha is exact below that only.
            if (options.AlphaDistance == 0 && options.Distance < 10 && T.Layout.HasAlpha && typeof(T) != typeof(RgbaF32))
            {
                ReadOnlySpan<byte> a = MemoryMarshal.AsBytes(raster.Row(y)), b = MemoryMarshal.AsBytes(again.Row(y));
                int channel = pixelBytes / 4;
                for (int x = 0; x < width; x++)
                {
                    int at = (x * pixelBytes) + (3 * channel);
                    if (!a.Slice(at, channel).SequenceEqual(b.Slice(at, channel)))
                    {
                        throw new InvalidOperationException($"Exact alpha differs at ({x}, {y}) in the lossy {typeof(T).Name} file at effort {options.Effort}: {Convert.ToHexString(a.Slice(at, channel))}, read back {Convert.ToHexString(b.Slice(at, channel))}.");
                    }
                }
            }
        }
    }

    private static bool AllFinite(ReadOnlySpan<float> s)
    {
        foreach (float v in s)
        {
            if (!float.IsFinite(v))
            {
                return false;
            }
        }

        return true;
    }

    public static void Recompress(byte[] data, EgamiContext context)
    {
        byte[] jxl;
        try
        {
            using MemoryStream output = new();
            JxlImage.RecompressJpeg(new MemoryStream(data), output, context);
            jxl = output.ToArray();
        }
        catch (EgamiException)
        {
            return;
        }

        using MemoryStream back = new();
        JxlImage.ReconstructJpeg(new MemoryStream(jxl), back, context);
        if (!back.ToArray().AsSpan().SequenceEqual(data))
        {
            throw new InvalidOperationException("A recompressed JPEG does not rebuild to its bytes.");
        }

        using Raster raster = Raster.Load(jxl, context);
    }

    /// <summary>
    /// The jxl-frames target (JPEG XL extras PR A): the frame writer. Byte 0 picks animation, layers or extra channels, lossless
    /// or lossy; the next bytes the size (for extra channels sometimes past one group), frame count and effort, the rest the
    /// pixels (frames mostly repeat the one before, with a changed patch, so frames get cut). Lossless animations must load back
    /// to the same frames, layers through ReadLayers to the same places, blend modes, names and pixels, and extra channels to the
    /// same samples; lossy files must load at their size with every frame.
    /// </summary>
    /// <summary>
    /// Decodes a file as it arrives in seven chunks (JPEG XL extras PR D), drawing a preview after each: previews of truncated
    /// data must not crash, and the rows after the last byte must be those of a whole-file load.
    /// </summary>
    public static void Partial(byte[] data, EgamiContext context)
    {
        Raster<Rgba16>? whole = null;
        try
        {
            whole = Raster.Load<Rgba16>(new MemoryStream(data), context);
        }
        catch (EgamiException)
        {
        }

        try
        {
            int chunk = Math.Max(1, data.Length / 7);
            using RasterDecoder decoder = RasterDecoder.OpenIncremental(context);
            Raster<Rgba16>? rows = null;
            for (int at = 0; at < data.Length; at += chunk)
            {
                decoder.Append(data.AsSpan(at, Math.Min(chunk, data.Length - at)));
                if (decoder.HeaderRead)
                {
                    rows ??= Raster.Create<Rgba16>(decoder.Info.Width, decoder.Info.Height, context);
                    decoder.RenderPreview(rows);
                }
            }

            decoder.EndOfInput();
            if (rows is not null && whole is not null && decoder.ReadRows(rows) == whole.Height && (rows.Width, rows.Height) == (whole.Width, whole.Height))
            {
                for (int y = 0; y < whole.Height; y++)
                {
                    if (!whole.Row(y).SequenceEqual(rows.Row(y)))
                    {
                        throw new InvalidOperationException($"Row {y} of the incremental decode differs from a whole-file load.");
                    }
                }
            }

            rows?.Dispose();
        }
        catch (EgamiException)
        {
        }
        finally
        {
            whole?.Dispose();
        }
    }

    public static void Frames(byte[] data, EgamiContext context)
    {
        if (data.Length < 8)
        {
            return;
        }

        bool lossless = (data[0] & 1) == 0;
        int width = 1 + (data[1] % 40), height = 1 + (data[2] % 40), count = 1 + (data[3] % 5);
        JxlWriterOptions options = new() { Lossless = lossless, Effort = 1 + (data[4] % 7), Distance = lossless ? null : 0.5f + (data[5] % 8) };
        ReadOnlySpan<byte> pixels = data.AsSpan(8);
        switch ((data[0] >> 1) % 3)
        {
            case 0:
                Animation(pixels, width, height, count, options, context);
                break;
            case 1:
                Layers(pixels, width, height, count, data[6], options, context);
                break;
            default:
                // Now and then past one group each way, so channels subsampled 3 times reach the LF groups' streams.
                if (data[7] >= 240)
                {
                    (width, height) = (width + 260 + (data[7] & 7), height + 260 + (data[7] & 8));
                }

                ExtraChannels(pixels, width, height, data[6], options, context);
                break;
        }
    }

    private static Raster<Rgba8> Pixels(ReadOnlySpan<byte> bytes, int width, int height, int seed, EgamiContext context)
    {
        Raster<Rgba8> r = Raster.Create<Rgba8>(width, height, context);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = MemoryMarshal.AsBytes(r.Row(y));
            for (int i = 0; i < row.Length; i++)
            {
                row[i] = bytes.IsEmpty ? (byte)(seed + i) : bytes[((y * row.Length) + i + (seed * 7)) % bytes.Length];
            }
        }

        return r;
    }

    private static void Animation(ReadOnlySpan<byte> bytes, int width, int height, int count, JxlWriterOptions options, EgamiContext context)
    {
        using Raster<Rgba8> animation = Pixels(bytes, width, height, 0, context);
        for (int f = 1; f < count; f++)
        {
            // The frame before with a patch changed, or opaque, or as it was.
            using Raster<Rgba8> next = Raster.Create<Rgba8>(width, height, context);
            for (int y = 0; y < height; y++)
            {
                animation.Frames[f - 1].Row(y).CopyTo(next.Row(y));
            }

            int x0 = bytes.IsEmpty ? 0 : bytes[f % bytes.Length] % width, y0 = bytes.IsEmpty ? 0 : bytes[(f * 3) % bytes.Length] % height;
            for (int y = y0; y < Math.Min(height, y0 + 1 + (f * 3)); y++)
            {
                for (int x = x0; x < Math.Min(width, x0 + 1 + (f * 5)); x++)
                {
                    Rgba8 p = next.Row(y)[x];
                    next.Row(y)[x] = new Rgba8((byte)(p.R + 40), p.G, (byte)(p.B ^ f), (f & 1) == 0 ? (byte)255 : p.A);
                }
            }

            animation.Frames.Add(next.Frames[0]).Metadata.Delay = TimeSpan.FromMilliseconds(f * 30);
        }

        using MemoryStream file = new();
        animation.Save(file, JxlFormat.Info, options);
        using Raster<Rgba8> again = Raster.Load<Rgba8>(file.ToArray(), context);
        if (again.FrameCount != animation.FrameCount || again.Width != width || again.Height != height)
        {
            throw new InvalidOperationException($"A {animation.FrameCount}-frame animation loads as {again.FrameCount} frames of {again.Width}×{again.Height}.");
        }

        for (int f = 0; f < animation.FrameCount && options.Lossless == true; f++)
        {
            for (int y = 0; y < height; y++)
            {
                if (!animation.Frames[f].Row(y).SequenceEqual(again.Frames[f].Row(y)))
                {
                    throw new InvalidOperationException($"Frame {f} of a lossless animation differs in row {y}.");
                }
            }
        }
    }

    private static void Layers(ReadOnlySpan<byte> bytes, int width, int height, int count, byte modes, JxlWriterOptions options, EgamiContext context)
    {
        List<JxlLayer> layers = [];
        try
        {
            for (int i = 0; i < count; i++)
            {
                int w = 1 + ((width * (i + 2)) % 37), h = 1 + ((height * (i + 3)) % 37);
                int x = (bytes.IsEmpty ? i : bytes[i % bytes.Length]) % (width + 20) - 10, y = (bytes.IsEmpty ? i : bytes[(i + 1) % bytes.Length]) % (height + 20) - 10;
                JxlLayerBlend blend = (JxlLayerBlend)((modes >> i) % 5);
                layers.Add(new JxlLayer(Pixels(bytes, w, h, i, context), x, y, blend, name: i % 2 == 0 ? $"layer {i}" : null));
            }

            using MemoryStream file = new();
            JxlImage.WriteLayers(file, width, height, layers, options, context);
            file.Position = 0;
            IReadOnlyList<JxlLayer> read = JxlImage.ReadLayers(file, context: context);
            try
            {
                if (read.Count != layers.Count)
                {
                    throw new InvalidOperationException($"{layers.Count} layers read back as {read.Count}.");
                }

                for (int i = 0; i < read.Count; i++)
                {
                    JxlLayer a = layers[i], b = read[i];
                    if ((a.X, a.Y, a.Blend, a.Name, a.Raster.Width, a.Raster.Height) != (b.X, b.Y, b.Blend, b.Name, b.Raster.Width, b.Raster.Height))
                    {
                        throw new InvalidOperationException($"Layer {i} reads back as another place, size, blend mode or name.");
                    }

                    using Raster<Rgba8> pa = a.Raster.As<Rgba8>();
                    using Raster<Rgba8> pb = b.Raster.As<Rgba8>();
                    for (int y = 0; y < pa.Height && options.Lossless == true; y++)
                    {
                        if (!pa.Row(y).SequenceEqual(pb.Row(y)))
                        {
                            throw new InvalidOperationException($"Layer {i} differs in row {y}.");
                        }
                    }
                }
            }
            finally
            {
                foreach (JxlLayer layer in read)
                {
                    layer.Dispose();
                }
            }

            file.Position = 0;
            using Raster blended = Raster.Load(file, context);
        }
        finally
        {
            foreach (JxlLayer layer in layers)
            {
                layer.Dispose();
            }
        }
    }

    private static void ExtraChannels(ReadOnlySpan<byte> bytes, int width, int height, byte kind, JxlWriterOptions options, EgamiContext context)
    {
        using Raster<Rgba8> image = Pixels(bytes, width, height, 0, context);
        int shift = kind % 4, bits = 1 + (kind >> 2) % 16;
        int w = (width + (1 << shift) - 1) >> shift, h = (height + (1 << shift) - 1) >> shift;
        using Raster<GrayF32> samples = Raster.Create<GrayF32>(w, h, context);
        float max = (1 << bits) - 1;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int v = bytes.IsEmpty ? x + y : bytes[((y * w) + x) % bytes.Length] | (bytes[((y * w) + x + 1) % bytes.Length] << 8);
                samples.Row(y)[x] = new GrayF32((v % (int)(max + 1)) / max);
            }
        }

        JxlExtraChannel channel = new() { Kind = JxlExtraChannelKind.Depth, BitsPerSample = bits, Subsampling = shift, Name = "fuzz" };
        options = new JxlWriterOptions { Lossless = options.Lossless, Effort = options.Effort, Distance = options.Distance, ExtraChannels = [new JxlExtraChannelImage(channel, samples)] };
        using MemoryStream file = new();
        image.Save(file, JxlFormat.Info, options);
        byte[] jxl = file.ToArray();
        using Raster again = Raster.Load(jxl, context);
        if (shift == 0)
        {
            // Full size: exactly the samples, in lossy files too (below distance 10, where the frame is coded at full size).
            // Alpha, when the image has it, comes first.
            int index = ((JxlDetails)again.Metadata.FormatDetails!).ExtraChannels.Count - 1;
            using Raster<GrayF32> back = JxlImage.ReadExtraChannel(new MemoryStream(jxl), index, context: context);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (Math.Abs(back.Row(y)[x].L - samples.Row(y)[x].L) > 1e-6f)
                    {
                        throw new InvalidOperationException($"The extra channel differs at ({x}, {y}).");
                    }
                }
            }
        }
    }
}
