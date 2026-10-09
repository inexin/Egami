namespace Egami.Samples;

/// <summary>Where a sample reads its images and writes its results.</summary>
internal static class SampleRunner
{
    /// <summary>Gets the bundled images next to the sample: duck.png, egret.png, duck.jpg, sideways.jpg and pan.gif.</summary>
    public static string Images { get; } = Path.Combine(AppContext.BaseDirectory, "images");

    /// <summary>Returns the path of a bundled image.</summary>
    /// <param name="name">The file name, such as "duck.png".</param>
    /// <returns>The full path.</returns>
    public static string Image(string name)
    {
        return Path.Combine(Images, name);
    }

    /// <summary>Runs a sample from the command line: the output folder is the first argument, or ./output.</summary>
    /// <param name="args">The command-line arguments.</param>
    /// <param name="run">The sample, given the output folder.</param>
    public static void Run(string[] args, Action<string> run)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(run);
        string output = Directory.CreateDirectory(args.Length > 0 ? args[0] : "output").FullName;
        run(output);
        Console.WriteLine($"Wrote {Directory.GetFiles(output).Length} files to {output}");
    }
}
