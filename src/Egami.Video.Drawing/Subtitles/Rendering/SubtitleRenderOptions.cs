using Egami.Fonts;

namespace Egami.Video.Subtitles.Rendering;

/// <summary>
/// Where <see cref="SubtitleRenderer"/> finds fonts, what it falls back to, and how it changes the file's styles. The defaults
/// look fonts up in the system's fonts and draw the styles as the file gives them.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/SubtitleBurnIn.cs" region="burnin-options"/>
/// </example>
public sealed record SubtitleRenderOptions
{
    /// <summary>
    /// Gets the app's own fonts, searched by family name before <see cref="FontFolders"/> and the system's fonts, or
    /// <see langword="null"/> for none.
    /// </summary>
    public FontCollection? Fonts { get; init; }

    /// <summary>
    /// Gets folders of font files (TrueType, OpenType, WOFF and collections), searched by family name after <see cref="Fonts"/>
    /// and before the system's fonts, as FFmpeg's <c>fontsdir</c> is. Subfolders are searched too.
    /// </summary>
    public IReadOnlyList<string> FontFolders { get; init; } = [];

    /// <summary>
    /// Gets whether the system's installed fonts are searched, last, and used for characters no named font has. Defaults to
    /// <see langword="true"/>. Turn it off on servers to draw only with the fonts given.
    /// </summary>
    public bool UseSystemFonts { get; init; } = true;

    /// <summary>
    /// Gets fonts to use when a style's font is found nowhere and for characters its font lacks, or <see langword="null"/>
    /// for none (the default). Egami.Fonts.Substitutes' collection makes files written for Arial or Times New Roman draw
    /// at the same widths where those fonts are not installed.
    /// </summary>
    public FontCollection? Fallback { get; init; }

    /// <summary>
    /// Gets a change made to every style before it is drawn, as FFmpeg's <c>force_style</c> is, or <see langword="null"/>. It
    /// gets the styles of ASS and SSA files and the built-in styles of SRT and WebVTT files.
    /// </summary>
    public Func<SubtitleStyle, SubtitleStyle>? ForceStyle { get; init; }

    /// <summary>
    /// Gets the frame size the subtitles were written for, used when the file does not say (an ASS file's
    /// <c>LayoutResX</c> and <c>LayoutResY</c>), or <see langword="null"/>. When frames have another shape, letters keep the
    /// proportions they had at this size, as FFmpeg's <c>original_size</c> does; without it they are never stretched.
    /// </summary>
    public Size? AuthoredSize { get; init; }
}
