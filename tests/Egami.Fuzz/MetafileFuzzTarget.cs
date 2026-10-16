using Egami.Drawing;
using Egami.Metafiles;
using Egami.Pixels;

namespace Egami.Fuzz;

/// <summary>
/// The wmf, emf and emfplus fuzz targets (Phase 2.3.7 task 17). wmf and emf take a file; emfplus takes a stream of EMF+
/// records and wraps it in EMF comments, so the EMF+ reader and player get the mutations. The file is loaded under small
/// limits (only an <see cref="EgamiException"/> may come out of the load), probed through MetafileFormat, and drawn at no more
/// than 128 pixels both ways a dual file plays, aliased and antialiased, and onto a box; drawing never throws.
/// </summary>
internal static class MetafileFuzzTarget
{
    private static readonly EgamiContext s_context = new()
    {
        Limits = new SafetyLimits { MaxPixels = 1 << 20, MaxWidth = 4096, MaxHeight = 4096, MaxTotalBytes = 16 << 20 },
    };

    public static void Run(byte[] data)
    {
        Metafile metafile;
        try
        {
            metafile = Metafile.Load(new MemoryStream(data), new MetafileLoadOptions { Context = s_context });
        }
        catch (EgamiException)
        {
            return;
        }

        try
        {
            _ = Raster.Probe(data, s_context.WithFormat(MetafileFormat.Info));
        }
        catch (EgamiException)
        {
            // Probe goes through the same load; a limit may stop it.
        }

        // At most 128 pixels a side whatever the picture's size, keeping its aspect ratio roughly.
        float w = Math.Max(1, metafile.Size.X), h = Math.Max(1, metafile.Size.Y), fit = 128 / Math.Max(w, h);
        int width = Math.Clamp((int)(w * fit), 1, 128), height = Math.Clamp((int)(h * fit), 1, 128);
        foreach (MetafilePlayback playback in (MetafilePlayback[])[MetafilePlayback.Auto, MetafilePlayback.Gdi])
        {
            foreach (bool antialias in (bool[])[true, false])
            {
                MetafileRenderOptions options = new() { Width = width, Height = height, Playback = playback, Antialias = antialias, Context = s_context };
                using Raster<Rgba8> raster = metafile.Rasterize<Rgba8>(options);
            }
        }

        using Raster<Rgba8> small = Raster.Create<Rgba8>(64, 48);
        using RasterCanvas<Rgba8> canvas = small.CreateCanvas();
        metafile.Draw(canvas, new RectF(4, 4, 56, 40), new MetafileRenderOptions { TextAsPaths = true, Context = s_context });
    }

    /// <summary>Wraps EMF+ records in an EMF: a header for a 100 by 100 pixel picture, the records in comments of up to 8 KB, EOF.</summary>
    public static void RunPlus(byte[] records)
    {
        List<byte> file = [];
        byte[] header = new byte[108];
        Span<byte> h = header;
        BitConverter.TryWriteBytes(h, 1);
        BitConverter.TryWriteBytes(h[4..], 108);
        BitConverter.TryWriteBytes(h[16..], 99);
        BitConverter.TryWriteBytes(h[20..], 99);
        BitConverter.TryWriteBytes(h[32..], 2645);
        BitConverter.TryWriteBytes(h[36..], 2645);
        BitConverter.TryWriteBytes(h[40..], 0x464D4520);
        BitConverter.TryWriteBytes(h[44..], 0x10000);
        BitConverter.TryWriteBytes(h[72..], 1920);
        BitConverter.TryWriteBytes(h[76..], 1080);
        BitConverter.TryWriteBytes(h[80..], 508);
        BitConverter.TryWriteBytes(h[84..], 286);
        file.AddRange(header);
        int count = 1;
        for (int at = 0; at < records.Length; at += 8192)
        {
            int n = Math.Min(8192, records.Length - at), size = 16 + ((n + 3) & ~3);
            byte[] comment = new byte[size];
            BitConverter.TryWriteBytes(comment, 70);
            BitConverter.TryWriteBytes(comment.AsSpan(4), size);
            BitConverter.TryWriteBytes(comment.AsSpan(8), n + 4);
            BitConverter.TryWriteBytes(comment.AsSpan(12), 0x2B464D45);
            records.AsSpan(at, n).CopyTo(comment.AsSpan(16));
            file.AddRange(comment);
            count++;
        }

        file.AddRange([14, 0, 0, 0, 20, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 20, 0, 0, 0]);
        byte[] bytes = [.. file];
        BitConverter.TryWriteBytes(bytes.AsSpan(48), bytes.Length);
        BitConverter.TryWriteBytes(bytes.AsSpan(52), count + 1);
        Run(bytes);
    }
}
