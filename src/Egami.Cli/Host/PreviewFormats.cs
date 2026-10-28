namespace Egami.Cli.Host;

/// <summary>
/// The formats Egami.Formats.Extended has in preview, which only <c>--preview-formats</c> registers (Egami.Formats.Extended
/// design, Q10). A preview's <c>With&lt;Format&gt;()</c> is marked <c>[Experimental]</c> with its own EGAMIX id; the call goes
/// here with that id suppressed. There are none in this release, so the option changes nothing yet.
/// </summary>
internal static class PreviewFormats
{
    /// <summary>Returns <paramref name="context"/> with every format in preview added.</summary>
    public static EgamiContext Register(EgamiContext context) => context;
}
