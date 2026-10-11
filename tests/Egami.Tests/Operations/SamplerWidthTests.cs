using System.Numerics;
using System.Security.Cryptography;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

/// <summary>
/// The footprint sampler's pixels do not depend on the vector width: the hashes below hold with hardware intrinsics on, with
/// AVX-512 off (DOTNET_EnableAVX512F=0) and with every intrinsic off (DOTNET_EnableHWIntrinsic=0), which the pre-push checks run.
/// A change to the sampler's arithmetic changes them on purpose; recompute them then, in all three modes.
/// </summary>
public class SamplerWidthTests
{
    private static readonly Dictionary<string, string> Expected = new()
    {
        ["rotate"] = "A5F0CD939ACC653A",
        ["shrink"] = "F405863C30C75EB3",
        ["quad"] = "2867544B177C896C",
        ["swirl"] = "708A1A26E30B0810",
        ["bgra-skew"] = "9677BC35CAD7CA5A",
    };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Cases() => [.. Expected.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public void PixelsAreTheSameAtEveryVectorWidth(string name)
    {
        using Raster<Rgba8> source = OperationCoverageTests.Noise(131, 97, EgamiContext.Default);
        using Raster<Bgra8> bgra = source.As<Bgra8>();
        using Raster result = name switch
        {
            "rotate" => source.Derive(ops => ops.Rotate(17), Ct),
            "shrink" => source.Derive(ops => ops.Transform(Matrix3x2.CreateRotation(0.3f) * Matrix3x2.CreateScale(0.4f)), Ct),
            "quad" => source.Derive(ops => ops.QuadDistort(new(30, 0), new(100, 10), new(131, 97), new(0, 90)), Ct),
            "swirl" => source.Derive(ops => ops.Warp(Warps.Swirl(120)), Ct),
            _ => bgra.Derive(ops => ops.Skew(10, 5, new TransformOptions { Filter = ResampleFilter.Lanczos3 }), Ct),
        };
        Assert.Equal(Expected[name], Hash(result));
    }

    private static string Hash(Raster raster)
    {
        using Raster<Rgba8> rgba = raster.As<Rgba8>();
        Rgba8[] pixels = new Rgba8[rgba.Width * rgba.Height];
        rgba.CopyPixelsTo(pixels);
        return Convert.ToHexString(SHA256.HashData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan())))[..16];
    }
}
