# AV1 video frames

`Av1StreamDecoder` decodes AV1 video a temporal unit at a time: key, inter, intra-only and switch frames with every inter tool of
the Main, High and Professional profiles (single and compound references, OBMC, local and global warps, wedge, difference-weighted
and distance-weighted compound, inter-intra), 8, 10 and 12 bits, 4:0:0 to 4:4:4, film grain, frame size changes with scaled
references, superres and scalable streams. Every frame equals libaom's sample for sample, on libaom's test vectors and on clips made
with its encoder for each tool. It reads temporal units, not files: WebM, Matroska, MP4 and IVF are read by Egami.Video, which uses
this decoder, and units from any other source decode the same way.

[!code-csharp[](../../../tests/Egami.DocSamples/Av1.cs#av1-stream)]

`Send` takes one temporal unit: the low-overhead OBUs a container sample holds, each with its size. A unit can show no frame (a
hidden frame, kept only as a reference), one frame, or with `OutputAllLayers` one per spatial layer. `NextFrame` gives the size,
bit depth and chroma format of the next frame, and `TryReceiveFrame` copies it into a `YuvRaster` of that shape: 8-bit samples for
an 8-bit stream, 16-bit samples holding 10 or 12 bits otherwise. The copy belongs to the caller, so it can be changed or kept while
later units decode. A frame shown again with show_existing_frame comes out without decoding.

`Av1FrameInfo` says what the frame was: its type (`Av1FrameKind`), whether it is a key frame shown in its own unit (a point a player can start
from), its size and render size, bit depth, chroma format, temporal and spatial layer, and the sequence header's color code points.
`LastHeaders` lists the fields of the last unit's sequence and frame headers under the specification's names.

## Options

`Av1DecoderOptions` chooses what comes out. `ApplyFilmGrain` (on by default) adds the stream's film grain to the frames; the
references keep the frames without grain either way. `OperatingPoint` picks the layers of a scalable stream that decode (0 by
default), and `OutputAllLayers` gives every spatial layer of a unit instead of only the highest.

## Broken data and limits

Decoding starts at a key frame. A broken unit, or one that refers to a frame the decoder does not have, throws
`CorruptDataException`, and the decoder carries on from the next unit it is given; `Reset` drops the references so the next unit
must start a sequence again. A frame larger than the context's `SafetyLimits` throws `LimitExceededException`.

## Memory and threads

The decoder keeps the eight reference frames and the frame being decoded. A frame's tiles decode on up to
`EgamiContext.MaxParallelism` threads, and its loop filter, CDEF and loop restoration run in rows on the same threads; the result
does not depend on the thread count.
