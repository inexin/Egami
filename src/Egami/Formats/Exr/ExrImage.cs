using Egami.Pixels;

namespace Egami.Formats.Exr;

/// <summary>Reads single OpenEXR channels that are not color, such as depth, object IDs or render passes.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
/// </example>
public static class ExrImage
{
    /// <summary>
    /// Reads one channel, such as "Z" or "diffuse.R", into a gray raster at the channel's stored resolution (smaller than the
    /// data window when the channel is subsampled). Half and uint samples convert to float; uint values above 2^24 lose
    /// their lowest bits.
    /// </summary>
    /// <param name="path">The file.</param>
    /// <param name="channel">The channel's full name.</param>
    /// <param name="options">Which part to read, and the general options; the layer and window are ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The channel's samples.</returns>
    /// <exception cref="ArgumentException">The part has no such channel.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
    /// </example>
    public static Raster<GrayF32> ReadChannel(string path, string channel, ExrReaderOptions? options = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        using FileStream stream = File.OpenRead(path);
        return ReadChannel(stream, channel, options, context);
    }

    /// <summary>Reads one channel from a stream; see <see cref="ReadChannel(string, string, ExrReaderOptions?, EgamiContext?)"/>.</summary>
    /// <param name="stream">The file's data, from its start.</param>
    /// <param name="channel">The channel's full name.</param>
    /// <param name="options">Which part to read, and the general options; the layer and window are ignored.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="EgamiContext.Default"/>.</param>
    /// <returns>The channel's samples.</returns>
    /// <exception cref="ArgumentException">The part has no such channel.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Hdr.cs" region="exr"/>
    /// </example>
    public static Raster<GrayF32> ReadChannel(Stream stream, string channel, ExrReaderOptions? options = null, EgamiContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ExrReaderOptions read = (options ?? ExrReaderOptions.Default) with { Channel = channel, Window = ExrWindow.Data, Format = ExrFormat.Info };
        return Raster.Load<GrayF32>(stream, context, read);
    }
}
