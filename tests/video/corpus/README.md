# Test corpus

Short clips with known content, made by `python tools/video/make-corpus.py` (FFmpeg and MKVToolNix in Docker, see
`tools/video/docker/Dockerfile`). Each `clip.ext.json` is ffprobe's view of the clip, trimmed to what the tests compare: the
streams and every packet (stream, pts, dts, duration, size, position, flags).

| Clip | What it covers |
| --- | --- |
| avi-mjpeg-420-pcm.avi | AVI with idx1, MJPG 4:2:0 frames using the standard Huffman tables, PCM audio |
| avi-mjpeg-422-mp3-ntsc.avi | 30000/1001 fps, 4:2:2, VBR MP3 audio |
| avi-mjpeg-444-noaudio.avi | 4:4:4, video only |
| mkv-mjpeg-pcm.mkv | FFmpeg's Matroska: V_MJPEG, SimpleBlocks, Cues |
| mkv-mjpeg-live.mkv | Unknown-size Segment and Clusters, no Cues (written to a pipe) |
| mkv-mjpeg-vfw.mkv | mkvmerge's V_MS/VFW/FOURCC compatibility mode with a BITMAPINFOHEADER |
| mkv-mp3-nocues.mkv | No Cues, so seeking builds an index; BlockGroups |
| mkv-vorbis-xiph-laced.mkv | Xiph lacing in the audio track |
| mkv-ac3.mkv | AC-3 audio passthrough |
| webm-vp8-opus.webm | WebM DocType, VP8 and Opus packets |
| y4m-420jpeg.y4m, y4m-422.y4m, y4m-mono.y4m | YUV4MPEG2 at 8 bits: 4:2:0, 4:2:2, gray |
| y4m-444p10.y4m, y4m-420p12.y4m | YUV4MPEG2 at 10 and 12 bits |
| y4m-444alpha.y4m | YUV4MPEG2 4:4:4 with alpha |
| avi-raw-yuy2.avi, avi-v210.avi | Uncompressed AVI: YUY2 and 10-bit v210 |
| avi-raw-bgr24.avi | Uncompressed AVI: top-down 24-bit BI_RGB |
| mkv-raw-i420.mkv, mkv-raw-uyvy.mkv | Uncompressed Matroska (V_UNCOMPRESSED with a ColourSpace FourCC) |
| mkv-metadata.mkv | Global and track tags, two chapters, a cover.jpg attachment |
| avi-tags.avi | AVI INFO tags (INAM, IART, ICMT, ICOP, ISFT) |

`python tools/video/make-corpus.py --large` also writes `.cache/video/corpus-large/opendml.avi`, an AVI over 1 GB in the OpenDML layout
(AVIX lists, indx super index), which is not checked in. Tests that need it are skipped when it is missing.

EBML and fixed lacing, frames without a DHT segment and interlaced (AVI1) frames are built by the tests themselves from the
specifications, since the tools above do not write them. No real camera or capture-card files are checked in: none with
clear redistribution terms were available.

## VP8 (V2)

`tests/video/corpus/vp8` is made by `python tools/video/make-vp8-corpus.py` with libvpx 1.14.1 and FFmpeg in Egami's reference image
(`docker build -t egami-vp8ref:2.3.5 tools/vp8ref`). Each clip is vpxenc's IVF output, remuxed by FFmpeg (`-c copy`)
into WebM, and for two clips into Matroska. `<clip>.frames.json` holds vpxdec's MD5 of every shown frame (packed I420 at the
display size) and whether FFmpeg's own VP8 decoder agreed; `<clip>.dump.gz` is vp8dump's reading of every frame (header fields
under RFC 6386 names, then each macroblock); `manifest.json` lists the clips, their vpxenc options and what each covers.

| Clip | What it covers |
| --- | --- |
| v0-altref | Two-pass, auto alternate reference: hidden frames, golden and alternate refresh |
| v0-partitions-8 | Eight token partitions |
| v0-keyframes | A keyframe every 7 frames |
| v0-resilient | Error resilient real-time coding: segmentation, no probability carry-over |
| v1-pan, v2-pan, v3-pan | VP8 versions 1 to 3 (bilinear and whole-pixel prediction, simple loop filter) |
| v0-odd-97x61 | An odd size with hidden frames |
| alpha-ramp, alpha-odd-75x45 | WebM with alpha (AlphaMode 1, alpha frames in BlockAdditional), from FFmpeg's libvpx encoder; `.frames.json` holds the MD5 of each plane (y, u, v, a) as FFmpeg's libvpx decoder writes yuva420p |

The libvpx VP8 test vectors (vp80-00 to vp80-06, 61 files; the rest of libvpx's list is not in the bucket) are fetched into
`.cache/video/vp8/vectors` with their references in `.cache/video/vp8/reference`. Their license is unconfirmed, so they are never committed;
tests that need them are skipped when they are missing. `--large` makes the 720p and 1080p clips and timings of the speed gate.

## VP9 (V3)

`tests/video/corpus/vp9` is made by `python tools/video/make-vp9-corpus.py` with libvpx 1.14.1 (high bit depth build), FFmpeg and
mkvmerge in `tools/video/vp9ref` (`docker build -t egami-vp9ref:v3 tools/video/vp9ref`). Each vpxenc clip is IVF, remuxed by FFmpeg
(`-c copy`) into WebM, and for two clips into Matroska. `<clip>.frames.json` holds vpxdec's MD5 of every shown frame (packed
planes at the coded size, 16-bit little-endian samples above 8 bits) and whether FFmpeg's own VP9 decoder agreed;
`<clip>.dump.gz` is vp9dump's reading of every frame libvpx decoded, hidden ones too (header fields, the probabilities after
adaptation, every block's modes and vectors, the frame's MD5); `manifest.json` lists the clips, their vpxenc options and what
each covers.

| Clip | What it covers |
| --- | --- |
| p0-altref | Two-pass, auto alternate reference: hidden frames in superframes, compound prediction |
| p0-tiles | 1024x256 with 4 tile columns and 4 tile rows |
| p0-lossless | Lossless coding (Walsh-Hadamard transform) |
| p0-resilient | Real-time error resilient and frame parallel coding with cyclic refresh segmentation |
| p0-keyframes | A keyframe every 5 frames, loop filter sharpness 4 |
| p0-odd-97x61 | An odd size with hidden frames |
| p0-aq | Variance adaptive quantization: segmentation with per-segment quantizers |
| p1-444, p1-422 | Profile 1: 4:4:4 and 4:2:2 |
| p1-440 | Profile 1 4:4:0, which Egami.Video reports as unsupported |
| p1-rgb | Profile 1 RGB (color_space 7), from FFmpeg's libvpx-vp9 encoder |
| p2-10bit, p2-12bit | Profile 2: 10 and 12 bit 4:2:0 |
| p3-10bit-422, p3-12bit-444 | Profile 3: 10-bit 4:2:2 and 12-bit 4:4:4 |
| hdr10 | 10-bit BT.2020 PQ in Matroska, with mastering display and light level metadata in the Colour element |
| hdr10plus | The hdr10 frames in WebM with an HDR10+ payload (ST 2094-40) in each block's BlockAdditional 4, made by the script; `.side_data.json` is ffprobe's reading of them |
| alpha-ramp, alpha-odd-75x45 | WebM with alpha (AlphaMode 1, alpha frames in BlockAdditional), from FFmpeg's libvpx-vp9 encoder; `.frames.json` holds the MD5 of each plane (y, u, v, a) as FFmpeg's libvpx decoder writes yuva420p |

The libvpx VP9 test vectors (vp90-2 to vp93-2, 314 files with their `.md5` files, checked against libvpx's
`test/test-data.sha1`) are fetched into `.cache/video/vp9/vectors` with their references in `.cache/video/vp9/reference`. Their license is
unconfirmed, so they are never committed; tests that need them are skipped when they are missing. `--large` makes the 720p,
1080p and 2160p clips and the timings of the speed gate.

## MP4 and QuickTime (V4.1)

`tests/video/corpus/mp4` is made by `python tools/video/make-mp4-corpus.py` with FFmpeg 6.1 and GPAC MP4Box 2.2 in
`tools/video/docker-mp4` (`docker build -t egami-video-mp4:4.1 tools/video/docker-mp4`). Each `<clip>.json` is ffprobe's view of the
clip: the format with its tags and chapters, the streams with their tags, side data (display matrix) and dispositions, and
every packet (stream, pts, dts, duration, size, position, flags, MD5). ffprobe applies edit lists, so packets before an edit
carry the discard flag (D) and start before zero.

| Clip | What it covers |
| --- | --- |
| mov-mjpeg-sowt.mov, mov-mjpeg-twos.mov | QuickTime: Motion JPEG (jpeg) with little- and big-endian 16-bit PCM |
| mov-mjpeg-lpcm24.mov | 24-bit PCM in a version 2 sound description (lpcm) |
| mov-mjpa.mov | Motion JPEG format A (mjpa) |
| mov-tmcd.mov | A tmcd timecode track starting at 01:00:00:00 |
| mov-rotate90.mov, mp4-rotate270-hflip.mp4 | Display matrices: 90 degrees, and 270 degrees mirrored |
| mp4-mjpeg.mp4 | Motion JPEG in ISO MP4 (mp4v with object type 0x6C) |
| mp4box-vp8.mp4 | VP8 (vp08, vpcC), written by MP4Box since FFmpeg cannot |
| mp4-vp9-opus.mp4 | VP9 (vp09, vpcC, colr) and Opus (dOps) |
| mp4-av1.mp4 | AV1 (av01, av1C) |
| mp4-flac.mp4 | FLAC (fLaC, dfLa) |
| mp4-h264-aac.mp4, mp4-h264-faststart.mp4 | Pass-through H.264 with B-frames (ctts, edit list) and AAC; moov last and first |
| mp4-hevc.mp4, m4a-aac.m4a | Pass-through HEVC (hvc1); audio-only AAC |
| mp4-editlist-trim.mp4 | Stream copy from 0.3 s: edits that start inside the media |
| mp4-editlist-delay.mp4 | Audio delayed by 0.2 s: an empty edit |
| mp4-frag.mp4 | Fragmented: empty moov, a moof per keyframe |
| mp4-cmaf.mp4 | CMAF fragments with a global sidx |
| mp4box-frag-mfra.mp4 | MP4Box fragments with an mfra index |
| mp4box-dash.mp4 | MP4Box DASH onDemand: one file of segments with a sidx |
| mp4box-co64.mp4, mp4box-stz2.mp4 | 64-bit chunk offsets (co64); compact sample sizes (stz2) |
| mp4box-chapqt.mp4 | MP4Box chapters: Nero chpl alone (GPAC 2.2 writes no chapter track for -chapqt) |
| mp4-metadata.mp4, mov-metadata.mov | ilst tags (QuickTime udta text boxes in the MOV), Nero chpl and a QuickTime chapter text track (tref chap), cover art (covr) |
| mp4-movtext.mp4 | mov_text (tx3g) subtitles with bold and italic style records |

ipcm, sdtp, stz2 with 4- and 8-bit fields, multiple sample descriptions, HDR boxes (mdcv, clli) and broken files are built
by the tests from the specifications, since the tools above do not write them. No phone or camera files are checked in: none
with clear redistribution terms were available.

## Audio (V5.0)

`tests/video/corpus/audio` is made by `python tools/video/make-audio-corpus.py` with FFmpeg and the flac tool (libFLAC 1.4.2) in
`tools/video/audioref` (`docker build -t egami-audioref:v5.0 tools/video/audioref`), from half a second of sine tones per channel.
`manifest.json` holds, for each clip, the MD5 of its samples as libFLAC's `flac -d` (FLAC files) or FFmpeg (the others) decodes
them: interleaved, little-endian, signed, in whole bytes of the stream's depth, as FLAC's STREAMINFO MD5 is defined.

| Clip | What it covers |
| --- | --- |
| wav-s16-stereo, wav-u8-mono, wav-s32-stereo, wav-f32-stereo | WAV PCM at 16, 8 (unsigned) and 32 bits, and 32-bit float |
| wav-s24-51 | WAVE_FORMAT_EXTENSIBLE: 24-bit 5.1 with a channel mask |
| wav-tags | LIST/INFO tags |
| w64-s16-stereo.w64, rf64-s16-stereo.wav | Sony Wave64, and RF64 with a ds64 chunk |
| flac-l0-stereo, flac-l5-stereo, flac-l8-stereo | flac -0, -5 and -8 |
| flac-24-51, flac-8-mono, flac-32-stereo | 24-bit 5.1, 8-bit and 32-bit FLAC |
| flac-odd-block | A block size of 1000 (coded in the frame header) |
| flac-tags-picture | Vorbis comments and a PNG front cover |
| mka-flac-stereo.mka, mp4-flac-stereo.mp4 | FFmpeg's FLAC in Matroska and in MP4 (dfLa) |

`--ietf` fetches the IETF FLAC test files into `.cache/audio/flac-test-files` and writes libFLAC's MD5 of each into its
`reference.json`; `AudioCorpusTests` decodes every file libFLAC decodes and is skipped when they are missing. After the
`EGAMI_AUDIO_GATES=1` tests have written Egami.Video's own encodings of them, `--gates` runs `flac -t` on those and compares
their size with `flac -0` to `-8`.

## AV1 (V4.2)

`tests/video/corpus/av1` holds our own AV1 clips, encoded by libaom 3.15.2's aomenc (`tools/avifref/av1corpus.py`, through
`python tools/make-av1-references.py clips`), one per inter tool or format. `python tools/video/make-av1-corpus.py` remuxes each
with FFmpeg (`-c copy`) into WebM, and some into Matroska, with ffprobe's view of each (`<file>.json`), and copies libaom's frame
hashes into `<clip>.frames.json`: per output frame its size, bit depth, whether it is monochrome, and the MD5 of its planes as
16-bit little-endian samples, with and without film grain (from `tools/make-av1-references.py`). Four clips are also raw AV1:
`<clip>.obu` written by FFmpeg's obu muxer and `<clip>.annexb.obu` (Annex B) written by the script itself; aomdec and dav1d decode
both to libaom's frames, checked by the script in Docker. `hdr10.ivf` is a 10-bit BT.2020 PQ clip from FFmpeg's libsvtav1 with mastering
display and content light level metadata OBUs, and `hdr10plus.ivf` the same with an HDR10+ ITU-T T.35 metadata OBU in each unit;
their `.side_data.json` is ffprobe's side data per frame, and their `.frames.json` aomdec's frames. libaom's own AV1 test vectors
(including the size-changing Matroska ones) are fetched into `.cache/avif/aom-test-data` and never committed; tests that need them
are skipped when they are missing.
