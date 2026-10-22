# VP8 video frames

`Vp8StreamDecoder` decodes VP8 video frame by frame and `Vp8StreamEncoder` encodes it; both work on frames, and Egami.Video uses
them for WebM, Matroska and IVF files.

## Decoding

`Vp8StreamDecoder` decodes VP8 video (RFC 6386) a frame at a time: key frames and inter frames, motion vectors, the last, golden and
alternate references and hidden frames. Every frame equals libvpx's sample for sample, on libvpx's own test vectors and on clips
made with its encoder, across the four versions, one to eight token partitions, segmentation and odd sizes. It reads frames, not
files: WebM and IVF are read by Egami.Video, which uses this decoder, and frames from any other source decode the same way.

[!code-csharp[](../../../tests/Egami.DocSamples/Vp8.cs#vp8-stream)]

`Decode` takes the frames in stream order and returns a `Vp8FrameInfo`: whether the frame is a key frame and whether it is shown,
the frame size from the last key frame, and the VP8 version. A hidden frame, typically an alternate reference, only updates the
references and has no picture. `CopyFrameTo` copies a shown frame's planes into a 4:2:0 8-bit `YuvRaster` of the frame's size; the
copy belongs to the caller, so it can be changed or kept while later frames decode. VP8 pictures are BT.601, limited range.

### Broken data

Decoding starts at a key frame. By default a broken frame (data that runs out, a header that cannot be read) or an inter frame with
no key frame before it throws `CorruptDataException`, and the decoder carries on from the next frame it is given; `Reset` drops the
references so the next frame must be a key frame again. With `ConcealErrors` set nothing throws for broken data: the macroblocks a
frame could not read are copied from the previous frame (mid-gray when there is none), a frame whose header cannot be read shows the
previous frame again, and an inter frame with no key frame before it predicts from mid-gray. `Vp8FrameInfo.IsConcealed` says which
frames were concealed. A key frame larger than the context's `SafetyLimits` throws `LimitExceededException` either way.

### Memory and threads

The decoder keeps four frame buffers, which the references and the frame being decoded share as libvpx shares them, so a stream
holds four frames however long it is. They come from the context's `IPixelMemorySource` and go back when the decoder is disposed.
A frame's macroblock rows decode in a wavefront on up to `EgamiContext.MaxParallelism` threads, as many as the frame's token
partitions allow (one partition decodes on one thread, as in libvpx); the result does not depend on the thread count. Decoding
takes at most 1.5 times libvpx's time at 720p and 1080p, on one thread and on all cores.

## Encoding

`Vp8StreamEncoder` encodes 4:2:0 8-bit `YuvRaster` pictures of one size into VP8 frames. Key frames use the lossy WebP encoder;
inter frames search the last, golden and alternate references for motion (whole pixels, then half and quarter pixels with the
six-tap filter), weigh every VP8 vector mode (ZERO, NEAREST, NEAR, NEW and the four SPLIT layouts) against the intra modes by
rate and distortion, and send only the probability updates that save bits. The frames decode in libvpx exactly as in
`Vp8StreamDecoder`.

[!code-csharp[](../../../tests/Egami.DocSamples/Vp8.cs#vp8-encode)]

Each call takes a `Vp8FramePlan`, so the caller decides what the stream looks like:

- `KeyFrame` starts a frame that decodes on its own; the first frame is always one.
- `Quality` (0 to 100, the scale of lossy WebP) or `Quantizer` (VP8's index, 0 to 127, for rate control) sets the frame's
  quantizer, and `Effort` (0 to 10, default 4) how hard the encoder searches: how far it looks for motion, whether it tries split
  vectors and every intra mode, and whether it tries loop filter levels by decoding the frame with each.
- `References` (a `Vp8References`) says which references an inter frame may predict from, and `RefreshLast`, `RefreshGolden`,
  `RefreshAlternate`, `CopyToGolden` and `CopyToAlternate` (a `Vp8ReferenceCopy`) what becomes of them. `GoldenSignBias` and `AlternateSignBias` mark a reference that lies in
  the future, as a hidden alternate frame does.
- `Show = false` codes a hidden frame, typically an alternate reference built from frames to come, which only updates the
  references.
- `RefreshEntropy = false` keeps the frame's probability updates from the frames after it, so a lost frame does not spoil the next,
  and `Partitions` splits the coefficients into 2, 4 or 8 parts that decoders can read in parallel: what real-time streams use.
- `FilterLevel` fixes the loop filter level instead of letting the encoder choose.

`Encode` returns a `Vp8EncodedFrame` with the frame's data, its size in bits, its quantizer and filter level, the squared error of
the decoded frame against the picture (`LumaPsnr` from it), and how many macroblocks were intra or had nothing to code: what rate
control needs to plan the next frame.

At the default effort the frames are about as small as libvpx's at `--good --cpu-used=1` for the same luma PSNR, within a few
percent on natural pictures and about 10% larger on synthetic test patterns, and higher efforts come out smaller still. The
encoder keeps the stream's four frame buffers from the context's memory source and decodes each frame it writes to keep its
references; its output does not depend on `MaxParallelism`. Lossy WebP stills are not affected: they encode as before.
