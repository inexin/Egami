# Egami.Video.Hardware: guide for coding agents

Egami.Video.Hardware reaches the GPU's video engine through Vulkan Video on Windows and Linux. This preview finds the hardware
and lists what it can do (H.264 and HEVC, decode and encode); the hardware decoders and encoders come in later previews. It is
Egami's only package that calls native code, and only the Vulkan loader the GPU driver installs.

Namespaces: `Egami.Video.Hardware`, with `Egami.Video` (`CodecIds`).

{{shared}}

## Entry points

- `HardwareVideoCodecs.IsAvailable(codecId, HardwareDirection)`: true when a GPU decodes or encodes the codec. Pass a
  `CodecIds` value (`CodecIds.Avc`, `CodecIds.Hevc`). Never throws; the answer is cached for the process.
- `HardwareVideoCodecs.GetDevices()`: `HardwareVideoDevice`s with their `HardwareVideoProfile`s (size limits, level,
  references, `HardwareRateControl`, quality levels). Empty without a GPU; `HardwareVideoCodecs.UnavailableReason` says why.
- `HardwareVideoException`: the driver refused or failed an operation; `ResultCode` is the negative `VkResult`.

## Samples

Check for hardware:

{{sample VideoHardware.cs#available}}

List devices and profiles:

{{sample VideoHardware.cs#devices}}

## Common mistakes

- Assuming hardware exists. Servers, CI runners and 32-bit processes have none: check `IsAvailable` and keep the software
  codec as the fallback.
- Expecting the same bytes as Egami's software encoders, or across GPUs and drivers. Hardware output differs.
- Passing codec names such as "h264". Use the `CodecIds` constants.

## More

- Hardware video: https://inexin.github.io/Egami/v1/articles/guides/video-hardware.html
- Video guide: https://inexin.github.io/Egami/v1/articles/guides/video.html
- Every page in one list: https://inexin.github.io/Egami/v1/llms.txt
