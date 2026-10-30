using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video;

/// <summary>Registers Egami.Video.Hevc with a <see cref="MediaContext"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoHevc.cs" region="register"/>
/// </example>
public static class HevcMediaContextExtensions
{
    /// <summary>
    /// Returns a copy of <paramref name="context"/> that decodes HEVC with <see cref="HevcCodec"/> and reads raw .h265 files with
    /// <see cref="HevcFormat"/>. Nothing registers it on its own: an app calls this once, for example
    /// <c>MediaContext.Default.WithHevc()</c>, and so opts in to decoding HEVC. MP4, MOV and Matroska files with HEVC video then
    /// open in <see cref="VideoReader"/> like any other.
    /// </summary>
    /// <remarks>
    /// HEVC (H.265) is covered by patents. Egami's authors grant no patent rights and are not lawyers. Whether you need a patent
    /// licence depends on your country and on how you use and distribute this package; commercial products may need one from the
    /// HEVC patent pools or holders. Egami.Video itself contains no HEVC decoder and does not depend on this package.
    /// </remarks>
    /// <param name="context">The context to add HEVC to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static MediaContext WithHevc(this MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.WithCodec(HevcCodec.Instance).WithContainer(HevcFormat.Instance);
    }
}
