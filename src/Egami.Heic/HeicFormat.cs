using Egami.Formats;
using Egami.Formats.Heic;
using Egami.Heic.Hevc;

namespace Egami.Heic;

/// <summary>
/// The HEIC format (HEIF with HEVC-coded images, as iPhones and most Android phones save photos), registered with
/// <see cref="HeicContextExtensions.WithHeic"/>. It replaces core Egami's own HEIC entry, which detects and probes HEIC files but
/// does not load them.
/// </summary>
/// <remarks>
/// HEVC is covered by patents, encoding as well as decoding. Egami's authors grant no patent rights and are not lawyers. Whether you need a patent licence
/// depends on your country and on how you use and distribute this package; commercial products may need one from the HEVC patent
/// pools or holders. The core Egami package contains no HEVC code and does not depend on this package.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Heic.cs" region="register"/>
/// </example>
public static class HeicFormat
{
    /// <summary>Gets the format's registration: name, MIME types, extensions, signature, reader and writer.</summary>
    public static FormatInfo Info { get; } = HeicSupport.Create(() => Info!, HeicSupport.PackageNeeded, _ => new HevcCodec(), HeicWriter.Instance);
}
