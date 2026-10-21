using Egami.Cli.Host;
using Egami.Colors;

namespace Egami.Cli.Tests;

/// <summary>The command review's answers (2026-10-21, all as recommended).</summary>
public sealed class ReviewTests : IDisposable
{
    private const string Duck = "jpeg/duck-420.jpg";
    private const string Png = "pngsuite/basn6a08.png";

    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    [Fact]
    public void FlipNeedsADirection()
    {
        string png = _tool.Copy(Png);
        ToolRun run = _tool.Run("flip", png, "-o", "x.png");
        Assert.Equal(ExitCodes.Usage, run.Code);
        Assert.Contains("horizontal|vertical|both", run.Error, StringComparison.Ordinal);
        Assert.Equal(0, _tool.Run("flip", png, "both", "-o", "y.png").Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("run", png, "flip", "-o", "z.png").Code);
    }

    [Fact]
    public void TextTakesFontSize()
    {
        string duck = _tool.Copy(Duck);
        Assert.Equal(0, _tool.Run("text", duck, "hello", "-o", "t.png", "--font-size", "30").Code);
        Assert.NotEqual(0, _tool.Run("text", duck, "hello", "-o", "u.png", "--size", "30").Code);
    }

    [Fact]
    public void AutoOrientIsTheCommandsName()
    {
        string duck = _tool.Copy(Duck);
        Assert.Equal(0, _tool.Run("auto-orient", duck, "-o", "a.jpg").Code);
        Assert.Equal(0, _tool.Run("autoorient", duck, "-o", "b.jpg").Code);
    }

    [Fact]
    public void StripKeepsTheColorProfile()
    {
        string duck = _tool.Copy(Duck);
        Assert.Equal(0, _tool.Run("color", duck, "-o", "p3.png", "--to", "display-p3").Code);
        Assert.Equal(0, _tool.Run("convert", "p3.png", "-o", "stripped.png", "--strip").Code);
        using Raster stripped = Raster.Load(_tool.PathOf("stripped.png"));
        Assert.False(stripped.Metadata.IccProfile.IsEmpty);
        Assert.Contains("P3", ColorProfile.FromIcc(stripped.Metadata.IccProfile.Span).Name, StringComparison.Ordinal);
        Assert.Empty(stripped.Metadata.Text);
    }

    [Fact]
    public void MetadataEditsRefuseLossyOutputsUnlessAsked()
    {
        string jpeg = _tool.Copy("metadata/synthetic/iptc-latin1.jpg", "tagged.jpg");
        ToolRun refused = _tool.Run("metadata", "strip", jpeg, "-o", "kept.jpg", "--keep", "icc");
        Assert.Equal(ExitCodes.Usage, refused.Code);
        Assert.Contains("--reencode", refused.Error, StringComparison.Ordinal);
        Assert.Equal(0, _tool.Run("metadata", "strip", jpeg, "-o", "kept.jpg", "--keep", "icc", "--reencode").Code);
        Assert.Equal(0, _tool.Run("metadata", "strip", jpeg, "-o", "kept.png", "--keep", "icc").Code);
        Assert.Equal(0, _tool.Run("metadata", "strip", jpeg, "-o", "lossless.jpg").Code);
        Assert.Equal(ExitCodes.Usage, _tool.Run("metadata", "copy", "kept.png", "--from", jpeg, "-o", "back.webp").Code);
        Assert.Equal(0, _tool.Run("metadata", "copy", "kept.png", "--from", jpeg, "-o", "back.webp", "--lossless").Code);
    }

    [Fact]
    public void FormatsNamesTheHeicTool()
    {
        ToolRun run = _tool.Run("formats");
        Assert.Contains("(needs egami-heic)", run.Text, StringComparison.Ordinal);
    }
}
