using System.Buffers;

using Egami.Tests.Memory;

namespace Egami.Tests;

public class ContextTests
{
    [Fact]
    public void DefaultsMatchTheDesign()
    {
        SafetyLimits limits = SafetyLimits.Default;
        Assert.Equal(65_535, limits.MaxWidth);
        Assert.Equal(65_535, limits.MaxHeight);
        Assert.Equal(268_435_456, limits.MaxPixels);
        Assert.Equal(10_000, limits.MaxFrames);
        Assert.Equal(2L * 1024 * 1024 * 1024, limits.MaxTotalBytes);
        Assert.Equal(16 * 1024 * 1024, limits.MaxMetadataBytes);

        EgamiContext context = EgamiContext.Default;
        Assert.Same(PooledPixelMemorySource.Shared, context.Memory);
        Assert.Equal(Environment.ProcessorCount, context.MaxParallelism);
        Assert.Same(SafetyLimits.Default, context.Limits);
    }

    [Fact]
    public void WithCreatesCopiesAndLeavesDefaultAlone()
    {
        EgamiContext custom = EgamiContext.Default with
        {
            Limits = SafetyLimits.Default with { MaxPixels = 100_000_000 },
            MaxParallelism = 4,
        };

        Assert.Equal(100_000_000, custom.Limits.MaxPixels);
        Assert.Equal(4, custom.MaxParallelism);
        Assert.Equal(268_435_456, EgamiContext.Default.Limits.MaxPixels);
        Assert.Equal(Environment.ProcessorCount, EgamiContext.Default.MaxParallelism);
    }

    [Fact]
    public void InvalidSettingsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EgamiContext.Default with { MaxParallelism = 0 });
        Assert.Throws<ArgumentNullException>(() => EgamiContext.Default with { Memory = null! });
        Assert.Throws<ArgumentNullException>(() => EgamiContext.Default with { Limits = null! });
        Assert.Throws<ArgumentOutOfRangeException>(() => SafetyLimits.Default with { MaxWidth = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => SafetyLimits.Default with { MaxTotalBytes = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PooledPixelMemorySource { MaxBlockBytes = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new PooledPixelMemorySource { PoolThresholdBytes = -1 });
    }

    [Theory]
    [InlineData(65_536, 1, "MaxWidth", 65_535)]
    [InlineData(1, 65_536, "MaxHeight", 65_535)]
    [InlineData(20_000, 20_000, "MaxPixels", 268_435_456)]
    public void FrameSizeChecksNameTheLimit(long width, long height, string limit, long allowed)
    {
        LimitExceededException e = Assert.Throws<LimitExceededException>(() => SafetyLimits.Default.CheckFrameSize(width, height));
        Assert.Equal(limit, e.LimitName);
        Assert.Equal(allowed, e.Allowed);
        Assert.Contains(limit, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FrameAndMetadataChecks()
    {
        SafetyLimits.Default.CheckFrameSize(16_384, 16_384);
        SafetyLimits.Default.CheckFrameCount(10_000);
        Assert.Equal(10_001, Assert.Throws<LimitExceededException>(() => SafetyLimits.Default.CheckFrameCount(10_001)).Found);
        SafetyLimits.Default.CheckMetadataBytes(16 * 1024 * 1024);
        Assert.Equal("MaxMetadataBytes", Assert.Throws<LimitExceededException>(() => SafetyLimits.Default.CheckMetadataBytes((16 * 1024 * 1024) + 1)).LimitName);
    }

    [Fact]
    public void BudgetCountsLiveBytesOnly()
    {
        BudgetedMemorySource budget = new(PooledPixelMemorySource.Shared, maxTotalBytes: 1000);
        IMemoryOwner<byte> a = budget.Allocate(600, ClearMode.None);
        LimitExceededException e = Assert.Throws<LimitExceededException>(() => budget.Allocate(401, ClearMode.None));
        Assert.Equal("MaxTotalBytes", e.LimitName);
        Assert.Equal(1001, e.Found);
        Assert.Equal(600, budget.LiveBytes);

        a.Dispose();
        a.Dispose();
        Assert.Equal(0, budget.LiveBytes);
        using IMemoryOwner<byte> b = budget.Allocate(1000, ClearMode.None);
        Assert.Throws<LimitExceededException>(() => budget.Check(1));
    }

    [Fact]
    public void BudgetAppliesToWholePlanes()
    {
        BudgetedMemorySource budget = new(PooledPixelMemorySource.Shared, maxTotalBytes: 4 * 100 * 100);
        using (new PlaneScope<Egami.Pixels.Rgba8>(PixelPlane.Allocate<Egami.Pixels.Rgba8>(100, 100, budget)))
        {
            Assert.Throws<LimitExceededException>(() => PixelPlane.Allocate<Egami.Pixels.Gray8>(1, 1, budget));
        }

        Assert.Equal(0, budget.LiveBytes);
    }

    [Fact]
    public void CorruptDataExceptionNamesFormatAndOffset()
    {
        CorruptDataException e = new("PNG", 1234, "The IHDR chunk is 12 bytes long; it must be 13.");
        Assert.Equal("Corrupt PNG data at byte 1234: The IHDR chunk is 12 bytes long; it must be 13.", e.Message);
        Assert.Equal("PNG", e.FormatName);
        Assert.Equal(1234, e.Offset);
        Assert.Equal("Corrupt PNG data: Truncated.", new CorruptDataException("PNG", -1, "Truncated.").Message);
        Assert.IsAssignableFrom<EgamiException>(new UnsupportedFormatException("x"));
    }
}
