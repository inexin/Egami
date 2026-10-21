using System.Reflection;

using Egami.Cli.Host;
using Egami.Cli.Steps;
using Egami.Formats;
using Egami.Formats.Png;
using Egami.Operations;

namespace Egami.Cli.Tests;

/// <summary>The steps and writer settings the generator writes from Egami's public API.</summary>
public class CatalogTests
{
    /// <summary>RasterOps methods whose required parameters the command line cannot spell (kernels, masks, callbacks, matrices).</summary>
    private static readonly string[] NotSteps =
    [
        "add", "apply-filter", "color-matrix", "convolve", "copy-alpha", "lighting", "morphology", "on-channels", "remap", "transform", "warp", "within",
    ];

    [Fact]
    public void EveryRasterOpsMethodIsAStepUnlessItsParametersCannotBeSpelled()
    {
        HashSet<string> methods = typeof(RasterOps).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.ReturnType == typeof(RasterOps))
            .Select(m => ValueParsers.Kebab(m.Name))
            .ToHashSet();
        string[] missing = [.. methods.Where(m => StepCatalog.Find(m) is null).Order()];
        Assert.Equal(NotSteps, missing);
    }

    [Fact]
    public void StepsCarryHelpFromTheXmlDocumentation()
    {
        StepInfo blur = StepCatalog.Find("blur")!;
        Assert.NotEmpty(blur.Summary);
        ParamInfo sigma = blur.Overloads[0].Parameters[0];
        Assert.Equal("sigma", sigma.Name);
        Assert.True(sigma.Required);
        Assert.NotEmpty(sigma.Help);
    }

    [Fact]
    public void StepsBindPositionalAndNamedArguments()
    {
        BoundStep blur = StepCatalog.Bind(StepCatalog.Find("blur")!, ["2.5"], [new("color-space", "linear")]);
        Assert.Equal(2.5f, blur.Args[0]);
        Assert.Equal(ColorSpace.Linear, blur.Args[1]);
        Assert.Equal("blur --sigma 2.5 --color-space linear", blur.ToString());

        // Crop has two overloads; one rectangle binds to the Rect one.
        BoundStep crop = StepCatalog.Bind(StepCatalog.Find("crop")!, ["10,20,30,40"], []);
        Assert.Equal(new Rect(10, 20, 30, 40), crop.Args[0]);
        BoundStep crop4 = StepCatalog.Bind(StepCatalog.Find("crop")!, ["1", "2", "3", "4"], []);
        Assert.Equal(4, crop4.Args.Length);
    }

    [Fact]
    public void StepErrorsSayWhatTheStepTakes()
    {
        UsageException missing = Assert.Throws<UsageException>(() => StepCatalog.Bind(StepCatalog.Find("blur")!, [], []));
        Assert.Contains("<sigma>", missing.Message, StringComparison.Ordinal);
        UsageException bad = Assert.Throws<UsageException>(() => StepCatalog.Bind(StepCatalog.Find("blur")!, ["soft"], []));
        Assert.Contains("'soft' is not a number", bad.Message, StringComparison.Ordinal);
        Assert.Throws<UsageException>(() => StepCatalog.Bind(StepCatalog.Find("sepia")!, [], [new("strength", "1")]));
    }

    [Fact]
    public void AutoorientIsAnAlias()
    {
        Assert.Same(StepCatalog.Find("auto-orient"), StepCatalog.Find("autoorient"));
    }

    [Fact]
    public void EveryWritableFormatWithOptionsHasSettings()
    {
        foreach (FormatInfo format in EgamiContext.Default.Formats.Where(f => f.Writer is not null))
        {
            if (format.Name is "CUR")
            {
                continue;
            }

            WriterInfo? writer = WriterCatalog.Find(format);
            Assert.True(writer is not null, $"{format.Name} has no writer settings");
            Assert.Contains(writer.Settings, s => s.Key == "strip-metadata");
        }
    }

    [Fact]
    public void PngSettingsBuildOptions()
    {
        WriterOptions options = WriterCatalog.Build(PngFormat.Info, new Dictionary<string, string>
        {
            ["compression-level"] = "1",
            ["filter"] = "paeth",
            ["palette.max-colors"] = "16",
            ["strip-metadata"] = "yes",
        });
        PngWriterOptions png = Assert.IsType<PngWriterOptions>(options);
        Assert.Equal(1, png.CompressionLevel);
        Assert.Equal(PngFilterMode.Paeth, png.Filter);
        Assert.Equal(16, png.Palette.MaxColors);
        Assert.True(png.StripMetadata);
        Assert.Equal(PngWriterOptions.Default.Interlace, png.Interlace);
    }

    [Fact]
    public void UnknownSettingsAreUsageErrors()
    {
        UsageException error = Assert.Throws<UsageException>(() => WriterCatalog.Build(PngFormat.Info, new Dictionary<string, string> { ["quality"] = "5" }));
        Assert.Contains("egami formats png", error.Message, StringComparison.Ordinal);
    }
}
