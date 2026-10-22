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
