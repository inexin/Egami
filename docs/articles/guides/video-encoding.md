# Encoding video

Egami.Video encodes VP8 into WebM, Matroska and IVF, with alpha in WebM and Matroska. The frame coding is Egami's
`Vp8StreamEncoder`; Egami.Video decides how each frame is coded: where keyframes go, which frames become references, and how
many bits each frame gets. The same settings will drive VP9 and AV1 when they can encode. Motion JPEG and uncompressed video
encode too, without these settings: every frame is a keyframe.

## Writing VP8

Set `VideoWriterOptions.Codec` to `VideoCodecs.Vp8`. `Quality` runs from 0 to 100 (default 60, the scale of Egami's AVIF
writer) and `Effort` from 0 (fastest) to 10 (smallest files, default 4). Frames given as 8-bit 4:2:0 planes are coded as they
are; anything else is converted to BT.601 limited range first. Frames with alpha get a second VP8 stream for the alpha plane,
as WebM carries it.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoEncode.cs#video-encode-vp8)]

`Vp8EncoderOptions` holds the expert settings: quantizer limits, alternate reference frames on or off, golden frame spacing,
the temporal filter's frames and strength, token partitions, error resilience and alpha.

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
(still content); with motion, zooms or fades the group just ends in a golden frame. A hidden frame's
packet carries the timestamp of the shown frame after it, and gives no frame when decoded. Turn `SceneCutKeyframes` off for
keyframes at a fixed spacing.

`LatencyMode.Realtime` looks nowhere ahead: each packet comes out as soon as its frame goes in, there are no hidden frames,
frames keep no probability updates for the next (so a lost packet spoils one frame), and 720p and larger frames get two or four
token partitions so decoders can use more threads.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoEncode.cs#video-encode-realtime)]

## Threads and repeatability

The output depends only on the frames and the options, never on the number of threads or the machine, so a test can compare
hashes. `EgamiContext.MaxParallelism` on the `MediaContext` sets how many threads the encoder uses.
