using Egami.Drawing;
using Egami.Fonts;
using Egami.Pixels;
using Egami.Svg;

namespace Egami.Fuzz;

/// <summary>
/// The svg fuzz target (Phase 2.10 task 16): the input is loaded as an SVG document (or SVGZ) leniently under small limits,
/// with one embedded font and no system fonts, drawn at no more than 128 by 128 pixels, saved and loaded again, then loaded
/// strictly. A lenient load may only throw an <see cref="EgamiException"/> (a limit, or XML that cannot be read); drawing and
/// saving what loaded never throw.
/// </summary>
internal static class SvgFuzzTarget
{
    private static readonly Lazy<SvgFontOptions> s_fonts = new(() =>
    {
        // The font is embedded in the assembly this file is compiled into (the fuzz harness, or Egami.Svg.Tests).
        using Stream data = typeof(SvgFuzzTarget).Assembly.GetManifestResourceStream("NotoSansArabic-subset.ttf")!;
        FontCollection fonts = new();
        fonts.Add(Typeface.FromStream(data));
        return new SvgFontOptions { Collection = fonts, UseSystemFonts = false, DefaultFamily = "Noto Sans Arabic" };
    });

    private static readonly EgamiContext s_context = new()
    {
        Limits = new SafetyLimits { MaxPixels = 1 << 20, MaxWidth = 4096, MaxHeight = 4096 },
    };

    public static void Run(byte[] data)
    {
        SvgLoadOptions options = new() { Fonts = s_fonts.Value, Context = s_context };
        SvgDocument document;
        try
        {
            document = SvgDocument.Load(new MemoryStream(data), options);
        }
        catch (EgamiException)
        {
            return;
        }

        _ = document.RenderTree;
        SvgRenderOptions render = new() { Context = s_context };
        float scale = Math.Min(1, 128 / Math.Max(1, Math.Max(document.Size.X, document.Size.Y)));
        if (float.IsFinite(scale) && scale > 0)
        {
            using Raster<Rgba8> raster = document.Rasterize<Rgba8>(render with { Scale = scale });
        }

        using (Raster<Rgba8> small = Raster.Create<Rgba8>(64, 64))
        using (RasterCanvas<Rgba8> canvas = small.CreateCanvas())
        {
            document.Draw(canvas, new RectF(0, 0, 64, 64), render);
        }

        using MemoryStream saved = new();
        document.Save(saved);
        saved.Position = 0;
        _ = SvgDocument.Load(saved, options).RenderTree;

        try
        {
            _ = SvgDocument.Load(new MemoryStream(data), options with { Strict = true });
        }
        catch (EgamiException)
        {
        }
    }
}
