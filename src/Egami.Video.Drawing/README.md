# Egami.Video.Drawing

Egami.Video.Drawing burns subtitles into video for Egami.Video, in managed code, as FFmpeg's `subtitles` filter does.

- `SubtitleRenderer` draws the cues of a `SubtitleDocument` (SRT, WebVTT, ASS, SSA) into a `YuvVideoFrame`'s planes, in its
  own matrix, range and bit depth (8 to 16 bits, 4:2:0, 4:2:2 and 4:4:4, PQ and HLG with white at 203 cd/m²), or onto a
  `Raster<Rgba8>`.
- Text is laid out with Egami.Fonts (shaping, bidi, UAX #14 line breaking, per-character font fallback) as libass lays it
  out: ASS styles, outlines, shadows and boxes, scaling, spacing, the nine alignments, margins, span styles and wrapping.
- Each cue is drawn once and kept while it shows: a frame whose cues have not changed allocates nothing.
- `SubtitleBurnIn` is the same as a video filter, and `AsEffect()` as an effect for a `VideoEdit`.
- Fonts from an app's `FontCollection`, font folders and the system; missing fonts are reported through `MediaContext.Warnings`.

Preview: ASS positioning and animation tags, WebVTT cue settings and regions, collision stacking and attached fonts are not
drawn yet, and are reported as warnings.

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Documentation: [inexin.github.io/Egami](https://inexin.github.io/Egami/). Source and changelog:
[github.com/inexin/Egami](https://github.com/inexin/Egami).
