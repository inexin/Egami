# VP8 video frames

`Vp8StreamDecoder` decodes VP8 video (RFC 6386) a frame at a time: key frames and inter frames, motion vectors, the last, golden and
alternate references and hidden frames. Every frame equals libvpx's sample for sample, on libvpx's own test vectors and on clips
made with its encoder, across the four versions, one to eight token partitions, segmentation and odd sizes. It reads frames, not
files: WebM and IVF are read by Egami.Video, which uses this decoder, and frames from any other source decode the same way.

[!code-csharp[](../../../tests/Egami.DocSamples/Vp8.cs#vp8-stream)]

`Decode` takes the frames in stream order and returns a `Vp8FrameInfo`: whether the frame is a key frame and whether it is shown,
the frame size from the last key frame, and the VP8 version. A hidden frame, typically an alternate reference, only updates the
references and has no picture. `CopyFrameTo` copies a shown frame's planes into a 4:2:0 8-bit `YuvRaster` of the frame's size; the
copy belongs to the caller, so it can be changed or kept while later frames decode. VP8 pictures are BT.601, limited range.

## Broken data

Decoding starts at a key frame. By default a broken frame (data that runs out, a header that cannot be read) or an inter frame with
no key frame before it throws `CorruptDataException`, and the decoder carries on from the next frame it is given; `Reset` drops the
references so the next frame must be a key frame again. With `ConcealErrors` set nothing throws for broken data: the macroblocks a
frame could not read are copied from the previous frame (mid-gray when there is none), a frame whose header cannot be read shows the
previous frame again, and an inter frame with no key frame before it predicts from mid-gray. `Vp8FrameInfo.IsConcealed` says which
frames were concealed. A key frame larger than the context's `SafetyLimits` throws `LimitExceededException` either way.

## Memory and threads

The decoder keeps four frame buffers, which the references and the frame being decoded share as libvpx shares them, so a stream
holds four frames however long it is. They come from the context's `IPixelMemorySource` and go back when the decoder is disposed.
A frame's macroblock rows decode in a wavefront on up to `EgamiContext.MaxParallelism` threads, as many as the frame's token
partitions allow (one partition decodes on one thread, as in libvpx); the result does not depend on the thread count. Decoding
takes at most 1.5 times libvpx's time at 720p and 1080p, on one thread and on all cores.
