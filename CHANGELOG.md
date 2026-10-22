# Changelog

All notable changes to Egami are listed here. Egami follows [Semantic Versioning](https://semver.org): from 1.0, public API
changes that break code come only in a new major version.

## Egami 1.2.0 (unreleased)

### Planar YUV

- `ChromaFormat.Yuv411` (chroma at a quarter of the luma width, as NTSC DV stores it) and `ChromaFormat.Yuv410` (a quarter of
  the width and height). `YuvRaster` creates, wraps, crops and resizes them, and `ToRaster`, `CopyTo` and `FromRaster` convert them
  with every siting and the existing upsampling and downsampling options, within one level of the exact reference. Saving them as
  JPEG writes 4:2:2 or 4:2:0; saving as AVIF goes through RGB, and `AvifWriterOptions.Chroma` set to either chooses as if unset.

## Egami.Cli 1.1.0 (2026-10-22)

A new package: the `egami` command-line tool, a .NET tool built on Egami's public API (Roadmap 2.0). It carries its own copy of
Egami, so it shares the Egami release train's version and releases with it. The command review froze its names and options
before this first release. The `cli-native` workflow builds it as a native program for win-x64, linux-x64 and osx-arm64,
which needs no .NET runtime.

### Commands

- `info` (summary, `--all`, `--json` with schema 1), `formats` (what egami reads and writes, and each format's writer settings).
- `convert`, `resize` (the size syntax `W`, `xH`, `WxH`, `P%` with `!`, `^`, `>` and `<`, and `--fit contain|cover|fill|pad`),
  `thumbnail` (upright, shrunk, sRGB, metadata left out), `crop`, `rotate`, `flip` (a direction is required) and `auto-orient`, the last three
  lossless on JPEG.
- `compare` (MAE, MSE, RMSE, PSNR, SSIM, DSSIM, peak and AE, `--threshold`, `--diff`, exit code 1 on a difference) and `stats`.
- `frames split` and `frames join` for GIF, APNG and WebP animations, with their delays.
- `metadata show`, `strip` (lossless for JPEG) and `copy`, which write a lossy format again only with `--reencode`; `color` for ICC conversion and HDR tone mapping.
- `text` (captions and watermarks with the paragraph layout, any font, a bundled default, `--font-size`) and `overlay` (any image, SVG
  included, by anchor, with opacity and blend mode); `montage` contact sheets.

### Pipelines and batches

- `run` chains any steps separated by `+`; every public `RasterOps` method is a step, generated at build time from the
  library, and `save` may come more than once. `--dry-run` prints the plan.
- JSON workflow files with a published schema, `run --workflow` and `run --save-workflow`.
- Every command takes many inputs, with egami's own glob expansion (`*`, `?`, `**`), `--recursive`, output templates (`{name}`,
  `{ext}`, `{dir}`, `{index}`, `{format}`), `--jobs`, `--fail-fast`, and `--in-place` to allow replacing an input.
- Writer settings: `--quality`, `--lossless`, `--effort`, `--bit-depth`, `--strip` (keeps the color profile), and `--set fmt.key=value` for every
  `WriterOptions` property, generated from the library.
- Exit codes 0 to 3, diagnostics on standard error (`-v`, `-vv`, `-q`), safety limits with `--limit` and `EGAMI_LIMITS`.
- `egami mcp` serves every command as a tool to AI assistants over the Model Context Protocol (Roadmap 2.0.1), with the official
  C# SDK (ModelContextProtocol.Core): one tool per command with the command's options and help text, `run` taking a workflow
  object, JSON results with the exit code and the files written, an optional PNG preview of the result, progress and
  cancellation for batches, and a cap of 1,000 inputs per call (`--max-inputs`). Tools read and write only inside the folders the
  assistant opened and those given with `--allow` (the working folder when there are neither), following links, and never
  replace an input without `in_place`. The package is also listed as an MCP server on NuGet, with its `server.json`.
- `egami <name>` runs an `egami-<name>` program from the PATH, so other families' tools join in; `egami formats` names
  `egami-heic` for HEIC, which the tool leaves out.
## Egami.Barcodes 1.0.1 (unreleased)

### Package icon

- The package has its own icon on NuGet: a graphite tile with barcode bars, in `branding/` with the other package icons.

### Faster reading

- Reading takes about 0.9 times zxing-cpp 3.1.1's time on one thread over the 817 ZXing test images, each read for its own symbology (3.1 times before), with
  the same symbols read or more. Linear barcodes, PDF417 and QR codes are within 1.5 times zxing-cpp's time; Aztec and Data
  Matrix within about 1.8 times.
- The inverted pass reuses the first pass's thresholds with dark and light swapped, the linear scan reads light-on-dark bars
  from the same scan lines, and inside a symbol the first pass found the inverted pass looks for no other.
- The thresholds, run cutting and Reed-Solomon decoding work on several pixels or values at a time; QR, Data Matrix and
  Aztec check a grid's fixed patterns (timing patterns, the L and clock tracks, the mode message) before sampling and
  decoding it.
- PDF417 rows are read every three quarters of a module instead of every pixel.

### Reading

- From effort 5 the linear scan also reads rows through the local threshold, which keeps bars under glare apart (from effort 6
  before), and scans 29 lines instead of 25, including effort 0 to 2's lines. EAN-13 photos with glare read 16 of 22 ZXing
  test images (11 before).
- QR looks for finder patterns again with more slack when it finds fewer than three, and tries every alignment pattern
  near the expected place, nearest first, then the nearest one nudged by quarter modules, so
  strongly tilted and curled symbols read (ZXing qrcode-3 39 of 42, 37 before; qrcode-4 38 of 48, 35 before).
- PDF417 cut off before its start pattern reads from the stop pattern, using the right row indicators (ZXing pdf417-3 19 of
  19, 16 before).

### Faster writing

- `Barcode.Code128` takes about 0.6 times ZXing.Net 0.16.10's time (1.6 times before): the code set search runs on the
  stack, and the symbol characters and bars are built without lists or copies. Code 128 and Code 39 no longer copy
  text without control characters for the human-readable text.

## Egami.Video 0.5.0 (unreleased)

V5.0 audio core: decoded audio, PCM and FLAC, WAV and FLAC files, resampling and channel mixing; V5.1: Opus decoding and Ogg. The V5 audio steps (V5.0,
V5.1 Opus, V5.5 Vorbis) get one API review and one release after V5.5, so these types may still change.

V4.2, API reviewed and accepted 2026-10-22 (all as recommended): AV1 decode, through Egami's AV1 stream decoder (Egami 1.1.0).

### Frames and conversion

- `AudioFrame`: samples for every channel with a timestamp, rate, `ChannelLayout` and `SampleFormat`, from the context's
  memory source. Twelve sample formats: unsigned 8-bit, signed 16, 32 and 64-bit, 32 and 64-bit float, each interleaved or
  planar; `ValidBits` says how many bits an integer sample uses (24-bit audio is `S32` with 24).
- `Convert` and `CopyTo` change the format, with optional triangular dither (`AudioConvertOptions.Dither`).
- `ChannelLayout` and `Speakers`: channels in WAVE_FORMAT_EXTENSIBLE order, presets from mono to 7.1.4, unpositioned channels.
- `ChannelMixer`: ITU-R BS.775 downmix and upmix matrices, or one of your own.
- `Resampler`: polyphase windowed-sinc (Kaiser) between any two rates, `ResamplerQuality.Fast`, `Good` (default: under 0.1 dB
  ripple to 0.9 of Nyquist, 100 dB stopband) and `Best` (140 dB), with exact timestamps.

### Codecs

- `AudioCodec`, `IAudioDecoder` (with `SendLoss`), `IAudioEncoder`, `AudioDecoderOptions`, `AudioEncoderOptions` and
  `AudioCodecs`, registered in `MediaContext.AudioCodecs` with `MediaContext.WithCodec(AudioCodec)`.
- `PcmCodec`: 8 to 32-bit integer PCM, little and big-endian, and 32 and 64-bit float, decoded and encoded.
- `FlacCodec`: all of RFC 9639 decoded, bit-exact with libFLAC on every IETF FLAC test file it decodes, with CRC and MD5
  checks (`Flac.BadCrc`, `Flac.Md5Mismatch`); encoding at levels 0 to 8 (default 5), 4 to 32 bits, 1 to 8 channels, files
  within 1 % of libFLAC's size at every level and accepted by `flac -t`.
- `AudioTrackInfo.Layout` and `AudioTrackInfo.SeekPreRoll`.

### Containers

- `WavFormat`: WAV, RF64 and BW64 (`WavFormat.Instance`) and Sony Wave64 (`WavFormat.Wave64`), read and written:
  WAVE_FORMAT_EXTENSIBLE, streamed files with unfilled sizes, LIST/INFO and id3 tags, cue points as chapters, and RF64 by
  itself past 4 GB.
- `FlacFormat`: native FLAC read and written, with Vorbis comments, pictures as attachments, seek tables and the channel mask.
- FLAC and PCM in Matroska and MP4 decode and encode through the existing containers.

### Reading and writing

- `AudioReader` and `AudioReaderOptions`: the first audio track of any file as planar float, or the format, rate and layout
  asked for; codec delay and edit lists trimmed (`KeepAllSamples` keeps them); sample-exact `Seek`.
- `AudioWriter` and `AudioWriterOptions`: frames of any length and format into WAV, Wave64, FLAC, Matroska or MP4.
- `MediaFile.Transcode`: copies a file and encodes its audio again with another codec, for example FLAC in Matroska.
- `VideoWriterOptions.AudioCodec` (with `AudioSampleRate`, `AudioLayout` and `AudioBitsPerSample`) and
  `VideoWriter.WriteAudio`: an audio track encoded next to the video.
- AFL++ fuzz targets `wav` and `flac`.

### Opus and Ogg (V5.1)

- `OpusCodec` (decode only), registered by default: SILK, CELT and hybrid frames at every bandwidth, mono, stereo and the RFC
  7845 multichannel mappings 0, 1 and 255, translated from libopus 1.5.2. All twelve RFC 8251 test vectors pass opus_compare
  in mono and stereo (96.2 to 100 %), with libopus's final range on every packet; files from opusenc decode within one 16-bit
  step of opusdec. Output is planar float at 48 kHz in the canonical channel order (5.1 and 7.1 remapped from Vorbis order).
- Lost packets, reported with `IAudioDecoder.SendLoss` or seen as a gap in timestamps, are concealed, or recovered from the
  next packet's in-band FEC. Ambisonic mappings (families 2 and 3) are reported as `Opus.UnsupportedMapping`.
- `AudioDecoderOptions.KeepPadding`: end padding past a packet's stated duration is trimmed by default (Ogg's last granule
  position, Matroska's DiscardPadding); `AudioReaderOptions.KeepAllSamples` keeps it.
- `OggFormat`: Ogg (.ogg, .opus, .oga) read and written with Opus, Vorbis and FLAC; CRC-checked pages,
  timestamps from granule positions, seeking from a one-pass index, and chained files joined into one track with each
  link's title as a chapter (`ogg-chained-stream` when a link changes the settings).
- Opus in Matroska and WebM gains SeekPreRoll and DiscardPadding, read and written; Opus in MP4 gets its 80 ms pre-roll.
- Decoding the twelve test vectors in stereo takes 0.72 s on one thread, against 0.89 s for libopus's opus_demo.
- AFL++ fuzz targets `ogg` and `opusraw`.

### Vorbis (V5.5)

- `VorbisCodec` (and `AudioCodecs.Vorbis`, decode only), registered by default, written from the Vorbis I specification:
  1 to 255 channels, floors 0 and 1, residues 0, 1 and 2 (floor 0 untested: no current encoder writes it). On eleven oggenc clips (8 to 48 kHz, mono to 7.1, q-1 to q10) every
  sample is within 2^-22 of libvorbisfile's float output, with the same sample count. Output is planar float in the
  canonical channel order (3 to 8 channels remapped from Vorbis order, as for Opus).
- Packet durations from the setup header's modes, in Ogg, Matroska and WebM, and MP4 (esds object type 0xDD, as FFmpeg
  writes it). The end is trimmed to Ogg's last granule position, Matroska's DiscardPadding (now read and written for Vorbis
  too) or a shorter last MP4 sample; Ogg's granule positions are written in samples when remuxing from Matroska.
- A corrupt packet is reported as `Vorbis.CorruptPacket`; with `DecodeErrorMode.Conceal` it becomes silence.
- Decoding 60 s of stereo 44.1 kHz takes 75 ms on one thread, against 227 ms for a libvorbisfile run (vorbisref, which also
  starts a process and writes its output).
- AFL++ fuzz target `vorbisraw`.

### AV1

- `Av1Codec` (and `VideoCodecs.Av1`), registered in `MediaContext.Codecs` by default: AV1 from WebM, Matroska, MP4 and IVF, 8, 10
  and 12 bit, 4:0:0 to 4:4:4, film grain, every frame equal to libaom's on our clips and libaom's size-changing Matroska vectors.
  Hidden frames give no frame; frames shown with show_existing_frame come out. The sequence header's color wins over the
  container's.
- Exact and keyframe seeks in IVF, WebM, Matroska and raw AV1 land on shown key frames; 100 random exact seeks per container
  match decoding from the start.
- Broken data: `Skip` (the default) reports `Av1.CorruptFrame`, or `Av1.MissingReference` for a unit that needs frames from
  before the first key frame, and resumes at the next shown key frame; `Conceal` repeats the last good frame in between (mid-gray
  before the first); `Throw` throws.
- `VideoDecoderOptions.ApplyFilmGrain` turns film grain off.
- HDR: content light level and mastering display metadata OBUs (in the units, or in av1C's configOBUs) give `VideoFrame.Hdr`,
  equal to ffprobe's side data on an SVT-AV1 HDR10 clip; an ITU-T T.35 metadata OBU with HDR10+ gives `VideoFrame.Hdr10Plus`.
- Keyframes are units that start with a shown key frame (forward key frames are not); `Av1.KeyframeFlagMismatch` when a
  container disagrees. The sequence header comes from the track's av1C when a unit has none.
- IVF AV1 tracks get their bit depth, chroma format and av1C from the first sequence header; the Matroska writer builds av1C
  from the first packet for a track that has none.
- `Av1Packets`: `SplitFrames`, `MergeFrames` and `ExtractConfig` (av1C from a sequence header, equal to FFmpeg's on every clip).
- `CodecHeaders.Describe` for AV1: each OBU, the sequence header, metadata (content light level, mastering display, ITU-T T.35,
  scalability, timecode) and the start of each frame header, under the specification's names. `CodecHeaderReader` reads a
  track's packets in order and lists each frame header whole, as the decoder read it.

### Raw AV1

- `Av1ObuFormat` (.obu, low-overhead OBUs as aomdec and dav1d read them) and `Av1AnnexBFormat` (the specification's Annex B,
  as the conformance streams use), registered by default and detected from their first bytes, read and written. Frames are timed
  at a `FrameRate` you set (30 a second by default). Reading and writing back gives the same bytes; packets match ffprobe's,
  and aomdec and dav1d decode the files the writers make to libaom's frames.

## Egami.Video 0.4.0 (unreleased)

V4.1, API reviewed and accepted 2026-10-22 (all as recommended): MP4 and QuickTime. Built on Egami's public ISO BMFF box
walker (Egami 1.1.0).

### MP4 and QuickTime

- `Mp4Format` (registered by default) reads .mp4, .m4v, .m4a and .mov files: moov before or after the media, 64-bit sizes and
  chunk offsets, compact sample sizes, composition offsets, several sample descriptions, and fragmented files (moof, traf,
  tfhd, tfdt, trun) read one fragment at a time. Packets, times, flags and bytes equal ffprobe's on the test files.
- Codecs: AV1 (av1C), VP9 and VP8 (vpcC), Motion JPEG (jpeg, mjpa, mp4v 0x6C), Opus (dOps), FLAC (dfLa), PCM (sowt, twos,
  lpcm, ipcm, fpcm), and mov_text map to their codec ids; H.264 and HEVC (`CodecIds.Avc`, `CodecIds.Hevc`), MPEG-4 Part 2, AAC
  and MP3 pass through with the setup Matroska stores; any other sample description passes through whole
  (`CodecIds.QuickTimeVideo`, `QuickTimeAudio`, `QuickTimeOther`).
- Edit lists are applied as FFmpeg shows them; `EncodedPacket.IsDiscardable` marks packets before the shown part, and readers
  drop their frames. `Mp4Format.IgnoreEditLists` turns this off.
- `VideoTrackInfo.Orientation` from the display matrix, and `VideoReaderOptions.AutoOrient` to turn frames upright.
- `MediaInfo.Timecode` from a timecode track; colr, pasp, fiel, clli, mdcv, SmDm and CoLL into the track info.
- Tags (iTunes items, QuickTime keys and udta text), cover art as attachments, and chapters from Nero chpl or a QuickTime chapter
  track.
- `MovTextSample`, `MovTextStyle` and `MovTextFace`: mov_text subtitles read and written with their style runs.
- Writing ISO MP4: half-second chunks interleaved by time, moov first by default (or last with `Mp4Format.MoovAtEnd`), edit
  lists, the display matrix, color and HDR boxes, each codec's config box, tags, cover art, and chapters both as Nero chpl
  and as a QuickTime chapter track. FFmpeg and MP4Box read the files without a warning they do not give for the originals.
- Seeking by sync samples in flat and fragmented files; exact seeks match decoding from the start.
- Broken files are read up to the damage, with `mp4-*` warnings; sample tables count against `MediaLimits.MaxIndexEntries`.
- AFL++ fuzz targets `mp4` (demux, seek, non-seekable) and `mp4mux` (read, write three ways, read back).

## Egami.Video 0.3.0 (unreleased)

Egami.Video now lives in the Egami repository (it was inexin/Egami.Video), built and tested with the other packages; its
version and NuGet package are its own.

V3, API reviewed and accepted 2026-10-22: VP9 decode in all four profiles, equal to libvpx 1.14.1 on the test vectors (the
decoder is translated closely from libvpx, notice in THIRD-PARTY-NOTICES.md); WebM alpha; HDR10 and HDR10+ metadata
(`Hdr10PlusMetadata`, `VideoFrame.Hdr10Plus`); `VideoFrame.RenderSize`; color-managed `YuvVideoFrame.ToRaster`; SIMD kernels
and threads within 1.5x libvpx's decode time.

### VP9 decoding

- `Vp9Codec` (and `VideoCodecs.Vp9`), registered in `MediaContext.Codecs` by default: VP9 from WebM, Matroska and IVF in all four
  profiles, 8, 10 and 12 bit, 4:2:0, 4:2:2, 4:4:4 and RGB, with superframes, hidden frames, show-existing frames, intra-only
  frames, tiles, lossless coding, compound prediction and references of another size. Every frame of libvpx's VP9 test vectors
  equals libvpx 1.14.1's. Frames come out as `YuvRaster<byte>` at 8 bits and `YuvRaster<ushort>` above, at their own coded size.
  4:4:0 streams are reported (`Vp9.UnsupportedSubsampling`) and not decoded.
- WebM VP9 with alpha (AlphaMode 1): the second VP9 stream in each packet's addition 1 decodes into the frame's alpha plane, as
  for VP8, unless `VideoDecoderOptions.DecodeAlpha` is off.
- `CodecHeaders.Describe` lists VP9 header fields: the superframe index, each frame's uncompressed header and the compressed
  header's modes with probability updates as counts.
- Streams that change size come out frame by frame at each frame's coded size; `VideoFrame.RenderSize` reports VP9's
  render_size without scaling to it, as libvpx and FFmpeg do. Exact seeks land on the same frames as decoding from the start,
  over hidden and show-existing frames.
- Broken VP9 data follows `VideoReaderOptions.Errors`: `Throw` throws, `Skip` drops frames up to the next keyframe, and
  `Conceal` repeats the last good frame (mid-gray before the first). Diagnostics: `Vp9.CorruptFrame` (a broken frame or alpha
  frame), `Vp9.MissingReference` (an inter frame with no keyframe before it), `Vp9.UnsupportedSubsampling`,
  `Vp9.KeyframeFlagMismatch` (the container's keyframe flag disagrees with the frame; the frame wins) and
  `Vp9.CodecPrivateMismatch` (the track's CodecPrivate disagrees with the stream; the stream wins).

### Safety

- VP9 frame sizes are checked against `EgamiContext.Limits` (width, height, pixels, and the memory of the nine frame buffers a
  stream can hold) before anything is allocated. A stream holds at most nine frame buffers, eighteen with alpha.
- AFL++ fuzz targets `vp9` (files through VideoReader in each error mode) and `vp9raw` (packets, superframes and CodecPrivate
  straight into the decoder).

### Speed

- VP9 decoding uses SSE2, SSSE3 and AVX2 vectors for the sub-pixel filters, the inverse transforms and the loop filter, at 8 bits
  and above, with the same output. One thread decodes 8-bit 720p, 1080p and 2160p within 1.5 times libvpx 1.14.1's time.
- VP9 frames decode their tile columns in parallel and loop filter their superblock rows as a wavefront, on up to
  `EgamiContext.MaxParallelism` threads, with the same output at any thread count.

### Color and HDR

- VP9 frames carry their color code points (the bitstream's color space and range, or the container's Colour element when it
  says more) and the track's mastering display and light level metadata, on the frame and in the raster's metadata.
- `YuvVideoFrame.ToRaster<TPixel>(ColorProfile, ColorTransformOptions?)` converts a frame to another encoding through Egami's
  color pipeline, tone mapping PQ and HLG to SDR from the static HDR metadata.
- `Hdr10PlusMetadata` and `VideoFrame.Hdr10Plus`: HDR10+ dynamic metadata (SMPTE ST 2094-40) from WebM's BlockAdditional 4,
  read per frame and kept unchanged when remuxing.

### Containers

- WebM and Matroska VP9 tracks take their bit depth and subsampling from the CodecPrivate feature records; IVF VP9 tracks from
  the first keyframe.
- Writing Matroska or WebM joins VP9 hidden frames that arrive as packets of their own into a superframe with the next shown
  frame, as WebM players expect.

## Egami.Video 0.2.1 (unreleased)

Built on the Egami with faster VP8 decoding; no API changes, and every frame is the same as before. Single-thread VP8 decode
through Egami.Video now takes 1.40 times libvpx 1.14.1's time on the 720p grain clip and 1.52 times on the 1080p one, down from
1.60–1.67; all-core decode takes 1.23–1.27 times. Egami's own decoder takes 1.43 times on both; Egami.Video's per-frame copy into
its frames adds the rest.

## Egami.Video 0.2.0 (unreleased)

V2, API reviewed and accepted 2026-10-15: VP8 decode through Egami's Vp8StreamDecoder, equal to libvpx on the test vectors;
WebM alpha (packet additions, DecodeAlpha); IVF read and write; exact seek by default with SeekMode.Keyframe; CodecHeaders.Describe
for VP8 header fields; DecodeErrorMode (Throw, Skip, Conceal) with Skip the reader's default, replacing
VideoReaderOptions.SkipCorruptFrames. Single-thread VP8 decode is 1.6x libvpx's (accepted; an Egami step to speed it up follows).

## Egami.Video 0.1.0 (unreleased)

First release (V1), API reviewed and accepted 2026-10-11: frame and packet model with bit depths to 12, alpha, packed
layouts, HDR and color carried end to end; AVI (with OpenDML), Matroska/WebM and YUV4MPEG2 read and write, and headerless raw
video; Motion JPEG and uncompressed video decode and encode; audio passthrough, keyframe seeking, tags, chapters and
attachments; PSNR, SSIM and framemd5-style hashes.

## Egami.Barcodes 1.0.0 (2026-10-19)

A new package that writes and reads barcodes, drawn through Egami.Drawing.

### Documentation

- A guide to reading barcodes (options, camera frames, GS1 data, what reads well, speed) and cookbook recipes for camera
  frames, pages of barcodes, GS1 labels and SVG output.

### API review changes (breaking against the previews)

- `BarcodeFormat` is a `[Flags]` enum (`Qr = 1` to `UpcE = 1024`, with `None`, `Linear`, `Matrix` and `All`), and
  `BarcodeReaderOptions.Formats` takes it; `BarcodeFormats` is gone. `Barcode.Format` and `BarcodeResult.Format` hold one flag.
- `Barcode.Gs1_128` is now `Barcode.Gs1Code128`.
- `BarcodeResult.AddOn` holds an EAN or UPC add-on; `Text` has the main digits only.
- `BarcodeReader.ReadBarcodes` and `ReadBarcode` also take an 8-bit luminance buffer with its width, height and stride, read in
  place.

### Modules and error correction

- `BarcodeModules` holds a symbol's modules: a grid of dark and light modules for 2D codes, or bar and space widths for linear
  codes. `BarcodeModules.Create` and `CreateLinear` make one from modules you already have.
- Reed-Solomon error correction for every symbology the package will write and read: QR, Data Matrix, PDF417 and Aztec, with
  errors and erasures corrected together.

### Reading

- `ReadBarcodes` and `ReadBarcode` read barcodes from a raster of any pixel type, with `BarcodeReaderOptions` for formats,
  effort, inverted symbols, a region and rendered (pure) barcodes. Each `BarcodeResult` has the format, text, bytes,
  corners, ECI, GS1 element strings and the codewords error correction restored.
- Linear barcodes are found in photos along rows, columns and (from effort 7) diagonals, read both ways with sub-pixel edges
  and two thresholds per line; two lines must agree.
- QR Codes are found by their finder patterns at any rotation and in perspective, corrected by the alignment pattern, several
  per image.
- Data Matrix symbols are found by their L finder and clock tracks, square and rectangular (DMRE too), at any rotation
  and in perspective, several per image.
- PDF417 symbols are found by their start and stop patterns at any rotation and in perspective, compact ones too.
- Aztec Codes are found by their bullseye at any rotation and in perspective: compact, full-range (with the reference grid)
  and runes.
- Read rates on the ZXing blackbox photos at or above zxing-cpp's for most sets, with no false positives; ink spread,
  glare and one-pixel bars handled (Code 128 and EAN edge distances, gray-level PDF417 rows, extra scan lines).
- Many symbols per image, each once and in reading order; inverted and mirrored symbols; large images searched on
  several threads with the same results at any thread count.
- The decoders read every symbology the package writes: QR (mirrored too), Data Matrix (DMRE and 144 x 144 too), PDF417
  (compact too), Aztec (runes too), Code 128 and GS1-128, Code 39 (Full ASCII too), ITF, EAN, UPC and their add-ons.

### QR Code

- `Barcode.Qr` writes QR Codes from text, bytes or GS1 element strings, versions 1 to 40, with the fewest bits over numeric,
  alphanumeric, byte and Kanji segments, and ECI 26 (UTF-8) for text outside ISO 8859-1 and Shift JIS.
- Level M by default, raised to Q or H while the symbol keeps its size; `QrOptions` fixes the level, version or mask.

### Data Matrix

- `Barcode.DataMatrix` writes Data Matrix ECC 200 from text, bytes or GS1 element strings, switching between its six
  encodation modes for the fewest codewords. Square by default; rectangular and DMRE sizes on request
  (`DataMatrixOptions`).

### PDF417

- `Barcode.Pdf417` writes PDF417 and compact PDF417 from text or bytes, with text, numeric and byte compaction, the error
  level from the standard's table and columns for about 3:1; `Pdf417Options` sets columns, level and the compact form.

### Aztec Code

- `Barcode.Aztec` writes compact and full-range Aztec Code from text or bytes, with the fewest bits over its modes and Binary
  Shift and at least 23 % error correction by default (`AztecOptions`); `Barcode.AztecRune` writes Aztec Runes.

### Code 128 and GS1

- `Barcode.Code128` writes Code 128 with the shortest mix of code sets A, B and C.
- `Gs1.Parse` and `Gs1.TryParse` read GS1 element strings such as `(01)09501101530003(17)261231` and check each against the
  GS1 General Specifications (AI, characters, lengths, check digits, dates and times), with messages that say what is wrong.
- `Barcode.Gs1Code128` writes GS1-128, with FNC1 separators only where the AIs need them.

### Code 39 and ITF

- `Barcode.Code39` writes Code 39, with Full ASCII when the text needs it and an optional mod 43 check character.
- `Barcode.Itf` writes Interleaved 2 of 5, and `Barcode.Itf14` writes ITF-14 with its check digit added or checked and bearer
  bars around it.

### EAN and UPC

- `Barcode.Ean13`, `Ean8`, `UpcA` and `UpcE` add the check digit when it is left out and check it when it is given.
  `Barcode.UpcE` also compresses a UPC-A number. `EanUpcOptions.AddOn` adds an EAN-2 or EAN-5 add-on.
- With a font, the digits go between the guard bars, which reach into the text line, and an add-on's digits above it.

### Drawing

- `Barcode` is one symbol: format, modules, the quiet zone of its standard and its human-readable text.
  `Barcode.FromModules` makes one from modules you already have, and `Barcode.Create` makes one of a format chosen at run
  time.
- `DrawBarcode` draws a barcode on any canvas as one path, fitted into a rectangle or at a module size. On raster canvases with
  no rotation or skew, modules snap to whole pixels.
- `ToPath` returns the symbol as one path, and `ToRaster` renders it to a `Raster<Gray8>` with whole-pixel modules.
- `BarcodeDrawOptions` turns the quiet zone off, sets a bar width reduction for ink spread, and gives the font for the
  human-readable text, which is drawn only when a font is given.

## Egami.Heic 1.1.0 (2026-10-22)

### HEIC package

- New opt-in package for HEIC, the HEVC-coded HEIF files phones save photos in. `WithHeic()` registers it in a context; nothing
  registers it on its own, because HEVC is covered by patents (the package's readme and XML docs carry the notice).
- `Raster.Load` and `YuvRaster.Load` read still HEIC images: grids (their tiles decoded in parallel), overlays, alpha, the clean
  aperture, `irot` and `imir` as the orientation, ICC profiles, Exif and XMP, `ReaderOptions.Region` (only the tiles it touches
  are decoded) and streaming rows, under the rules AVIF follows.
- RGB is converted as libheif's heif-dec converts it. Every phone photo and HEVC-coded file of the test corpus equals heif-dec's
  output sample for sample, except a clean aperture at half-sample offsets and one file where heif-dec's HEVC decoder departs
  from HM, the reference.
- An iPhone photo loads in about the time heif-dec takes on one thread, and well under half of it on all threads.

### Gain maps, items and sequences

- iPhone HDR photos: Apple's HDRGainMap auxiliary image and ISO 21496-1 tone-map items load into `RasterMetadata.GainMap`, and
  `ReaderOptions.Hdr` renders them as HDR. Apple's map loads with the same samples as heif-dec's auxiliary image output.
- `HeicDetails` (core) describes a file: brands, HEVC profile, color, grid, and `Items`, every item with its `HeicItemRole`.
  `HeicImage.ReadItem` reads thumbnails, depth maps and mattes on their own.
- Image sequences whose frames are all intra load every frame; others give their still image or first frame.

### HEVC decoder

- A managed HEVC decoder for intra pictures: Main, Main 10, Main Still Picture and the 4:0:0 to 4:4:4 range extensions up to 12
  bits, with tiles, wavefronts, dependent slices, PCM, lossless coding and scaling lists. The intra pictures of the HEVC
  conformance streams in those profiles, and every HEIC test image, decode bit-exact to HM 18.0, the reference decoder.
- Single-threaded it decodes an iPhone photo in about 1.3 times the time of libde265. Tiles, wavefront rows and the loop
  filters run on several threads, with the same output at any thread count.
- Ships at Egami's version and depends on exactly that version of Egami.

## Egami 1.1.0 (2026-10-22)

### AV1 video

API reviewed and accepted 2026-10-22 (all as recommended). Decoding runs at about 3.7 times dav1d's time on one thread.

- `Av1StreamDecoder` (in `Egami.Formats.Av1`) decodes AV1 video a temporal unit at a time: key, inter, intra-only and switch
  frames with every inter tool of the Main, High and Professional profiles, 8 to 12 bits, 4:0:0 to 4:4:4, film grain, scaled
  references, superres and scalable streams (operating point and layer options). Every frame equals libaom's on libaom's test
  vectors and on clips for each tool. `Av1DecoderOptions`, `Av1FrameInfo` and `Av1FrameKind` go with it. Egami.Video uses it for
  AV1 in WebM, Matroska, MP4 and IVF.
- Animated AVIF: `Raster.Load` gives every frame of an image sequence, inter frames included, with each frame's duration, the
  loop count and the alpha track; `Avif.SequenceFirstFrame` is no longer reported. Every frame equals avifdec's.

### ISO BMFF boxes

- `IsoBoxReader`, `IsoBox` and `FourCC` (in `Egami.Formats.Iso`) walk the boxes of MP4, QuickTime, HEIF and AVIF files from
  bytes in memory, one level at a time, with every size checked; `IsoBoxReader.ReadHeader` reads box headers from a stream,
  so a large file's media data can be skipped. This is the walker Egami's AVIF and HEIC readers use, now public for
  Egami.Video's MP4 reader and your own tools. API reviewed and accepted 2026-10-22 (all as recommended).

### HEIC detection

- HEIC files (HEIF with HEVC-coded images, as iPhones and most Android phones save photos) are recognized by their brands.
  `Raster.Probe` reads their container (size after the clean aperture, pixel type by bit depth, alpha, orientation), and
  `Raster.Load` throws an `UnsupportedFormatException` that names the Egami.Heic package. Core Egami holds no HEVC code.
- `HeicDetails`, `HeicItem` and `HeicItemRole` describe a HEIC file's brands, HEVC profile, color, grid and items; Egami.Heic
  fills them when it loads.
- `AvifImage.ReadItem` gives an item's own rotation and mirroring (irot, imir) as its orientation, as `HeicImage.ReadItem` does.

### AVIF writing

- `Save` writes AVIF, coded by Egami's own managed AV1 intra encoder: lossy on avifenc's quality scale (`AvifWriterOptions.Quality`,
  default 60), or lossless as 4:4:4 RGB with the identity matrix (`Lossless`); 8, 10 and 12 bits; 4:2:0, 4:2:2, 4:4:4 and 4:0:0;
  transparency as an alpha item (`AlphaQuality`). Files decode in dav1d and libavif to the same pixels as in Egami.
- `AvifDetails.Lossless` says whether a file's image is coded losslessly, so saving it again writes lossless.
- `AvifWriterOptions.Effort` (0 to 10, default 4) trades time for size, like avifenc's speed. The encoder searches partitions,
  modes and transforms by rate and distortion, quantizes with a trellis, varies the quantizer by superblock as libaom's tune IQ
  does, and picks the loop filters.
- Screen content (text, user interfaces, graphics) is detected and coded with palettes and intra block copy.
- From effort 5, 8-bit 4:2:0 images are converted with sharp RGB to YUV.
- `YuvRaster.Save` writes AVIF from the planes as they are, at 8, 10 or 12 bits, with their matrix, range and chroma siting.
- AVIF files keep EXIF and XMP, the orientation (irot and imir), the ICC profile when the colors need one, HDR luminances
  (clli and mdcv) and gain maps (tmap), which libavif reads and applies.
- Images larger than 8192 x 4352 are written as grids of equal cells (`AvifWriterOptions.GridTileSize` sets the cells); tiles
  and cells are coded in parallel, with the same file at any thread count.

### Faster lossless WebP

- Lossless WebP decodes about 2.4 times faster: a 2048 × 1536 photo in 41 ms instead of 100 ms, against libwebp's 53 ms
  through SkiaSharp, and a 1920 × 1080 screenshot in 2.2 ms instead of 7.2 ms (libwebp 4.8 ms), on one thread. Alpha in
  lossy files, which is coded the same way, gains too.
- The fastest lossless encode (`Effort = 0`) is about 2.3 times faster on screen content: the screenshot in 15 ms instead
  of 35 ms, as fast as libwebp at its defaults, with a 27% smaller file. Higher efforts, and photos at effort 0, gain up to 20%.
  Files are byte for byte the same as before at every effort.

### Faster lossy WebP encoding

- Lossy WebP encodes about 1.5 times faster at the default effort: a 1920 × 1080 screenshot in 190 ms instead of 289 ms on
  one thread, and photos gain as much. Efforts 0 to 5 are 15% to 40% faster, effort 6 about 15%. The 4 × 4 mode search tries
  two modes at once with AVX2, the rate of a mode is only worked out when its distortion leaves it a chance, and the token
  costs and bits are computed with fewer branches. Files are byte for byte the same as before at every effort, with or
  without AVX2.

### Package links

- Every package's NuGet page now links to the documentation at [inexin.github.io/Egami](https://inexin.github.io/Egami/) as its
  project website, and its readme links to the guides there instead of the Markdown sources on GitHub.

## Egami.Fonts 1.1.0 (2026-10-22)

### Speed
- `Paragraph.Create` allocates about a third less for short text: a face's color formats are worked out once, glyph buffers
  are sized to their run, and the GSUB matcher keeps no unused scratch array.

## Egami.Drawing, Egami.Svg and Egami.Css 1.1.0 (2026-10-22)

- No changes of their own. From 1.1.0, Egami, Egami.Drawing, Egami.Fonts, Egami.Svg, Egami.Css, Egami.Heic and Egami.Cli share
  one version and release together, so these three move to 1.1.0 with Egami.

## Egami.Fonts.Substitutes 1.0.0 (2026-10-18)

A new opt-in package with open fonts that stand in for the fonts documents name most, so text keeps its line breaks on
machines that lack them. It replaces Egap.Fonts 1.0.0, which held the same fonts in the Egap repository:
`Egap.Fonts.StandardFonts` is now `Egami.Fonts.Substitutes.SubstituteFonts`, with the same members.

- Liberation Sans, Serif and Mono for Helvetica, Arial, Times, Times New Roman, Courier and Courier New, Carlito for Calibri
  and Caladea for Cambria, each in four styles, under the SIL Open Font License 1.1.
- `SubstituteFonts.Find` finds a face by family name or common alias in the nearest style; `SubstituteFonts.Collection` holds
  all twenty as a `FontCollection`, for text layout, Egami.Metafiles and Egami.Svg.

## Egami 1.0.0 (2026-10-18)

The first public release.

### Formats

- PNG and APNG: every color type and bit depth, interlaced, transparency, text, EXIF, XMP, ICC; writing with adaptive filtering,
  built palettes and changed-rectangle animation.
- JPEG: baseline and progressive, any subsampling, gray, CMYK and YCCK, scaled decode, pixels identical to libjpeg-turbo;
  writing at quality 1 to 100 with optimized Huffman tables, EXIF, XMP and ICC.
- GIF: every disposal, interlaced, transparency, loop count; writing with exact or quantized palettes (Wu's quantizer,
  Floyd–Steinberg or ordered dithering) and frame optimization.
- BMP: every Windows and OS/2 2.x header, 1 to 32 bits, RLE4, RLE8, RLE24, bit fields, embedded PNG and JPEG; writing 24 and
  32-bit and 1, 2, 4 and 8-bit palettes.
- TGA: color-mapped, true-color and gray, raw and RLE, all origins, the TGA 2.0 footer and extension area.
- QOI: the whole format, with output byte-identical to the reference encoder.
- PBM, PGM, PPM and PAM: plain and binary, any maxval.

### Operations

Resize, fit and cover with six filters, in encoded or linear light; crop, pad, flip, rotate, auto-orient; brightness, contrast,
saturation, hue, sepia, grayscale, invert, opacity and color matrices; Gaussian and box blur, unsharp mask and any convolution;
source-over drawing of whole images or areas. Chains are planned before they run and run in parallel bands of rows.

### Core

- Nine pixel types, and outside pixel types through `IPixelType<TSelf>`.
- Editable frames for animations; per-frame delays and loop counts.
- Typed EXIF reading and editing; XMP, ICC, text and resolution kept through edits and saves.
- Safety limits checked before any allocation; lenient or strict recovery from damaged files.
- Pooled pixel memory with live usage counts and opt-in leak tracking.
- Custom operations and formats through public interfaces, including animated formats.

### Diagnostics

- `EgamiContext.Diagnostics` takes an `Action<EgamiDiagnostic>` that receives Egami's warnings and notable decisions, so one line
  sends them to `ILogger`, Serilog or the console; Egami takes no logging dependency. Off by default and free: with no handler
  nothing is built, and a load or save allocates exactly what it would without diagnostics.
- Each `EgamiDiagnostic` has a level (`Warning`, `Info`, `Debug`; `EgamiContext.DiagnosticLevel` sets the lowest delivered), a
  source, a stable id such as `Png.ChecksumMismatch`, a message, and the byte offset and frame when known. Every id is listed in
  the diagnostics article.
- `RasterMetadata.Warnings` is an `IReadOnlyList<EgamiDiagnostic>`: every problem a lenient read recovered from, with its id. One
  id is kept at most 10 times per call, then counted; the handler gets the same list as each call ends.
- Saves report what they lose without being asked (alpha, depth, CMYK inks, frames, metadata the format cannot hold), and Info
  and Debug events say what Egami chose: the format detected, a scaled decode, conversion on load, writer settings, parallel
  bands, chain planning.
- `EgamiContext.StartDiagnostics()` and `EgamiInput.Diagnostics` let format plug-ins and other libraries report through the same
  handler, filter and cap; `EgamiInput.Warn` takes an id.

### Package icons

- Every package has its own icon on NuGet, and the docs site has the Egami logo and favicon. The icons, logos and favicons
  are in `branding/`.

### CCITT fax encoding

- `CcittFax.Encode` writes packed bi-level rows as Group 4 (ITU-T T.6) or one-dimensional Group 3 (T.4) with PDF's
  CCITTFaxDecode parameters, the inverse of `CcittFax.Decode`, so a PDF optimizer can store scans as G4. It is the encoder
  TIFF's compressions 3 and 4 already used; `Decode` gives the rows back bit for bit.

### Faster blur, shadow, lighting and emboss on 8-bit images

- `Blur(σ, …, BlurMethod.Fast)` and `Blur` above σ 100 on 8-bit images (Rgba8, Bgra8, Rgb8, Gray8 and the rest of the
  whole-byte types) run each axis as one streaming sweep, rows in vector lanes, in encoded or linear light: 1.4 to 2.5 times
  faster at one thread (a 4000 × 3000 photo at σ 20 in about 105 ms, at σ 60 about 120 ms). Results move by at most one level
  from 1.x; they are still the same at any thread count.
- `DropShadow` reads alpha from its byte and blurs its alpha plane the same way: σ 10 on a 1000 × 1000 logo in 10 ms at one
  thread, down from 48.
- `Lighting` takes the spot cone and specular shine from a power table and writes Rgba8 and Bgra8 bytes directly: on a
  2000 × 2000 logo a distant light takes 15 ms (was 27) and a specular spot 38 ms (was 70), within a level of before.
- `Emboss` reads and writes Rgba8 and Bgra8 bytes directly, twice as fast with the same pixels.

### FOGRA39 print profile

- `ColorProfile.Fogra39` is a built-in CMYK profile for offset printing on coated paper (FOGRA39L, ISO 12647-2, built for
  a 300% ink limit), so RGB to CMYK and back works without finding an .icc file. It is colord's free profile, built by
  ArgyllCMS from Fogra's data, shipped unchanged from CTAN (121 KB in Egami.dll, read on first use); `ToIcc` gives its bytes
  for embedding. Conversions through it match Little CMS 2 within 0.03% of ink and 0.01 in L*a*b*. CMYK files without a
  profile still convert by the plain formula.

### Printing with inks

- Inks: `Colorant` (process, spot or technical, with an `InkAppearance` in CMYK, sRGB or Lab for where it cannot be printed),
  `InkColor` (PDF's DeviceCMYK, Separation and DeviceN: `Cmyk`, `Spot`, `Mix`) and `InkSet` (a profile's process inks plus
  spots, at most 64; `FromProfile`, `WithSpot`, `WithDisabled`).
- n-colour ICC profiles (2CLR to FCLR) read as `ColorModel.NColor`, with `ChannelCount` and `ColorantNames`;
  `ColorTransform.Apply(ReadOnlySpan<float>, Span<float>)` converts to and from any channel count. Conversions match Little
  CMS 2.19 within 0.4 ΔE2000, its black point rules included.
- `InkRaster`: a page as 8 or 16-bit plates plus alpha (`InkPlate`, each also a gray raster over the same memory), saved and
  loaded as one separated TIFF with InkNames (`InkTiffOptions`); libtiff's tools read the files.
- `InkRaster.Proof` shows the plates on screen (spots add their CMYK equivalent to the process inks, as Ghostscript's and
  MuPDF's overprint simulation; within 1.1 ΔE2000 of Ghostscript's), optionally with the paper's tone (`ProofOptions`);
  `MeasureCoverage` gives the largest total ink, where it is, the share over a limit and each plate's coverage (`InkCoverage`).
- Fixed: a TIFF with an empty Deflate strip threw instead of loading the strip as empty.

### AVIF

- `AvifFormat` reads AVIF (AV1 Image File Format over HEIF) stills, registered by default. The AV1 decoder, written from the
  specification with libaom and dav1d as references, covers every intra coding tool, the loop filter, CDEF, loop restoration,
  superres and film grain at 8, 10 and 12 bits and every chroma format; its output is bit-exact against dav1d on libaom's and
  dav1d's test streams, the Argon conformance suite and the corpus.
- `Raster.Load` converts to RGB as libavif does (equal to avifdec sample for sample) in the natural pixel type: `Rgb8` or
  `Rgba8` for 8-bit files, `Rgb16` or `Rgba16` for 10 and 12 bits, gray types for monochrome. Alpha items (straight or
  premultiplied, which loads straight), grids (alpha grids and per-cell alpha included) and overlays (iovl) are supported;
  the clean aperture is cropped, and `irot` and `imir` become the orientation.
- `YuvRaster.Load` gives the decoded planes. ICC, CICP, HDR metadata (clli, mdcv), Exif and XMP are read; `AvifDetails`
  describes the AV1 configuration, color, alpha, grid and brands, and `AvifFilmGrain` the grain parameters.
  `AvifReaderOptions.FilmGrain` false loads the image without its grain.
- Tiles and the in-loop filters run on up to `MaxParallelism` threads with the same output at every thread count; rows stream,
  and `ReaderOptions.Region` decodes only the grid cells it touches. On all cores Load is faster than libavif with dav1d on
  large photos; on one thread it is about 2 to 3.5 times slower, which a later release works on.
- Gain maps (tmap items) load into `RasterMetadata.GainMap`, and `ReaderOptions.Hdr` renders the other rendition (from an SDR
  or an HDR base) within 2.9.1's tolerance of libavif's.
- `AvifDetails.Items` lists every item with its role (`AvifItem`, `AvifItemRole`), and `AvifImage.ReadItem` decodes thumbnails,
  depth maps and other auxiliary images on their own.
- Image sequences (avis) load every frame as an animation when all are key frames, else the first frame; layered images load
  their base layer. The rest of both waits for inter frames.

### JPEG XL

- `JxlFormat` reads JPEG XL (ISO/IEC 18181) in the box format or as a bare codestream, at Level 5 and 10, registered by
  default. Lossless Modular files (every predictor, the self-correcting one included, RCT, palette and delta palette,
  squeeze, 1 to 31-bit and float samples) decode bit for bit as djxl (libjxl 0.12.0) decodes them; lossy VarDCT files
  (every transform from DCT2x2 to DCT256x256, AFV, Gaborish, the edge-preserving filter, LF frames, upsampling, patches,
  splines and noise) within one 8-bit level of djxl, and every 18181-3 conformance file within its thresholds.
- Lossy color comes out in the file's own color space: enumerated spaces as djxl gives them, ICC profiles through Egami's
  color pipeline (where djxl gives linear sRGB), PQ and HLG kept with intensity_target as `HdrMetadata.MaxContentLightLevel`;
  the space is recorded on the raster. `JxlReaderOptions.LinearOutput` gives linear light in float instead. Pixel types come
  from the declared bit depth.
- Animations load through `Raster.LoadAll` as their blended frames with delays and loop count; `JxlImage.ReadLayers` gives
  the frames before blending (`JxlLayer`, `JxlLayerBlend`), `JxlImage.ReadPreview` the preview, and
  `JxlReaderOptions.MaxDownscale` 8 a lossy still's LF image alone (1:8, much faster).
- Alpha loads straight, CMYK as `Cmyk8` or `Cmyk16`, spot colors are mixed in (`JxlReaderOptions.RenderSpotColors` turns
  that off); `JxlDetails.ExtraChannels` lists every channel (`JxlExtraChannel`, `JxlExtraChannelKind`) and
  `JxlImage.ReadExtraChannel` reads one alone.
- Recompressed JPEGs load as pixels, and `JxlImage.ReconstructJpeg` gives the original JPEG back byte for byte from the jbrd
  box (`JxlDetails.HasJpegReconstruction`). A jhgm gain map loads into `RasterMetadata.GainMap`, and `ReaderOptions.Hdr`
  renders HDR from it as for Ultra HDR JPEGs.
- Exif, XMP and orientation are read, Brotli-compressed (brob) boxes included; JUMBF boxes are kept in `JxlDetails.Jumbf`.
  Damaged metadata or gain map boxes are dropped with a warning (`Jxl.BoxDropped`, `Jxl.BrobDropped`, `Jxl.ExifDropped`,
  `Jxl.GainMapDropped`).
- Groups decode on up to `MaxParallelism` threads with the same output at every thread count; `ReaderOptions.Region` on a
  still decodes only the groups it touches (`Jxl.RegionGroups` at Debug level counts them).
- Speed against djxl, threads matched: lossless within 1.5 times; lossy 1.4 to 2.3 times on one thread and 1.9 to 3 times on
  all cores; recompressed JPEGs further behind.
- `Raster.Save` writes lossless JPEG XL (`JxlWriterOptions`): every sample back exactly in the raster's own precision (8 or
  16-bit, half or single float, gray or RGB, alpha unless opaque everywhere), at `Effort` 1 to 7 on cjxl's scale, with files
  within 0.3% of cjxl's (libjxl 0.12.0) at the same effort and djxl decoding every one bit for bit. Effort 1 is libjxl's fast
  lossless path, 2 and 3 its fixed trees, 4 and up learn the context tree, 5 and up try local palettes and color transforms.
  The color space goes in enumerated when it is sRGB, linear, PQ or HLG, else as the ICC profile; EXIF and XMP go in boxes,
  the orientation in the codestream too. Encoding runs on up to `MaxParallelism` threads with the same file at every count,
  within 1.75 times cjxl's time on one thread and about as fast as cjxl on all cores.
- `Raster.Save` writes lossy JPEG XL too: VarDCT in XYB as libjxl 0.12.0's encoder makes it at efforts 1 to 7, with files
  within 5% of cjxl's size and Butteraugli score at the same settings and decoding in djxl. `JxlWriterOptions.Lossless` picks
  the kind; by default a JPEG XL source keeps its own and anything else is written lossy, as cjxl does for PNG. `Quality` is
  cjxl's `-q` (90, distance 1.0, by default) and `Distance` its `-d` (0.1 to 25); from distance 10 the image is coded at half
  size, as cjxl codes it. Effort 5 and up add adaptive quantization, chroma from luma and Gaborish, 6 the full block-size
  search and per-block edge-preserving filter strength, 7 patches for text and repeated shapes.
- Lossy files keep alpha exact (`AlphaDistance` codes it lossy through squeeze) and the color under transparent pixels
  (`KeepTransparentColors` false lets the encoder replace it, as libjxl does by default); CMYK keeps its black channel. sRGB,
  Display P3, Rec. 2020, PQ and HLG go in as enumerated spaces (HDR with its intensity target), other profiles as ICC, and a
  gain map as a jhgm box. `Progressive` writes cjxl `-p`'s LF frame and three HF passes, `PhotonNoiseIso` its film-like noise.
- Large lossy images are coded in 2048-pixel chunks as cjxl codes them, so a 16,384 × 16,384 image encodes in about 2.5 GB.
  Encoding runs on up to `MaxParallelism` threads with the same file at every count, within 1.9 times cjxl's time on all
  cores and 2.5 times on one thread.
- `JxlImage.RecompressJpeg` recompresses a JPEG into JPEG XL without loss, as cjxl does: the JPEG's coefficients become a
  VarDCT frame with chroma from luma, and a jbrd box keeps what rebuilding the file needs, so `ReconstructJpeg` (and djxl)
  give the original back byte for byte. Files come out 0.82 of the JPEGs' size, within 0.1% of cjxl's (libjxl 0.12.0), and
  decode in djxl to cjxl's pixels exactly. ICC goes in the codestream, Exif and XMP in Brotli-compressed boxes, the Exif
  orientation in the header. Arithmetic-coded, lossless, hierarchical, 12-bit and CMYK JPEGs, 4:1:1 sampling and two DRI
  markers are refused (`UnsupportedFormatException`), as cjxl refuses them. Each file is checked by rebuilding the JPEG from it;
  the few damaged JPEGs whose data does not re-encode exactly (which cjxl recompresses into files djxl cannot rebuild) are
  refused too.
- Fixed: `ReconstructJpeg` failed on scans holding some of the components (such as Cb and Cr together), and rebuilt JPEGs
  whose components all have the same sampling other than 1×1 (such as 1×2) wrongly; those files' pixels also lacked chroma
  from luma. It also wrote padding bits that are not all ones in the wrong order, and named quantization tables by list
  position where the JPEG used other slots.

### Raw CCITT fax data

- `CcittFax.Decode` decodes Group 3 and Group 4 fax data (ITU-T T.4 and T.6) as PDF's CCITTFaxDecode filter does, taking all
  eight of its parameters in `CcittFaxParameters`: K, EndOfLine, EncodedByteAlign (byte-aligned Group 4 included, which a TIFF
  wrapper cannot say), Columns, Rows, EndOfBlock, BlackIs1 and DamagedRowsBeforeError. It returns packed rows in PDF's bit sense.
  Every CCITT stream of a 4,000-file PDF corpus and of a generated matrix of every parameter combination decodes bit for bit as
  PDFium and pdf.js decode it, about six times faster than PDFium and two and a half times faster than PdfPig.
- `CcittFormat` loads the same data as a `Gray8` image with its parameters in `CcittReaderOptions`, with Probe, the row reader,
  regions and reductions. It has no signature, so detection never picks it.
- A damaged row ends decoding: the rows before it are kept, the rest are white, and `Ccitt.Damaged` warns (strict loading
  throws). With EOLs, up to DamagedRowsBeforeError bad rows are skipped first. TIFF and JBIG2's MMR regions use the same decoder.

### VP8 video frames

- `Vp8StreamDecoder` decodes VP8 video (RFC 6386) a frame at a time: inter frames with the last, golden and alternate references,
  motion vectors and split macroblocks, six-tap, bilinear and whole-pixel prediction, hidden frames and probability carry-over.
  Headers, modes, vectors and every frame equal libvpx 1.14.1's on its 59 test vectors and on clips made with its encoder in all
  four versions. `Decode` returns a `Vp8FrameInfo`; `CopyFrameTo` copies a shown frame into a 4:2:0 `YuvRaster` of the caller's.
- `ConcealErrors` fills the macroblocks of a frame whose data runs out from the previous frame and repeats the previous frame for
  one that cannot be read, instead of throwing `CorruptDataException`; `Vp8FrameInfo.IsConcealed` says which frames were.
- Four frame buffers from the context's memory source, macroblock rows in a wavefront on up to `MaxParallelism` threads; decoding
  takes at most 1.5 times libvpx's time at 720p and 1080p on one thread and on all cores.
- Lossy WebP decodes about 30% faster: the VP8 token loop keeps its decoder state in registers and the loop filter works in
  saturating 8-bit lanes, with the same pixels as before.

### VP8 decode speed

No API changes. Every frame decodes as before: equal to libvpx 1.14.1's on its 59 test vectors and the VP8 clips, and lossy WebP
equal to libwebp's.

- One-thread VP8 video decodes about 8% faster on the 1080p speed-gate clips and 20% faster on Egami.Video's film-grain clips.
  Against libvpx 1.14.1 (its SIMD build, one thread) it now takes 1.28–1.40 times libvpx's time on the 720p and 1080p gate clips
  and 1.43 times on the grain clips, down from 1.32–1.50 and 1.53–1.67. All-core decoding is no slower. Intra prediction fills
  whole rows with vector stores; one-thread decoding reconstructs a macroblock row before filtering it, as libvpx does, so
  inter macroblocks no longer keep copies of their edges; residuals are added two blocks at a time in 16-bit lanes; each
  coefficient's sign is decoded without a branch.
- One-thread video decoding allocates nothing per frame after the first key frame: the frame decoder and its arrays are kept
  from frame to frame (they took 85 KB a frame at 720p and 183 KB at 1080p).
- Lossy WebP decodes 9–18% faster from the same changes (a 2048 × 1536 photo in 25 ms instead of 29 ms on one thread).

### Radiance, OpenEXR and HDR gain maps

- `RadianceFormat` reads Radiance pictures (RGBE and XYZE, flat, old and new run-length scanlines, all eight scan orders) to
  linear `RgbF32`, bit for bit what stb_image decodes, and writes RGBE with run-length scanlines. `RadianceDetails` keeps the
  header; the brightest value is measured into `HdrMetadata.MaxContentLightLevel`.
- `ExrFormat` reads OpenEXR scanline, tiled and multi-part files with NONE, RLE, ZIPS, ZIP, PIZ, PXR24, B44, B44A, DWAA and DWAB,
  half, float and uint channels, layers and luminance-chroma images, equal to OpenEXR 3.5.2's decode (DWA within one half-float
  step) on the openexr-images set. `ExrDetails` lists parts, channels, layers, windows and every header attribute;
  `ExrReaderOptions` picks the part, layer and window, and `ExrImage.ReadChannel` loads one channel. A damaged chunk table is
  rebuilt (`Exr.ChunkTableRebuilt`), and blocks that cannot be read are left black (`Exr.BlocksDamaged`). The writer writes
  NONE, RLE, ZIPS, ZIP or PIZ in half or float, read back by OpenEXR to the same values.
- JPEG reads the CIPA Multi-Picture index: `JpegDetails.MultiPicture` lists the images with ExifTool's offsets and sizes,
  `Raster.Probe` counts them and `Raster.LoadAll` loads each.
- Gain maps: Ultra HDR, ISO 21496-1, Adobe hdrgm and Apple JPEGs load with `RasterMetadata.GainMap` (`GainMap`, `GainMapInfo`);
  `ReaderOptions.Hdr` (`HdrDecode`) or `GainMap.Apply` gives the HDR rendition for a display headroom, within about 1% of
  libultrahdr 2.0.2. JPEG saves write the map back with MPF, ISO 21496-1 and hdrgm metadata as Skia and libultrahdr write
  them (`JpegWriterOptions.KeepGainMap`); crops, turns, flips and resizes move it, pixel operations drop it
  (`Operation.GainMapDropped`), as do other formats (`Save.GainMapDropped`). `JpegWriterOptions.GainMap` and `GainMap.Create`
  (`GainMapOptions`, libultrahdr's defaults) write Ultra HDR from an HDR raster; libultrahdr decodes Egami's files to the same
  HDR image.
- Saving a floating-point raster with light above reference white to an 8 or 16-bit format reports `Save.HdrClipped`.
- Speed, single thread on Linux: Radiance decodes and encodes 1.3 to 1.9 times as fast as stb_image; Ultra HDR decodes 2 to 3
  times and encodes 1.1 to 1.7 times as fast as libultrahdr; OpenEXR NONE, RLE, ZIP, ZIPS and PIZ are level with the OpenEXR
  library or faster, PXR24, B44 and DWA 1.2 to 1.6 times slower. With all cores Egami is faster than OpenEXR on every
  compression but DWA.

### ICO and CUR

- `IcoFormat` and `CurFormat` read and write Windows icons and cursors. Entries are PNG files, decoded by Egami's PNG reader, or
  BMPs at 1 to 32 bits with palettes, bit fields and the AND mask, decoded by its BMP reader; a 32-bit entry whose alpha is 0
  everywhere falls back to the mask, as Windows does. Every entry of the corpus equals ImageMagick's and Pillow's decode.
- `Raster.Load` returns the best entry (the largest, then the deepest, as SkiaSharp picks but preferring depth among equal
  sizes); `MinimumSize` picks the smallest entry at least that big before reducing; `IcoReaderOptions.Entry` picks one by index;
  `Raster.LoadAll` returns every entry and `Raster.Probe` lists them (`ImageCount`, `IcoDetails.Entries`) without decoding.
- `IcoWriterOptions`: `Sizes` with `FaviconSizes` and `WindowsSizes`, resized from the largest raster with `Filter` and centred on
  transparency when not square, or taken as drawn; `Encoding` (null: PNG at 256, 32-bit BMP with an AND mask below; or all PNG
  or all BMP); `Hotspot` for cursors, scaled to each entry. Rasters over 256 are fitted with the `Save.IcoFitted` warning. Files
  open in ImageMagick, Pillow, SkiaSharp and Windows' WIC with the same pixels; decoding is 1.1 to 2.2 times SkiaSharp's speed
  and writing Windows' six sizes twice Magick.NET's.

### PSD and PSB

- `PsdFormat` reads the flattened image of Photoshop documents and large documents in every color mode (bitmap, gray, indexed,
  RGB, CMYK, Lab, duotone, multichannel) at 1, 8, 16 and 32 bits, raw, PackBits and Deflate with and without prediction.
  Transparency is divided back out of the white Photoshop flattens onto; CMYK loads as inks, Lab converts to sRGB, 32-bit files
  are linear. A file without a real composite loads its placeholder with the `Psd.NoComposite` warning; Egami does not
  composite layers.
- `PsdDetails`: version, `PsdColorMode`, depth, channels, `PsdCompression`, `HasRealComposite`, extra channel names, the layer
  records as `PsdLayer` (name, bounds, opacity, blend mode, visibility, clipping, `PsdLayerKind` and nesting; no pixels), and
  every image resource Egami does not map as `PsdResource`. ICC, resolution, EXIF, XMP and IPTC map to `RasterMetadata`.
- Rows stream from the channel planes, reading only what a region needs; layers are skipped without being read. Pixels equal
  ImageMagick's in every mode it reads alike and the written values everywhere; layers equal psd-tools', metadata exiftool's.
  12-megapixel files decode three to ten times faster than with Magick.NET.

### Comparing and measuring

- `Raster.Compare` returns MAE, MSE, RMSE, PSNR, peak error and the pixels that differ (ImageMagick's AE, with a fuzz) per
  channel and overall in one pass, on the 0 to 1 scale, with SSIM and DSSIM on request (Wang et al. 2004, 11 × 11 Gaussian).
  Colors are premultiplied by default, as ImageMagick 7's metrics are; `AlphaCompare.Straight` compares stored channels. Every
  metric is within 1e-6 of ImageMagick 7.1, AE identical, SSIM within 1e-4, and results do not depend on the thread count.
- `Raster.Difference` draws ImageMagick compare's highlight picture (identical at 8 bits) or |a − b| per channel.
- `Raster.Statistics` (min, max, mean, sample standard deviation, skewness, kurtosis, entropy per channel and luma, within 1e-6
  of identify -verbose), `Histogram` with `Percentile` and `Median`, `CountColors` (exact, as %k), `GetColors`, and `IsOpaque` and
  `IsGray` with early exit.
- `PerceptualHash.Dct` and `Difference`: 64-bit hashes within 4 bits of Python imagehash, with `Distance`; `Raster.Signature`, a
  SHA-256 of the pixels as 16-bit RGBA, equal for any pixel type of the same picture.

### Palettes and dithering

- The palette machinery behind the GIF, palette PNG, BMP and TGA writers is public: `Palette` (`FromColors`, `Build`,
  `FromRaster`, `Nearest`), `Palettes` (web-safe, black and white, grays, color cubes), `RasterOps.Quantize` and `Remap` keeping the
  pixel type, and `Raster.ToIndexed` returning an `IndexedImage`.
- `DitherMode` adds Atkinson, Burkes, Jarvis–Judice–Ninke, three Sierras, Stevenson–Arce, Stucki, Riemersma and Bayer 2, 4 and 16
  and 3 × 3 patterns; `PaletteOptions` adds `FixedPalette` (honoured by every writer, which throws when it has too many colors),
  `DitherStrength`, `Serpentine`, `RefineIterations` (k-means polish), `ExactMatching` and a custom `ErrorDiffusionKernel`.
  `Posterize` takes every mode. Defaults are unchanged: every file 1.x wrote stays the same byte for byte.
- `Raster.DominantColors`: the main colors with their shares, by k-means in Oklab seeded by Wu's palette, the same on every run.

### Animation tools

- `MergeDuplicates`, `DropZeroDelay`, `Retime`, `ChangeSpeed`, `Reverse` and `DrawAnimation` work on whole animations in `Apply`,
  `Derive` and `Transcode`. The first two equal ImageMagick's -layers RemoveDups and RemoveZero; `Retime` samples fixed ticks as
  ffmpeg's fps filter and keeps the total duration; `DrawAnimation` lays one animation over another by time, with a repeat count
  and start, or by index as -layers Composite (identical), checking `MaxFrames` before drawing.

### Tone and color

- Tone steps with ImageMagick's formulas and values from 0 to 1: `Level` (`-level` and `+level` in one), `Gamma`,
  `SigmoidalContrast`, `Posterize` (with ordered or Floyd–Steinberg dithering), `Solarize`, `Threshold` (luminance, each channel,
  saturation or chroma, with any two colors), `Colorize`, `Tint` and `Duotone`; Skia's `HighContrast`; and
  `SimulateColorBlindness` from Machado, Oliveira and Fernandes (2009) with a severity. Each is within one level of ImageMagick 7
  at 8 bits and two at 16; thresholds are identical. Tone steps, color matrices and filters next to each other run as one pass,
  as exact lookup tables on 8- and 16-bit images.
- `ColorFilter` holds a color adjustment as a value, as Skia's color filters: every tone step and color matrix, `Curves` of
  `ToneCurve`s (monotone curves through points, tables, functions), 3D tables, `Then` and `Lerp`. `RasterOps.ApplyFilter` runs one;
  `Apply` runs one on your own pixels; `ToLut3D` samples one into a table.
- `Lut3D` with tetrahedral (the default), trilinear and nearest reading and a 1D shaper; `CubeFile` reads and writes .cube files
  (Adobe 1.0 and Resolve's 1D-plus-3D form), strictly and within `SafetyLimits`, and has a fuzz target; `Lut3D.FromHald`, `ToHald`
  and `Raster.HaldIdentity` round-trip Hald CLUT images. Trilinear tables equal ImageMagick's `-hald-clut` within a level.
- Adjustments that measure the image first: `AutoLevel`, `ContrastStretch`, `Normalize`, `AutoGamma`, `AutoWhiteBalance`,
  `Equalize` and `AutoThreshold` (Otsu, triangle, Kapur). Inside a chain they measure what the steps before them made; inside
  `Within` and `OnChannels`, only the scope; across an animation, all frames at once unless `perFrame`. Their black and white
  points, gammas and levels equal ImageMagick's. `Raster.MeasureAutoLevel`, `MeasureContrastStretch`, `MeasureAutoGamma`,
  `MeasureWhiteBalance`, `MeasureEqualize` and `FindThreshold` return the filter or level without changing anything.
- `WhiteBalance` by a picked neutral color or by color temperature and tint (Bradford adaptation to D65), also as
  `ColorFilter.WhiteBalance`; `Clahe` (Zuiderveld's
  CLAHE on L*, within the closeness gate of ImageMagick's `-clahe`); `LocalContrast` (ImageMagick's `-local-contrast`, within a
  level); and `AdaptiveThreshold` (Bradley and Roth) for unevenly lit pages.

### TIFF

- `TiffFormat` reads classic TIFF and BigTIFF: strips and tiles, chunky and planar, 1 to 64-bit integer and float samples, in
  gray, palette, RGB, CMYK and other inks, YCbCr, CIELab, ICCLab, ITULab and LogLuv. Codecs: LZW, Deflate, PackBits, CCITT RLE,
  Group 3 and Group 4, JPEG and old-style JPEG, WebP, ThunderScan and SGILog, with both predictors and either fill order. Every
  file in the test corpus and in libtiff's sample images decodes as libtiff does. Damaged files and IFD loops throw
  `CorruptDataException`, within `SafetyLimits`.
- `Raster.Load` reads the first full-resolution page, or `TiffReaderOptions.Page`; a `MinimumSize` read takes a SubIFD or reduced
  page of exactly the right size instead of decoding full size. Rows stream for strip and tile files alike. Exif, GPS, IPTC, XMP,
  ICC and resolution come from the page's tags; tags Egami doesn't model are kept in `TiffDetails.OtherTags` and written back.
- `TiffWriterOptions` writes uncompressed, LZW, Deflate, PackBits, CCITT Group 3 and 4, JPEG (gray and RGB) and WebP, with
  predictors, strips or tiles, classic or BigTIFF (`Layout`), and `Reduce` for the smallest exact form (1-bit, gray, palette).
  Each pixel type is written at its own depth. A TIFF source's compression, predictor, layout and byte order are kept unless set;
  a lossy compression is kept only while the pixels are unchanged. `ParallelCompression` compresses strips and tiles on up to
  `MaxParallelism` threads, with the same bytes. libtiff,
  ImageMagick and Pillow read them all. JPEG compression takes CMYK too, written as libtiff writes separated JPEG.
- JPEG XL strips and tiles (compression 50002, and DNG 1.7's 52546 for previews and other pages that are not camera RAW) are
  read through Egami's JPEG XL decoder: chunky gray, RGB and YCbCr pages of 8 or 16-bit or float samples, lossless or lossy,
  each unit as the same codestream decodes alone (`TiffCompression.JpegXl`, `TiffCompression.DngJpegXl`, read only).

### WebP

- `WebpFormat` reads and writes WebP: lossless (VP8L), lossy (VP8) with or without alpha, and animated, with no native code.
  Lossless files decode to libwebp's pixels exactly; lossy files to libwebp's YUV planes sample for sample and to RGB within one
  level (`YuvRaster.Load` returns the planes); animations compose every frame as libwebp's animation decoder does. Every kind
  streams through `RasterDecoder`, incrementally too, and an animation holds one canvas whatever its frame count.
- `WebpWriterOptions`: `Lossless` (by default a WebP source keeps its kind and anything else is lossy), `Quality` and `Effort` with
  cwebp's meanings, `AlphaQuality`, `KeepTransparentColors`, `SharpYuv`, `NearLossless`, `MixedFrames` and `FirstFrameOnly`.
  Animations store changed rectangles; a GIF or APNG converts with Load and Save.
- `WebpReaderOptions.UseBackgroundColor` starts animations from the ANIM color, and `FastUpsampling` repeats chroma samples.
  `WebpDetails` and `WebpFrameDetails` say what a file held and keep unknown chunks (`WebpChunk`, `WebpChunkPosition`) for round
  trips. ICC, EXIF and XMP are read and written; the resolution travels in EXIF.

### JBIG2

- `Jbig2Format` reads JBIG2 (T.88): generic, refinement, text, symbol dictionary, pattern and halftone segments, arithmetic and
  Huffman coded, with MMR and custom code tables, from standalone files (sequential or random access) and from the embedded
  streams PDF holds, with `Jbig2ReaderOptions.Globals` (`Jbig2Globals.Parse`) and `Page`. Every page in the test corpus decodes
  bit for bit as PDFium and jbig2dec do, two to seventeen times faster than PdfPig. Pages load as `Gray8` and stream a stripe at
  a time; `Jbig2Details` says how a page was coded. Damaged regions are skipped with a warning; decoding stays within
  `SafetyLimits`.
- `Raster.Load`, `LoadAll`, `Probe` and `RasterDecoder.Open` take a `FormatInfo` to read data in a known format without
  detection, as PDF's JBIG2 streams need.

### JPEG 2000

- `Jpeg2000Format` reads all of JPEG 2000 Part 1 (T.800): JP2 and JPX files and raw codestreams, including a PDF's JPXDecode
  streams, which load with plain `Raster.Load`. Every progression order and progression change, every code-block style, tiles,
  tile-parts, precincts, packed headers, ROI, the reversible 5/3 and irreversible 9/7 wavelets, RCT and ICT, subsampled and
  signed components. Every ISO/IEC 15444-4 conformance codestream decodes within its tolerances: reversible ones identical to
  OpenJPEG 2.5.4, irreversible ones within one level, and the same at reduced resolutions.
- Palettes, channel definitions, sYCC and CIELab (converted to sRGB), ICC profiles (kept in the metadata), XMP and resolution are
  read; `Jpeg2000ReaderOptions.Components` returns the codestream's components as stored instead, for a PDF that names its own
  color space; `MaxQualityLayers` decodes the first layers only; `FastUpsampling` repeats subsampled chroma as OpenJPEG does.
  Subsampled sYCC also loads into a `YuvRaster`. `Jpeg2000Details` says how the codestream was coded.
- `MinimumSize` drops whole wavelet levels, `Region` decodes only the tiles and code-blocks it needs, and rows stream a row of
  tiles at a time. Tier 1 runs on every core; at one thread lossy files decode faster than OpenJPEG through Magick.NET, and at
  all cores every file decodes two to four times faster. Part 2 coding tools and High-Throughput code-blocks throw
  `NotSupportedException` naming the feature.

### Several images in one file

- `Raster.LoadAll` and `LoadAllAsync` return a `RasterList` of every image in a file, each at its own size and pixel type;
  single-image formats give a list of one. The list disposes its rasters; `Detach` keeps one. `RasterInfo.ImageCount` says how
  many there are without decoding.
- `Raster.SaveAll` and `SaveAllAsync` write several rasters as one multi-page TIFF; a format that holds one image takes one.

### Geometry

- `Transform` takes an affine `Matrix3x2`, a perspective `Homography` or a `Matrix4x4` projected as CSS `matrix3d` draws a flat
  element; `Skew` and `QuadDistort` build one. `Homography` is a double-precision 3 × 3 matrix with `FromQuad`, the usual factories,
  `TryInvert` and `Map`. `TransformOptions` chooses the filter, the color space, the edges (transparent, clamp, mirror, wrap), the
  background and a viewport; without one the result is the transformed bounding box.
- Where a transform shrinks or tilts the image, each pixel averages the ellipse its footprint covers (elliptical weighted
  averaging, capped at 32 source pixels), within 40 dB of ImageMagick's `-distort`; elsewhere it samples exactly as `Rotate` does.
  A pure scale equals `Resize` and a pure rotation equals `Rotate`, pixel for pixel.
- `Warp` runs any `IWarp` (or `Warp.FromFunction`). `Warps` holds ImageMagick's Barrel, BarrelInverse, Polar, DePolar, Arc, Swirl,
  Wave and Implode, with its parameters, whose points land where ImageMagick's do, and Skia's Magnifier. `Displace`, `Offset` and
  `Tile` move pixels by a map, by whole pixels, and in repeats.
- `Trim` and `EntropyCrop` crop to the content, `Deskew` straightens a page, and `Raster.TrimBounds`, `Raster.EntropyBounds` and
  `Raster.DetectSkew` measure without changing anything. Their size depends on the pixels: the chain runs up to them, measures every
  frame and plans the rest from the real size. Trim's rectangles equal ImageMagick's `-trim -fuzz` at 8 and 16 bits.
- `ResampleFilter` gains `Lanczos2`, `Hermite`, `Gaussian` and `Spline`, with ImageMagick's definitions, everywhere a filter is taken.
- `MipChain` makes many sizes from one image fast, each from the smallest halved level at least twice its size.
- `Raster.Append`, `Raster.Grid` and `SpriteSheet.Pack` build strips, contact sheets and packed sprite sheets.

### Planar YUV

- `YuvRaster<TSample>` holds a picture as Y, U and V planes and an optional alpha plane: 4:2:0, 4:2:2, 4:4:4 and 4:0:0, 8 bits
  in bytes, 10, 12 and 16 bits in `ushort`s. `YuvColor` states the matrix (`YuvMatrix`), range (`YuvRange`) and chroma siting
  (`ChromaSiting`), with presets and ITU-T H.273 codes (`YuvColor.TryFromH273`, `ColorCodePoints.TryGetYuvColor`). `Create` allocates the planes in one block from the memory source;
  `Wrap` takes a decoder's planes with strides, without a copy; `YuvPlane.AsGray8` views an 8-bit plane.
- `ToRaster`, `CopyTo`, `FromRaster` (with an alpha plane only when asked) and `CopyFrom` convert to and from every pixel type, within one level of the exact H.273
  result, with bilinear chroma by siting (or nearest and fast with `YuvConvertOptions`), in SIMD on every core. A repeated
  `CopyTo` or `CopyFrom` allocates nothing. 1080p 4:2:0 to `Rgba8` takes 0.5 ms on 24 cores.
- `CopyFromPacked` and `CopyToPacked` copy NV12, NV21, P010, P016, YUYV and UYVY frames (`YuvPackedFormat`).
- `YuvRaster.Load` and `Probe` give a JPEG's own planes, identical to libjpeg-turbo's raw output, also at reduced scale; saving
  JPEG-colored planes encodes them straight, with libjpeg-turbo's bytes. `Save` writes any other format through a raster.
- `Crop` (on chroma boundaries), `Resize` and `ResizeTo` (into an existing raster) work on the planes.

### Blending

- `BlendMode` names 31 modes: the twelve Porter–Duff operators, Plus, Modulate, the W3C separable and non-separable blend modes
  (Multiply to Luminosity), Add and Subtract. `DrawImage` takes a mode, an opacity and a color space (stored values by default,
  linear light as an option); only the overlap changes. `SourceOver` on stored values gives exactly what 1.0's `DrawImage` gives.
- On 8-bit rasters the Porter–Duff operators, Plus, Modulate and the product-and-sum modes are exact integer math, equal bit for
  bit to the W3C formulas rounded to nearest; ColorDodge, ColorBurn, SoftLight and the non-separable modes are within a level,
  with alpha exact. Every mode is within a level of SkiaSharp (two for Difference and Exclusion, where Skia rounds).
- `BlendColor` blends one color over every pixel (tint with Multiply, colorize with Color). `DrawArithmetic` draws with SVG's
  arithmetic composite, k1·S·D + k2·S + k3·D + k4.
- `Compositor.Blend` blends spans of any pixel type, with per-pixel coverage for antialiased edges or one color through coverage,
  for custom operations and rasterizers. It allocates nothing once warm and runs on many threads at once.
- `Compositor.Blend` also takes an `RgbaF32` color, kept at full precision on float and 16-bit rows. `Raster.Context` is public, so
  code outside Egami (a canvas) uses a raster's memory source and `MaxParallelism`.
- Fixed: a one-pixel straight `Rgba8` or `Bgra8` source row blended as if premultiplied (Multiply of a half-transparent gray gave
  178 where 139 is right).

### Filters

- Fixed: `BoxBlur`, and `Blur` above σ 100, could differ by a level in a few pixels between one thread and many. Their running
  sums now start at fixed rows and columns, so the pixels are the same at any thread count. `Blur` above σ 100 runs its three
  boxes in one pass, without rounding between them: about three times faster on many cores, and a third faster on one.

### Paints and layers in core

- `RasterOps.Reach` and `OperationReach`: how far a chain reads past each pixel (blurs, convolutions, morphology, glows, drop and
  inner shadows; zero for pixel-by-pixel steps; `null` when a step reads the whole image), so a chain can run on a padded part of
  a picture. Egami.Drawing's layers use it to grow before a filter.
- `RasterOps.InnerShadow`: a shadow inside the alpha shape, cast by everything outside it, as an editor's inner shadow layer
  style; exactly the chain written by hand.

### Regions and masks

- `Within(Rect, …)` runs any same-size chain so that only pixels inside the rectangle change. Filters read the real pixels
  around it, so a blurred rectangle has no seam; flips mirror the rectangle; custom operations work unchanged. Steps that change
  the size or the color encoding throw when the chain is recorded, naming the step.
- `Within(Mask, …)` mixes the chain's result with the original by a `Mask`'s coverage: any raster's gray value, alpha or
  luminance (`MaskChannel`), placed at an offset and optionally inverted. Coverage 0 and full are exact; 16-bit masks keep their
  gradient on 16-bit images. Scopes nest.
- `ExtractChannel` returns one channel (`ColorChannel`) as a gray raster and `CombineChannels` builds an image from gray rasters,
  both identical to ImageMagick's -separate and -combine at 8 and 16 bits. `ShuffleChannels` rearranges channels (`ChannelSource`)
  as one color matrix that fuses with the other color steps. `OnChannels` keeps a chain's result only in chosen channels
  (`ColorChannels`), as ImageMagick's -channel does.
- `AlphaToGray`, `LuminanceToAlpha`, `CopyAlpha` and `Flatten` match ImageMagick's -alpha extract, copy and remove; the JPEG
  writer's Background option now runs on Flatten's arithmetic, with unchanged bytes.

### Keying and arithmetic

- `MakeTransparent`, `ReplaceColor` and `FloodFill` equal ImageMagick's -transparent, -opaque and -floodfill with -fuzz at 8 and
  16 bits; the tolerance is ImageMagick's color distance, and `MakeTransparent`'s softness ramps alpha for clean edges. `FloodFill`
  is four-connected by default (`Connectivity`). `ColorMask` and `FloodMask` return selections for `Within(Mask, …)`.
- `Arithmetic` applies a `PixelMath` operator with a constant, identical to ImageMagick's -evaluate at 8 and 16 bits.
  `Raster.Stack` combines several rasters by mean, median, min or max (`StackMode`) a few rows at a time.

### Streaming

- `RasterDecoder`: open an image and read it a band of rows (`ReadRows`) or a frame (`ReadFrame`) at a time, in any pixel
  type. `RasterInfo.Access` says whether a file streams or is decoded into a buffer first, and `WorkingBytes` what it holds.
- `ReaderOptions.Region` decodes only a rectangle, for every format, exactly as Load then Crop gives it.
- `Raster.LoadInto` decodes into a raster the caller already has.
- PNG, APNG, JPEG, GIF, BMP, TGA, PNM and QOI stream: `RasterDecoder` holds a few rows (or, for animations, one canvas) instead
  of the whole image. Interlaced PNG, progressive JPEG and bottom-up BMP and TGA on streams that cannot seek are buffered at the
  cheapest stage.
- `RasterDecoder.OpenIncremental`, `Append` and `EndOfInput` decode while bytes are still arriving; `Status` (`DecodeStatus`) and
  `HeaderRead` say how far it has got, and `RenderPreview` draws an interlaced PNG, a progressive JPEG or a GIF frame as far
  as it has arrived. Every 1.x format decodes as its bytes arrive, except files that are decoded into a buffer first.
- `LoadAsync` from a stream that cannot seek, such as an upload, decodes it in 256 KB chunks as it arrives instead of reading the
  whole file into memory first; results are the same. Seekable streams, plugins, progressive JPEG and files decoded into a buffer
  first keep 1.0's faster buffered path. `RasterDecoder.OpenAsync` with `ReadRowsAsync`, `ReadFrameAsync` and `NextFrameAsync`
  reads rows and frames from an asynchronous stream.
- `RasterEncoder.Create` with `WriteRows`, `WriteFrame` and `Complete` (and their async forms) writes an image a band of rows or
  a frame at a time, with the bytes `Save` writes. Options that need every pixel first throw `NotSupportedException` at
  `Create`. `PngWriterOptions.FrameCount` declares an APNG's frame count for streams that cannot seek. `SaveAsync` now writes
  each band out as it is encoded instead of holding the whole file.
- `Raster.Transcode` and `TranscodeAsync` decode, run a chain of streamable operations (crop, the resize family, horizontal flip,
  per-pixel color adjustments) and encode, a band of rows at a time. A leading resize shrinks on load and a leading crop
  becomes a region; other operations throw `NotSupportedException`. A 20,000 × 20,000 PNG to a 1,024-pixel JPEG peaks at
  0.4 MB of pixels.
- Plugins can stream: `IRowFormatReader` with `RowReader<TNatural>`, and `IRowFormatWriter` with `RowEncoder<TPixel>` and
  `EncodeStart<TPixel>`. Formats without them keep working through the buffered fallback.
- A 32-bit BMP whose alpha byte is zero everywhere now loads into 16-bit and premultiplied pixel types without a lossy round trip
  when the stream can seek.
- Behaviour change: `ReaderOptions.MinimumSize` now reduces every format, by the largest whole factor that keeps the image at
  least that big, averaging blocks of pixels. JPEG keeps its 1/2, 1/4 and 1/8 IDCT scaling and averages blocks beyond 1/8.
  The average is bit-identical to a full decode and a box resize.
- `ConvertColorTo` applies to streamed reads too, with the same HDR tone mapping as `ConvertColor`.

### Metadata

- IPTC: `IptcData`, `IptcEntry` and `IptcKey` read and write IPTC-IIM datasets, with typed getters for keywords, caption,
  headline, bylines, copyright, credit and the creation date. Text follows the coded character set (1:90), and Egami sets it to
  UTF-8 whenever a value is not ASCII; `Set` and `Add` enforce the IIM length caps. `RasterMetadata.Iptc` holds it, parsed on
  first use.
- XMP: `XmpData` parses a packet into properties by namespace URI and local name (`XmpName`, `XmpNamespaces`), each text, an
  `XmpArray` (bag, sequence or language alternatives) or an `XmpStruct`, with typed properties for the common Dublin Core,
  XMP Basic, Photoshop and TIFF values. `RasterMetadata.XmpData` is a live view of `RasterMetadata.Xmp`: an edit is what every
  format saves. DTDs are refused and nothing outside the packet is read.
- EXIF: the Windows XP tags (`ExifKey.XPTitle` and four more) and the encoded strings (`UserComment`, `GpsProcessingMethod`,
  `GpsAreaInformation`) read and write as text. `ExifData.ReadThumbnail` decodes the IFD1 thumbnail; `SetThumbnail` makes one
  from any raster.
- Behaviour change: JPEG keeps APP13 (`JpegDetails.PhotoshopResources`, with the IPTC) through load and save; 1.0 dropped it.
  After an IPTC edit the other Photoshop resources are written back unchanged and the IPTC digest is kept current.
- Behaviour change: PNG reads its XMP iTXt chunk and the legacy "Raw profile type" chunks (exif, APP1, iptc, xmp) into `Xmp`,
  `Exif` and `Iptc` instead of `Text`, and writes `Xmp` (1.0 ignored it) and `Iptc` (as "Raw profile type iptc").
- Behaviour change: flips, turns, rotation and auto-orient drop the IFD1 thumbnail, which would show sideways; crops and resizes
  already did.
- Behaviour change: `ExifEntry.AsString()` returns readable text for the XP tags and encoded strings instead of their raw bytes.
- Behaviour change: geometry operations move EXIF `SubjectArea` and `SubjectLocation` with the picture, and keep the XMP copies
  of the orientation, size and subject in step.

### Color management

- `ColorProfile.FromIcc` reads RGB, gray and CMYK ICC profiles, v2 and v4, matrix-shaper and table based; `ToIcc` writes
  v4.3 (v4.4 with cicp for HDR encodings). `IccData` and `IccTag` read any profile's header and tags.
- `ColorTransform` converts spans of any pixel types between two profiles under the four rendering intents, with black point
  compensation. It is compiled once and cached per `EgamiContext`: a matrix-shaper pair runs as curves, one matrix and curves
  with SIMD, anything else through a 33³ or 17⁴ grid. It matches Little CMS 2 within 0.0005 in float and one level in
  8 bits on 13,040 matrix transforms, and within ΔE2000 0.3 on table and CMYK ones; Display P3 to sRGB runs about 1.8× as
  fast as SkiaSharp.
- `RasterOps.ConvertColor`, `Raster.ConvertColor<TTo>` and `ReaderOptions.ConvertColorTo` convert images, the last while
  decoding, a band of rows at a time. `WriterOptions.OmitSrgbProfile` leaves out an embedded profile that is just sRGB.
- `RasterMetadata.CodePoints`, `Primaries` and `Hdr` (`HdrMetadata`) hold format-neutral color tags: PNG cICP, cHRM, gAMA,
  mDCV and cLLI and BMP endpoints are read into them and written from them. `GetColorProfile` resolves what the pixels mean.
- HDR: PQ and HLG convert to SDR with tone mapping (`ToneMapping.Bt2390` by default, `Reinhard`, `Clip`) and a hue-keeping
  gamut clip; `RasterOps.ToneMap` tone maps in place.
- Color values: `HslColor`, `HsvColor`, `XyzColor`, `LabColor` (with `DeltaE2000`), `LchColor`, `OklabColor`, `OklchColor`,
  `YCbCrColor` and `CmykColor`, all `IColorValue<TSelf>`; `ColorConvert` converts single values and whole rows. `YuvMatrix` and `YuvRange` name the Y'CbCr coefficients.
- Behaviour change: writers now emit the color tags held in `CodePoints`, `Primaries` and `Hdr` (PNG cICP, cHRM, mDCV, cLLI;
  BMP endpoints), which 1.0 left out.
- Behaviour change: operations that work in linear light (`ColorSpace.Linear`) now use the raster's own transfer curve, so a
  gamma 2.2 or Rec. 709 image is linearized with its curve rather than sRGB's.
- New enums: `RenderingIntent`, `IccProfileClass`, `ToneMapping`, `YuvMatrix`, `YuvRange`.

### Pixel types

- `Rgb16` and `GrayAlpha16`: 16-bit color without alpha and 16-bit gray with alpha, with exact conversions and SIMD to and
  from `Rgb8` and `GrayAlpha8`.
- `Argb8` and `Abgr8`: alpha-first byte orders, with SIMD byte shuffles to and from `Rgba8` and `Bgra8`.
- `RgbaF16`, `RgbF32` and `GrayF32`: half-float color, and float color and gray without alpha. Half conversions round as
  `(Half)f` does, with SIMD kernels identical to the casts for every one of the 2^32 float and 65,536 half values.
- `Bgr565`, `Bgra4444` and `Rgb10A2`: packed 16 and 32-bit color in the DXGI and Vulkan layouts. Widening and narrowing both
  round to nearest, so every route gives the same pixel and every packed value round-trips; SIMD kernels convert 5-6-5 to
  and from `Rgba8` and `Bgra8`.
- `Cmyk8` and `Cmyk16`: ink amounts, 0 meaning none, converted to and from RGB with the plain formula until color profiles
  arrive. `Raster.Load<Cmyk8>` of a CMYK or YCCK JPEG returns the file's inks without the RGB step. Resize, blur, sharpen,
  convolution and rotation filter the four inks, so black stays black; `DrawImage` blends inks; color adjustments go through
  RGB and rebuild black.
- `PremulRgba8`, `PremulBgra8` and `PremulRgbaF32`: color stored premultiplied by alpha. Conversions take and return straight
  colors (exact rounding both ways, SIMD for the 8-bit pairs, every stored pixel round-trips); resize, blur, sharpen,
  convolution, rotation and `DrawImage` run on the stored values directly. On translucent images resize is about 30% and
  `DrawImage` about 35% faster than on `Rgba8`.
- `Alpha8`: an alpha-only mask. It reads as black with that alpha; converting a color to it keeps only the alpha.

### PFM and more direct pixel types

- PFM float maps (PF and Pf) in the Netpbm family: both byte orders, loaded as `RgbF32` or `GrayF32` in linear light, written
  little-endian; `.pfm` saves as PFM, and `PnmDetails` gains `Scale` and `LittleEndian`. New enum value `PnmKind.FloatMap`.
- PNG and PNM read and write `Rgb16` and `GrayAlpha16` directly, and BMP reads 5-6-5 and 4-4-4-4 bit fields straight into
  `Bgr565` and `Bgra4444`, with no intermediate type. Natural types (what `Raster.Load` returns without a type) are unchanged.

### PNG writing

- `PngWriterOptions.Interlace` writes Adam7 interlaced PNG and APNG, for every color type, bit depth and palette. Off by default,
  and not taken from an interlaced source. A streamed encode holds the rows until the last one.
- `PngWriterOptions.ParallelCompression` compresses images with more than 4 MiB of image data in fixed 1 MiB bands on up to
  `MaxParallelism` threads. The bytes are the same at any thread count; files were 0.1% to 0.3% larger, and a 24-megapixel
  photo saved 8 times as fast on 12 cores. Off by default; with it off, or below the threshold, files are unchanged.

### JPEG reading

- Arithmetic-coded files (SOF9 and SOF10, sequential and progressive, with DAC conditioning) load like Huffman-coded ones, with
  the same pixels as libjpeg-turbo's djpeg, at every scale and when streamed. `JpegDetails.Arithmetic` says which a file is.
- 12-bit files (extended and progressive, Huffman or arithmetic) load in their natural 16-bit types, `Gray16`, `Rgb16` or
  `Cmyk16`, each sample scaled from 0..4095 to the full range; `Load<Rgba8>` and other 8-bit types round them. Every corpus file
  matches djpeg at 12 bits exactly, including at 1/2, 1/4 and 1/8 scale. `JpegDetails.Precision` gives the bits per sample.
- Lossless files (SOF3, and SOF11 with arithmetic coding) at 2 to 16 bits, every predictor and point transform, decode to
  exactly the samples djpeg gives (Thomas Richter's libjpeg for arithmetic coding, which libjpeg-turbo lacks): up to 8 bits as
  `Gray8` or `Rgb8`, deeper as `Gray16`, `Rgb16` or `Cmyk16`. They have no DCT, so a smaller size comes from the general
  reduction after a full decode. `JpegDetails.Lossless` says so. Hierarchical files are still refused.
- Large files decode on several cores, with the same pixels at every `MaxParallelism`. A sequential file with restart markers is
  split at the markers already in it; the scans of a progressive file run as a pipeline, each refinement scan following the
  scan before it row by row, and the last pass is split by rows. The 4000 × 3000 progressive benchmark photo decodes in half the
  time on 8 cores; the same photo with a marker every MCU row in under half. Files without markers decode as before.

### JPEG writing

- `JpegWriterOptions.Progressive` writes progressive files with libjpeg's standard scan script (ten scans for color, six for
  gray) and Huffman tables optimized per scan. They decode to exactly the baseline file's pixels and are the same bytes as
  libjpeg-turbo's `cjpeg -progressive` at the same quality and sampling. The quantized coefficients are held until the last
  scan, about 2 bytes per sample, within `SafetyLimits.MaxTotalBytes`; a streamed encode buffers. Off by default, and a
  progressive source is re-saved as baseline unless it is set.
- `JpegColorType.Cmyk` and `JpegColorType.Ycck` write four components behind an Adobe marker, inverted as Photoshop writes CMYK;
  `Auto` picks CMYK for `Cmyk8` and `Cmyk16` pixels, which 1.x converted to RGB on save. YCCK subsamples its chroma and keeps
  black at full size. djpeg, ImageMagick and Pillow read every file to within a level of Egami's inks. 12 MP encodes in 95 ms as CMYK
  and 91 ms as YCCK at one thread.
- `JpegWriterOptions.Precision = 12` writes 12-bit files (SOF1, or SOF2 when progressive) that keep the detail of 16-bit and
  float pixels, with optimized Huffman tables and 16-bit quantization tables where quality needs them. djpeg reads them within a
  level of Egami's decode; browsers, Pillow and many ImageMagick builds don't read 12-bit JPEG at all.
- TIFF's JPEG compression takes CMYK pages (see TIFF above).
- Arithmetic and lossless JPEG are still read only.

### Lossless JPEG transforms

- `JpegTransform.Apply` rotates (90, 180, 270), flips, transposes and transverses JPEG files on their DCT coefficients, as
  `jpegtran` does, so nothing is lost; `JpegTransformOperation.AutoOrient` turns a photo upright by its EXIF orientation and
  resets the tag. `Crop` keeps an area (its corner moved to the MCU grid), `Grayscale` keeps luma alone, and `Edges` says what
  happens to partial MCUs at a mirrored edge: `Trim` (the default, `-trim`), `Strict` (`-perfect`) or `Keep` (jpegtran's
  default). Every mode the reader takes works except lossless JPEG; arithmetic sources are written with Huffman coding.
- Results decode to the same pixels as jpegtran 3.0's on every chroma sampling, gray, CMYK, progressive, arithmetic and 12-bit
  sources, with partial MCUs on both edges, for every operation, edge mode, crop and gray conversion.
- Output is sequential with optimized Huffman tables by default, or progressive. EXIF, ICC, XMP, IPTC and comments are copied;
  EXIF's pixel dimensions follow the result and its thumbnail is turned too (dropped after a crop). A file can be transformed in
  place. The coefficients are held while it runs, within `SafetyLimits.MaxTotalBytes`.
- A 12-megapixel photo turns faster than jpegtran: 170 against 305 ms with optimized tables, 130 against 246 ms with standard
  ones, 260 against 458 ms written progressive.

### Pixel layouts

- New enum values, for code that switches on them: `PixelChannelOrder.Argb`, `Abgr`, `Alpha` and `Cmyk`;
  `PixelChannelType.Float16` and `Packed`; `AlphaKind.Premultiplied`.
- `PixelLayoutInfo` describes packed layouts such as 5-6-5: a constructor taking the pixel size and channel widths, and the
  `BitsPerPixel` and `ChannelBits` properties. `BytesPerPixel` and `BitsPerChannel` are unchanged for every 1.0 type.
- `IPixelType` gains `FromRgba16` and `ToRgba16` with default bodies, so types written against 1.0 keep compiling.
  Conversions between integer types wider than 8 bits now go through `Rgba16` and are exact; results for the 1.0 types are
  unchanged.
- Writers and color operations handle unknown, packed and premultiplied layouts: PNG and PNM write 8-bit samples for packed
  types no wider than 8 bits, and an unknown layout without alpha is written as RGB. Color operations never read premultiplied
  bytes as straight values.

## Egami.Drawing 1.0.0 (2026-10-18)

The first public release. The API is frozen (PublicAPI.Shipped.txt): later 1.x versions only add to it.

### Canvas, paths and shapes

Over Egami's public API only, it draws antialiased paths, shapes, strokes, clips and images on a canvas. See the
[Drawing guide](docs/articles/guides/drawing.md). Text comes with Egami.Fonts, below.

- `Canvas` is abstract: public calls are checked and tracked (transform, clip bounds, save stack) and each ends in one protected
  hook, so other backends (a PDF page, a recorder) see shapes and pens as data. `RasterCanvas<TPixel>` draws into any pixel type;
  `raster.CreateCanvas()` and the `Draw` operation step (per frame, inside `Within`) are the ways in.
- `Path` (immutable lines, quadratics and cubics with a `FillRule`) and `PathBuilder` with SVG endpoint arcs, tangent arcs, rectangles,
  round rectangles with per-corner radii (`RoundRect`, scaled as CSS does), ovals and polygons; `Contains`, `TightBounds`,
  `IsConvex`, `Reverse` and `Transform` by a matrix or a homography.
- Exact-area antialiasing: every pixel gets its covered area to 1/255, abutting shapes leave no seams, and results are identical at
  any core count. Fills are within a mean level of SkiaSharp and closer than SkiaSharp to the exact area.
- `Stroke` with butt, round and square caps, miter, round and bevel joins and a miter limit; width 0 is a one-pixel hairline.
- Antialiased clips by rectangle, round rectangle or path, intersecting or cutting out; pixel-aligned rectangles are free, and a
  fill through a mask costs about 1.16 times the plain fill.
- `DrawImage` under the transform and clip with Egami's samplers; at whole-pixel placement it equals `DrawImage` in a chain.
- Speed (ms, 1 thread): 1,000 small shapes 4.0, a 4000×4000 star 19.8 (4.8 on 24 cores), 10,000 icons 109, 200 thick curves
  40, 10,000 hairlines 48.

### Path geometry

Path geometry. See [Path geometry](docs/articles/guides/drawing.md#path-geometry).

- Boolean operations on the curves themselves: `Path.Op` with `PathOp` (`Union`, `Intersect`, `Difference`, `Xor`,
  `ReverseDifference`) and shortcuts, `Path.Simplify`, and `PathOps.Combine` for many paths in one pass. Results keep the inputs'
  lines, quadratics and cubics, cut exactly where outlines cross, with outer contours clockwise and holes counterclockwise. Each
  input keeps its fill rule. Area identities hold on 100,000 random cases and 10,000 nearly parallel ones, and results are within
  a mean level of SkiaSharp's PathOps on the 2.6.2 corpus.
- `Stroke.ToPath`: the outline a pen draws, which fills to exactly the stroke's pixels; `simplify: true` removes its overlaps.
  `Path.Offset` grows or shrinks an area with a join.
- `Stroke.Dash` with `DashPattern` (absolute lengths and a phase, as PDF, SVG and Skia; `Scaled` for width-relative patterns),
  received by backends as data and cut along the curves by the raster backend.
- `PathMeasure` and `ContourMeasure`: lengths by adaptive Gauss–Legendre quadrature (within 1e-4 units, and 1e-3 of SkiaSharp's
  PathMeasure), positions, tangents, placing matrices and curve-keeping pieces.
- `PathEffect`: `Dash`, `Corner`, `Trim`, `Jitter` (seeded, the same on every thread and machine), `Stamp` with `StampStyle`,
  `Hatch` and `HatchPath`, combined with `Then` and `Plus`; `path.Apply` or `Stroke.Effect`. Within a mean level of SkiaSharp's
  matching effects.
- SVG path data: `Path.ParseSvg` (every command of SVG 1.1 and 2, compact numbers, arc flags without separators; throws
  `SvgPathException` with the character position, or keeps what comes before an error with `lenient: true`), `TryParseSvg`, and
  `ToSvg` with `SvgPathFormat` (absolute or relative, compact, a precision; relative offsets never drift). Fuzzed for 20 minutes.
- Boolean speed (ms, 1 thread, best of 7): 100 random 12-gon pairs under four operations 28 against SkiaSharp's 39;
  50 curved pairs 12.5 against 4.7; 200 letters unioned 24 against 503.

### Paints and layers

Paints and layers. See [Gradients](docs/articles/guides/drawing.md#gradients),
[Layers](docs/articles/guides/drawing.md#layers) and [Blurs and shadows](docs/articles/guides/drawing.md#blurs-and-shadows).

- Gradients: `LinearGradient`, `RadialGradient`, `ConicalGradient` (two circles, as the HTML canvas, Skia and PDF) and
  `SweepGradient`, with `GradientStop`s, `TileMode` (`Clamp`, `Repeat`, `Mirror`, `Decal`), PDF's one-sided extend
  (`ExtendStart`, `ExtendEnd`), mixing in encoded sRGB, linear sRGB, Oklab or Oklch with CSS Color 4's hue methods
  (`GradientInterpolation`), premultiplied by default, and ordered `Dither` (off by default). Every paint has a `LocalMatrix`.
  Within a mean level of SkiaSharp's gradients on every kind and tile mode.
- `ImagePaint`: an image as a paint, tiled per axis, sampled with Egami's filters and from a `MipChain` level when shrunk; a
  whole-pixel shift copies rows. Within a mean level of SkiaSharp's image shaders.
- `NoisePaint`: SVG feTurbulence's `Fractal` and `Turbulence` (within a mean level of SkiaSharp's noise shaders unstitched) and
  `Simplex`, smoother and exactly tileable.
- `BlendPaint` (two paints with any blend mode), `Paint.WithColorMatrix` (`ColorMatrixPaint`) and `CustomPaint` (rows from C#,
  the same at any core count; vector backends use the raster fallback).
- Layers: `SaveLayer` with `LayerOptions` (`Bounds`, `Opacity`, `BlendMode`, `Filter` as any chain of Egami operations,
  `Backdrop`, `Mask`, `Isolated`) and `SaveLayer(opacity)`; backends get `OnSaveLayer` and `OnRestoreLayer`. Layers are
  premultiplied in the canvas's class and cover only their bounds in the clip, grown by the filter's reach. `SoftMask` with
  `SoftMaskKind.Alpha` or `Luminosity`, a backdrop color and a transfer curve, as PDF soft masks. Within a mean level of SkiaSharp's
  saveLayer with alpha and blend modes.
- `Paint.Blur` with `MaskBlur` and `BlurStyle` (`Normal`, `Solid`, `Outer`, `Inner`), on Egami's fast blur, as Skia's blur mask
  filter (within a mean level of SkiaSharp's in every style); axis-aligned rectangles are blurred exactly from the error
  function. `DrawShadow` with `ShadowOptions`: ambient and spot shadows after Skia's shadow model.
- Speed (ms, 2048 × 2048, 1 thread and 24 cores): a full-frame linear gradient 26 and 3.8;
  radial 22 and 2.9; a tiled image brush 5.6 and 0.8. A full-frame half-opacity
  layer costs 1.38 times the drawing inside it at 1 thread and 1.14 at 24.

### Advanced drawing

Advanced drawing. See [Nine-patches and lattices](docs/articles/guides/drawing.md#nine-patches-and-lattices),
[Meshes and patches](docs/articles/guides/drawing.md#meshes-and-patches), [Recorded drawings](docs/articles/guides/drawing.md#recorded-drawings)
and [the .egdr format](docs/articles/guides/egdr-format.md).

- `DrawImageNinePatch` and `DrawImageLattice` with `Lattice` (x and y divisions, bounds, `LatticeCellKind` and cell colors), as
  Skia's; fixed spans shrink in proportion when the destination is small. The raster backend draws the cells as one mesh, so they
  meet without seams, each sampled inside its own part of the image. `NinePatch.FromAndroid` reads .9.png markers into the
  image, its lattice and its content padding. Within a mean level of SkiaSharp's drawImageNine and drawImageLattice.
- `DrawAtlas` with `SpriteTransform` (Skia's RSXform) and optional colors combined by a blend mode; each sprite blends on its own.
  Within a mean level of SkiaSharp's drawAtlas. 10,000 16-pixel sprites draw in 23 ms.
- `Mesh` (`MeshMode` `Triangles`, `Strip` or `Fan`, with colors, texture coordinates, values and indices) and `DrawMesh`: the
  triangles' union is filled once, antialiased on its outer edge only, so meshes have no cracks; Gouraud colors in the canvas's
  color space, textures mapped through any paint, and per-vertex values mapped through gradient stops for PDF's mesh shadings.
  Within a mean level of SkiaSharp's drawVertices.
- `Patch.Coons` and `Patch.Tensor` with corner colors, values or texture coordinates and `DrawPatch`, cut into triangles until
  they stray less than a quarter of a device pixel, later parts covering earlier ones where a patch folds, as PDF requires.
  Within a mean level of SkiaSharp's drawPatch.
- `PictureRecorder` records any canvas code into an immutable `Picture`: `DrawPicture` replays it under a matrix, with a paint
  that merges it as one layer or as an image cached at the current scale; `Playback`, `ToRaster`. `PicturePaint` tiles a
  drawing as a pattern rendered at the device scale. Within a mean level of SkiaSharp's drawPicture and picture shaders.
- `Picture.Save` and `Picture.Load`: Egami's own documented binary format, .egdr, versioned and chunked, with images as PNG;
  loading checks every count and number, honors `SafetyLimits` and bounds nesting and replay, and has a fuzz target (`egdr`).
- For document backends: `DrawImage` with an `EncodedImage` (a file's bytes, probed and decoded only when needed, so a PDF page
  can embed a JPEG as it is), `DrawAnnotation` with `Annotation` links and destinations as Skia's drawAnnotation, and
  `BeginMarkedContent` and `EndMarkedContent` with `MarkedContent` tags for tagged PDF; hooks `OnDrawEncodedImage` (decodes by
  default), `OnDrawAnnotation`, `OnBeginMarkedContent` and `OnEndMarkedContent` (nothing by default). Pictures and .egdr keep
  them.
- Backends get `OnDrawLattice`, `OnDrawAtlas`, `OnDrawMesh`, `OnDrawPatch` and `OnDrawPicture`, with defaults: images per cell or
  sprite, meshes through the raster fallback, patches as meshes, drawings replayed.
- Fixed: `DrawImage` under a transform that collapses the image threw; it draws nothing. Scaling overlapping `RoundRect` radii
  was not idempotent; scaled radii now round down.

### Text quality, text on a path and color glyphs

Text quality, text on a path and color glyphs. See [Rendering quality](docs/articles/guides/text.md#rendering-quality),
[Text on a path](docs/articles/guides/text.md#text-on-a-path) and [Color fonts](docs/articles/guides/text.md#color-fonts).

- The raster canvas draws `Font.Hinting`, `Subpixel` and `Edging`: each subpixel phase, edging and hinting gets its own cached
  mask; LCD edging renders at three times the resolution along `CanvasOptions.PixelGeometry` with a 5-tap filter, and falls back to
  grayscale with the `Text.LcdFallback` warning where it cannot be drawn (no geometry, a layer, a paint other than solid
  source-over). Synthetic bold and italic draw on every backend. Glyphs whose contours overlap (variable fonts, and instances
  marked so) are merged before rasterizing, so overlaps do not darken their edges.
- `DrawTextOnPath` and `Font.ShapeOnPath` with `TextOnPathOptions` (start offset or fraction, anchor, baseline shift, side,
  wrapping on closed paths), by SVG's textPath rules; the result is a transformed glyph run, so a PDF backend keeps it as text.
  Transformed runs draw on the raster canvas through paths for turned glyphs and masks for the rest.
- Color glyphs draw in color through ordinary canvas calls (clips by glyph outlines, solid and gradient fills, transforms and
  layers with blend modes; images for sbix and CBDT), so every backend gets them; on a raster they are cached as premultiplied
  images per palette and color. The glyph is one group under the paint's alpha; the paint's color fills foreground entries.
  `PictureRecorder` and .egdr files keep color glyphs as the calls they draw with.
- Gates against SkiaSharp 4.153: variable text at 12 to 48 pixels in five fonts and subpixel-positioned text at 9 to 24 pixels in
  four within a mean level of SkiaSharp filling the same glyphs and closer than it to the exact area; COLR v0 and v1 glyphs within
  a mean level of SkiaSharp's own drawing at 24 to 96 pixels, sbix and CBDT within 2. Aliased, LCD, light-hinted and synthetic text
  are recorded against SkiaSharp's nearest settings.
- Speed (ms, 1 thread): a 2,000-word page in Roboto Flex at 16 pixels draws in 20 at its default and at weight 700, width 75 alike;
  a page of 500 COLR v0 emoji in 2.7 cold and 1.6 warm, and of COLR v1 emoji in 15 cold and
  2.1 warm; plain text is within 10% of 2.7.1 (11.0 against 10.7 at 12 pixels).
- Fixed: a curve under a transform so large that rounding split it again and again overflowed the stack.

### Shaped text

- `Canvas.DrawText` takes `ShapingOptions` and draws shaped text (with fallback fonts when the options list them);
  `Canvas.DrawShapedText` draws a `ShapedText` shaped once. Latin text draws as before, at the same speed.

### Paragraphs

- `Canvas.DrawTextLayout` draws a laid-out paragraph in CSS's order (shadows, backgrounds, underlines, glyphs and placeholders,
  line-throughs), skipping lines outside the clip; an ellipsis or a soft hyphen's hyphen is marked content with ActualText.
  `Canvas.DrawText` into a `RectF` with `VerticalAlign` lays out and draws in one call, and `TextLayout.ToPath(origin)` gives a
  `Path`. Drawn paragraphs are within 2 levels of the exact coverage on over 99% of pixels, closer than SkiaSharp, and drawing
  costs at most 10% over drawing the glyph runs alone.

### Document backends

- `LayerOptions.Knockout` for PDF's knockout groups and `DrawImageMask` (hook `OnDrawImageMask`) for its stencil masks, drawn
  exactly by the raster backend; pictures and .egdr keep them (for Egap's PDF renderer).
- `Canvas.RasterizeFallback` scales a paint's mask blur with the raster, so a blur drawn through a PDF page's fallback at
  300 dpi keeps its size; layer filter and backdrop chains run on the raster's pixels as they are (found by Egap's PDF canvas).

### Printing with inks

- `InkCanvas` draws on an `InkRaster` with every `Canvas` call (`InkCanvasOptions`: what RGB means, intent, black point
  compensation): RGB colours, gradients and images convert into the process inks; ink colours print their plates.
  `Clear(InkColor)` and `DrawImage(InkRaster)` are its own. Blend modes, groups (isolated, non-isolated, knockout) and soft
  masks follow ISO 32000-2 plate by plate; every plate is within 2 levels of Ghostscript 10.05's tiffsep on the gate scenes.
- Ink paints: `SolidPaint(InkColor)`, `GradientStop(offset, InkColor)`, `Mesh.WithInks` and `Patch.WithInks`. Every other
  backend draws their appearance.
- `Paint.Overprint` (`Off`, `On`, `NonZero`, PDF's overprint and overprint mode 1): plates a fill does not paint keep what is
  under them. RGB canvases ignore it, as PDF viewers do by default.
- `LayerOptions.ColorSpace`: a layer blends in an RGB or gray space and converts into the inks as it merges; spots stay plates.
- Pictures keep ink paints, overprint and layer colour spaces; .egdr 1.1 holds them in an INKS chunk, which 1.0 readers skip
  to draw the appearances.

### Changes from the previews

Renamed or changed in the 1.0 API review, for code written against the previews:

- `CanvasOptions.ColorGlyphs` is `ColorGlyphRenderer`; `Canvas.ColorGlyphRenderer`, `DrawColorGlyphs` and `DrawRendererGlyphs`
  are `protected`, so other backends draw color glyphs as the raster backend does.
- `OnDrawLattice` is `OnDrawImageLattice`; `OnDrawImageMask` takes its arguments in `DrawImageMask`'s order.
- `EncodedImage.FromBytes` and `FromFile` are `EncodedImage.Load`, from a path, a stream or bytes, as `Raster.Load`.
- `PathOps.Combine` is `Path.Combine`; `Path.Offset` is `Path.Inflate`.
- `Patch.Coons` and `Patch.Tensor` take corner texture coordinates before values, as `Mesh` does; `Patch.ToTensorNet` returns
  read-only memory; `DrawMesh`'s paint is optional, as `DrawPatch`'s.
- `NoiseType` is `NoisePaintKind` (`Fractal`, `Turbulence`, `Simplex`), `NoisePaint.Type` is `Kind`, and the tile parameter is
  `tileSize` everywhere.
- `TextAnchor.Middle` and `VerticalAlign.Middle` are `Center`.
- New hook `OnClear`, so a document backend can write a clear; `GlyphCache.ByteLimit` is documented as process-wide.
- Fixed: a non-isolated layer merged with a blend mode other than source-over blended the backdrop it started from with itself;
  the backdrop's part is now removed first, as PDF's non-isolated groups require (ISO 32000 11.4.8). Found by Egap's renderer.

## Egami.Fonts 1.0.0 (2026-10-18)

The first public release. The API is frozen (PublicAPI.Shipped.txt): later 1.x versions only add to it. Glyph ids stay
`ushort` for all of 1.x.

### Fonts and text on the canvas

A new package, over Egami's public API only, that reads TrueType and OpenType fonts in managed code; Egami.Drawing uses it to draw
text. See the [Fonts and text guide](docs/articles/guides/text.md).

- `Typeface` loads TrueType, OpenType CFF (CID-keyed too), collections and bare CFF streams as PDF embeds them, lazily from one
  block of memory; leniently by default, with `Font.*` diagnostics and documented defaults for missing or damaged tables, or
  strictly. Names in every language, `FontStyle`, `FontEmbedding` (reported, never enforced), cmap formats 0, 4, 6, 10, 12, 13 and
  14 with symbol and Mac Roman fonts, glyph outlines through `IGlyphOutlineSink` (TrueType quadratics, a Type 2 charstring
  interpreter with subroutines, flex and seac), advances, glyph bounds, `FontExtents` by the browsers' vertical-metrics rule, and
  GPOS pair kerning (or the kern table). Every table field, mapped code point, outline point, bound and sampled kerning pair of
  the test fonts equals fontTools'.
- `FontCollection` with `FontCollection.System`, found without native calls (503 installed faces indexed in 152 ms on first use),
  names in any language, CSS Fonts 4 style matching with the synthesis a lighter or upright face wants, and generic families.
- `FontSubsetter` writes TrueType, OpenType CFF or bare CID-keyed CFF subsets with composite and seac closure, dense or kept glyph
  ids and unused CFF subroutines dropped; every subset re-loads with its glyphs unchanged and opens in fontTools and FreeType;
  200 glyphs from a 16 MB CJK font in about 2 ms.
- `Font` (a typeface at an em size in canvas units), `Font.Shape` (cmap, pair kerning, newlines) and `MeasureText`; `GlyphRun`
  keeps the source text and a cluster per glyph. `Canvas.DrawText`, `DrawGlyphRun` and the `OnDrawGlyphRun` hook; small glyphs
  are drawn from `GlyphCache` masks at whole pixels, which changes speed, not pixels; `GetGlyphPath`, `GetTextPath` and
  `GlyphRun.ToPath`. Drawn text is within a mean level of SkiaSharp filling the same glyphs and closer than it to the exact area.
- Speed: nine fonts load in 0.18 ms, 1,000 strings match and measure in 3.3 ms, and a 2,000-word page draws in 15 ms at 12
  pixels.
- Fuzz targets `font`, `cff` and `subset`.

### Web, variable and color fonts

Web, variable and color fonts. See [Variable fonts](docs/articles/guides/text.md#variable-fonts),
[Color fonts](docs/articles/guides/text.md#color-fonts) and [Faces](docs/articles/guides/text.md#faces).

- WOFF and WOFF2 load wherever TrueType and OpenType do (`Typeface.FromFile`, `FromMemory`, `FontLoader`, `FontCollection`
  directory scans): WOFF with the BCL's zlib, WOFF2 with its Brotli decoder and the glyf, loca and hmtx transforms, WOFF2
  collections too. `Typeface.Container` says which container a face came in; `GetWoffMetadata` and `GetWoffPrivateData` return the
  extra blocks. Every decoded table equals fontTools' and the reference woff2 decoder's. Four WOFF2 files load in 15 to 21 ms.
- Variable fonts: `Typeface.VariationAxes` (`VariationAxis`), `NamedInstances` (`NamedInstance`), `IsVariable`, `Variations`, and
  `WithVariations` and `WithNamedInstance`, which return a face at a position sharing the font's memory (positions are cached).
  `FontVariation` with `Weight`, `Width`, `Slant`, `Italic` and `OpticalSize`; avar 1 and 2; gvar with shared and private points,
  intermediate regions, IUP, composites and phantom points; CFF2 with blend and vsindex; HVAR, MVAR and GDEF variation deltas, so
  outlines, advances, extents, kerning and font metrics all vary. Each equals fontTools at 25 positions of five variable fonts.
  Names and PostScript names follow the position.
- `FontCollection` matching picks a position inside a variable family's weight, width and slant ranges by CSS Fonts 4 rules, as
  well as its named instances. `Font.OpticalSizing` (Auto by default) sets the opsz axis to the font size;
  `Font.EffectiveTypeface` is the face drawn.
- Static instances: `SubsetOptions.Variations`, or subsetting a face at a position, writes a static TrueType or CFF font with the
  outlines, advances and metrics at that position and no variation tables, equal to fontTools' instancer; glyphs keep their
  overlaps and are marked OVERLAP_SIMPLE or OVERLAP_COMPOUND as fontTools marks them. They open in fontTools and FreeType. An
  instance subset to 200 glyphs takes under 1 ms.
- Color fonts: `Typeface.ColorFormats`, `Palettes` (`FontPaletteInfo`, from CPAL), `HasColorGlyph`, `TryGetColorGlyph` and
  `TryGetSvgDocument`. COLR v0 layers and the full COLR v1 paint graph (solid, linear, radial and sweep gradients with their color
  lines, glyph and color-glyph references, transforms, composites, clip boxes, variable paints) come as `ColorGlyph` trees with a
  visitor; sbix and CBDT images as `BitmapColorGlyph` with the strike nearest the size. `FontPalette` (Default, Light, Dark, an
  index and color overrides) and `Font.Palette`; `Font.ColorGlyphs` turns color off. Paint trees, colors, clips and images equal
  fontTools'; cycles and graphs deeper than 64 paints are cut with the `Font.BadColorGlyph` warning.
- Rendering settings for canvases: `Font.Hinting` (`FontHinting.Light`: vertical-only snapping to blue zones from CFF or measured
  from TrueType outlines, up to 48 pixels per em; no x value or advance moves), `Font.Subpixel` (quarter-pixel glyph origins, on
  by default), `Font.Edging` (`FontEdging.Antialias`, `Alias`, `Lcd`), `Font.AllowSynthesis` with synthetic bold (an outset by
  the size) and italic (a 14-degree slant), in outlines and bounds too.
- `GlyphRun.CreateTransformed` and `GlyphRun.Transforms` (`GlyphTransform`, Skia's RSXform): a run whose glyphs each have their
  own turn and scale, kept as text.
- `FontOutlines.None` for bitmap-only faces. New warnings `Font.BadContainer` and `Font.BadColorGlyph`.
- Fuzz targets `woff2`, `gvar`, `cff2`, `colr` and `instance`; their first 10-minute runs found five crashes, all fixed and kept as
  regressions, and the reruns were clean.

### Text shaping

OpenType shaping in managed code, a translation of HarfBuzz 14.5.1's. See
[Unicode text and shaping](docs/articles/guides/unicode-text.md).

- Unicode 17.0 in `Egami.Fonts.Unicode`, from Egami's own tables (never the runtime's): `UnicodeData` (categories, scripts and
  script extensions, bidi, joining, combining, Indic, vertical orientation, East Asian width, emoji properties), `Bidi.Resolve`
  (UAX #9 with isolates, brackets and mirroring; `BidiResult.Reorder` for a line), `TextSegmenter` graphemes and words (UAX #29)
  and `Script` with its OpenType tags. Unicode's BidiTest, BidiCharacterTest, GraphemeBreakTest and WordBreakTest pass in full.
- `TextShaper.ShapeRun` shapes one run into a reusable `GlyphBuffer` (glyph ids, UTF-16 clusters, `GlyphFlags`, positions) with
  every GSUB and GPOS lookup type, FeatureVariations, the variation deltas of variable fonts, vertical runs (vmtx, VORG, VVAR) and
  HarfBuzz's script shapers: Arabic and its family (with fallback shaping from the presentation forms), Hebrew, the nine Indic
  scripts, Khmer, Myanmar, Thai and Lao, Hangul and the Universal Shaping Engine. `RunProperties` give the script, BCP 47 language,
  direction, `FontFeature`s (HarfBuzz's syntax: `FontFeature.Parse("liga=0")`) and `ClusterLevel`.
- `TextShaper.Shape` shapes whole text: lines at hard breaks, bidi, script and span runs (`ShapingOptions`, `TextSpan`) and font
  fallback per grapheme cluster (`FontFallback`, `IFontFallbackResolver`, the opt-in `SystemFontFallback`), returning `ShapedText`.
- `Font.Shape` and `Font.MeasureText` take `ShapingOptions` and shape with all of the font's features; text without ligature or
  mark rules gives 2.7.1's glyphs and positions. `GlyphRun` gains `BidiLevel`, `Script` and `Language`.
- `Typeface.Layout` lists scripts, language systems and features and gives `GetClosure`, hb-subset's GSUB closure;
  `SubsetOptions.LayoutClosure` keeps it and the layout tables, for subsets that are shaped again.
- Gates: glyphs, clusters, positions and flags equal hb-shape 14.5.1's on all 276 AOTS tests, 4,974 of HarfBuzz's 4,987
  in-house tests (the rest test fonts without outlines, the Zawgyi encoding, repeated axis tags, other shapers and output Egami
  leaves out) and 24 corpus runs in 15 scripts; the closure equals HarfBuzz's on 1,116 cases. Shaping English, Arabic and
  Hindi pages takes 3.7, 7.5 and 13.4 ms, about half HarfBuzzSharp's speed.
- Fuzz targets `shaping` and `bidi`.
- From the API review: a font with no glyph data (a subset made for shaping) loads with empty glyphs and a `Font.MissingTable`
  warning; `WithVariations` sets every axis of a repeated tag; the cmap subtable is chosen as HarfBuzz chooses it (a symbol
  subtable first, Windows 3.1 Arabic font pages through their private use characters), for drawing and shaping alike.

### Paragraph layout

Wrapped, aligned and interactive text in `Egami.Fonts.Layout`. See
[Paragraph layout](docs/articles/guides/paragraph-layout.md).

- `LineBreaks.Enumerate`: UAX #14 break opportunities (Unicode 17) with CSS's `word-break`, `overflow-wrap` and `line-break`
  strictness and the Chinese, Japanese and Korean rules; LineBreakTest.txt passes in full.
- `Paragraph.Create` takes a string or `RichText` (`RichTextBuilder`: nested `TextStyle` spans and inline `Placeholder` boxes)
  and a `ParagraphStyle`; `Layout(width)` gives a `TextLayout` of `TextLine`s and `LayoutRun`s with shaped glyph runs, per
  character font fallback in CSS's order, bidi per line, alignment and justification (`TextJustify`), tabs and tab stops,
  line heights and struts, soft hyphens, balanced filling, `MaxLines` and `MaxHeight` with an ellipsis, and vertical writing
  (`WritingMode`, `TextOrientation`). `LineFlow` lays out a line at a time at changing widths, for columns and wrap-around.
- Interaction: `HitTest`, `GetCaret` (with a second caret at bidi boundaries and GDEF ligature carets), `MoveCaret`,
  `GetSelectionRects`, word and line boundaries; min-content and max-content widths, ink bounds and placeholder boxes.
- Decorations: underline, overline and line-through, solid, double, dotted, dashed or wavy, with skip-ink, as
  `TextLayout.Decorations`; `TextLayout.ToPath` writes glyphs and decorations to an outline sink.
- Gates: line breaks, line boxes, glyph origins, intrinsic widths, ellipses, hit tests, carets, selections and decoration ink
  equal headless Chromium 129's on a corpus of eleven scripts at five widths and the feature cases (the few differences are
  Chromium's own, listed in the tests). Breaking 1 MB of mixed text takes 17 ms;
  a 2,000-word page at three widths 7.8 ms; 10,000 hit tests 19 ms.
- Fuzz target `paragraph`, and a robustness suite on the same harness.

### Font data for PDF

What a PDF font needs to reach its glyphs, public before Egami.Fonts 1.0. See [Font data for PDF](docs/articles/guides/text.md#font-data-for-pdf).

- `Typeface.GetGlyphName` and `TryGetGlyphByName`: names from a name-keyed CFF font's charset (the Expert and ExpertSubset
  charsets included) or the post table, versions 1, 2 and 2.5.
- `Typeface.CidSystemInfo` (`CidSystemInfo`), `GetCid` and `TryGetGlyphByCid` for CID-keyed CFF, bare or in OpenType.
- `Typeface.CharacterMaps` and `FindCharacterMap`: every cmap subtable as stored (`CharacterMap`, `CharacterMapping`), with
  format 2 newly read; `Typeface.BuiltInEncoding`, a CFF font's own encoding as a platform 7 map, as FreeType gives it.
- `CffStandardStrings`: the 391 CFF standard strings and the Standard and Expert encodings.
- On the 1,723 font programs embedded in Egap's PDF corpus, names, CIDs, subtables and built-in encodings equal FreeType's and
  fontTools' (FreeType's where they differ: the first of overlapping format 4 segments wins, and a code past the font's glyphs
  maps to 0). The Expert Encoding gives code 175 Macronsmall, as pdf.js does, where FreeType repeats Dotaccentsmall.
- Fixes: a CID-keyed CFF font's Font DICT FontMatrix now applies before the Top DICT's, so a font whose Font DICTs scale by
  1/2048 (pdf.js's text_clip_cff_cid.pdf) draws its glyphs at their size rather than about twice it; outlines and advances
  equal FreeType's on every CID-keyed CFF in the corpus. A CFF DICT with a reserved operator (pdf.js's bug1308536.pdf) loads,
  the operator skipped as FreeType, PDFium and pdf.js skip it. A cmap format 6 subtable whose count runs past the table keeps
  the entries there are.
- A `fontdata` fuzz target reads every name, CID, subtable and encoding of what loads; the font targets do too.

### Changes from the previews

Renamed or changed in the 1.0 API review, for code written against the previews:

- Fixed: shaping a run longer than 65,536 characters threw on a thread that had shaped a short run before, because the shaper
  kept the short run's limits. Each run now gets limits from its own length.
- Breaking: `FontCollection.System` is read-only; `Add`, `AddFile` and `AddDirectory` on it throw `InvalidOperationException`.
  To match your own fonts beside the installed ones, call the new `FontCollection.AddSystemFonts` on a collection of your own:
  it copies the installed-font index, and the faces load with that collection's options.
- Breaking: `IndicSyllabicCategory`, `IndicPositionalCategory` and `UnicodeData.GetIndicSyllabicCategory` and
  `GetIndicPositionalCategory` are internal. The shaper still uses them; they were shaping data rather than a public feature.
- `default(Font)` has no typeface: its metrics, advances, `Shape` and `MeasureText` now throw `InvalidOperationException`
  instead of a `NullReferenceException` or `ArgumentNullException` from deeper in.
- `Font.MeasureText(ReadOnlySpan<char>, ShapingOptions?)` measures a slice of a longer text or a reused buffer without making a
  string of it. Measuring a string allocates about 8% less than before.
- `IGlyphOutlineSink`, `IColorPaintVisitor`, `IFontFallbackResolver` and `ITextPaint` are frozen as they are; a member added
  in 1.x comes as a default interface method, so your implementations keep compiling.
- `FontCollection.Families` and `Count` keep the grouped families until the next `Add` or `Refresh`, instead of grouping every
  face on each call.

## Egami.Svg 1.0.0 (2026-10-18)

The first public release. The API is frozen: 1.x versions add to it without breaking your code. See [SVG](docs/articles/guides/svg.md).

- `SvgDocument` loads SVG and SVGZ into an editable tree of `SvgElement`, `SvgAttribute` and text, with a locked-down XML reader:
  internal DTD entities are capped, nothing outside the file is opened unless an `SvgLoadOptions.Resolver` allows it
  (`SvgResolvers.SameDirectory`), and element, depth and size limits come from `SafetyLimits`. A lenient load reports what it
  skips as `Svg.*` warnings; a strict one throws `SvgException` with the line and column. `Save` writes the tree back with its
  declaration, DOCTYPE, comments and foreign namespaces, numbers set in code at `SvgSaveOptions.Precision`.
- Styles cascade with Egami.Css: `style` elements with media queries, the `style` attribute and presentation attributes.
- Drawing follows resvg where SVG leaves a choice: shapes, fills, strokes, paint-order, markers, nested svg, use and symbol,
  linear and radial gradients, patterns, Inkscape's mesh gradients, clip paths, masks, opacity, blend modes, raster and SVG
  images and switch. `Rasterize` draws into a new raster at the document's size, a scale or a target size; `Draw` draws on any
  Egami.Drawing canvas, the whole document or one element by id; `RenderTree` exposes the resolved `SvgNode` tree.
- Text: x, y, dx, dy and rotate lists, tspan, tref and textPath, text-anchor, the baseline properties, letter-spacing,
  word-spacing, textLength and lengthAdjust, decorations, vertical writing and font variations, laid out as resvg lays text
  out and drawn as `SvgTextRun` glyph runs (`SvgRenderOptions.TextAsPaths` draws outlines). `SvgLoadOptions.Fonts`
  (`SvgFontOptions`) gives the fonts: the caller's collection, the document's `@font-face` rules, then the machine's unless
  turned off. SVG 2's wrapped text: `inline-size` lines as 2.8.2's paragraph layout fills them, and `shape-inside` text flowed
  into shapes as Inkscape flows it (16 of 20 samples match Inkscape's export).
- OpenType SVG glyphs: `SvgGlyphRenderer.Instance` draws an SVG table's glyphs in color. Egami.Drawing gains the hook it
  plugs into, `IColorGlyphRenderer` on `CanvasOptions.ColorGlyphs`, asked after COLR version 1 and before COLR version 0,
  sbix and CBDT.
- Filters: every SVG 1.1 primitive, feDropShadow and the CSS filter functions, in linearRGB or sRGB, run in a raster lined up
  with the device pixels (`SvgRenderOptions.FilterScale` sets its resolution on vector canvases). feGaussianBlur is SVG's three
  box blurs on premultiplied colors; feConvolveMatrix and the lighting primitives follow the specification's kernels.
- `SvgCanvas` writes any Egami.Drawing canvas drawing as SVG: paths, strokes and dashes, gradients, patterns, pictures,
  clips, layers with opacity, CSS blend modes and masks, images and text as glyph outlines or `text` elements, with shared
  `defs` (`SvgWriteOptions`). What SVG cannot express is drawn by Egami into an embedded PNG and reported as
  `Svg.FallbackImage`. Read back by Egami.Svg its files draw like the canvas calls, and Chromium and Inkscape open them cleanly.
- `SvgFormat.Info` registers SVG and SVGZ as an Egami format with `EgamiContext.WithFormat`, so `Raster.Load` draws a file at
  its own size (times `SvgDecodeOptions.Scale`) and `Raster.Probe` gives the size without drawing. It is found by its `svg`
  root, opens nothing outside the file, and only reads. Detection now passes data shorter than the longest registered
  signature to every format, as `SignatureMatcher` describes, so short SVG files are found.
- Gates: on resvg's own test suite, 94 to 100% of the shapes, painting, structure, paint-server and masking tests match resvg's
  reference renders (at most 0.5% of pixels differ, allowing a one-pixel shift; deviations are listed with reasons in
  egami-compare), 99% of the filter tests and 98.7% of the text tests (78% of the text tests are also close to
  Chromium's renders); 196 of the 200 corpus files
  match resvg's CLI. Seven of eight mesh samples match Inkscape's export.
- Speed, one thread, on 200 real files: loading and drawing them takes 0.92 s, against 2.0 s for Svg.Skia, 3.1 s for the resvg
  CLI (which also writes PNG; Egami does that in 1.4 s) and 9.5 s for resvg's library with text on (0.69 s with text off).
  resvg's text chapter takes 0.12 s against Svg.Skia's 0.45 s and resvg's 26 s; its filter chapter 0.91 s against 1.25 s and
  12 s.
- A fuzz target for Load, Rasterize and Save (`svg` in tests/Egami.Fuzz); its first runs found image data that was damaged
  after its header made drawing throw, and images are now decoded when the document is read.

## Egami.Css 1.0.0 (2026-10-18)

The first public release. The API is frozen: 1.x versions add to it without breaking your code. See [CSS](docs/articles/guides/css.md).

- Egami.Css parses CSS Syntax 3, matches Selectors 4 over any `ICssElement` tree, cascades with specificity, `!important`,
  custom properties and `var()`, evaluates media queries, and parses colors, lengths, transforms and fonts.

### Pages, boxes and the document properties

- `CssPropertyRegistry.CreateDefault` knows the box properties (`margin`, `padding`, `border` with its sides and
  `border-radius`, `width` and `height` with their `min-` and `max-` forms, `box-sizing`), backgrounds (the `background-*`
  layers and the `background` shorthand), `list-style`, the table properties, `float`, `clear`, `overflow`, the
  fragmentation properties (`break-*`, `orphans`, `widows`, `page` and the legacy `page-break-*` as shorthands) and
  `content` with the counters, each with its value grammar, so invalid declarations are dropped as browsers drop them.
- `@page` is interpreted: `CssPageRule` with `CssPageSelector`s (page type names and `:first`, `:left`, `:right`,
  `:blank`, with their specificity) and `CssPageMarginRule`s for the sixteen `CssMarginBox`es. `CssCascade.CascadePage`
  cascades them for a `CssPage` into a `CssPageStyle`, and `CssPageSize` reads `size`, page size names included.
- `overflow` is now a shorthand of `overflow-x` and `overflow-y`; Egami.Svg reads the two longhands and draws as before.

### Changes from the previews

- `CssAttribute`, `CssPage`, `CssRange` and `CssTransformOrigin` declare their `ToString`, so it is annotated as returning a
  non-null string. The text is the same as before.
- `ICssElement` is frozen as it is; a member added in 1.x comes as a default interface method, as `MatchesPseudoClass` already
  is, so your element trees keep compiling.

## Egami.Metafiles 1.0.0 (2026-10-18)

The API is frozen (PublicAPI.Shipped.txt): later 1.x versions only add to it. It is 0.1.1's API plus one overload; nothing
was removed or renamed.

- `Metafile.Load(ReadOnlyMemory<byte>)` reads a picture already in memory, such as one taken out of a Word or PowerPoint
  file, without wrapping it in a stream.

## Egami.Metafiles 0.1.1 (2026-10-18)

The first release; its API may still change while it is 0.x.

- `Metafile` reads WMF (placeable or not), EMF and EMF+ files and their gzip forms WMZ and EMZ, and plays them onto any
  Egami.Drawing canvas, so pictures from Word, Excel and Visio stay vectors in PDF and SVG output and rasterize on any OS.
  `Draw` stretches the picture's frame to a box as Word does; `Rasterize` draws at a DPI or a size. Kind, size, frame,
  DPI and description come from the header, matching GDI+ and Pillow on 2,106 of 2,111 corpus files (the other five are WMFs
  without a placeable header, sized by their window extent).
- The GDI records play as GDI does: map modes, world transform, pens, brushes, hatches and pattern brushes, paths, clip
  regions, bitmaps and raster operations, gradients and text. Drawn aliased, the test files match Windows GDI within a mean of
  one level and 99% of pixels within 16 levels; text within a looser bound. Text keeps the file's glyph positions in a
  substitute face (Carlito for Calibri, Liberation for Arial, Times New Roman and Courier New, and your own substitutions).
- EMF+ records play as GDI+ plays them: transforms and containers, clips, gradients (path gradients as meshes), pens with
  dashes, compound lines and caps, images (JPEG and PNG passed to PDF unchanged, nested metafiles as vectors), image
  effects, `DrawString` and `DrawDriverString`; dual files draw their EMF+ records, or their GDI part with
  `MetafilePlayback.Gdi`.
- `MetafileFormat` lets `Raster.Load` and `Raster.Probe` read metafiles (opt-in, as SVG). Drawing the 2,111-file corpus
  takes 0.74 times what GDI+ takes.
