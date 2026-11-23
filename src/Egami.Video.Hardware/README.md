# Egami.Video.Hardware

Egami.Video.Hardware decodes and encodes video on the GPU's video engine through Vulkan Video, on Windows and Linux. It is a
preview: this release finds the hardware, reports what it can do, and decodes and encodes H.264 and HEVC.

- `HardwareVideoCodecs.IsAvailable(codecId, direction)` says whether a GPU can decode or encode a codec. It never throws.
- `HardwareVideoCodecs.GetDevices()` lists the GPUs and their profiles: H.264 and HEVC (Main, Main 10), picture size limits,
  levels, reference pictures, and for encoders the rate control modes and quality levels.
- `HardwareVideoCodecs.H264` encodes H.264 on the GPU with the same options as Egami's software encoder: hand it to
  `VideoWriterOptions.Codec`. On a GeForce GTX 1070 it writes about 300 frames per second at 1080p.
- It also decodes progressive 8-bit 4:2:0 H.264, to the same samples as Egami's software decoder: add it with
  `MediaContext.WithCodec` and `VideoReader` uses it. On a GeForce GTX 1070 it reads about 200 frames per second at 1080p.
- `HardwareVideoCodecs.Hevc` does the same for HEVC: it encodes Main and Main 10 with the software HEVC encoder's options, and
  decodes 4:2:0 Main and Main 10 to the same samples as Egami's software decoder.
- Needs a GPU driver with Vulkan 1.3 and Vulkan Video (current NVIDIA, AMD and Intel drivers) and a 64-bit process. Without
  one, hardware video is simply not available.

Egami.Video's software codecs stay the default: hardware is used only when an app asks for it.

## Native code

This is Egami's one package that calls native code, and only the Vulkan loader that the GPU driver installs (`vulkan-1.dll` on
Windows, `libvulkan.so.1` on Linux). It ships no native binary and has no third-party dependencies: its Vulkan bindings are
generated from the Khronos registry (Apache-2.0 or MIT). It is trimming and Native AOT clean.

## Patents

H.264 and HEVC are covered by patents. Egami's authors grant no patent rights and are not lawyers. The GPU vendor's licence may
or may not cover your use of its video engine; check before you ship.

Egami is MIT-licensed C# for .NET 10. Documentation: [inexin.github.io/Egami](https://inexin.github.io/Egami/). Source and
changelog: [github.com/inexin/Egami](https://github.com/inexin/Egami).
