# Egami.Video.Hevc

Egami.Video.Hevc decodes and encodes HEVC (H.265) video for Egami.Video, in managed code, on Egami.Heic's HEVC decoder and
encoder core.

- Register it once with `WithHevc()`; nothing registers it on its own. Without it, Egami.Video reads HEVC tracks' packets and
  copies them through untouched, but does not decode them.
- Main, Main 10, Main 12 and the format range extensions to 12 bits (4:0:0, 4:2:0, 4:2:2 and 4:4:4), from MP4, MOV, Matroska
  and raw Annex B `.h265` files (`HevcFormat`), and the HEVC base layer of Dolby Vision profile 8 files. Frames equal the
  reference decoder's.
- HDR10 mastering display and content light level, and HDR10+ metadata, come with the frames.
- Tiles, wavefronts and slices decode on several threads; broken slices can be concealed.
- Encodes 4:2:0 Main and Main 10 streams through `VideoWriter` (MP4 hvc1, MOV, Matroska, or Annex B packets): B-frame
  pyramids, constant quality, quantizer, one- and two-pass bitrate and constant bitrate, adaptive quantization and HDR10
  metadata, the same stream on any number of threads. At the default effort it needs about 2 % fewer bits than x265's medium
  preset for the same PSNR, at 1.5 to 1.8 times its CPU time on one thread, and 12 % fewer than Egami's H.264 encoder.

Screen content coding, multi-layer HEVC and Dolby Vision enhancement layers are not decoded.

## Patents

Egami.Video.Hevc decodes and encodes HEVC, a format covered by patents. Egami's authors grant no patent rights and are not lawyers. Whether
you need a patent licence depends on your country and on how you use and distribute this package; commercial products may need
one from the HEVC patent pools or holders. Egami.Video contains no HEVC decoder or encoder and does not depend on this package.

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Documentation: [inexin.github.io/Egami](https://inexin.github.io/Egami/). Source and changelog:
[github.com/inexin/Egami](https://github.com/inexin/Egami).
