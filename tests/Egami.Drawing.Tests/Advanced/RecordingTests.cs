using System.Numerics;

using Egami.Drawing.Tests.Canvas;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Advanced;

public class RecordingTests
{
    /// <summary>A scene with most kinds of call: state, clips, shapes, strokes, paints, an image, a layer and the 2.6.4 calls.</summary>
    internal static void Scene(Egami.Drawing.Canvas c, Raster image)
    {
        c.Clear(new Rgba8(250, 245, 230, 255));
        c.Save();
        c.Translate(4, 3);
        c.ClipRoundRect(new RoundRect(new RectF(0, 0, 90, 70), 8));
        c.FillRect(new RectF(2, 2, 30, 20), new LinearGradient(new Vector2(2, 2), new Vector2(32, 22), new RgbaF32(1, 0, 0, 1), new RgbaF32(0, 0, 1, 1)));
        c.StrokeOval(new RectF(36, 4, 30, 20), new Stroke(2.5f) { Dash = new DashPattern([4, 2]) }, new Rgba8(20, 120, 40, 255));
        c.FillPath(Path.Star(new Vector2(20, 45), 14, 6, 5), new RadialGradient(new Vector2(20, 45), 14, new RgbaF32(1, 1, 0, 1), new RgbaF32(1, 0, 0, 1)) { Tile = TileMode.Mirror });
        c.Rotate(10, new Vector2(60, 45));
        c.SaveLayer(0.6f);
        c.FillRoundRect(new RoundRect(new RectF(45, 32, 30, 24), 5), new Rgba8(0, 90, 200, 255));
        c.FillCircle(new Vector2(70, 40), 9, new Rgba8(220, 40, 40, 255) { });
        c.Restore();
        c.DrawPoints(PointMode.Polygon, [new(5, 60), new(25, 66), new(45, 58)], new Stroke(1.5f) { Cap = LineCap.Round }, new Rgba8(0, 0, 0, 255));
        c.Restore();
        c.DrawImage(image, new RectF(60, 52, 24, 18), new SamplingOptions { Filter = ResampleFilter.Bilinear });
        c.DrawImageNinePatch(image, new Rect(4, 4, 8, 6), new RectF(4, 76, 40, 18));
        c.DrawAtlas(image, [new SpriteTransform(1, 0, 50, 76), SpriteTransform.FromRotationScale(0.8f, 45, new Vector2(80, 86))], [new RectF(0, 0, 8, 8), new RectF(8, 0, 8, 8)], [new RgbaF32(1, 1, 1, 1), new RgbaF32(0, 1, 0, 1)]);
        c.DrawMesh(new Mesh(MeshMode.Fan, [new(95, 10), new(110, 2), new(118, 20), new(100, 28)], [new(1, 0, 0, 1), new(0, 1, 0, 1), new(0, 0, 1, 1), new(1, 1, 1, 1)]), Rgba8.White);
        c.DrawPatch(Patch.Coons(
            [new(95, 40), new(102, 34), new(110, 46), new(118, 40), new(122, 50), new(114, 58), new(118, 66), new(110, 62), new(102, 72), new(95, 66), new(100, 58), new(92, 48)],
            cornerValues: [0, 0.3f, 1, 0.6f]), valueStops: [new(0, new RgbaF32(0, 0, 0.5f, 1)), new(1, new RgbaF32(1, 0.8f, 0, 1))]);
        c.DrawPaint(new SolidPaint(new RgbaF32(0, 0, 0, 0.05f)) { BlendMode = BlendMode.Multiply });
    }

    [Fact]
    public void PlaybackGivesTheOriginalCallLog()
    {
        using Raster<Rgba8> image = Pixels.Photo(16, 12);
        using TraceCanvas direct = new(128, 100);
        Scene(direct, image);

        Picture picture = Record(image);
        using TraceCanvas replayed = new(128, 100);
        picture.Playback(replayed);

        // Playback brackets the calls in a Save and Restore of its own.
        Assert.Equal("Save", replayed.Calls[0]);
        Assert.Equal("Restore", replayed.Calls[^1]);
        Assert.Equal(direct.Calls, replayed.Calls[1..^1]);
    }

    [Fact]
    public void PlaybackDrawsTheOriginalPixels()
    {
        using Raster<Rgba8> image = Pixels.Photo(16, 12);
        using Raster<Rgba8> direct = Pixels.Draw(128, 100, c => Scene(c, image));
        Picture picture = Record(image);
        using Raster<Rgba8> replayed = Pixels.Draw(128, 100, picture.Playback);
        Pixels.Equal(direct, replayed);
        using Raster<Rgba8> nested = Pixels.Draw(128, 100, c => c.DrawPicture(picture));
        Pixels.Equal(direct, nested);
    }

    [Fact]
    public void ADrawingReplaysUnderAMatrixAsDrawnThere()
    {
        using Raster<Rgba8> image = Pixels.Photo(16, 12);
        Picture picture = Record(image);
        Matrix3x2 m = Matrix3x2.CreateScale(3) * Matrix3x2.CreateTranslation(-40, -20);
        using Raster<Rgba8> scaled = Pixels.Draw(256, 200, c => c.DrawPicture(picture, m));
        using Raster<Rgba8> direct = Pixels.Draw(256, 200, c =>
        {
            c.SetMatrix(m);
            Scene(new Offset(c, m), image);
        });
        Pixels.Equal(direct, scaled);
    }

    [Fact]
    public void APaintMergesTheDrawingAsOneLayer()
    {
        PictureRecorder recorder = new(new RectF(0, 0, 40, 40));
        recorder.FillRect(new RectF(5, 5, 20, 20), Rgba8.Red);
        recorder.FillRect(new RectF(15, 15, 20, 20), Rgba8.Red);
        Picture picture = recorder.Finish();
        using Raster<Rgba8> drawn = Pixels.Draw(40, 40, c => c.DrawPicture(picture, paint: new SolidPaint(Rgba8.White) { Opacity = 0.5f }));

        // Group opacity: the overlap is no darker than the rest.
        Assert.Equal(drawn.GetPixel(10, 10), drawn.GetPixel(20, 20));
        Assert.InRange(drawn.GetPixel(20, 20).G, 127, 128);

        using TraceCanvas trace = new(40, 40);
        trace.DrawPicture(picture, paint: new SolidPaint(Rgba8.White) { Opacity = 0.5f });
        Assert.Contains("SaveLayer 0.5 SourceOver True", trace.Calls);
    }

    [Fact]
    public void ToRasterRendersTheBoundsAtAScale()
    {
        PictureRecorder recorder = new(new RectF(10, 20, 16, 8));
        recorder.FillRect(new RectF(10, 20, 8, 8), Rgba8.Blue);
        Picture picture = recorder.Finish();
        using Raster<Rgba8> raster = picture.ToRaster<Rgba8>(2.5f);
        Assert.Equal(40, raster.Width);
        Assert.Equal(20, raster.Height);
        Assert.Equal(Rgba8.Blue, raster.GetPixel(5, 5));
        Assert.Equal(default, raster.GetPixel(30, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => picture.ToRaster<Rgba8>(0));
    }

    [Fact]
    public void CacheAsImageDrawsTheSameAtAWholeScale()
    {
        using Raster<Rgba8> image = Pixels.Photo(16, 12);
        Picture picture = Record(image);
        using Raster<Rgba8> vector = Pixels.Draw(128, 100, c => c.DrawPicture(picture));
        using Raster<Rgba8> cached = Pixels.Draw(128, 100, c =>
        {
            c.DrawPicture(picture, cacheAsImage: true);
            c.DrawPicture(picture, cacheAsImage: true);
        });
        using Raster<Rgba8> once = Pixels.Draw(128, 100, c => c.DrawPicture(picture, cacheAsImage: true));
        Assert.True(Pixels.MeanDifference(vector, once) < 0.5, $"mean {Pixels.MeanDifference(vector, once)}");
    }

    [Fact]
    public void TheRecorderEndsOnce()
    {
        PictureRecorder recorder = new(new RectF(0, 0, 10, 10));
        recorder.Save();
        recorder.Translate(1, 1);
        Picture picture = recorder.Finish();
        Assert.Equal(3, picture.CallCount);
        Assert.Throws<InvalidOperationException>(() => recorder.Finish());
        Assert.Throws<ObjectDisposedException>(() => recorder.FillRect(new RectF(0, 0, 1, 1), Rgba8.Red));
        Assert.Throws<ArgumentException>(() => new PictureRecorder(new RectF(0, 0, 0, 5)));
    }

    [Fact]
    public void DrawingPatternsTileThePicture()
    {
        PictureRecorder recorder = new(new RectF(0, 0, 10, 10));
        recorder.FillCircle(new Vector2(5, 5), 3.5f, Rgba8.Red);
        Picture dot = recorder.Finish();
        using Raster<Rgba8> tile = dot.ToRaster<Rgba8>();
        using Raster<Rgba8> pattern = Pixels.Draw(50, 40, c => c.FillRect(new RectF(0, 0, 50, 40), new PicturePaint(dot)));
        using Raster<Rgba8> images = Pixels.Draw(50, 40, c => c.FillRect(new RectF(0, 0, 50, 40), new ImagePaint(tile, TileMode.Repeat, TileMode.Repeat, new SamplingOptions { Filter = ResampleFilter.Bilinear })));
        Pixels.Equal(pattern, images);

        // Zoomed, the pattern is rendered at the device scale and stays sharp, unlike a scaled raster.
        using Raster<Rgba8> zoomed = Pixels.Draw(80, 80, c =>
        {
            c.Scale(8);
            c.FillRect(new RectF(0, 0, 10, 10), new PicturePaint(dot));
        });
        using Raster<Rgba8> sharp = Pixels.Draw(80, 80, c =>
        {
            c.Scale(8);
            c.FillCircle(new Vector2(5, 5), 3.5f, Rgba8.Red);
        });
        using Raster<Rgba8> blurry = Pixels.Draw(80, 80, c =>
        {
            c.Scale(8);
            c.FillRect(new RectF(0, 0, 10, 10), new ImagePaint(tile, TileMode.Repeat, TileMode.Repeat, new SamplingOptions { Filter = ResampleFilter.Bilinear }));
        });
        double fromVector = Pixels.MeanDifference(zoomed, sharp), fromRaster = Pixels.MeanDifference(blurry, sharp);
        Assert.True(fromVector < 0.5 && fromRaster > 4 * fromVector, $"pattern {fromVector}, raster {fromRaster}");
    }

    [Fact]
    public void DrawingsNest()
    {
        PictureRecorder inner = new(new RectF(0, 0, 10, 10));
        inner.FillRect(new RectF(0, 0, 10, 10), Rgba8.Green);
        Picture square = inner.Finish();
        PictureRecorder outer = new(new RectF(0, 0, 40, 20));
        outer.DrawPicture(square, Matrix3x2.CreateTranslation(2, 2));
        outer.DrawPicture(square, Matrix3x2.CreateTranslation(20, 5));
        Picture two = outer.Finish();
        Assert.Equal(2, two.CallCount);
        using Raster<Rgba8> drawn = Pixels.Draw(40, 20, c => c.DrawPicture(two));
        Assert.Equal(Rgba8.Green, drawn.GetPixel(5, 5));
        Assert.Equal(Rgba8.Green, drawn.GetPixel(25, 10));
        Assert.Equal(Rgba8.White, drawn.GetPixel(15, 15));
    }

    [Fact]
    public void ReplayUnderAnOverflowingTransformDrawsNothing()
    {
        using Raster<Rgba8> image = Pixels.Photo(16, 12);
        Picture picture = Record(image);
        using Raster<Rgba8> drawn = Pixels.Draw(128, 100, c =>
        {
            c.Scale(3e37f);
            c.DrawPicture(picture, Matrix3x2.CreateScale(1e10f));
            c.ResetMatrix();
            c.Scale(0);
            c.DrawImage(image, new Vector2(3, 3));
        });

        // Only the calls that fill the clip whatever the transform, the scene's Clear and its last DrawPaint, leave a mark.
        Assert.Equal(drawn.GetPixel(2, 2), drawn.GetPixel(64, 50));
        Assert.Equal(drawn.GetPixel(2, 2), drawn.GetPixel(5, 5));
    }

    internal static Picture Record(Raster image)
    {
        PictureRecorder recorder = new(new RectF(0, 0, 128, 100));
        Scene(recorder, image);
        return recorder.Finish();
    }

    /// <summary>Lets <see cref="Scene"/> draw under a base matrix: its SetMatrix and Rotate calls compose with the base.</summary>
    private sealed class Offset(Egami.Drawing.Canvas target, Matrix3x2 under) : Egami.Drawing.Canvas(target.Width, target.Height)
    {
        protected override void OnSave() => target.Save();

        protected override void OnRestore() => target.Restore();

        protected override void OnSaveLayer(LayerOptions options) => target.SaveLayer(options);

        protected override void OnRestoreLayer() => target.Restore();

        protected override void OnSetMatrix(Matrix3x2 matrix) => target.SetMatrix(matrix * under);

        protected override void OnClipRect(RectF rect, ClipOp op, bool antialias) => target.ClipRect(rect, op, antialias);

        protected override void OnClipRoundRect(RoundRect roundRect, ClipOp op, bool antialias) => target.ClipRoundRect(roundRect, op, antialias);

        protected override void OnClipPath(Path path, ClipOp op, bool antialias) => target.ClipPath(path, op, antialias);

        protected override void OnDrawPaint(Paint paint) => target.DrawPaint(paint);

        protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint)
        {
            if (stroke is null)
            {
                target.FillPath(path, paint);
            }
            else
            {
                target.StrokePath(path, stroke, paint);
            }
        }

        protected override void OnDrawRect(RectF rect, Stroke? stroke, Paint paint)
        {
            if (stroke is null)
            {
                target.FillRect(rect, paint);
            }
            else
            {
                target.StrokeRect(rect, stroke, paint);
            }
        }

        protected override void OnDrawRoundRect(RoundRect roundRect, Stroke? stroke, Paint paint)
        {
            if (stroke is null)
            {
                target.FillRoundRect(roundRect, paint);
            }
            else
            {
                target.StrokeRoundRect(roundRect, stroke, paint);
            }
        }

        protected override void OnDrawOval(RectF oval, Stroke? stroke, Paint paint)
        {
            if (stroke is null)
            {
                target.FillOval(oval, paint);
            }
            else
            {
                target.StrokeOval(oval, stroke, paint);
            }
        }

        protected override void OnDrawPoints(PointMode mode, ReadOnlySpan<Vector2> points, Stroke stroke, Paint paint) => target.DrawPoints(mode, points, stroke, paint);

        protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint) => target.DrawImage(image, source, destination, sampling, paint);

        protected override void OnDrawImageLattice(Raster image, Lattice lattice, RectF destination, SamplingOptions sampling, Paint? paint) => target.DrawImageLattice(image, lattice, destination, sampling, paint);

        protected override void OnDrawAtlas(Raster image, ReadOnlySpan<SpriteTransform> transforms, ReadOnlySpan<RectF> sources, ReadOnlySpan<RgbaF32> colors, BlendMode colorBlend, SamplingOptions sampling, Paint? paint)
            => target.DrawAtlas(image, transforms, sources, colors, colorBlend, sampling, null, paint);

        protected override void OnDrawMesh(Mesh mesh, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops) => target.DrawMesh(mesh, paint, colorBlend, valueStops);

        protected override void OnDrawPatch(Patch patch, Paint paint, BlendMode colorBlend, ReadOnlySpan<GradientStop> valueStops) => target.DrawPatch(patch, paint, colorBlend, valueStops);
    }
}
