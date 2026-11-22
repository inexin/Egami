# Egami.Video.Hardware: guide for coding agents

Egami.Video.Hardware reaches the GPU's video engine through Vulkan Video on Windows and Linux. This preview finds the hardware,
lists what it can do (H.264 and HEVC, decode and encode) and encodes H.264; the hardware decoders and the HEVC encoder come in
later previews. It is Egami's only package that calls native code, and only the Vulkan loader the GPU driver installs.

Namespaces: `Egami.Video.Hardware`, with `Egami.Video` (`CodecIds`, `VideoWriter`) and `Egami.Video.Codecs`
(`H264EncoderOptions`).

{{shared}}

## Entry points

- `HardwareVideoCodecs.IsAvailable(codecId, HardwareDirection)`: true when a GPU decodes or encodes the codec. Pass a
  `CodecIds` value (`CodecIds.Avc`, `CodecIds.Hevc`). Never throws; the answer is cached for the process.
- `HardwareVideoCodecs.GetDevices()`: `HardwareVideoDevice`s with their `HardwareVideoProfile`s (size limits, level,
  references, `HardwareRateControl`, quality levels). Empty without a GPU; `HardwareVideoCodecs.UnavailableReason` says why.
- `HardwareVideoCodecs.H264`: a `VideoCodec` that encodes H.264 on the GPU. Use it as `VideoWriterOptions.Codec` or call
  `CreateEncoder` with the same `VideoEncoderOptions` and `H264EncoderOptions` as the software encoder. It does not decode.
- `HardwareVideoException`: the driver refused or failed an operation; `ResultCode` is the negative `VkResult`.

## Samples

Check for hardware:

{{sample VideoHardware.cs#available}}

List devices and profiles:

{{sample VideoHardware.cs#devices}}

Encode H.264 on the GPU:

{{sample VideoHardware.cs#encode}}

## Common mistakes

- Assuming hardware exists. Servers, CI runners and 32-bit processes have none: check `IsAvailable` and keep the software
  codec as the fallback.
- Expecting the same bytes as Egami's software encoders, or across GPUs and drivers. Hardware output differs.
- Passing codec names such as "h264". Use the `CodecIds` constants.
- Asking `HardwareVideoCodecs.H264` for two-pass encoding (`FirstPassStats`) or a decoder: both throw.
- Turning B-frames on by default. Some GPUs predict B pictures from the earlier reference only and files get bigger; they are
  off unless `H264EncoderOptions.BFrames` asks.

## More

- Hardware video: https://inexin.github.io/Egami/v1/articles/guides/video-hardware.html
- Video guide: https://inexin.github.io/Egami/v1/articles/guides/video.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
