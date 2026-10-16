using Egami.Drawing;
using Egami.Metafiles.Reading;

namespace Egami.Metafiles.Playback;

/// <summary>Chooses the player for a metafile and the records it plays (design question 5).</summary>
internal static class MetafilePlayer
{
    /// <summary>Plays the metafile onto a canvas whose coordinates are the frame's points; depth counts metafiles inside metafiles.</summary>
    public static void Play(Metafile metafile, Canvas canvas, MetafileRenderOptions options, MetafileReport report, int depth = 0)
    {
        if (metafile.Kind is MetafileKind.EmfPlusDual or MetafileKind.EmfPlusOnly && options.Playback == MetafilePlayback.Auto)
        {
            EmfPlusPlayer.Play(metafile, canvas, options, report, depth);
        }
        else
        {
            GdiPlayer.Play(metafile, canvas, options, report, depth);
        }
    }
}
