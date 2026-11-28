# QuickTime movies

`MovFormat` writes QuickTime movies (Apple's QuickTime File Format), the .mov files that cameras, phones and editing
applications make and read. A .mov or .qt output is a QuickTime movie; reading a .mov is the same as with `Mp4Format`, which
reads both. `MediaContext.Default` lists `MovFormat` ahead of `Mp4Format`, so the extension picks QuickTime, and the name
"MP4" (or a .mp4, .m4v or .m4a output) still writes ISO MP4.

[!code-csharp[](../../../tests/Egami.DocSamples/Video.cs#video-mov)]

## What it holds

| Kind | Codecs | Stored as |
| --- | --- | --- |
| Video | H.264, HEVC | avc1 with avcC, hvc1 with hvcC |
| Video | Motion JPEG | jpeg (Photo JPEG); a track read from QuickTime keeps its type (mjpa, dmb1, AVDJ) |
| Video | Uncompressed 8-bit 4:2:2 | 2vuy, from UYVY, YUY2 or planar 4:2:2 packets |
| Video | Uncompressed 10-bit 4:2:2 | v210, from v210 or planar 10-bit 4:2:2 packets |
| Video | Uncompressed RGB | raw (24-bit, top-down), from raw, AVI's BI_RGB or planar G, B and R |
| Audio | AAC, MP3 | mp4a with the esds in a wave atom |
| Audio | ALAC | alac with its config in a wave atom |
| Audio | PCM | sowt and twos (16-bit), raw (8-bit), in24, in32, fl32 and fl64 with their byte order in a wave atom; lpcm above 65,535 Hz |
| Subtitles | mov_text | tx3g on a subtitle (sbtl) track |
| Any | A track read from MP4 or QuickTime in another codec, such as a timecode | its sample description and samples unchanged |

AV1, VP9, VP8, FFV1, Opus, FLAC, Vorbis, AC-3 and the other codecs throw `NotSupportedException`; its message names MP4 or
Matroska, whichever holds the codec. So does uncompressed video in another layout (4:2:0, say), which Matroska holds.

## What the file looks like

- ftyp with the qt brand, moov, then a wide atom and one mdat. The wide atom becomes the 64-bit size when mdat passes 4 GB.
- Each track has QuickTime's media handler (mhlr) and data handler (dhlr), with counted names, and a data reference to the
  file itself (alis). Tracks that are neither video nor sound have a base media header (gmhd).
- Video descriptions carry colr (nclc; nclx for full range, which nclc cannot say), pasp, fiel for interlaced, Motion JPEG
  and uncompressed tracks, clap for 2vuy and v210, and the HDR boxes. Sound descriptions are version 0, 1 or 2 as QuickTime
  wants for the codec, with the channel layout (chan) when it is known.
- Edit lists, the display matrix for `VideoTrackInfo.Orientation`, tags and cover art as iTunes items, and chapters as Nero
  chpl and a QuickTime chapter text track, as in MP4. Tags with a QuickTime name (title as ©nam, artist, comment, date and
  the like) are also written as QuickTime user data text, in UTF-8.
- moov comes before the media, so the movie plays while it downloads: on a stream that can seek and read, the media is moved
  forward at the end; on any other stream it waits in a temporary file. `new MovFormat { MoovAtEnd = true }` writes moov last,
  straight to a seekable stream. QuickTime has no fragmented layout.

Packets of 2vuy, v210 and raw tracks are written as they come; planar 4:2:2, YUY2 and BGR packets are repacked frame by frame.

## Reading uncompressed video

A 2vuy, v210 or 24-bit raw track reads as uncompressed video (`CodecIds.Uncompressed`) with `VideoTrackInfo.FourCC` "UYVY",
"v210" or "raw ", so `VideoReader` and `RawVideoCodec` decode it to frames: 8-bit or 10-bit 4:2:2 planes, or G, B and R planes
(`YuvMatrix.Identity`) for raw. A MOV to MOV (or MP4) remux writes such a track back with its own sample description and
samples, byte for byte. Matroska has no name for raw RGB, so a raw track goes there as a QuickTime track
(`CodecIds.QuickTimeVideo`). raw with a palette or of 16 or 32 bits still reads as a pass-through track.

Writers report the same warnings as MP4's (`mp4-timestamps`, `metadata-dropped`).
