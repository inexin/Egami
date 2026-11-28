# Burning in subtitles

Egami.Video.Drawing draws subtitles into video frames: the cues of a `SubtitleDocument` (SRT, WebVTT, ASS or SSA, see
[Subtitles](subtitles.md)) become pixels of the picture, as FFmpeg's `subtitles` filter burns them in. It is fully managed:
the text is laid out with Egami.Fonts (shaping, bidi, UAX #14 line breaking, per-character font fallback) and drawn with
Egami.Drawing. The package is in preview with Egami.Video 1.4; this page grows with it.

- `SubtitleRenderer` draws into a `YuvVideoFrame`'s planes, in the frame's own matrix, range and bit depth (8 to 16 bits,
  4:2:0, 4:2:2 and 4:4:4), or onto a `Raster<Rgba8>`. PQ and HLG frames get white at 203 cd/m², as BT.2408 says.
- Each cue is drawn once, when it first shows, and kept while it stays on screen. A frame whose cues have not changed only
  blends them in, and allocates nothing.
- `SubtitleBurnIn` is the same as a video filter (`IVideoFilter`), and `AsEffect` as an effect for a `VideoEdit`.

## What it draws

ASS and SSA styles: the font, size, bold, italic, underline and strikeout, the colors with their alpha, the outline and shadow
(`BorderStyle` 1) or the opaque box (`BorderStyle` 3), `ScaleX` and `ScaleY`, spacing, the nine alignments and the margins,
all scaled from the script's `PlayResX` and `PlayResY` to the frame. Spans inside a cue change bold, italic, underline,
strikeout, color, font and size, and `\N` breaks lines. Lines wrap at the margins as libass wraps them for the script's
`WrapStyle`, and the layout follows libass's: a font size is the height of the font's line, there is no kerning, and a missing
`ScaledBorderAndShadow` means the outline is not scaled.

SRT cues get the look FFmpeg gives them: Arial 16 at a 384×288 script, a thin black outline, at the bottom. WebVTT cues get the
W3C default look: white sans-serif text 5% of the frame's height, on a box of 80% black, centered at the bottom.

Not drawn yet: ASS override tags beyond the span styles (`\pos`, `\move`, `\fad`, `\t`, karaoke, clips, blur, rotation,
drawings), WebVTT cue settings, regions and `::cue` CSS, collision stacking of cues shown together, and fonts attached to the
file. Each is reported once through `MediaContext.Warnings` as `subtitle-not-drawn`.

## Fonts

`SubtitleRenderOptions` says where fonts come from: `Fonts` (a `FontCollection` the app made), then `FontFolders`, then the
system's fonts unless `UseSystemFonts` is off. A family no source has is replaced by the default one with a
`subtitle-font-missing` warning. `Fallback` adds families for characters the style's font lacks; `ForceStyle` changes every
style, as FFmpeg's `force_style` does, and `AuthoredSize` is the size the subtitles were made for when the file does not say.

[!code-csharp[](../../../tests/Egami.DocSamples/SubtitleBurnIn.cs#burnin-options)]

## Frames

[!code-csharp[](../../../tests/Egami.DocSamples/SubtitleBurnIn.cs#burnin-frames)]

On an RGBA raster, at a time on the subtitles' clock:

[!code-csharp[](../../../tests/Egami.DocSamples/SubtitleBurnIn.cs#burnin-raster)]

## Edits and filters

[!code-csharp[](../../../tests/Egami.DocSamples/SubtitleBurnIn.cs#burnin-edit)]

[!code-csharp[](../../../tests/Egami.DocSamples/SubtitleBurnIn.cs#burnin-filter)]

## Matching FFmpeg

The layout is checked against `ffmpeg -vf subtitles` (libass) on a corpus of 60 SRT, WebVTT and ASS files at 720p and 1080p:
the line breaks and the subtitles' boxes, and the pixels inside them. Fonts differ in their details: libass makes bold and
italic up from the regular face when a family has no such face, and Egami.Fonts makes them differently, so give the renderer
the real faces.
