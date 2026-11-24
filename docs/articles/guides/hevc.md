# HEVC

Egami.Video.Hevc decodes and encodes HEVC (H.265), the codec of iPhone and Android video, GoPro footage, 4K streaming and HDR10. It is a
separate, opt-in package because HEVC is covered by patents (see [Patents](#patents)); Egami.Video itself contains no HEVC
decoder and copies HEVC tracks through untouched when this package is not registered. The decoder is Egami.Heic's, the same one
that reads HEIC photos, so Egami.Video.Hevc depends on Egami.Heic; apps can also drive it directly through Egami.Heic's
`HevcStreamDecoder` (see [HEIC](../formats/heic.md)).

- Profiles: Main, Main 10, Main 12 and the format range extensions up to 12 bits, 4:0:0, 4:2:0, 4:2:2 and 4:4:4. I, P and B
  pictures with merge, AMVP and temporal motion vector prediction, weighted prediction, asymmetric partitions, lossless and
  transform-skip blocks, scaling lists, tiles, wavefronts and several slices per picture.
- Containers: MP4 and MOV (hvc1 and hev1), Matroska through Egami.Video's readers, and raw Annex B `.h265` files through
  `HevcFormat`. Dolby Vision profile 8 files decode as their HEVC base layer, dvh1 and dvhe tracks included.
- Frames equal the reference decoder HM's sample for sample on the JCT-VC conformance bitstreams, and FFmpeg's on every clip
  in the test corpus but one, whose chroma FFmpeg decodes against the stream's own picture hashes there; that clip is checked
  against HM.
- Not decoded: the screen content coding extensions, which give `UnsupportedFormatException`, and layers above the base
  (multi-layer HEVC, Dolby Vision enhancement layers and RPU), which are skipped.

## Registering

`WithHevc()` (from `HevcMediaContextExtensions`) adds the decoder, `HevcCodec`, and the raw stream reader, `HevcFormat`, to a
`MediaContext`. Nothing registers them on its own; set the context as the default with `MediaContext.SetDefault` to use it
everywhere.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHevc.cs#register)]

## Decoding

Frames come out in display order through `VideoReader`, like any other codec, as `YuvVideoFrame`s with the stream's own
planes (8-bit samples in bytes, 10 and 12-bit in ushorts) or converted to a pixel type. The colour comes from the stream's
VUI, then the container, and otherwise BT.601 for 576 lines and fewer and BT.709 above; HDR10 mastering display and content
light level SEI become the frame's `Hdr`, and HDR10+ user data SEI its `Hdr10Plus`.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHevc.cs#decode)]

Broken data follows `VideoReaderOptions.Errors` and reports `Hevc.CorruptFrame`. With `Conceal` a picture whose slice data is
damaged keeps what decoded before the damage and comes out; with `Skip`, the default, and when concealing cannot help (a
broken slice header or parameter set), frames are dropped up to the next IRAP picture; `Throw` throws. Decoding that starts
in the middle of a stream waits for the next IRAP picture, and the leading pictures a CRA picture skips there are not output.

Tiles, wavefront rows and slices decode on `EgamiContext.MaxParallelism` threads, and so do the deblocking filter and SAO.

## Raw .h265 files

`HevcFormat` reads raw Annex B streams (`.h265`, `.265` and `.hevc`), as encoders and `ffmpeg -f hevc` write them. Such a
stream has no timestamps, so frames are timed by the stream's VUI frame rate, or 25 frames a second, unless `FrameRate` is
set. Seeking goes back to the IRAP picture before the target.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHevc.cs#raw)]

## Encoding

`HevcCodec.Instance` encodes through `VideoWriter` (or `CreateEncoder` for packets): 4:2:0 progressive frames with an even
width and height, in the Main profile for 8-bit frames and Main 10 for 16-bit planes (other frames are converted). `Quality`
(0 to 100, default 60, on the H.264 encoder's scale: QP 51 - 0.56 × quality) and `Effort` (0 to 10, default 4) work as for
Egami's other encoders. `HevcEncoderOptions` (`VideoWriterOptions.Hevc`, `VideoEncoderOptions.Hevc`) holds the HEVC settings,
each with a default that suits most uses:

- `Profile`: Main or Main 10, by default from the first frame. 8-bit frames coded as Main 10 keep their values.
- `Level`: by default the lowest Main tier level that the frame size, rate and bitrate fit; a stream that does not fit the
  level given is refused when the encoder is made.
- `MinQuantizer` and `MaxQuantizer`: the QP range rate control may use.
- `AdaptiveQuantization` (on): busy areas are coded coarser and flat ones finer, which looks better at the same size but
  lowers PSNR; letterbox bars and blank areas are left out of the picture's mean. Turn it off to measure PSNR.
- `BFrames`: the most B-frames between two reference frames, 3 by default (none at effort 0).
- `AnnexB`: start-code packets with the parameter sets in every keyframe, for raw `.h265` files, MPEG-TS and RTP. By default
  packets are length-prefixed with the parameter sets in the track's hvcC record, which MP4 and MOV write as an hvc1 sample
  entry (the one Apple players need) and Matroska as its codec private data.

Keyframes are IDR pictures, every 10 seconds unless `KeyframeInterval` is set, and at scene cuts unless `SceneCutKeyframes` is
off. P pictures predict from up to three earlier pictures at effort 4 (one at 0 and 1, two at 2 and 3, four from 6), with
motion search to a quarter sample, merge and skip modes, and intra blocks where they pay. B pictures predict from both sides
and take fewer bits; they need `LatencyMode.Quality` (the default), because they are coded out of order and packets carry
decode timestamps. A run of two or three B pictures is a pyramid whose middle picture the others predict from.
`LatencyMode.Realtime` turns B pictures and the lookahead off, so each packet comes out as soon as its frame goes in.

Rate control works as for the H.264 encoder: constant quality (the default), a fixed `Quantizer`, an average `Bitrate` (one
pass, or two with `FirstPassStats`) and `RateControlMode.ConstantBitrate` with a buffer model, whose bitrate and buffer the
level chosen then fits too. From effort 5 a block tree codes the blocks later pictures predict from finer. A frame's `Hdr`
metadata goes into every IDR access unit as mastering display and content light level SEI messages, and MP4 writes it into
mdcv and clli boxes as well.

Each picture's CTB rows are wavefront substreams coded in parallel on `EgamiContext.MaxParallelism` threads, and the
lookahead's analysis runs as a wavefront too; the stream is the same on any number of threads. Every picture decodes in
Egami's decoder, FFmpeg's and the reference decoder HM to the encoder's own reconstruction, sample for sample.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHevc.cs#encode)]

### Compression and speed

The encoder is measured against x265 3.6's medium preset on four Xiph test clips (crowd_run, ducks_take_off, park_joy and
old_town_cross at 720p, 50 frames a second, 60 frames), both tuned for PSNR. At effort 4 it needs 2.3 % fewer bits than x265
medium for the same PSNR on average (from 8.0 % fewer on old_town_cross to 1.2 % more on park_joy), and with adaptive
quantization on, as both encoders default to, 3.1 % fewer for the same SSIM. At effort 5 it needs 13.1 % fewer for the same
PSNR. Against Egami's own H.264 encoder at effort 4 it saves about 12 % of the bits on average (x265 medium saves 10.3 % on
the same clips). It is slower: on one thread, with x265 at the CRF that gives the same PSNR-Y and no adaptive quantization,
effort 4 took 1.5 times x265 medium's CPU cycles a frame at quality 50 (1.2 to 2.3 by clip) and 1.8 times at quality 34
(1.6 to 2.4), and 2.5 and 2.8 times its instructions, before a tuning of the efforts that added about 2 % to effort 4's
instructions. Cycles vary by up to a third between runs on a busy machine.

#### Speed by effort

Effort trades time for size. BD-rate is against x265 medium on the same four clips over four qualities and 60 frames, on
PSNR-Y (adaptive quantization off) and on SSIM-Y (on): the mean of the clips and, for PSNR, the worst clip. Work is the encoder's
instructions a frame at one thread against effort 4's (quality 50, the first 10 frames of the four clips); effort 4's time
against x265 is above.

| Effort | BD-rate PSNR (mean) | PSNR (worst clip) | BD-rate SSIM (mean) | Work |
|---:|---:|---:|---:|---:|
| 0 | +108.9 % | +188.7 % | +81.1 % | 0.52 × |
| 1 | +9.0 % | +17.3 % | +4.7 % | 0.64 × |
| 2 | +3.6 % | +9.3 % | +1.4 % | 0.74 × |
| 3 | −1.1 % | +1.9 % | −2.1 % | 0.86 × |
| 4 (default) | −2.3 % | +1.2 % | −3.1 % | 1 |
| 5 | −13.1 % | −5.6 % | −17.4 % | 2.9 × |
| 6 | −13.7 % | −5.8 % | −16.7 % | 3.7 × |
| 7 | −14.4 % | −5.9 % | −18.0 % | 5.0 × |
| 8 | −15.3 % | −8.7 % | −18.0 % | 8.3 × |
| 9 | −16.0 % | −8.8 % | −19.1 % | 10.2 × |
| 10 | −16.1 % | −8.7 % | −19.2 % | 11.5 × or more |

Efforts 9 and 10 were measured with the intra search of one effort lower (8 and 9), which they now search harder; their
BD-rates and effort 10's work are from those runs.

What each effort adds:

- **0**: 16x16 and 32x32 coding units, one reference, two merge candidates, no B pictures, motion to half samples only and no SAO.
- **1**: B pictures (three between P pictures, in a pyramid), quarter-sample motion, two references, three merge candidates,
  inter coding units from 16x16 to 64x64, a CTB's units split at least as deep as the co-located ones in the references (to
  effort 5), and the intra search's 8x8 and NxN units with RDOQ and transform skip.
- **2**: inter transform tree splits.
- **3**: 8x8 inter coding units.
- **4** (default): three references, a wider search range, the second candidate coded when it is within 1.25 times the
  best, and in the intra search the most probable mode candidates and 64x64 units only without the adaptive quantizer.
- **5**: Nx2N and 2NxN partitions, two references for B pictures, four merge candidates, transform trees two levels deep,
  square sub-sample search, RDOQ on every trial, the block tree, and no early skip: a skipped coding unit is still split.
- **6**: four references, five merge candidates, and every split tried.
- **7**: transform trees three levels deep, halves searched from every start, and the second candidate coded within 2 times
  the best.
- **8**: intra searched in full in P and B pictures (with effort 6's transform trees and second candidates).
- **9**: effort 7's transform trees, halves and second candidates with the full intra search.
- **10**: a wider search range and the second candidate coded within 4 times the best.

tools/video/time-hevc-encoder.py and check-hevc-encoder.py in the repository repeat these measurements, and egami-compare's
video/native/hevc-encode-gate.py runs them as release gates.

## Recipes

Short answers to common tasks in this area. Each one compiles and runs in Egami's test run.

### Re-encode an H.264 video as HEVC

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHevc.cs#cookbook-hevc-from-h264)]

The decoder's 4:2:0 planes go to the encoder as they are, without a trip through RGB. Audio tracks are not copied here; see
[Video](video.md) for muxing.

### Make an HDR10 video

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHevc.cs#cookbook-hevc-hdr10)]

16-bit planes holding 10 bits pick Main 10. The PQ transfer and BT.2020 primaries come from the frame's colour code points.

### Send a live stream as raw .h265

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHevc.cs#cookbook-hevc-live)]

Annex B packets can be written back to back; any HEVC decoder, and `HevcFormat`, reads the file.

## Patents

HEVC is covered by patents. Egami's authors grant no patent rights and are not lawyers. Whether you need a patent licence
depends on your country and on how you use and distribute this package; commercial products may need one from the HEVC patent
pools or holders. Egami.Video does not depend on this package.
