# Encoding video

Egami.Video encodes VP8 and VP9 into WebM, Matroska and IVF (VP9 into MP4 too), with alpha in WebM and Matroska, and AV1 into
WebM, Matroska, MP4, IVF, raw OBU and Annex B files. VP8's frame coding is Egami's `Vp8StreamEncoder`, VP9's Egami.Video's own
and AV1's Egami's `Av1StreamEncoder`; for all three, Egami.Video decides how each frame is coded: where keyframes go, which
frames become references, and how many bits each frame gets. Motion JPEG and uncompressed video encode too, without these
settings: every frame is a keyframe.

## Writing VP8

Set `VideoWriterOptions.Codec` to `VideoCodecs.Vp8`. `Quality` runs from 0 to 100 (default 60, the scale of Egami's AVIF
writer) and `Effort` from 0 (fastest) to 10 (smallest files, default 4). Frames given as 8-bit 4:2:0 planes are coded as they
are; anything else is converted to BT.601 limited range first. Frames with alpha get a second VP8 stream for the alpha plane,
as WebM carries it.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoEncode.cs#video-encode-vp8)]

`Vp8EncoderOptions` holds the expert settings: quantizer limits, alternate reference frames on or off, golden frame spacing,
the temporal filter's frames and strength, token partitions, error resilience and alpha.

## Writing VP9

Set `VideoWriterOptions.Codec` to `VideoCodecs.Vp9`. `Quality` and `Effort` work as for VP8 (default 60 and 4); `Quantizer`
runs from 0 to 255, and 0 is lossless. The encoder searches each 64x64 superblock's partition down to 4x4, its intra modes,
motion vectors to 1/8 pixel with three interpolation filters, compound prediction from two references and its transform sizes
by rate and distortion, quantizes with a trellis, and picks the loop filter level per frame. Hidden alternate references go
out in a superframe with the next shown frame, so every packet shows one frame.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoEncode.cs#video-encode-vp9)]

`Vp9EncoderOptions` holds the expert settings: quantizer limits, alternate reference frames on or off, golden frame spacing,
the temporal filter's frames and strength, tile columns, adaptive quantization by block detail (libvpx's aq-mode 1), error
resilience, the bit depth and alpha. Frames are split into up to four tile columns by default (each at least 256 pixels
wide), which the encoder searches on separate threads, superblock rows running as a wavefront within them.

### Bit depths, chroma and HDR

8-bit 4:2:0 planes are coded as they are (profile 0); 4:2:2 and 4:4:4 planes as profile 1; 10 and 12-bit planes as profile 2
(4:2:0) or 3. `Vp9EncoderOptions.BitDepth` codes another depth, and `VideoEncoderOptions.ChromaFormat` sets the chroma format
for RGB frames. The track's color comes from the frames: their code points (primaries, transfer, matrix, range), and their
static HDR metadata (mastering display, content light levels), which WebM and Matroska keep in the Colour element and MP4 in its
`colr`, `mdcv` and `clli` boxes. Frames with alpha get a second, 8-bit 4:2:0 VP9 stream for the alpha plane. Alternate
references of other formats than 8-bit 4:2:0 are their frame unfiltered.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoEncode.cs#video-encode-vp9-hdr)]

## Writing AV1

Set `VideoWriterOptions.Codec` to `VideoCodecs.Av1`. `Quality` and `Effort` work as for VP8 and VP9 (default 60 and 4), and
equal qualities give the same quantizer step as VP9's; `Quantizer` is AV1's base_q_idx, 0 to 255, and 0 is lossless. Each
packet is a temporal unit of OBUs, as MP4 and Matroska store them, and the track's `av1C` comes from the sequence header.

The frame coding is `Av1StreamEncoder`'s: partitions, intra and inter modes from up to seven references with compound
prediction, transform types and sizes chosen by rate and distortion, a trellis, delta quantizers per superblock by block detail,
and deblocking levels and CDEF strengths per frame; from effort 5, 6 and 7 on it adds compound masks, overlapped and warped
motion, dual filters and global motion. Egami.Video plans the references: each group's last frame, blended with its neighbours along
their motion, is coded first as a hidden alternate reference, then a hidden frame halfway to it (a two-level pyramid, AV1's
ALTREF2). Neither costs a frame of its own when its time comes: AV1 shows it again with `show_existing_frame`, a packet of a
few bytes. The frames in between read their three nearest past frames, the golden frame and the hidden frames ahead.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoEncode.cs#video-encode-av1)]

`Av1EncoderOptions` holds the expert settings: quantizer limits, alternate references and the pyramid on or off, golden frame
spacing, the temporal filter's frames and strength, adaptive quantization, screen content tools, error resilience and the bit
depth. AV1 codes no alpha in video files (Matroska and MP4 have no standard place for it); animated AVIF, through Egami's
`AvifWriter`, does.

### Profiles and HDR

8 and 10-bit 4:2:0 planes make Main profile streams, 4:4:4 High and 12-bit or 4:2:2 Professional, all coded as they are;
`Av1EncoderOptions.BitDepth` codes another depth and `VideoEncoderOptions.ChromaFormat` sets the chroma format for RGB frames,
which are converted to BT.601 limited range. The frames' code points (primaries, transfer, matrix, range) and the chroma
siting of 4:2:0 planes go into the sequence header and the container. Their static HDR metadata goes into content light level
and mastering display metadata OBUs on every keyframe, and into the container as for VP9; a frame's HDR10+ metadata goes into
its own temporal unit as an ITU-T T.35 metadata OBU. The level is the smallest whose picture size and sample rate hold the
stream.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoEncode.cs#video-encode-av1-hdr)]

## How bits are spent

`RateControl` picks one of four modes:

| Mode | What it does | For |
| --- | --- | --- |
| `Quality` (default) | Every frame aims at `Quality`; files are as large as the content needs. | Files |
| `Bitrate` | An average `Bitrate`, spread over frames by how hard they are; capped by `MaxBitrate`. | Files of a known size, streaming |
| `ConstantBitrate` | `Bitrate` with a decoder buffer of `BufferSize` bits (one second by default) that never runs dry. | Live streams, WebRTC |
| `Quantizer` | The same quantizer for every frame (`Quantizer`, or the one `Quality` maps to). | Tests, comparisons |

[!code-csharp[](../../../tests/Egami.DocSamples/VideoEncode.cs#video-encode-bitrate)]

### Two passes

A two-pass encode first reads the whole clip to learn how hard each frame is, then spreads the bitrate over all of it.
`VideoWriter.AnalyzeFirstPass` runs the first pass over frames or a file and returns `FirstPassStats`, which can be saved and
loaded; pass it as `FirstPassStats` with `RateControlMode.Bitrate`.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoEncode.cs#video-encode-two-pass)]

## Keyframes, groups and latency

The encoder looks 25 frames ahead (`LatencyMode.Quality`, the default). It analyzes each frame on a half-size copy as it
arrives and starts a keyframe at a scene cut, at least every 10 seconds (`KeyframeInterval` sets another spacing, in frames),
and wherever `EncodeFrameOptions.ForceKeyframe` asks. Between keyframes it codes groups of 4 to 16 frames: the group's last
frame is blended with its neighbours along their motion into a hidden alternate reference, coded first at a finer step, so
the frames before it can predict from it. That happens only where the group's first frame still predicts its last well
(still content); with motion, zooms or fades the group just ends in a golden frame. A VP8 hidden frame's
packet carries the timestamp of the shown frame after it, and gives no frame when decoded; VP9 and AV1 put their hidden frames in
the next shown frame's packet. Turn `SceneCutKeyframes` off for
keyframes at a fixed spacing.

`LatencyMode.Realtime` looks nowhere ahead: each packet comes out as soon as its frame goes in, there are no hidden frames,
frames keep no probability updates for the next (so a lost packet spoils one frame), and 720p and larger VP8 frames get two or
four token partitions so decoders can use more threads.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoEncode.cs#video-encode-realtime)]

## Threads and repeatability

The output depends only on the frames and the options, never on the number of threads or the machine, so a test can compare
hashes. `EgamiContext.MaxParallelism` on the `MediaContext` sets how many threads the encoder uses. VP9's tile columns depend
on the frame width and `Vp9EncoderOptions.TileColumns`, never on the threads; AV1's are one per 4096 samples of width, so
frames up to 4K are one tile, and its threads help little within a frame.
