namespace Egami.DocSamples;

/// <summary>Runs every console app in samples/, each into its own folder, and checks it wrote something.</summary>
[Collection("Samples")]
public sealed class SampleApps
{
    public static TheoryData<string, Action<string>> Apps { get; } = new()
    {
        { "Animation", Egami.Samples.Animation.Sample.Run },
        { "Color", Egami.Samples.Color.Sample.Run },
        { "Compositing", Egami.Samples.Compositing.Sample.Run },
        { "Extending", Egami.Samples.Extending.Sample.Run },
        { "Filters", Egami.Samples.Filters.Sample.Run },
        { "Formats", Egami.Samples.Formats.Sample.Run },
        { "Geometry", Egami.Samples.Geometry.Sample.Run },
        { "Jpeg", Egami.Samples.Jpeg.Sample.Run },
        { "MemoryAndThreading", Egami.Samples.MemoryAndThreading.Sample.Run },
        { "Metadata", Egami.Samples.Metadata.Sample.Run },
        { "PixelTypes", Egami.Samples.PixelTypes.Sample.Run },
        { "Security", Egami.Samples.Security.Sample.Run },
    };

    [Theory]
    [MemberData(nameof(Apps))]
    public void Runs(string name, Action<string> run)
    {
        using SampleFiles files = new();
        string output = Directory.CreateDirectory(Path.Combine(files.Output, name)).FullName;
        run(output);
        Assert.NotEmpty(Directory.GetFiles(output));
    }
}
