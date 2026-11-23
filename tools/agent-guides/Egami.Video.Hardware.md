# Egami.Video.Hardware: guide for coding agents

Egami.Video.Hardware reaches the GPU's video engine through Vulkan Video on Windows and Linux. This preview finds the hardware,
lists what it can do (H.264 and HEVC, decode and encode), and decodes and encodes H.264 and HEVC. It is Egami's only package that calls native code, and only the Vulkan loader the GPU driver installs.

Namespaces: `Egami.Video.Hardware`, with `Egami.Video` (`CodecIds`, `VideoWriter`) and `Egami.Video.Codecs`
(`H264EncoderOptions`, `HevcEncoderOptions`).

{{shared}}

## Entry points

- `HardwareVideoCodecs.IsAvailable(codecId, HardwareDirection)`: true when a GPU decodes or encodes the codec. Pass a
  `CodecIds` value (`CodecIds.Avc`, `CodecIds.Hevc`). Never throws; the answer is cached for the process.
- `HardwareVideoCodecs.GetDevices()`: `HardwareVideoDevice`s with their `HardwareVideoProfile`s (size limits, level,
  references, `HardwareRateControl`, quality levels). Empty without a GPU; `HardwareVideoCodecs.UnavailableReason` says why.
- `HardwareVideoCodecs.H264`: a `VideoCodec` that encodes H.264 on the GPU. Use it as `VideoWriterOptions.Codec` or call
  `CreateEncoder` with the same `VideoEncoderOptions` and `H264EncoderOptions` as the software encoder. It decodes
  progressive 8-bit 4:2:0 streams to the same samples as the software decoder: `MediaContext.Default.WithH264().WithCodec(
  HardwareVideoCodecs.H264)` makes `VideoReader` use it.
- `HardwareVideoCodecs.Hevc`: the same for HEVC. It encodes 4:2:0 in the Main and Main 10 profiles with `VideoEncoderOptions` and
  `HevcEncoderOptions`, and decodes 4:2:0 Main and Main 10 streams to the same samples as the software decoder:
  `MediaContext.Default.WithHevc().WithCodec(HardwareVideoCodecs.Hevc)`.
- `context.WithHardwareCodecs(HardwareUse use = HardwareUse.Decode)`: wraps the H.264 and HEVC codecs already in the context
  so that tracks decode on the GPU where it can, in software otherwise, with the same frames. Call it after `WithH264()` and
  `WithHevc()`. `HardwareUse.All` also encodes on the GPU, falling back to software for what the GPU cannot code. Returns
  the context unchanged without a GPU.
- `egami-video hwprobe [--json]` lists the devices and profiles from a shell.
- `HardwareVideoException`: the driver refused or failed an operation; `ResultCode` is the negative `VkResult`.

## Samples

Check for hardware:

{{sample VideoHardware.cs#available}}

List devices and profiles:

{{sample VideoHardware.cs#devices}}

Encode H.264 on the GPU:

{{sample VideoHardware.cs#encode}}

Decode H.264 on the GPU, with the software decoder where there is no GPU:

{{sample VideoHardware.cs#decode}}

Encode HEVC on the GPU:

{{sample VideoHardware.cs#encodehevc}}

Decode HEVC on the GPU, with the software decoder where there is no GPU:

{{sample VideoHardware.cs#decodehevc}}

Use the GPU wherever it can, the software codecs elsewhere:

{{sample VideoHardware.cs#withhardware}}

## Common mistakes

- Assuming hardware exists. Servers, CI runners and 32-bit processes have none: check `IsAvailable` and keep the software
  codec as the fallback.
- Expecting the same bytes as Egami's software encoders, or across GPUs and drivers. Hardware output differs.
- Passing codec names such as "h264". Use the `CodecIds` constants.
- Asking `HardwareVideoCodecs.H264` for two-pass encoding (`FirstPassStats`): it throws.
- Decoding interlaced (field or MBAFF), monochrome, 4:2:2 or 10-bit H.264 with it: `UnsupportedFormatException`. Use the
  software decoder.
- Calling `WithHardwareCodecs()` before `WithH264()` or `WithHevc()`: the software codec registered later replaces the
  hardware one.
- Calling `WithCodec(HardwareVideoCodecs.H264)` before `WithH264()`, or `WithCodec(HardwareVideoCodecs.Hevc)` before
  `WithHevc()`: the software codec then replaces it.
- Turning B-frames on by default. Some GPUs predict B pictures from the earlier reference only and files get bigger; they are
  off unless `H264EncoderOptions.BFrames` asks.

## More

- Hardware video: https://inexin.github.io/Egami/v1/articles/guides/video-hardware.html
- Video guide: https://inexin.github.io/Egami/v1/articles/guides/video.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
