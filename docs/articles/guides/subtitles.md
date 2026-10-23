# Subtitles

Egami.Video reads and writes text subtitle files in the `Egami.Video.Subtitles` namespace: SubRip (.srt), WebVTT (.vtt),
Advanced SubStation Alpha (.ass) and SubStation Alpha v4 (.ssa, read only). It is fully managed, with no rendering: cues are
text with styling and placement, ready to edit, retime, convert or show.

- Reading is lenient, as players are. A malformed line is repaired and reported as `Subtitle.Repaired`; a
  `SubtitleReaderOptions` with `Strict` set throws a `CorruptDataException` instead.
- WebVTT is parsed exactly as the W3C spec says, and passes the web-platform-tests parsing suite.
- An unchanged file saved in its own format gives back the same bytes. Edited cues are written in a canonical form; the rest
  of the file stays as it was.
- Converting reports what the target format cannot hold as `Subtitle.Dropped`, once per kind of feature.

## Loading and saving

`SubtitleDocument.Load` reads a file, stream or bytes, and `Parse` reads a string. The format is found from the content
(`DetectFormat` tells it alone); `SubtitleReaderOptions.Format` forces one. `Save` writes a file or stream, and `ToText` a
string: the `SubtitleFormat` comes from the argument, then the file's extension, then the document's `Format`.
`SubtitleWriterOptions` sets the byte order mark and the line ending; by default they follow the source file when the
format is the same, and are "\n" for WebVTT and "\r\n" for the others.

[!code-csharp[](../../../tests/Egami.DocSamples/Subtitles.cs#subtitles-load)]

## Cues and spans

A document's `Cues` are `SubtitleCue` objects: a `Start` and an `End` as exact `MediaTime` values, an `Id`, and `Spans` of
text. A `SubtitleSpan` is a run of text with one look: `Bold`, `Italic`, `Underline`, `Strikeout`, a `Color`, a `Font` and
`FontSize`, a WebVTT `Voice`, `Classes`, `Language` and `Ruby`, a karaoke `Timestamp`, ASS tags Egami does not model in
`OtherTags`, and `IsDrawing` for ASS vector drawings. `PlainText` joins the spans' text; line breaks are "\n" in every format.

Placement is the cue's `Alignment`, a `SubtitleAlignment` laid out like a phone keypad (`BottomCenter` is the default, 2),
and an exact `Position` (a `SubtitlePosition` in script pixels, ASS `\pos`). `RawText` keeps the cue's text as the file had
it.

The document retimes all cues, karaoke times included, without rounding: `Shift` adds a time, `Scale` multiplies by a
`Rational` (24000/25025 turns 23.976 fps timing into 25 fps), and `Sync` fixes an offset and a drift together from two
pairs of times. `Clip` keeps the cues inside a range and cuts the ones across its edges, `Sort` orders the cues, and
`FixOverlaps` ends each cue before the next one on the same layer starts. Times are rounded only when written: to
milliseconds for SRT and WebVTT, centiseconds for ASS.

[!code-csharp[](../../../tests/Egami.DocSamples/Subtitles.cs#subtitles-cues)]

## ASS and SSA

ASS documents keep their `ScriptInfo` lines, `Styles` (`SubtitleStyle`: font, colors, outline, shadow, margins and the
rest of a V4+ style line), embedded fonts and graphics as `Attachments` (`SubtitleAttachment`, decoded from the file's
uuencoding), and any other section as written. A cue has the event's `Style`, `Actor`, `Layer`, `IsComment`,
`MarginLeft`, `MarginRight`, `MarginVertical` and `Effect`.

The override tags `\b`, `\i`, `\u`, `\s`, `\c` and `\1c`, `\fn`, `\fs` and `\k` become span fields, and `\an`, `\a` and
`\pos` cue fields. Every other tag, such as `\blur`, `\fad` or `\t`, stays as written in the span's `OtherTags`, so an
animated script loaded, retimed and saved still plays the same. SSA files are read the same way, with their legacy
alignment numbers mapped; they are written as ASS.

[!code-csharp[](../../../tests/Egami.DocSamples/Subtitles.cs#subtitles-ass)]

## WebVTT

A WebVTT cue's settings are in `SubtitleCue.WebVtt`, a `WebVttCueSettings` with the browser `VTTCue`'s meaning: `Vertical`
(`WebVttVertical`), `Line`, `SnapToLines` and `LineAlign` (`WebVttLineAlign`), `Position` and `PositionAlign`
(`WebVttPositionAlign`), `Size`, `Align` (`WebVttAlign`) and `Region`. The document's `Regions` are `SubtitleRegion`
blocks, and its `StyleSheets` the `STYLE` blocks' CSS, kept as text. `HeaderText` is the text after `WEBVTT` on the first
line.

Cue text keeps voices (`<v>`), classes (`<c.name>`), languages (`<lang>`), ruby and karaoke timestamps. Named character
references use the full HTML table.

[!code-csharp[](../../../tests/Egami.DocSamples/Subtitles.cs#subtitles-webvtt)]

## Converting between formats

Any document saves to any writable format. Alignment and positions carry across: a WebVTT cue placed with `line` and
`position` percentages becomes an ASS `\an` and `\pos`, and an ASS alignment becomes WebVTT `line` and `align` settings.
SRT keeps bold, italic, underline, colors and fonts as the common `<b>`, `<i>`, `<u>` and `<font>` tags and alignment as
`{\an8}`. What a format cannot hold is left out and reported once per kind, such as ASS animation tags in SRT, or colors in
WebVTT.

| Feature | SRT | WebVTT | ASS |
| --- | --- | --- | --- |
| Bold, italic, underline | Yes | Yes | Yes |
| Strikeout, colors, fonts | Yes | No | Yes |
| Alignment | `{\an}` tag | Settings | Yes |
| Exact position | No | Percentages | `\pos` |
| Voices, classes, languages, ruby | No | Yes | Actor only |
| Karaoke timing | No | Timestamps | `\k` |
| Styles, layers, margins, effects, other tags | No | No | Yes |

## Encodings and diagnostics

Files are read by their byte order mark (UTF-8, UTF-16 little or big-endian), then as UTF-8 when the bytes are valid UTF-8,
then in `SubtitleReaderOptions.Encoding` when it is set. Without one, WebVTT is UTF-8 (as its spec says), and other files
are read as UTF-16 when they look like it, or else as Windows-1252, with a `Subtitle.Encoding` warning. Output is always
UTF-8. The warnings go to the `Diagnostics` handler of the `MediaContext`'s `Egami` context; see
[Diagnostics](../diagnostics.md).

[!code-csharp[](../../../tests/Egami.DocSamples/Subtitles.cs#subtitles-encoding)]
