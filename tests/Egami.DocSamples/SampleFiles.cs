namespace Egami.DocSamples;

/// <summary>The bundled public-domain images the samples read, and a fresh folder for what they write.</summary>
public sealed class SampleFiles : IDisposable
{
    public SampleFiles()
    {
        Output = Path.Combine(Path.GetTempPath(), "egami-docs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Output);
        Directory.SetCurrentDirectory(Output);
        foreach (string file in Directory.GetFiles(Images))
        {
            File.Copy(file, Path.Combine(Output, Path.GetFileName(file)));
        }
    }

    /// <summary>Gets the folder with duck.png, egret.png, duck.jpg, sideways.jpg and pan.gif.</summary>
    public static string Images { get; } = Path.Combine(AppContext.BaseDirectory, "images");

    /// <summary>Gets the working folder: a copy of the images, and where samples write. It is the current directory.</summary>
    public string Output { get; }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        try
        {
            Directory.Delete(Output, recursive: true);
        }
        catch (IOException)
        {
            // A file still open on a slow machine; the temp folder is cleaned up later.
        }
    }
}

/// <summary>Samples change the current directory, so they never run in parallel.</summary>
[CollectionDefinition("Samples", DisableParallelization = true)]
public sealed class SampleGroup;
