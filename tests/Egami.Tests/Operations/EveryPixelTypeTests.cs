using System.Reflection;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

/// <summary>
/// Phase 2.1.1 task 11: every operation chain on every pixel type added in 2.1.1 matches the float path (the same chain on the
/// same pixels as RgbaF32, or RgbF32 for types without alpha, then converted to the type) within the 1.2 tolerances, and stays
/// in the allocation budget. CMYK filters work on the four inks by design, so only CMYK geometry and color operations are
/// compared with the RGB float path; CmykTests checks the ink filtering itself.
/// </summary>
public sealed class EveryPixelTypeTests : IDisposable
{
    private static readonly Type[] NewTypes =
    [
        typeof(Rgb16), typeof(GrayAlpha16), typeof(Argb8), typeof(Abgr8), typeof(Alpha8), typeof(RgbaF16), typeof(RgbF32),
        typeof(GrayF32), typeof(Bgr565), typeof(Bgra4444), typeof(Rgb10A2), typeof(Cmyk8), typeof(Cmyk16), typeof(PremulRgba8),
        typeof(PremulBgra8), typeof(PremulRgbaF32),
    ];

    private readonly Raster<Rgba8> _logo = OperationCoverageTests.Logo();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string> Cases()
    {
        TheoryData<string, string> data = [];
        foreach (string operation in OperationCoverageTests.Chains.Keys)
        {
            foreach (Type type in NewTypes)
            {
                data.Add(operation, type.Name);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesTheFloatPath(string operation, string type)
    {
        Invoke(nameof(Compare), type, operation);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void StaysInTheAllocationBudget(string operation, string type)
    {
        Invoke(nameof(Budget), type, operation);
    }

    public void Dispose()
    {
        _logo.Dispose();
    }

    private void Invoke(string method, string type, string operation)
    {
        MethodInfo m = typeof(EveryPixelTypeTests).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!;
        try
        {
            m.MakeGenericMethod(NewTypes.Single(t => t.Name == type)).Invoke(this, [operation]);
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            throw e.InnerException;
        }
    }

    private static readonly HashSet<string> GeometryAndColor =
    [
        "Crop", "Pad", "Flip-Horizontal", "Flip-Vertical", "Flip-Both", "Rotate90", "Rotate270", "FillColor", "Color-Matrix",
        "Color-Tables", "Grayscale-Linear", "Sepia",
    ];

    private void Compare<T>(string operation)
        where T : unmanaged, IPixelType<T>
    {
        if (T.Layout.ChannelOrder == PixelChannelOrder.Cmyk && !GeometryAndColor.Contains(operation))
        {
            return;
        }

        using Raster<Rgba8> source = OperationCoverageTests.Noise(61, 37, EgamiContext.Default);
        Action<RasterOps> chain = OperationCoverageTests.Chains[operation](new Size(61, 37), _logo);
        using Raster<T> typed = source.As<T>();
        using Raster<T> result = typed.Derive(chain, Ct);
        using Raster<T> expected = T.Layout.HasAlpha ? ThroughFloats<T, RgbaF32>(typed, chain) : ThroughFloats<T, RgbF32>(typed, chain);
        Assert.Equal((expected.Width, expected.Height), (result.Width, result.Height));

        // Compared as premultiplied 8-bit color, so colors under no coverage do not count; one step of the type's own
        // precision is allowed where that is coarser than the 1.2 tolerance of 8 levels (then PSNR, which a 2-bit alpha cannot
        // reach after a wide blur, is not checked).
        using Raster<Rgba8> e8 = expected.As<Rgba8>();
        using Raster<Rgba8> a8 = result.As<Rgba8>();
        int bits = Math.Min(8, T.Layout.ChannelBits.IsEmpty ? 8 : Min(T.Layout.ChannelBits));
        int allowed = Math.Max(8, 2 * 255 / ((1 << bits) - 1));
        int worst = 0;
        double squares = 0;
        long count = 0;
        for (int y = 0; y < e8.Height; y++)
        {
            Span<Rgba8> er = e8.Row(y), ar = a8.Row(y);
            for (int x = 0; x < er.Length; x++)
            {
                for (int c = 0; c < 4; c++)
                {
                    int ev = Premultiplied(er[x], c), av = Premultiplied(ar[x], c);
                    worst = Math.Max(worst, Math.Abs(ev - av));
                    squares += (ev - av) * (ev - av);
                    count++;
                }
            }
        }

        double psnr = squares == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (squares / count));
        Assert.True(worst <= allowed && (bits < 8 || psnr >= 40), $"{operation} on {typeof(T).Name}: worst {worst} levels (allowed {allowed}), PSNR {psnr:F1} dB");

        static int Min(ReadOnlySpan<int> values)
        {
            int min = int.MaxValue;
            foreach (int v in values)
            {
                min = Math.Min(min, v);
            }

            return min;
        }

        static int Premultiplied(Rgba8 p, int channel) => channel switch
        {
            0 => ((p.R * p.A) + 127) / 255,
            1 => ((p.G * p.A) + 127) / 255,
            2 => ((p.B * p.A) + 127) / 255,
            _ => p.A,
        };
    }

    private static Raster<T> ThroughFloats<T, TFloat>(Raster<T> typed, Action<RasterOps> chain)
        where T : unmanaged, IPixelType<T>
        where TFloat : unmanaged, IPixelType<TFloat>
    {
        using Raster<TFloat> floats = typed.As<TFloat>();
        using Raster<TFloat> result = floats.Derive(chain, Ct);
        return result.As<T>();
    }

    private void Budget<T>(string operation)
        where T : unmanaged, IPixelType<T>
    {
        EgamiContext context = EgamiContext.Default with { MaxParallelism = 1 };
        using Raster<Rgba8> smallSource = OperationCoverageTests.Noise(120, 90, context);
        using Raster<Rgba8> largeSource = OperationCoverageTests.Noise(480, 360, context);
        using Raster<T> small = smallSource.As<T>();
        using Raster<T> large = largeSource.As<T>();
        long smallBytes = Allocated(small, OperationCoverageTests.Chains[operation](new Size(120, 90), _logo));
        long largeBytes = Allocated(large, OperationCoverageTests.Chains[operation](new Size(480, 360), _logo));
        Assert.True(largeBytes - smallBytes < 1024, $"{operation} on {typeof(T).Name}: {largeBytes:N0} bytes for 480×360 against {smallBytes:N0} for 120×90.");
        Assert.True(largeBytes < 8 * 1024, $"{operation} on {typeof(T).Name}: {largeBytes:N0} bytes per call.");
    }

    private static long Allocated<T>(Raster<T> raster, Action<RasterOps> chain)
        where T : unmanaged, IPixelType<T>
    {
        for (int i = 0; i < 2; i++)
        {
            raster.Derive(chain, Ct).Dispose();
        }

        long fewest = long.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            raster.Derive(chain, Ct).Dispose();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return fewest;
    }
}
