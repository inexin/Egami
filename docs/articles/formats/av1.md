# AV1 video frames

`Av1StreamDecoder` decodes AV1 video a temporal unit at a time and `Av1StreamEncoder` encodes it; both work on frames, and
Egami.Video uses them for its containers.

## Decoding

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

### Options

`Av1DecoderOptions` chooses what comes out. `ApplyFilmGrain` (on by default) adds the stream's film grain to the frames; the
references keep the frames without grain either way. `OperatingPoint` picks the layers of a scalable stream that decode (0 by
default), and `OutputAllLayers` gives every spatial layer of a unit instead of only the highest.

### Broken data and limits

Decoding starts at a key frame. A broken unit, or one that refers to a frame the decoder does not have, throws
`CorruptDataException`, and the decoder carries on from the next unit it is given; `Reset` drops the references so the next unit
must start a sequence again. A frame larger than the context's `SafetyLimits` throws `LimitExceededException`.

### Memory and threads

The decoder keeps the eight reference frames and the frame being decoded. A frame's tiles decode on up to
`EgamiContext.MaxParallelism` threads, and its loop filter, CDEF and loop restoration run in rows on the same threads; the result
does not depend on the thread count.

## Encoding

`Av1StreamEncoder` encodes `YuvRaster` pictures of one size into AV1 frames: key frames and intra-only frames with the AVIF
encoder's intra search, and inter frames that predict each block from up to seven references. The motion search starts from the
vectors the neighbours and the reference frames give, searches whole pixels and then refines to a quarter or an eighth of a pixel,
and every block weighs NEARESTMV, NEARMV, GLOBALMV and NEWMV, the compound pairs and the intra modes by rate and distortion.
Higher efforts add distance-weighted, wedge and difference-weighted compounds, inter-intra, overlapped and locally warped motion,
dual interpolation filters and each reference's global motion. libaom and dav1d decode every frame exactly as `Av1StreamDecoder`
does.

[!code-csharp[](../../../tests/Egami.DocSamples/Av1.cs#av1-encode)]

`Av1SequenceOptions` describes the stream: its size, bit depth (8, 10 or 12), chroma format, color code points, chroma siting and
level; the encoder picks the Main, High or Professional profile they need. `Av1SequenceOptions.FromSequenceHeader` takes the
sequence header of an existing stream instead, and the encoder then writes that header byte for byte, so its frames can join the
stream, as a frame-exact cut needs. `SequenceHeader` is the OBU an MP4 or Matroska av1C box holds.

Each call takes an `Av1FramePlan`, so the caller decides what the stream looks like:

- `Kind` makes a key frame, an intra-only frame or an inter frame; the first frame is always a key frame.
- `Quality` (0 to 100, AVIF's scale) or `QIndex` (base_q_idx, 0 to 255, for rate control; 0 is lossless) sets the frame's
  quantizer, and `Effort` (0 to 10, default 4) how hard the encoder searches.
- `ReferenceSlots` names the slot each of LAST to ALTREF reads, `References` (an `Av1References`) which of them blocks may use, and
  `RefreshSlots` which slots the frame replaces. References that read the same frame are searched once.
- `Show = false` codes a hidden frame, such as an alternate reference from frames to come; `OrderHint` says where it shows, and
  `ShowExisting` shows it later with show_existing_frame.
- `Tune` is `Av1Tune.Video` (the default: one quantizer for luma and chroma, no quantization matrices, rate-distortion weights by
  frame type) or `Av1Tune.Image`, the still-picture tools AVIF uses. `Layer` is the frame's layer in its group's pyramid: the
  deeper it is, the fewer frames predict from it and the fewer bits it spends. `Referenced` says whether any later frame does.
- `ErrorResilient` makes a frame that decodes without the probabilities and vectors of the frames before it, `PrimaryReference`
  picks the reference a frame takes them from, `DeltaQuantizers` turns the per-superblock quantizers off, `VarianceBoost` gives
  flat superblocks finer ones, and `ScreenContent` the palettes on or off.

`Encode` returns an `Av1EncodedFrame` with the temporal unit's OBUs, without temporal delimiters, as MP4 samples and Matroska blocks
store them; a key frame's unit starts with the sequence header. A hidden frame's data is empty: its OBUs lead the next shown frame's
unit, so every unit has one shown frame. The result also gives the frame's bits, quantizer, order hint and the squared error of
the decoded frame against the picture (`LumaPsnr` from it), what rate control needs to plan the next frame.

The encoder keeps the eight reference slots as a decoder does, and decides a frame's tiles on up to `EgamiContext.MaxParallelism`
threads; its output does not depend on the thread count. AVIF stills are not affected: they encode as before, byte for byte.

## Temporal analysis

Before coding a group, `Analyze` can look at its frames and the group after it in coding order, each naming the frames it will
predict from. For every 16x16 block it measures what predicting from the references saves against coding the block alone, and
carries that back from the last frame to the first: a block the frames after it reuse a lot carries much. `Importance` sums it
over the frame, so a group's key frame and alternate reference can take finer quantizers the more they are reused, and a plan's
`Analysis` gives a referenced layer-0 frame finer quantizers in the superblocks that matter most and the other frames lower
rate-distortion weights there. The analysis reads the pictures only and is the same at any thread count.

[!code-csharp[](../../../tests/Egami.DocSamples/Av1.cs#av1-analyze)]
