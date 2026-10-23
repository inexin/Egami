using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video;

/// <summary>Registers Egami.Video.H264 with a <see cref="MediaContext"/>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoH264.cs" region="register"/>
/// </example>
public static class H264MediaContextExtensions
{
    /// <summary>
    /// Returns a copy of <paramref name="context"/> that decodes H.264 with <see cref="H264Codec"/> and reads raw .h264 files with
    /// <see cref="H264Format"/>. Nothing registers it on its own: an app calls this once, for example
    /// <c>MediaContext.Default.WithH264()</c>, and so opts in to decoding H.264. MP4, MOV, Matroska and AVI files with H.264 video
    /// then open in <see cref="VideoReader"/> like any other.
    /// </summary>
    /// <remarks>
    /// H.264 (AVC) is covered by patents. Egami's authors grant no patent rights and are not lawyers. Whether you need a patent
    /// licence depends on your country and on how you use and distribute this package; commercial products may need one from the
    /// AVC patent pools or holders. Egami.Video itself contains no H.264 decoder and does not depend on this package.
    /// </remarks>
    /// <param name="context">The context to add H.264 to.</param>
    /// <returns>The new context.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static MediaContext WithH264(this MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.WithCodec(H264Codec.Instance).WithContainer(H264Format.Instance);
    }
}
