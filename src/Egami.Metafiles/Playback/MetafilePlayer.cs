using Egami.Drawing;
using Egami.Metafiles.Reading;

namespace Egami.Metafiles.Playback;

/// <summary>Chooses the player for a metafile and the records it plays (design question 5).</summary>
internal static class MetafilePlayer
{
    /// <summary>Plays the metafile onto a canvas whose coordinates are the frame's points.</summary>
    public static void Play(Metafile metafile, Canvas canvas, MetafileRenderOptions options, MetafileReport report)
    {
        // The GDI and EMF+ players come in stages B and C.
        _ = metafile;
        _ = canvas;
        _ = options;
        _ = report;
    }
}
