# Egami.Video.Drawing: guide for coding agents

Egami.Video.Drawing burns subtitles into video: it draws the cues of an Egami.Video `SubtitleDocument` (SRT, WebVTT, ASS, SSA)
into `YuvVideoFrame` planes in the frame's own matrix and bit depth, HDR included, or onto a `Raster<Rgba8>`, laid out with
Egami.Fonts as libass lays them out. It is in preview: positioning tags, WebVTT cue settings and the rest of the ASS tags come
later, and are reported as warnings meanwhile.

Namespaces: `Egami.Video.Subtitles.Rendering` (`SubtitleRenderer`, `SubtitleRenderOptions`, `SubtitleBurnIn`), with
`Egami.Video`, `Egami.Video.Subtitles` and `Egami.Pixels`.

{{shared}}

## Entry points

- `new SubtitleRenderer(document, options, context)`: draws with `Draw(YuvVideoFrame)` (at the frame's time) or
  `Draw(Raster<Rgba8>, MediaTime)`. Dispose it. Not thread safe.
- `SubtitleRenderOptions`: `Fonts`, `FontFolders`, `UseSystemFonts` (true), `Fallback`, `ForceStyle`, `AuthoredSize`.
- `new SubtitleBurnIn(...)`: the same as an `IVideoFilter`; it copies the frames sent to it.
- `renderer.AsEffect()` with `EditEffect.Of`: burn subtitles into a `VideoEdit`.

## Samples

Options and fonts:

{{sample SubtitleBurnIn.cs#burnin-options}}

Into a file's frames:

{{sample SubtitleBurnIn.cs#burnin-frames}}

Onto an RGBA raster:

{{sample SubtitleBurnIn.cs#burnin-raster}}

In an edit:

{{sample SubtitleBurnIn.cs#burnin-edit}}

As a filter:

{{sample SubtitleBurnIn.cs#burnin-filter}}

## Common mistakes

- Relying on system fonts on a server. Containers often have none: pass `FontFolders` or `Fonts`, and watch
  `MediaContext.Warnings` for `subtitle-font-missing`.
- Making a renderer per frame. Cues are drawn once and kept; a new renderer draws them again each time.
- Sharing one renderer between threads. Use one per thread, or lock (the edit effect locks for you).
- Expecting `\pos`, `\move`, `\fad`, karaoke or WebVTT positions to apply. They are not drawn yet; each warns once as
  `subtitle-not-drawn`.
- Disposing the frame `Draw` drew into before writing it. `Draw` changes the frame in place.

## More

- Burning in subtitles: https://inexin.github.io/Egami/v1/articles/guides/subtitle-burn-in.html
- Subtitles: https://inexin.github.io/Egami/v1/articles/guides/subtitles.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
