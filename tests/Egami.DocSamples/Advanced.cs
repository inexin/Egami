using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Advanced
{
    [Fact]
    public void LatticesAndNinePatches()
    {
        using Raster<Rgba8> raster = Raster.Create(300, 120, Rgba8.White);
        using Raster<Rgba8> file = NinePatchFile();
        using Raster<Rgba8> frame = Raster.Create(24, 24, new Rgba8(60, 90, 160, 255));

        #region lattice
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            // An Android .9.png: its one-pixel border marks what stretches. The corners stay sharp at any size.
            (Raster inner, Lattice lattice, Rect? padding) = NinePatch.FromAndroid(file);
            canvas.DrawImageLattice(inner, lattice, new RectF(10, 10, 180, 48));
            inner.Dispose();

            // A nine-patch from a plain image: the 8-pixel corners keep their size, the edges and center stretch.
            canvas.DrawImageNinePatch(frame, new Rect(8, 8, 8, 8), new RectF(10, 70, 120, 40));

            // A lattice can leave cells out or fill them with a color: here the center becomes a flat panel.
            LatticeCellKind[] kinds = [.. Enumerable.Repeat(LatticeCellKind.Default, 9)];
            kinds[4] = LatticeCellKind.FixedColor;
            RgbaF32[] colors = new RgbaF32[9];
            colors[4] = new RgbaF32(0.95f, 0.95f, 0.9f, 1);
            canvas.DrawImageLattice(frame, new Lattice([8, 16], [8, 16]) { CellKinds = kinds, CellColors = colors }, new RectF(150, 70, 140, 40));
        }
        #endregion

        Assert.Equal(new Rgba8(60, 90, 160, 255), raster.GetPixel(12, 72));
    }

    [Fact]
    public void Atlas()
    {
        using Raster<Rgba8> raster = Raster.Create(320, 200, Rgba8.Black);
        using Raster<Rgba8> spark = Glow(16), star = Glow(24);

        #region atlas
        // Pack the sprites once, then draw thousands of them in one call: each with its own place, turn, scale and tint.
        using SpriteSheet sheet = SpriteSheet.Pack([spark, star], new SpritePackOptions { Padding = 1 });
        Random random = new(7);
        SpriteTransform[] transforms = new SpriteTransform[2000];
        RectF[] sources = new RectF[transforms.Length];
        RgbaF32[] tints = new RgbaF32[transforms.Length];
        for (int i = 0; i < transforms.Length; i++)
        {
            Rect frame = sheet.Frames[i % 2];
            sources[i] = frame;
            transforms[i] = SpriteTransform.FromRotationScale(
                scale: 0.3f + random.NextSingle(),
                degrees: random.NextSingle() * 360,
                position: new Vector2(random.NextSingle() * 320, random.NextSingle() * 200),
                anchor: new Vector2(frame.Width / 2f, frame.Height / 2f));
            tints[i] = new RgbaF32(1, 0.5f + (random.NextSingle() / 2), random.NextSingle(), 0.8f);
        }

        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            // Plus adds the sprites up, as light does.
            canvas.DrawAtlas(sheet.Image, transforms, sources, tints, BlendMode.Modulate, paint: new SolidPaint(Rgba8.White) { BlendMode = BlendMode.Plus });
        }
        #endregion

        Assert.NotEqual(Rgba8.Black, raster.GetPixel(160, 100));
    }

    [Fact]
    public void Meshes()
    {
        using Raster<Rgba8> raster = Raster.Create(300, 150, Rgba8.White);
        using Raster<Rgba8> photo = Raster.Create(64, 64, new Rgba8(200, 140, 60, 255));

        #region mesh
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            // Gouraud shading: a color at each corner, blended across each triangle without cracks between them.
            Vector2[] quad = [new(10, 10), new(140, 20), new(130, 140), new(20, 120)];
            RgbaF32[] colors = [new(1, 0.2f, 0.2f, 1), new(1, 0.9f, 0.2f, 1), new(0.2f, 0.6f, 1, 1), new(0.6f, 0.2f, 0.9f, 1)];
            canvas.DrawMesh(new Mesh(MeshMode.Fan, quad, colors), Rgba8.White);

            // A textured mesh: each vertex says which point of the paint lands on it, here bending a photo into a trapezoid.
            Vector2[] trapezoid = [new(170, 20), new(280, 30), new(290, 140), new(160, 130)];
            Vector2[] corners = [new(0, 0), new(64, 0), new(64, 64), new(0, 64)];
            canvas.DrawMesh(new Mesh(MeshMode.Fan, trapezoid, texCoords: corners), new ImagePaint(photo));
        }
        #endregion

        Assert.NotEqual(Rgba8.White, raster.GetPixel(70, 70));
    }

    [Fact]
    public void Patches()
    {
        using Raster<Rgba8> raster = Raster.Create(240, 200, Rgba8.White);

        #region patch
        // A Coons patch: four cubic edges, clockwise from the top-left corner, and a color at each corner.
        Vector2[] edges =
        [
            new(20, 20), new(80, 0), new(150, 40), new(220, 20),    // top, left to right
            new(200, 80), new(240, 140), new(220, 180),             // right, down
            new(150, 160), new(80, 200), new(20, 180),              // bottom, right to left
            new(40, 120), new(0, 60),                               // left, up
        ];
        Patch patch = Patch.Coons(edges, [new RgbaF32(1, 0, 0, 1), new RgbaF32(1, 1, 0, 1), new RgbaF32(0, 0, 1, 1), new RgbaF32(0, 1, 0, 1)]);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.DrawPatch(patch);

            // PDF's function-based shadings give each corner a value instead, mapped through a gradient's stops.
            Patch shaded = Patch.Tensor(patch.ToTensorNet(), cornerValues: [0, 0.3f, 1, 0.6f]);
            canvas.Translate(10, 10);
            canvas.Scale(0.3f);
            canvas.DrawPatch(shaded, valueStops: [new GradientStop(0, Rgba8.Black), new GradientStop(1, Rgba8.White)]);
        }
        #endregion

        Assert.NotEqual(Rgba8.White, raster.GetPixel(120, 100));
    }

    [Fact]
    public void Recording()
    {
        using Raster<Rgba8> raster = Raster.Create(400, 200, Rgba8.White);

        #region recording
        // Record any picture code once...
        PictureRecorder recorder = new(new RectF(0, 0, 40, 40));
        DrawLogo(recorder);
        Picture logo = recorder.Finish();

        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            // ...and replay it anywhere, at any scale, still sharp: replay is vector.
            canvas.DrawPicture(logo, Matrix3x2.CreateTranslation(10, 10));
            canvas.DrawPicture(logo, Matrix3x2.CreateScale(4) * Matrix3x2.CreateTranslation(60, 10));

            // A paint with an opacity merges the whole picture as one layer.
            canvas.DrawPicture(logo, Matrix3x2.CreateTranslation(240, 10), new SolidPaint(Rgba8.White) { Opacity = 0.4f });

            // Drawn many times at one scale, a cached image is cheaper than replaying.
            for (int i = 0; i < 6; i++)
            {
                canvas.DrawPicture(logo, Matrix3x2.CreateTranslation(230 + (i * 25), 150), cacheAsImage: true);
            }
        }

        using Raster<Rgba8> icon = logo.ToRaster<Rgba8>(scale: 2);
        #endregion

        Assert.Equal(80, icon.Width);
    }

    [Fact]
    public void Patterns()
    {
        using Raster<Rgba8> raster = Raster.Create(300, 200, Rgba8.White);

        #region pattern
        // A tile drawn once as vectors fills any shape, repeated, and stays sharp when the canvas is zoomed.
        PictureRecorder tile = new(new RectF(0, 0, 20, 20));
        tile.FillCircle(new Vector2(10, 10), 4, new Rgba8(220, 60, 60, 255));
        tile.StrokeRect(new RectF(0.5f, 0.5f, 19, 19), new Stroke(1), new Rgba8(230, 230, 230, 255));
        PicturePaint dots = new(tile.Finish(), TileMode.Repeat, TileMode.Repeat) { LocalMatrix = Matrix3x2.CreateRotation(MathF.PI / 8) };
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillRoundRect(new RoundRect(new RectF(10, 10, 280, 180), 24), dots);
        }
        #endregion

        int red = 0;
        for (int y = 0; y < 200; y++)
        {
            red += raster.Row(y).ToArray().Count(p => p.R > 200 && p.G < 100);
        }

        Assert.True(red > 2000, $"{red} red pixels");
    }

    [Fact]
    public void Files()
    {
        PictureRecorder recorder = new(new RectF(0, 0, 40, 40));
        DrawLogo(recorder);
        Picture logo = recorder.Finish();

        #region egdr
        // Keep a picture in Egami's own documented format, and load it later; a loaded file is untrusted, so the context's
        // safety limits apply.
        using (FileStream file = File.Create("logo.egdr"))
        {
            logo.Save(file);
        }

        Picture loaded;
        using (FileStream file = File.OpenRead("logo.egdr"))
        {
            loaded = Picture.Load(file);
        }
        #endregion

        Assert.Equal(logo.CallCount, loaded.CallCount);
    }

    [Fact]
    public void Cookbook()
    {
        using Raster<Rgba8> skin = Raster.Create(30, 30, new Rgba8(40, 120, 220, 255));

        #region stretchable-button
        // One small button image serves every size: corners stay round, the label sits on the stretched center.
        using Raster<Rgba8> button = Raster.Create<Rgba8>(200, 48);
        using (RasterCanvas<Rgba8> canvas = button.CreateCanvas())
        {
            canvas.DrawImageNinePatch(skin, new Rect(12, 12, 6, 6), new RectF(0, 0, 200, 48), new SamplingOptions { Filter = ResampleFilter.Bilinear });
        }
        #endregion

        #region gradient-mesh
        // A gradient mesh, as an illustration app draws one: a 3×3 grid of colored points, smoothly shaded.
        Vector2[] grid = new Vector2[16];
        RgbaF32[] colors = new RgbaF32[16];
        for (int j = 0; j < 4; j++)
        {
            for (int i = 0; i < 4; i++)
            {
                grid[(j * 4) + i] = new Vector2(i * 60, j * 40) + new Vector2(MathF.Sin(j) * 8, MathF.Cos(i) * 6);
                colors[(j * 4) + i] = new RgbaF32(i / 3f, 0.3f + (j * 0.2f), 1 - (i / 3f), 1);
            }
        }

        int[] indices = [.. Enumerable.Range(0, 9).SelectMany(k => new[] { k + (k / 3), k + (k / 3) + 1, k + (k / 3) + 5, k + (k / 3), k + (k / 3) + 5, k + (k / 3) + 4 })];
        using Raster<Rgba8> mesh = Raster.Create(200, 140, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = mesh.CreateCanvas())
        {
            canvas.Translate(10, 10);
            canvas.DrawMesh(new Mesh(MeshMode.Triangles, grid, colors, indices: indices), Rgba8.White);
        }
        #endregion

        Assert.Equal(new Rgba8(40, 120, 220, 255), button.GetPixel(100, 24));
        Assert.NotEqual(Rgba8.White, mesh.GetPixel(100, 60));
    }

    private static void DrawLogo(Egami.Drawing.Canvas canvas)
    {
        canvas.FillCircle(new Vector2(20, 20), 18, new LinearGradient(new Vector2(2, 2), new Vector2(38, 38), new RgbaF32(0.2f, 0.5f, 1, 1), new RgbaF32(0.1f, 0.2f, 0.6f, 1)));
        canvas.FillPath(Path.Star(new Vector2(20, 20), 12, 5, 5), Rgba8.White);
    }

    private static Raster<Rgba8> Glow(int size)
    {
        Raster<Rgba8> glow = Raster.Create<Rgba8>(size, size);
        using RasterCanvas<Rgba8> canvas = glow.CreateCanvas();
        canvas.FillCircle(new Vector2(size / 2f), size / 2f, new RadialGradient(new Vector2(size / 2f), size / 2f, new RgbaF32(1, 1, 1, 1), new RgbaF32(1, 1, 1, 0)));
        return glow;
    }

    private static Raster<Rgba8> NinePatchFile()
    {
        // A 26×14 button with a one-pixel border: the middle columns and rows stretch.
        Raster<Rgba8> file = Raster.Create(28, 16, default(Rgba8));
        for (int y = 1; y < 15; y++)
        {
            for (int x = 1; x < 27; x++)
            {
                file.SetPixel(x, y, new Rgba8(60, 90, 160, 255));
            }
        }

        for (int x = 8; x < 20; x++)
        {
            file.SetPixel(x, 0, new Rgba8(0, 0, 0, 255));
        }

        for (int y = 5; y < 11; y++)
        {
            file.SetPixel(0, y, new Rgba8(0, 0, 0, 255));
        }

        return file;
    }
}
