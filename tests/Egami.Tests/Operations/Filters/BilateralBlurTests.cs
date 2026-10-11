using Egami.Operations;
using Egami.Operations.Filters;
using Egami.Pixels;

using static Egami.Tests.Operations.Filters.FilterAssert;

namespace Egami.Tests.Operations.Filters;

/// <summary>Phase 2.5.4 task 5: BilateralBlur's properties and its strided path against the full window.</summary>
public class BilateralBlurTests
{
    [Theory]
    [InlineData("duck")]
    [InlineData("egret")]
    public void StridedTapsAreCloseToTheFullWindow(string image)
    {
        using Raster<Rgba8> source = Load(image);
        using Raster<Rgba8> strided = source.Derive(o => o.BilateralBlur(8, 0.12f), Ct);
        using Raster<Rgba8> full = source.Derive(o => o.Add(new BilateralBlurOperation(8, 0.12f, 16, stride: 1)), Ct);
        double psnr = Psnr(full, strided);
        Assert.True(psnr >= 45, $"PSNR {psnr:F2} dB");
    }

    [Fact]
    public void FlatAreasStayAndEdgesKeep()
    {
        // A hard step: a small range sigma leaves it nearly untouched, while a Gaussian of the same reach smears it.
        using Raster<Rgba8> step = Raster.Create(40, 20, new Rgba8(20, 20, 20, 255));
        for (int y = 0; y < 20; y++)
        {
            step.Row(y)[20..].Fill(new Rgba8(220, 220, 220, 255));
        }

        using Raster<Rgba8> result = step.Derive(o => o.BilateralBlur(3, 0.05f), Ct);
        Assert.Equal(step.Row(10)[5], result.Row(10)[5]);
        Assert.InRange(Math.Abs(result.Row(10)[19].R - 20), 0, 1);
        Assert.InRange(Math.Abs(result.Row(10)[20].R - 220), 0, 1);
    }

    [Fact]
    public void SameOnOneThreadAndInsideAnArea()
    {
        SameOnOneThread("egret", o => o.BilateralBlur(2, 0.1f));
        SameInsideArea("duck", new Rect(51, 33, 123, 97), o => o.BilateralBlur(4, 0.2f));
        SameInsideArea("duck", new Rect(51, 33, 123, 97), o => o.BilateralBlur(6, 0.2f));
    }
}
