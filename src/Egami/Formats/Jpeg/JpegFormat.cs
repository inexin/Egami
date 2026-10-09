namespace Egami.Formats.Jpeg;

/// <summary>The JPEG format (JFIF and EXIF files), registered in <see cref="EgamiContext.Default"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="jpeg-write"/>
/// </example>
public static class JpegFormat
{
    /// <summary>Gets the format's registration: name, MIME type, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = new()
    {
        Name = "JPEG",
        MimeTypes = ["image/jpeg"],
        Extensions = ["jpg", "jpeg", "jpe", "jfif"],
        SignatureLength = 3,
        Matches = JpegReader.Matches,
        Reader = JpegReader.Instance,
        Writer = JpegWriter.Instance,
    };
}
