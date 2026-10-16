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
/// a JPEG, and a file it makes must load and rebuild the input byte for byte. Only EgamiExceptions may escape the readers.
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
        pixel.Save(s, JxlFormat.Info, new JxlWriterOptions { Effort = 1 });
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
        JxlWriterOptions options = new() { Effort = effort };
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
}
