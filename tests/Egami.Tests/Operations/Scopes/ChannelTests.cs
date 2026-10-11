using Egami.Formats.Jpeg;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Scopes;

public sealed class ChannelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ExtractChannelPicksTheGrayTypeAndExactValues()
    {
        using Raster<Rgba8> rgba = OperationCoverageTests.Noise(20, 10, EgamiContext.Default);
        using Raster red = rgba.ExtractChannel(ColorChannel.Red);
        using Raster alpha = rgba.ExtractChannel(ColorChannel.Alpha);
        Assert.IsType<Raster<Gray8>>(red);
        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                Assert.Equal(rgba.GetPixel(x, y).R, ((Raster<Gray8>)red).GetPixel(x, y).L);
                Assert.Equal(rgba.GetPixel(x, y).A, ((Raster<Gray8>)alpha).GetPixel(x, y).L);
            }
        }

        using Raster<Rgba16> wide = Raster.Create(3, 1, new Rgba16(1, 40000, 65535, 12345));
        using Raster green = wide.ExtractChannel(ColorChannel.Green);
        Assert.Equal(40000, Assert.IsType<Raster<Gray16>>(green).GetPixel(2, 0).L);
        using Raster<RgbaF32> floats = Raster.Create(1, 1, new RgbaF32(0.25f, 0.5f, 0.75f, 1));
        Assert.Equal(0.75f, Assert.IsType<Raster<GrayF32>>(floats.ExtractChannel(ColorChannel.Blue)).GetPixel(0, 0).L);
        using Raster<Rgb8> opaque = Raster.Create(1, 1, new Rgb8(1, 2, 3));
        Assert.Equal(255, ((Raster<Gray8>)opaque.ExtractChannel(ColorChannel.Alpha)).GetPixel(0, 0).L);
        using Raster<Gray8> gray = Raster.Create(1, 1, new Gray8(77));
        Assert.Equal(77, ((Raster<Gray8>)gray.ExtractChannel(ColorChannel.Blue)).GetPixel(0, 0).L);
        Assert.Equal(77, ((Raster<Gray8>)gray.ExtractChannel(ColorChannel.Luminance)).GetPixel(0, 0).L);
        Assert.Throws<ArgumentOutOfRangeException>(() => gray.ExtractChannel((ColorChannel)9));
    }

    [Fact]
    public void ExtractChannelKeepsExifAndDropsTheProfile()
    {
        using Raster<Rgba8> rgba = Raster.Create(4, 4, Rgba8.Red);
        rgba.Metadata.SetColorProfile(Egami.Colors.ColorProfile.DisplayP3);
        rgba.Metadata.Exif = new Egami.Metadata.ExifData();
        rgba.Metadata.Exif.Set(Egami.Metadata.ExifKey.Artist, "Lars");
        using Raster red = rgba.ExtractChannel(ColorChannel.Red);
        Assert.True(red.Metadata.IccProfile.IsEmpty);
        Assert.True(red.Metadata.Exif!.TryGet(Egami.Metadata.ExifKey.Artist, out _));
    }

    [Fact]
    public void CombineUndoesExtract()
    {
        using Raster<Rgba8> source = OperationCoverageTests.Noise(31, 17, EgamiContext.Default);
        using Raster r = source.ExtractChannel(ColorChannel.Red), g = source.ExtractChannel(ColorChannel.Green);
        using Raster b = source.ExtractChannel(ColorChannel.Blue), a = source.ExtractChannel(ColorChannel.Alpha);
        using Raster<Rgba8> combined = Raster.CombineChannels<Rgba8>(r, g, b, a);
        Assert.Equal(Pixels(source), Pixels(combined));
        using Raster<Rgba8> opaque = Raster.CombineChannels<Rgba8>(r, g, b);
        Assert.All(Pixels(opaque), p => Assert.Equal(255, p.A));

        using Raster<Gray8> one = Raster.Create<Gray8>(31, 17);
        using Raster<Gray8> small = Raster.Create<Gray8>(30, 17);
        Assert.Throws<ArgumentException>(() => Raster.CombineChannels<Rgba8>(r, g, small));
        Assert.Throws<ArgumentNullException>(() => Raster.CombineChannels<Rgba8>(r, null!, b));
    }

    [Fact]
    public void ShuffleSwapsAndFusesWithColorSteps()
    {
        using Raster<Rgba8> source = Raster.Create(1, 1, new Rgba8(10, 20, 30, 40));
        using Raster<Rgba8> swapped = source.Derive(ops => ops.ShuffleChannels(ChannelSource.Blue, ChannelSource.Green, ChannelSource.Red, ChannelSource.Alpha), Ct);
        Assert.Equal(new Rgba8(30, 20, 10, 40), swapped.GetPixel(0, 0));
        using Raster<Rgba8> constants = source.Derive(ops => ops.ShuffleChannels(ChannelSource.Zero, ChannelSource.One, ChannelSource.Alpha, ChannelSource.Green), Ct);
        Assert.Equal(new Rgba8(0, 255, 40, 20), constants.GetPixel(0, 0));
        OperationPipeline.Plan plan = OperationPipeline.Build(ops => ops.Brightness(1.1f).ShuffleChannels(ChannelSource.Blue, ChannelSource.Green, ChannelSource.Red, ChannelSource.Alpha).Contrast(0.9f), new Size(4, 4));
        Assert.Single(plan.Steps);
    }

    [Fact]
    public void OnChannelsKeepsTheResultOnlyInTheChosenChannels()
    {
        using Raster<Rgba8> source = OperationCoverageTests.Noise(40, 30, EgamiContext.Default);
        using Raster<Rgba8> blurred = source.Derive(ops => ops.Blur(2), Ct);
        using Raster<Rgba8> alphaOnly = source.Derive(ops => ops.OnChannels(ColorChannels.Alpha, o => o.Blur(2)), Ct);
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                Rgba8 s = source.GetPixel(x, y), w = blurred.GetPixel(x, y);
                Assert.Equal(new Rgba8(s.R, s.G, s.B, w.A), alphaOnly.GetPixel(x, y));
            }
        }

        // Color steps alone become one matrix with the other rows the identity.
        OperationPipeline.Plan plan = OperationPipeline.Build(ops => ops.Saturation(1.2f).OnChannels(ColorChannels.Red | ColorChannels.Alpha, o => o.Invert().Brightness(0.9f)).Contrast(1.1f), new Size(4, 4));
        Assert.Single(plan.Steps);
        using Raster<Rgba8> red = source.Derive(ops => ops.OnChannels(ColorChannels.Red, o => o.Invert()), Ct);
        Assert.Equal(new Rgba8((byte)(255 - source.GetPixel(3, 3).R), source.GetPixel(3, 3).G, source.GetPixel(3, 3).B, source.GetPixel(3, 3).A), red.GetPixel(3, 3));

        // On a float raster, through the float path.
        using Raster<RgbaF32> floats = source.As<RgbaF32>();
        using Raster<RgbaF32> floatAlpha = floats.Derive(ops => ops.OnChannels(ColorChannels.Alpha, o => o.FlipHorizontal()), Ct);
        Assert.Equal(floats.GetPixel(0, 0).R, floatAlpha.GetPixel(0, 0).R, 1e-6f);
        Assert.Equal(floats.GetPixel(39, 0).A, floatAlpha.GetPixel(0, 0).A, 1e-6f);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(ops => ops.OnChannels((ColorChannels)16, o => o.Invert()), Ct));
    }

    [Fact]
    public void AlphaUtilities()
    {
        using Raster<Rgba8> source = Raster.Create(1, 1, new Rgba8(200, 100, 50, 77));
        using Raster<Rgba8> gray = source.Derive(ops => ops.AlphaToGray(), Ct);
        Assert.Equal(new Rgba8(77, 77, 77, 255), gray.GetPixel(0, 0));
        using Raster<Rgba8> luma = source.Derive(ops => ops.LuminanceToAlpha(), Ct);
        Assert.Equal(new Rgba8(200, 100, 50, (byte)Math.Round((0.2126 * 200) + (0.7152 * 100) + (0.0722 * 50))), luma.GetPixel(0, 0));
        using Raster<Gray8> mask = Raster.Create(1, 1, new Gray8(33));
        using Raster<Rgba8> copied = Raster.Create(2, 1, new Rgba8(1, 2, 3, 4));
        copied.Apply(ops => ops.CopyAlpha(new Mask(mask)), Ct);
        Assert.Equal(new Rgba8(1, 2, 3, 33), copied.GetPixel(0, 0));
        Assert.Equal(new Rgba8(1, 2, 3, 0), copied.GetPixel(1, 0));
        using Raster<Rgba8> flat = source.Derive(ops => ops.Flatten(new Rgba8(0, 0, 255, 0)), Ct);
        Assert.Equal(new Rgba8((byte)((200 * 77 + 127) / 255), (byte)((100 * 77 + 127) / 255), (byte)((50 * 77 + (255 * 178) + 127) / 255), 255), flat.GetPixel(0, 0));
    }

    /// <summary>The JPEG writer's Background now runs on the Flatten kernel: its bytes equal saving a flattened copy.</summary>
    [Fact]
    public void JpegBackgroundEqualsFlatten()
    {
        using Raster<Rgba8> source = OperationCoverageTests.Noise(64, 48, EgamiContext.Default);
        Rgba8 background = new(250, 240, 200, 255);
        using MemoryStream withBackground = new(), flattened = new();
        source.Save(withBackground, JpegFormat.Info, new JpegWriterOptions { Background = background, Quality = 90 });
        using Raster<Rgba8> flat = source.Derive(ops => ops.Flatten(background), Ct);
        flat.Save(flattened, JpegFormat.Info, new JpegWriterOptions { Quality = 90 });
        Assert.Equal(flattened.ToArray(), withBackground.ToArray());
    }

    private static Rgba8[] Pixels(Raster<Rgba8> raster)
    {
        Rgba8[] result = new Rgba8[raster.Width * raster.Height];
        for (int y = 0; y < raster.Height; y++)
        {
            raster.Row(y).CopyTo(result.AsSpan(y * raster.Width, raster.Width));
        }

        return result;
    }
}
