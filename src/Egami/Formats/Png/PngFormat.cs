namespace Egami.Formats.Png;

/// <summary>The PNG format, registered in <see cref="EgamiContext.Default"/>.</summary>
public static class PngFormat
{
    /// <summary>Gets the format description.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "PNG",
        MimeTypes = ["image/png"],
        Extensions = ["png"],
        SignatureLength = 8,
        Matches = PngReader.Matches,
        Reader = PngReader.Instance,
        Writer = PngWriter.Instance,
    };
}
