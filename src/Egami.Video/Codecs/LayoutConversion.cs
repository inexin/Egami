using Egami.Pixels;

namespace Egami.Video.Codecs;

/// <summary>
/// Brings a frame to a stream's layout, for encoders that fix one layout per stream at the first frame (FFV1, uncompressed):
/// an edit mixes source planes with composed RGBA frames, and every frame must still go out in the first one's layout.
/// </summary>
internal static class LayoutConversion
{
    /// <summary>The frame converted to the chroma format, storage depth, color and alpha given. The caller must dispose it.</summary>
    public static YuvRaster Convert(VideoFrame frame, ChromaFormat format, int depth, YuvColor color, bool alpha)
    {
        Raster? owned = null;
        try
        {
            Raster pixels = frame.Pixels ?? (owned = frame.Planes!.ToRaster());
            return depth > 8
                ? YuvRaster.FromRaster(pixels, format, depth, color, alpha)
                : YuvRaster.FromRaster(pixels, format, color, alpha);
        }
        finally
        {
            owned?.Dispose();
        }
    }
}
