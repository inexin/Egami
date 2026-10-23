# Egami.Video.H264

Egami.Video.H264 decodes H.264 (AVC) video for Egami.Video, in managed code.

- Register it once with `WithH264()`; nothing registers it on its own. Without it, Egami.Video reads H.264 tracks' packets and
  copies them through untouched, but does not decode them.
- Progressive Constrained Baseline, Main, High, High 10 and High 4:2:2 streams, 8 and 10 bit, and their Intra profiles, from
  MP4, MOV, Matroska, AVI and raw Annex B `.h264` files (`H264Format`). Frames equal FFmpeg's decode.
- Slices decode in parallel and deblocking runs on several threads; broken slices are concealed from the frame before.

Interlaced streams, 4:4:4, lossless and the Extended profile are not decoded.

## Patents

Egami.Video.H264 decodes H.264, a format covered by patents. Egami's authors grant no patent rights and are not lawyers.
Whether you need a patent licence depends on your country and on how you use and distribute this package; commercial products
may need one from the AVC patent pools or holders. Egami.Video contains no H.264 decoder and does not depend on this package.

Egami is MIT-licensed, fully managed C# for .NET 10: no native binaries, no P/Invoke and no third-party dependencies, trimming
and Native AOT clean. Documentation: [inexin.github.io/Egami](https://inexin.github.io/Egami/). Source and changelog:
[github.com/inexin/Egami](https://github.com/inexin/Egami).
