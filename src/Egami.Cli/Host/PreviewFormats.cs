using Egami.Formats.Extended;

namespace Egami.Cli.Host;

/// <summary>
/// The formats Egami.Formats.Extended has in preview, which only <c>--preview-formats</c> registers (Egami.Formats.Extended
/// design, Q10). A preview's <c>With&lt;Format&gt;()</c> is marked <c>[Experimental]</c> with its own EGAMIX id; the call goes
/// here with that id suppressed. In this release that is DICOM (EGAMIX001).
/// </summary>
internal static class PreviewFormats
{
    /// <summary>Returns <paramref name="context"/> with every format in preview added.</summary>
#pragma warning disable EGAMIX001 // DICOM is in preview; --preview-formats is the user's opt-in.
    public static EgamiContext Register(EgamiContext context) => context.WithDicom();
#pragma warning restore EGAMIX001
}
