// The drawing code that Egami.SystemDrawing records here and System.Drawing records on Windows in egami-compare, which links
// this file (EGAMI_COMPARE) to write tests/images/metafiles/recorded: the same calls, so the two metafiles can be compared.
#if EGAMI_COMPARE
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Egami.Compare;

using Point = System.Drawing.Point;
#else
using Egami.SystemDrawing.Drawing2D;

namespace Egami.SystemDrawing.Tests;

using Point = System.Drawing.Point;
#endif

/// <summary>Scenes drawn into a recording metafile in pixel units, 200 by 150, each a family of Graphics calls.</summary>
#if EGAMI_COMPARE
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
#endif
internal static class RecordingScenes
{
    /// <summary>The frame every scene is recorded with, in pixels.</summary>
    public static readonly Rectangle Frame = new(0, 0, 200, 150);

    /// <summary>Gets the scenes by name.</summary>
    public static IReadOnlyList<(string Name, Action<Graphics> Draw)> All { get; } =
    [
        ("shapes", Shapes),
        ("smooth", Smooth),
        ("pens", Strokes),
        ("gradients", Gradients),
        ("transforms", Transforms),
        ("clips", Clips),
        ("paths", Paths),
        ("images", Images),
        ("states", States),
    ];

    private static void Shapes(Graphics g)
    {
        g.Clear(Color.White);
        using SolidBrush red = new(Color.FromArgb(255, 220, 40, 30));
        using SolidBrush blue = new(Color.FromArgb(160, 20, 60, 200));
        g.FillRectangle(red, 10, 10, 80, 50);
        g.FillEllipse(blue, 50, 30, 100, 70);
        g.FillPolygon(Brushes.Green, [new Point(120, 10), new Point(190, 40), new Point(150, 80)]);
        g.FillPie(Brushes.Orange, 20, 80, 60, 60, 30, 240);
        g.DrawRectangle(Pens.Black, 100, 100, 80, 40);
    }

    private static void Smooth(Graphics g)
    {
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using Pen pen = new(Color.Navy, 3);
        g.DrawEllipse(pen, 20, 20, 160, 110);
        g.DrawBezier(pen, 10, 140, 60, 10, 140, 140, 190, 10);
        g.FillEllipse(Brushes.Crimson, 80, 55, 40, 40);
    }

    private static void Strokes(Graphics g)
    {
        g.Clear(Color.White);
        using Pen wide = new(Color.DarkGreen, 9) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Square };
        g.DrawLines(wide, [new Point(20, 20), new Point(90, 60), new Point(30, 110)]);
        using Pen dashed = new(Color.Purple, 3) { DashStyle = DashStyle.Dash };
        g.DrawLine(dashed, 110, 20, 190, 130);
        using Pen dotted = new(Color.Black, 1) { DashStyle = DashStyle.Dot };
        g.DrawRectangle(dotted, 100, 40, 60, 60);
    }

    private static void Gradients(Graphics g)
    {
        using LinearGradientBrush linear = new(new Point(0, 0), new Point(200, 0), Color.Yellow, Color.Blue);
        g.FillRectangle(linear, 0, 0, 200, 75);
        using HatchBrush hatch = new(HatchStyle.DiagonalCross, Color.Black, Color.LightGray);
        g.FillRectangle(hatch, 0, 75, 100, 75);
        using LinearGradientBrush turned = new(new Rectangle(100, 75, 100, 75), Color.Red, Color.White, 45f);
        g.FillEllipse(turned, 100, 75, 100, 75);
    }

    private static void Transforms(Graphics g)
    {
        g.Clear(Color.White);
        g.TranslateTransform(100, 75);
        for (int i = 0; i < 6; i++)
        {
            using SolidBrush b = new(Color.FromArgb(255, 40 * i, 100, 255 - (40 * i)));
            g.FillRectangle(b, 10, -5, 60, 10);
            g.RotateTransform(60);
        }

        g.ResetTransform();
        g.ScaleTransform(2, 1);
        g.FillEllipse(Brushes.Teal, 5, 5, 20, 20);
    }

    private static void Clips(Graphics g)
    {
        g.Clear(Color.White);
        using GraphicsPath oval = new();
        oval.AddEllipse(20, 10, 160, 130);
        g.SetClip(oval);
        g.FillRectangle(Brushes.SteelBlue, 0, 0, 200, 150);
        g.SetClip(new Rectangle(60, 40, 80, 70), CombineMode.Exclude);
        g.FillRectangle(Brushes.Gold, 0, 60, 200, 30);
        g.ResetClip();
        g.DrawLine(Pens.Black, 0, 149, 199, 0);
    }

    private static void Paths(Graphics g)
    {
        g.Clear(Color.White);
        using GraphicsPath path = new(FillMode.Alternate);
        path.AddRectangle(new Rectangle(20, 20, 160, 110));
        path.AddEllipse(50, 35, 100, 80);
        path.AddPolygon([new Point(100, 5), new Point(130, 60), new Point(70, 60)]);
        g.FillPath(Brushes.Maroon, path);
        using Pen outline = new(Color.Black, 2);
        g.DrawPath(outline, path);
    }

    private static void Images(Graphics g)
    {
        g.Clear(Color.White);
        using Bitmap tile = new(8, 8);
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                tile.SetPixel(x, y, Color.FromArgb(255, x * 32, y * 32, ((x + y) % 2) * 255));
            }
        }

        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.DrawImage(tile, new Rectangle(10, 10, 80, 80));
        g.InterpolationMode = InterpolationMode.HighQualityBilinear;
        g.DrawImage(tile, new Rectangle(110, 10, 80, 80));
        using TextureBrush texture = new(tile);
        g.FillRectangle(texture, 10, 100, 180, 40);
    }

    private static void States(Graphics g)
    {
        g.Clear(Color.White);
        GraphicsState saved = g.Save();
        g.TranslateTransform(50, 0);
        g.SetClip(new Rectangle(0, 0, 60, 150));
        g.FillRectangle(Brushes.Red, 0, 0, 200, 150);
        g.Restore(saved);
        GraphicsContainer container = g.BeginContainer(new RectangleF(100, 50, 100, 100), new RectangleF(0, 0, 50, 50), GraphicsUnit.Pixel);
        g.FillEllipse(Brushes.Blue, 0, 0, 50, 50);
        g.EndContainer(container);
        g.FillRectangle(Brushes.Green, 0, 120, 40, 30);
    }
}
