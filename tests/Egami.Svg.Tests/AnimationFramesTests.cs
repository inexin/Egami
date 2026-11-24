using System.Runtime.InteropServices;
using System.Text;

using Egami.Formats;
using Egami.Pixels;

namespace Egami.Svg.Tests;

/// <summary>Stage D, tasks 8 and 9: animations drawn as frames, loaded through Raster.Load, and frozen at a moment.</summary>
public class AnimationFramesTests
{
    private const string Ns = "xmlns='http://www.w3.org/2000/svg'";

    // x is 0 for the first second of every two, 50 for the second: two distinct pictures, looping every 2 s.
    private const string Loop = $"<svg {Ns} width='100' height='20'><rect width='10' height='10' fill='blue'><animate attributeName='x' values='0;50' dur='2s' repeatCount='indefinite' calcMode='discrete'/></rect></svg>";

    // x moves from 0 to 40 over a second and stays.
    private const string Once = $"<svg {Ns} width='100' height='20'><rect width='10' height='10' fill='blue'><animate attributeName='x' from='0' to='40' dur='1s' fill='freeze'/></rect></svg>";

    // Each repeat adds 10 to x: it runs forever and never loops.
    private const string Endless = $"<svg {Ns} width='100' height='20'><rect width='10' height='10' fill='blue'><animate attributeName='x' by='10' dur='1s' repeatCount='indefinite' accumulate='sum' additive='sum'/></rect></svg>";

    private static readonly EgamiContext s_format = EgamiContext.Default.WithFormat(SvgFormat.Info);

    private static bool Same<TPixel>(Raster<TPixel> a, Raster<TPixel> b)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (a.Width != b.Width || a.Height != b.Height)
        {
            return false;
        }

        for (int y = 0; y < a.Height; y++)
        {
            if (!MemoryMarshal.AsBytes(a.Row(y)).SequenceEqual(MemoryMarshal.AsBytes(b.Row(y))))
            {
                return false;
            }
        }

        return true;
    }

    private static Raster<Rgba8> FrameOf(Raster<Rgba8> raster, int index) => raster.CopyFrame(index);

    [Fact]
    public void ALoopIsOnePeriodWithStillStretchesMerged()
    {
        SvgDocument document = SvgDocument.Parse(Loop);
        using Raster<Rgba8> raster = document.RasterizeAnimation<Rgba8>(new SvgAnimationOptions { FrameRate = 10 });
        Assert.Equal(2, raster.Frames.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), raster.Frames[0].Metadata.Delay);
        Assert.Equal(TimeSpan.FromSeconds(1), raster.Frames[1].Metadata.Delay);
        Assert.Equal(0, raster.Metadata.LoopCount);

        using Raster<Rgba8> at0 = document.Rasterize<Rgba8>(new SvgRenderOptions { Time = TimeSpan.Zero });
        using Raster<Rgba8> at1 = document.Rasterize<Rgba8>(new SvgRenderOptions { Time = TimeSpan.FromSeconds(1) });
        using Raster<Rgba8> first = FrameOf(raster, 0);
        using Raster<Rgba8> second = FrameOf(raster, 1);
        Assert.True(Same(at0, first));
        Assert.True(Same(at1, second));
    }

    [Fact]
    public void WithoutMergingEveryFrameIsKept()
    {
        SvgDocument document = SvgDocument.Parse(Loop);
        using Raster<Rgba8> raster = document.RasterizeAnimation<Rgba8>(new SvgAnimationOptions { FrameRate = 10, MergeDuplicates = false });
        Assert.Equal(20, raster.Frames.Count);
        Assert.All(raster.Frames, f => Assert.Equal(TimeSpan.FromMilliseconds(100), f.Metadata.Delay));
    }

    [Fact]
    public void AnAnimationThatEndsIsDrawnToItsEndAndPlaysOnce()
    {
        SvgDocument document = SvgDocument.Parse(Once);
        using Raster<Rgba8> raster = document.RasterizeAnimation<Rgba8>(new SvgAnimationOptions { FrameRate = 4 });

        // 0, 0.25, 0.5, 0.75 and the end at 1 s, each a quarter second.
        Assert.Equal(5, raster.Frames.Count);
        Assert.All(raster.Frames, f => Assert.Equal(TimeSpan.FromMilliseconds(250), f.Metadata.Delay));
        Assert.Equal(1, raster.Metadata.LoopCount);
        using Raster<Rgba8> end = document.Rasterize<Rgba8>(new SvgRenderOptions { Time = TimeSpan.FromSeconds(1) });
        using Raster<Rgba8> last = FrameOf(raster, 4);
        Assert.True(Same(end, last));
        Assert.Equal(new Rgba8(0, 0, 255, 255), last.Row(5)[45]);
    }

    [Fact]
    public void AStillDocumentIsOneFrame()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='10' height='10'><rect width='5' height='5'/></svg>");
        using Raster<Rgba8> raster = document.RasterizeAnimation<Rgba8>();
        Assert.Single(raster.Frames);
        using Raster<Rgba8> still = document.Rasterize<Rgba8>();
        Assert.True(Same(still, raster));
    }

    [Fact]
    public void AnEndlessAnimationIsCutAtTheMaximumWithAWarning()
    {
        List<EgamiDiagnostic> seen = [];
        SvgDocument document = SvgDocument.Parse(Endless, new SvgLoadOptions { Context = EgamiContext.Default with { Diagnostics = seen.Add } });
        Assert.Null(document.Animation.Duration);
        Assert.Null(document.Animation.LoopPeriod);
        using Raster<Rgba8> raster = document.RasterizeAnimation<Rgba8>(new SvgAnimationOptions { FrameRate = 2, MaxLength = TimeSpan.FromSeconds(2) });
        Assert.Equal(4, raster.Frames.Count);
        Assert.Equal(1, raster.Metadata.LoopCount);
        Assert.Contains(seen, d => d.Id == "Svg.AnimationLength");
    }

    [Fact]
    public void AGivenLengthAndStartAreKept()
    {
        SvgDocument document = SvgDocument.Parse(Loop);
        SvgAnimationOptions options = new() { FrameRate = 2, Start = TimeSpan.FromSeconds(0.5), Length = TimeSpan.FromSeconds(1.25), MergeDuplicates = false };
        List<SvgFrame<Rgba8>> frames = [.. document.RenderFrames<Rgba8>(options)];
        try
        {
            Assert.Equal([0.5, 1.0, 1.5], frames.Select(f => f.Time.TotalSeconds));
            Assert.Equal([0.5, 0.5, 0.25], frames.Select(f => f.Duration.TotalSeconds));
        }
        finally
        {
            frames.ForEach(f => f.Raster.Dispose());
        }
    }

    [Fact]
    public void StreamedFramesMatchTheRasterAndADrawAtTheirTime()
    {
        SvgDocument document = SvgDocument.Parse(Once);
        SvgAnimationOptions options = new() { FrameRate = 8 };
        using Raster<Rgba8> all = document.RasterizeAnimation<Rgba8>(options);
        int i = 0;
        TimeSpan total = TimeSpan.Zero;
        foreach (SvgFrame<Rgba8> frame in document.RenderFrames<Rgba8>(options))
        {
            using Raster<Rgba8> raster = frame.Raster;
            using Raster<Rgba8> copy = FrameOf(all, i);
            using Raster<Rgba8> direct = document.Rasterize<Rgba8>(new SvgRenderOptions { Time = frame.Time });
            Assert.True(Same(copy, raster));
            Assert.True(Same(direct, raster));
            Assert.Equal(all.Frames[i].Metadata.Delay, frame.Duration);
            total += frame.Duration;
            i++;
        }

        Assert.Equal(all.Frames.Count, i);
        Assert.Equal(TimeSpan.FromSeconds(1.125), total);
    }

    [Fact]
    public void OneThreadAndManyDrawTheSameFrames()
    {
        const string css = $"<svg {Ns} width='60' height='60'><style>@keyframes spin {{ to {{ rotate: 360deg }} }} rect {{ transform-origin: 30px 30px; animation: spin 1s linear infinite }}</style><rect x='15' y='15' width='30' height='30' fill='teal'/></svg>";
        SvgDocument one = SvgDocument.Parse(css, new SvgLoadOptions { Context = EgamiContext.Default with { MaxParallelism = 1 } });
        SvgDocument many = SvgDocument.Parse(css, new SvgLoadOptions { Context = EgamiContext.Default with { MaxParallelism = 4 } });
        using Raster<Rgba8> a = one.RasterizeAnimation<Rgba8>(new SvgAnimationOptions { FrameRate = 24 });
        using Raster<Rgba8> b = many.RasterizeAnimation<Rgba8>(new SvgAnimationOptions { FrameRate = 24 });
        Assert.Equal(a.Frames.Count, b.Frames.Count);
        Assert.True(a.Frames.Count > 12);
        for (int i = 0; i < a.Frames.Count; i++)
        {
            using Raster<Rgba8> x = FrameOf(a, i);
            using Raster<Rgba8> y = FrameOf(b, i);
            Assert.True(Same(x, y), $"frame {i}");
            Assert.Equal(a.Frames[i].Metadata.Delay, b.Frames[i].Metadata.Delay);
        }
    }

    [Fact]
    public void LimitsAreCheckedBeforeDrawing()
    {
        EgamiContext frames = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxFrames = 19 } };
        SvgDocument document = SvgDocument.Parse(Loop);
        SvgAnimationOptions options = new() { FrameRate = 10, Render = new SvgRenderOptions { Context = frames } };
        Assert.Equal("MaxFrames", Assert.Throws<LimitExceededException>(() => document.RasterizeAnimation<Rgba8>(options)).LimitName);
        Assert.Throws<LimitExceededException>(() => document.RenderFrames<Rgba8>(options));

        // 20 frames of 100 × 20 Rgba8 are 160,000 bytes, before merging.
        EgamiContext bytes = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 159_999 }, MaxParallelism = 2 };
        options = new() { FrameRate = 10, Render = new SvgRenderOptions { Context = bytes } };
        Assert.Equal("MaxTotalBytes", Assert.Throws<LimitExceededException>(() => document.RasterizeAnimation<Rgba8>(options)).LimitName);

        // Streaming holds a frame per thread and the one waiting, not all of them.
        foreach (SvgFrame<Rgba8> frame in document.RenderFrames<Rgba8>(options))
        {
            frame.Raster.Dispose();
        }
    }

    [Fact]
    public void BadOptionsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgAnimationOptions { FrameRate = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgAnimationOptions { FrameRate = double.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgAnimationOptions { FrameRate = 1001 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgAnimationOptions { Start = TimeSpan.FromSeconds(-1) });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgAnimationOptions { Length = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgAnimationOptions { MaxLength = TimeSpan.Zero });
        Assert.Throws<ArgumentOutOfRangeException>(() => new SvgDecodeOptions { AnimationFrameRate = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => SvgDocument.Parse(Loop).Freeze(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void LoadingWithAFrameRateGivesAnAnimation()
    {
        byte[] data = Encoding.UTF8.GetBytes(Loop);
        SvgDecodeOptions options = new() { AnimationFrameRate = 10 };
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(data, s_format, options);
        Assert.Equal(2, raster.Frames.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), raster.Frames[1].Metadata.Delay);
        Assert.Equal(0, raster.Metadata.LoopCount);
        Assert.Equal(20, Raster.Probe(data, s_format, options).FrameCount);

        // Without a rate the static document loads, as before.
        using Raster<Rgba8> still = Raster.Load<Rgba8>(data, s_format);
        Assert.Single(still.Frames);
        Assert.Equal(1, Raster.Probe(data, s_format).FrameCount);
    }

    [Fact]
    public void LoadingAnEndlessAnimationWarnsInTheMetadata()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(Encoding.UTF8.GetBytes(Endless), s_format, new SvgDecodeOptions { AnimationFrameRate = 1 });
        Assert.Equal(10, raster.Frames.Count);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Svg.AnimationLength");
    }

    [Fact]
    public void LoadingChecksTheFrameLimit()
    {
        EgamiContext small = s_format with { Limits = SafetyLimits.Default with { MaxFrames = 10 } };
        Assert.Throws<LimitExceededException>(() => Raster.Load<Rgba8>(Encoding.UTF8.GetBytes(Loop), small, new SvgDecodeOptions { AnimationFrameRate = 10 }));
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.4)]
    [InlineData(1.5)]
    public void AFrozenSmilDocumentDrawsAsTheMoment(double seconds)
    {
        const string text = $"""
            <svg {Ns} width='100' height='60'>
              <path id='track' d='M10,10 L90,50' fill='none'/>
              <rect id='a' width='10' height='10' fill='blue'>
                <animate attributeName='x' values='0;50' dur='2s' repeatCount='indefinite' calcMode='discrete'/>
                <animate attributeName='fill' from='red' to='lime' dur='1s' fill='freeze'/>
              </rect>
              <circle r='5' fill='orange'><animateMotion dur='2s' rotate='auto' repeatCount='indefinite'><mpath href='#track'/></animateMotion></circle>
              <g transform='translate(0 30)'><rect id='b' width='8' height='8' transform='rotate(10)' style='transform-origin: 4px 4px'><animateTransform attributeName='transform' type='scale' from='1' to='2' dur='2s' additive='sum'/></rect></g>
              <rect id='gone' x='80' width='10' height='10'><discard begin='1s'/></rect>
            </svg>
            """;
        SvgDocument document = SvgDocument.Parse(text);
        TimeSpan time = TimeSpan.FromSeconds(seconds);
        SvgDocument frozen = document.Freeze(time);
        Assert.False(frozen.Animation.HasAnimations);
        Assert.DoesNotContain(frozen.Root.Descendants(), e => e.Name is "animate" or "animateMotion" or "animateTransform" or "discard");
        Assert.Equal(seconds >= 1, frozen.FindById("gone") is null);
        using Raster<Rgba8> expected = document.Rasterize<Rgba8>(new SvgRenderOptions { Time = time });
        using Raster<Rgba8> actual = frozen.Rasterize<Rgba8>();
        using Raster<Rgba8> still = document.Rasterize<Rgba8>();
        Assert.False(Same(still, expected));
        Assert.True(Same(expected, actual));

        // Saved and read again it is the same picture, and the original is not changed.
        using MemoryStream stream = new();
        frozen.Save(stream);
        using Raster<Rgba8> reread = SvgDocument.Parse(Encoding.UTF8.GetString(stream.ToArray())).Rasterize<Rgba8>();
        Assert.True(Same(expected, reread));
        Assert.True(document.Animation.HasAnimations);
        Assert.Equal(2, document.FindById("a")!.Elements().Count());
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.6)]
    public void AFrozenCssDocumentDrawsAsTheMoment(double seconds)
    {
        const string text = $$"""
            <svg {{Ns}} width='100' height='100'>
              <style>
                @keyframes spin { to { rotate: 360deg } }
                @keyframes pulse { 50% { fill: orange; scale: 1.2 } }
                @keyframes grow { to { r: 30px } }
                .spinner { transform-origin: 50px 50px; animation: spin 2s linear infinite, pulse 1s ease-in-out infinite !important }
                circle { animation: grow 1s both }
              </style>
              <rect class='spinner' x='30' y='30' width='40' height='40' fill='teal' style='opacity: 0.9'/>
              <circle cx='20' cy='80' r='5' fill='purple'/>
            </svg>
            """;
        SvgDocument document = SvgDocument.Parse(text);
        TimeSpan time = TimeSpan.FromSeconds(seconds);
        SvgDocument frozen = document.Freeze(time);
        Assert.Null(frozen.Animation.Duration);
        using Raster<Rgba8> expected = document.Rasterize<Rgba8>(new SvgRenderOptions { Time = time });
        using Raster<Rgba8> actual = frozen.Rasterize<Rgba8>();
        using Raster<Rgba8> still = document.Rasterize<Rgba8>();
        Assert.False(Same(still, expected));
        using Raster<Rgba8> later = frozen.Rasterize<Rgba8>(new SvgRenderOptions { Time = TimeSpan.FromSeconds(1.7) });
        Assert.True(Same(expected, actual));
        Assert.True(Same(expected, later));
    }

    [Fact]
    public void AStillDocumentFreezesToACopy()
    {
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='10' height='10'><rect id='r' width='5' height='5'/></svg>");
        SvgDocument frozen = document.Freeze(TimeSpan.FromSeconds(3));
        Assert.NotSame(document.Root, frozen.Root);
        Assert.NotSame(document.FindById("r"), frozen.FindById("r"));
        using Raster<Rgba8> a = document.Rasterize<Rgba8>();
        using Raster<Rgba8> b = frozen.Rasterize<Rgba8>();
        Assert.True(Same(a, b));
    }

    [Fact]
    public void TransformTranslateTakesEmsAndViewportUnits()
    {
        // translate() in the transform property: 2em of a 10px font is 20px, 10vw of a 100px viewport is 10px.
        SvgDocument document = SvgDocument.Parse($"<svg {Ns} width='100' height='40'><rect width='10' height='10' style='font-size: 10px; transform: translate(2em, 1vw)'/></svg>");
        using Raster<Rgba8> raster = document.Rasterize<Rgba8>();
        Assert.Equal(0, raster.Row(5)[5].A);
        Assert.Equal(255, raster.Row(5)[25].A);
        Assert.Equal(0, raster.Row(0)[25].A);
    }
}
