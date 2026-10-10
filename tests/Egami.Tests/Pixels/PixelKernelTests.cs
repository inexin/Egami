using System.Reflection;
using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Tests.Pixels;

/// <summary>
/// Every SIMD kernel must produce exactly the bytes the scalar path does, at every vector width.
/// CI also runs these tests with AVX-512, AVX2 and all hardware intrinsics turned off.
/// </summary>
public class PixelKernelTests
{
    private static readonly int[] Lengths = [.. Enumerable.Range(0, 70), 127, 128, 129, 255, 256, 1000, 4099];

    public static TheoryData<string, string, int> KernelPairsAndWidths()
    {
        TheoryData<string, string, int> data = [];
        foreach ((Type from, Type to) in KernelPairs)
        {
            foreach (int bits in new[] { 512, 256, 128, 0 })
            {
                data.Add(from.Name, to.Name, bits);
            }
        }

        return data;
    }

    private static readonly (Type From, Type To)[] KernelPairs =
    [
        (typeof(Rgba8), typeof(Bgra8)), (typeof(Bgra8), typeof(Rgba8)),
        (typeof(Rgb8), typeof(Bgr8)), (typeof(Bgr8), typeof(Rgb8)),
        (typeof(Rgb8), typeof(Rgba8)), (typeof(Bgr8), typeof(Bgra8)),
        (typeof(Rgb8), typeof(Bgra8)), (typeof(Bgr8), typeof(Rgba8)),
        (typeof(Rgba8), typeof(Rgb8)), (typeof(Bgra8), typeof(Bgr8)),
        (typeof(Rgba8), typeof(Bgr8)), (typeof(Bgra8), typeof(Rgb8)),
        (typeof(Gray8), typeof(Rgba8)), (typeof(Gray8), typeof(Bgra8)),
        (typeof(Gray8), typeof(Rgb8)), (typeof(Gray8), typeof(Bgr8)),
        (typeof(GrayAlpha8), typeof(Rgba8)), (typeof(GrayAlpha8), typeof(Bgra8)),
        (typeof(Rgba8), typeof(Rgba16)), (typeof(Gray8), typeof(Gray16)),
        (typeof(Rgba16), typeof(Rgba8)), (typeof(Gray16), typeof(Gray8)),
        (typeof(Rgb8), typeof(Rgb16)), (typeof(GrayAlpha8), typeof(GrayAlpha16)),
        (typeof(Rgb16), typeof(Rgb8)), (typeof(GrayAlpha16), typeof(GrayAlpha8)),
        (typeof(Rgba8), typeof(Argb8)), (typeof(Argb8), typeof(Rgba8)), (typeof(Bgra8), typeof(Abgr8)), (typeof(Abgr8), typeof(Bgra8)),
        (typeof(Rgba8), typeof(Abgr8)), (typeof(Abgr8), typeof(Rgba8)), (typeof(Bgra8), typeof(Argb8)), (typeof(Argb8), typeof(Bgra8)),
        (typeof(Argb8), typeof(Abgr8)), (typeof(Abgr8), typeof(Argb8)),
        (typeof(RgbaF16), typeof(RgbaF32)), (typeof(RgbaF32), typeof(RgbaF16)),
        (typeof(Rgba8), typeof(PremulRgba8)), (typeof(Bgra8), typeof(PremulBgra8)), (typeof(PremulRgba8), typeof(Rgba8)), (typeof(PremulBgra8), typeof(Bgra8)),
        (typeof(Bgr565), typeof(Rgba8)), (typeof(Bgr565), typeof(Bgra8)), (typeof(Rgba8), typeof(Bgr565)), (typeof(Bgra8), typeof(Bgr565)),
        (typeof(Rgba8), typeof(Gray8)), (typeof(Bgra8), typeof(Gray8)), (typeof(Rgb8), typeof(Gray8)), (typeof(Bgr8), typeof(Gray8)),
    ];

    [Theory]
    [MemberData(nameof(KernelPairsAndWidths))]
    public void KernelMatchesScalarPath(string from, string to, int bits)
    {
        Invoke(nameof(CheckKernel), from, to, bits);
    }

    [Theory]
    [MemberData(nameof(KernelPairsAndWidths))]
    public void PairHasAKernel(string from, string to, int bits)
    {
        _ = bits;
        Assert.True((bool)Invoke(nameof(HasKernel), from, to, 0)!, $"{from} -> {to} has no SIMD kernel");
    }

    [Fact]
    public void PublicConvertMatchesScalarPathForEveryKernelPair()
    {
        foreach ((Type from, Type to) in KernelPairs)
        {
            Invoke(nameof(CheckKernel), from.Name, to.Name, PixelKernels.HardwareBits);
        }
    }

    private static object? Invoke(string method, string from, string to, int bits)
    {
        MethodInfo m = typeof(PixelKernelTests).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!;
        Type[] all = [typeof(Rgba8), typeof(Bgra8), typeof(Rgb8), typeof(Bgr8), typeof(Gray8), typeof(Gray16), typeof(GrayAlpha8), typeof(Rgba16), typeof(Rgb16), typeof(GrayAlpha16), typeof(Argb8), typeof(Abgr8), typeof(RgbaF16), typeof(RgbaF32), typeof(Bgr565), typeof(PremulRgba8), typeof(PremulBgra8)];
        return m.MakeGenericMethod(all.Single(t => t.Name == from), all.Single(t => t.Name == to)).Invoke(null, [bits]);
    }

    private static bool HasKernel<TFrom, TTo>(int bits)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        _ = bits;
        return PixelKernels.HasKernel<TFrom, TTo>();
    }

    private static void CheckKernel<TFrom, TTo>(int bits)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TTo : unmanaged, IPixelType<TTo>
    {
        Random random = new(12345);
        foreach (int length in Lengths)
        {
            TFrom[] source = new TFrom[length];
            random.NextBytes(MemoryMarshal.AsBytes(source.AsSpan()));

            // Two guard pixels after the converted range must survive untouched.
            TTo[] expected = new TTo[length + 2];
            TTo[] actual = new TTo[length + 2];
            random.NextBytes(MemoryMarshal.AsBytes(expected.AsSpan()));
            expected.CopyTo(actual, 0);

            PixelConvert.ConvertScalar<TFrom, TTo>(source, expected);
            int done = PixelKernels.Convert<TFrom, TTo>(source, actual, bits);
            Assert.InRange(done, 0, length);
            PixelConvert.ConvertScalar<TFrom, TTo>(source.AsSpan(done), actual.AsSpan(done));

            for (int i = 0; i < expected.Length; i++)
            {
                Assert.True(
                    expected[i].Equals(actual[i]),
                    $"{typeof(TFrom).Name} -> {typeof(TTo).Name} at {bits} bits, length {length}: pixel {i} is {actual[i]}, expected {expected[i]}");
            }
        }
    }
}
