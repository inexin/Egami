using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Advanced;

public class DocumentHookTests
{
    [Fact]
    public void AnEncodedImageDrawsItsDecodedPixels()
    {
        using Raster<Rgba8> photo = Pixels.Photo(40, 30);
        EncodedImage jpeg = EncodedImage.FromBytes(Jpeg(photo));
        Assert.Equal(40, jpeg.Width);
        Assert.Equal("JPEG", jpeg.Info.Format.Name);
        SamplingOptions nearest = new() { Filter = ResampleFilter.Nearest };
        using Raster<Rgba8> encoded = Pixels.Draw(64, 48, c => c.DrawImage(jpeg, new RectF(10, 10, 15, 12), new RectF(5, 5, 30, 24), nearest));
        using Raster<Rgba8> decoded = Pixels.Draw(64, 48, c => c.DrawImage(jpeg.Decode(), new RectF(10, 10, 15, 12), new RectF(5, 5, 30, 24), nearest));
        Pixels.Equal(encoded, decoded);
        Assert.Same(jpeg.Decode(), jpeg.Decode());
        Assert.Throws<UnsupportedFormatException>(() => EncodedImage.FromBytes([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12]));
    }

    [Fact]
    public void ADocumentBackendGetsTheBytesTheOthersThePixels()
    {
        using Raster<Rgba8> photo = Pixels.Photo(20, 10);
        byte[] bytes = Jpeg(photo);
        EncodedImage jpeg = EncodedImage.FromBytes(bytes);
        using DocumentCanvas document = new();
        document.DrawImage(jpeg, new RectF(0, 0, 40, 20));
        Assert.Equal(["encoded 20x10 [0 0 20 10] [0 0 40 20]"], document.Calls);
        Assert.True(document.Last!.Data.Span.SequenceEqual(bytes));

        using Canvas.TraceCanvas trace = new(64, 48);
        trace.DrawImage(jpeg, new RectF(0, 0, 40, 20));
        Assert.Contains(trace.Calls, c => c.StartsWith("DrawImage 20x10", StringComparison.Ordinal));
    }

    [Fact]
    public void AnnotationsAndMarkedContentReachTheBackendAndLeaveNoPixels()
    {
        using DocumentCanvas document = new();
        document.BeginMarkedContent(new MarkedContent("P") { NodeId = 7 });
        document.DrawAnnotation(new RectF(1, 2, 30, 10), Annotation.Url("https://example.com"));
        document.BeginMarkedContent(new MarkedContent("Span") { ActualText = "fi" });
        Assert.Equal(2, document.MarkedContentDepth);
        document.EndMarkedContent();
        document.EndMarkedContent();
        Assert.Throws<InvalidOperationException>(document.EndMarkedContent);
        Assert.Equal(["begin P 7", "annotation Url https://example.com [1 2 30 10]", "begin Span ", "end", "end"], document.Calls);

        // Disposing ends what is still open.
        document.BeginMarkedContent(new MarkedContent("Artifact"));
        document.Dispose();
        Assert.Equal("end", document.Calls[^1]);

        using Raster<Rgba8> drawn = Pixels.Draw(16, 16, c =>
        {
            c.BeginMarkedContent(new MarkedContent("Figure") { AlternateText = "A dot" });
            c.DrawAnnotation(new RectF(0, 0, 16, 16), Annotation.NamedDestination("top"));
            c.EndMarkedContent();
        });
        Assert.Equal(Rgba8.White, drawn.GetPixel(8, 8));

        Assert.Throws<ArgumentException>(() => new MarkedContent("Two words"));
        Assert.Throws<ArgumentException>(() => new MarkedContent("/P"));
        Assert.Throws<ArgumentException>(() => Annotation.Url(string.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Annotation((AnnotationKind)9, "x"));
    }

    [Fact]
    public void PicturesRecordAndSaveTheNewCalls()
    {
        using Raster<Rgba8> photo = Pixels.Photo(20, 10);
        EncodedImage jpeg = EncodedImage.FromBytes(Jpeg(photo));
        PictureRecorder recorder = new(new RectF(0, 0, 100, 60));
        recorder.BeginMarkedContent(new MarkedContent("Figure") { NodeId = 3, ActualText = "logo", AlternateText = "Egami's logo" });
        recorder.DrawImage(jpeg, new RectF(10, 10, 40, 20));
        recorder.EndMarkedContent();
        recorder.DrawAnnotation(new RectF(10, 10, 40, 20), Annotation.LinkToDestination("chapter-2"));
        recorder.BeginMarkedContent(new MarkedContent("Artifact"));
        Picture picture = recorder.Finish();
        Assert.Equal(6, picture.CallCount);

        using DocumentCanvas direct = new();
        picture.Playback(direct);
        Assert.Equal(["begin Figure 3", "encoded 20x10 [0 0 20 10] [10 10 40 20]", "end", "annotation LinkToDestination chapter-2 [10 10 40 20]", "begin Artifact ", "end"], direct.Calls);

        using MemoryStream first = new();
        picture.Save(first);
        Picture loaded = Picture.Load(new MemoryStream(first.ToArray()));
        using MemoryStream second = new();
        loaded.Save(second);
        Assert.Equal(first.ToArray(), second.ToArray());
        using DocumentCanvas replayed = new();
        loaded.Playback(replayed);
        Assert.Equal(direct.Calls, replayed.Calls);
        Assert.True(replayed.Last!.Data.Span.SequenceEqual(jpeg.Data.Span));
    }

    [Fact]
    public void TheFallbackScalesMaskBlursWithTheRaster()
    {
        // A blurred rectangle through the fallback at 4 raster pixels per unit equals the same rectangle drawn at 4 times the size.
        SolidPaint blurred = new(Rgba8.Black) { Blur = new MaskBlur(2) };
        using FallbackCanvas page = new();
        FallbackImage image = page.Fallback(c => c.FillRect(new RectF(10, 10, 20, 10), blurred), 4)!;
        using Raster<Rgba8> direct = Pixels.Draw(160, 120, c => c.FillRect(new RectF(40, 40, 80, 40), blurred with { Blur = new MaskBlur(8) }), default(Rgba8));
        using Raster<Rgba8> fallback = image.Image.As<Rgba8>();
        Assert.Equal(160, fallback.Width);
        Assert.True(Pixels.MaxDifference(direct, fallback) <= 1, $"{Pixels.MaxDifference(direct, fallback)}");
    }

    private static byte[] Jpeg(Raster<Rgba8> raster)
    {
        using MemoryStream stream = new();
        raster.Save(stream, "jpeg");
        return stream.ToArray();
    }

    private sealed class FallbackCanvas() : Egami.Drawing.Canvas(40, 30)
    {
        public FallbackImage? Fallback(Action<Egami.Drawing.Canvas> draw, float scale) => RasterizeFallback(draw, scale);

        protected override void OnClipPath(Path path, ClipOp op, bool antialias)
        {
        }

        protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint)
        {
        }

        protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
        {
        }
    }

    /// <summary>A document backend: it logs encoded images, annotations and marked content, and draws nothing else.</summary>
    private sealed class DocumentCanvas() : Egami.Drawing.Canvas(200, 100)
    {
        public List<string> Calls { get; } = [];

        public EncodedImage? Last { get; private set; }

        protected override void OnDrawEncodedImage(EncodedImage image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
        {
            Last = image;
            Calls.Add(FormattableString.Invariant($"encoded {image.Width}x{image.Height} {R(source)} {R(destination)}"));
        }

        protected override void OnDrawAnnotation(RectF area, Annotation annotation) => Calls.Add($"annotation {annotation.Kind} {annotation.Value} {R(area)}");

        protected override void OnBeginMarkedContent(MarkedContent content) => Calls.Add($"begin {content.Tag} {content.NodeId}".TrimEnd(' ') + (content.NodeId is null ? " " : string.Empty));

        protected override void OnEndMarkedContent() => Calls.Add("end");

        protected override void OnClipPath(Path path, ClipOp op, bool antialias)
        {
        }

        protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint)
        {
        }

        protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
        {
        }

        private static string R(RectF r) => FormattableString.Invariant($"[{r.X} {r.Y} {r.Width} {r.Height}]");
    }
}
