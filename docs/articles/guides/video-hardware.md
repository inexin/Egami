# Hardware video

Egami.Video.Hardware decodes and encodes video on the GPU's video engine, the fixed-function block that NVIDIA, AMD and
Intel GPUs carry next to their shader cores. It reaches the engine through Vulkan Video, the cross-vendor video API of the
Vulkan driver, on Windows and Linux. A hardware encoder runs many times faster than a software one and leaves the CPU almost
idle; the price is bigger files at the same quality than Egami's own encoders at their default effort, and output that
differs between GPU models and driver versions.

The package is a preview. This release finds the hardware and reports what it can do; the hardware decoders and encoders
follow in the next previews. Egami.Video's software codecs stay the default: hardware is used only when an app asks for it.

## What it needs

- A GPU and driver with Vulkan 1.3 and the Vulkan Video extensions (`VK_KHR_video_queue` and the decode or encode queue
  and codec extensions). Current NVIDIA, AMD and Intel drivers on Windows and Linux have them.
- A 64-bit process on Windows or Linux.
- Nothing to install with the app. Egami ships no native code: the package calls the Vulkan loader the graphics driver
  installs (`vulkan-1.dll` on Windows, `libvulkan.so.1` on Linux) through one exported function and the function pointers it
  hands back. On a machine without that loader, such as a server without a GPU or a CI runner, hardware video is simply not
  available.

## Is hardware video available?

`HardwareVideoCodecs.IsAvailable` answers for a codec, by its `CodecIds` value, and a `HardwareDirection`. The first call asks
the driver and keeps the answer for the life of the process. It never throws: without a GPU, a loader or Vulkan Video it
returns false.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHardware.cs#available)]

## Devices and profiles

`HardwareVideoCodecs.GetDevices` lists every GPU with video hardware Egami can use, as `HardwareVideoDevice`s: the name,
the `HardwareDeviceKind` (integrated, discrete, virtual), the PCI vendor and device ids, the Vulkan version and the driver.
Each device lists its `HardwareVideoProfile`s: the codec, the direction, the profile (H.264 High to decode; High, Main and
Baseline to encode; HEVC Main and Main 10), the bit depth, the smallest and largest picture, the highest level and how many
reference pictures a picture can use. Encoder profiles add the `HardwareRateControl` modes (constant quantiser, constant
bit rate, variable bit rate), the number of quality levels, the highest bit rate and how many references P and B pictures
can use: an encoder whose `MaxBackwardReferences` is 0 writes no B pictures. When the list is empty,
`HardwareVideoCodecs.UnavailableReason` says why.

[!code-csharp[](../../../tests/Egami.DocSamples/VideoHardware.cs#devices)]

On a GeForce GTX 1070 with driver 582.66 it prints, among others:

```text
NVIDIA GeForce GTX 1070 (Vulkan 1.4.312, NVIDIA 582.66)
  H.264 decode High 8-bit, 48x16 to 4096x4096, level 5.2, 16 references
  HEVC decode Main 10 10-bit, 144x144 to 8192x8192, level 6.0, 16 references
  H.264 encode High 8-bit, 48x32 to 4096x4096, level 5.2, 16 references, rate control ConstantQp, ConstantBitrate, VariableBitrate, 5 quality levels, P 1 / B 1
  HEVC encode Main 8-bit, 96x32 to 4096x4096, level 6.2, 15 references, rate control ConstantQp, ConstantBitrate, VariableBitrate, 5 quality levels, P 1 / B 0
```

AV1 hardware (newer GPUs) is not listed yet. Every profile is 4:2:0.

## Errors

When the driver refuses or fails an operation, the package throws `HardwareVideoException`, an `EgamiException` whose
`ResultCode` holds the driver's result (a negative `VkResult`, such as -4 when the driver reset the GPU). The probe itself
never throws.

## Troubleshooting

- On Windows, overlay layers that games install (Steam, Epic, GOG Galaxy) hook every Vulkan app. If the probe is slow or
  fails, set the environment variable `VK_LOADER_LAYERS_DISABLE=~implicit~` to turn them off for the process.
- Set `EGAMI_VULKAN_VALIDATION=1` to turn on the Khronos validation layer when the Vulkan SDK is installed: it reports misuse
  of the API on the console or in the debugger's output.
