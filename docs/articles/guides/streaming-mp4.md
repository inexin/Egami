# Streaming MP4

Flat MP4 needs its index (moov) before or after all of the media, so a writer must either seek back or hold the media
somewhere until the end. Fragmented MP4 puts only the track setup in moov and the media after it in short moof and mdat
pairs, each with its own small index, so it can be written as it is made: to a cloud blob's upload stream, an HTTP response or
a pipe, with no temporary file. Every current player and browser reads it, and it is what HLS and DASH serve.

## Fragmented files

Set `Mp4Format.Fragments` to an `Mp4FragmentOptions` to write fragmented MP4 on any output. A fragment starts at the first
video keyframe at least `Duration` (2 seconds by default) after the one before, so each fragment can be played or seeked to
on its own; files without video cut every `Duration`. `MaxBytes` (32 MB by default) closes a fragment early, between
keyframes if need be: the writer holds one fragment in memory, so this caps its memory whatever the stream's keyframe interval.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-fragments)]

What a fragmented file holds:

- ftyp (iso6 and mp41, and cmfc for a single track, which is CMAF), then moov with each track's sample description and an
  mvex box. moov waits for the first fragment, whose samples set the edit lists for start offsets and codec delays.
- A moof and mdat pair a fragment: a traf a track with its decode time (tfdt) and one trun of sizes, durations, keyframe flags
  and composition offsets, then the samples, a track after the other.
- mfra at the end, listing each fragment's first keyframe, so a player can seek without reading every moof.
- On a seekable output, the durations (mvhd, tkhd, mehd and the edit lists') are written back at the end; a forward-only
  output leaves them to the fragments, which players add up.
- Tags and cover art as in flat MP4, and chapters as Nero chpl only: the QuickTime chapter track needs its samples in moov,
  so it is left out with an mp4-chapter-track warning.

`Mp4Format.MoovAtEnd` and `Fragments` cannot both be set.

## Forward-only streams

When the output cannot seek and read back and `Fragments` is not set, `Mp4Format` writes fragmented MP4 with the default
options and an mp4-fragmented warning, so `MediaFile.Remux` and `MediaFile.Transcode` with a `Stream` output need no
temporary file. `new Mp4Format { NonSeekable = Mp4NonSeekableLayout.TempFile }` keeps flat MP4 there instead: the media waits
in a temporary file and moov goes first. Register either format with `MediaContext.WithContainer`.

## HLS and DASH segments

`Mp4SegmentWriter` cuts the same fragments into files a web server or CDN serves as they are: for each track an init segment
(`video1-init.mp4`) and numbered media segments (`video1-00001.m4s`, each styp, sidx, moof and mdat), as CMAF asks, plus the
playlists `Mp4SegmentOptions.Playlists` names. A segment starts at the first video keyframe after
`Mp4SegmentOptions.SegmentDuration` (6 seconds by default), and the audio segments are cut at the same times.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-segments)]

- `SegmentPlaylists.Hls` writes master.m3u8, an HLS multivariant playlist (RFC 8216, version 7) with each video track as a
  variant and the audio tracks as its renditions, and a media playlist a track (video1.m3u8) with EXT-X-MAP for its init
  segment. Bandwidths are measured from the segments; CODECS come from the tracks (avc1, hvc1, av01, vp09, mp4a, opus, fLaC).
- `SegmentPlaylists.Dash` writes manifest.mpd, a static MPD in the ISO live profile with an adaptation set a track and a
  SegmentTemplate with a SegmentTimeline.

The writer takes a folder, or a `Func<string, Stream>` that opens each file by name, for blob storage: it writes the whole
file and disposes the stream. Segments hold audio and video tracks only. One rendition is written; for a bitrate ladder, run a
transcode a rung. Live playlists, low-latency chunks and encryption are not written.
