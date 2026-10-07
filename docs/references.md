# Libraries we may learn from

These permissively licensed projects may be studied for ideas. Study the idea, then write your own C#. If code is ever translated closely, its license notice goes into [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md) in the same pull request.

Licenses were checked on 2026-10-07 against each project's own license file or package manifest.

| Library | Language | License | What to learn from it | Egami step |
|---|---|---|---|---|
| [Skia](https://skia.org) / [SkiaSharp](https://github.com/mono/SkiaSharp) | C++ / C# | BSD-3 / MIT | Codec design, resampling and blending | All |
| [StbImageSharp](https://github.com/StbSharp/StbImageSharp) | C# | Public domain or MIT | A managed port of stb_image | 1.1, 1.3, 1.5 |
| [stb](https://github.com/nothings/stb) | C | MIT or public domain | Compact decoders and encoders; stb_image_resize2 for SIMD resampling | All |
| [BigGustave](https://github.com/EliotJones/BigGustave) | C# | Public domain | Small, readable managed PNG reader and writer | 1.1 |
| [Wuffs](https://github.com/google/wuffs) | Wuffs, generated C | MIT or Apache 2.0 | One of the fastest PNG decoders | 1.1, 1.4 |
| [image-rs png](https://github.com/image-rs/image-png) | Rust | MIT or Apache 2.0 | Safe, fast PNG close to how C# would look | 1.1 |
| [image-rs image](https://github.com/image-rs/image) | Rust | MIT or Apache 2.0 | Overall library design | Core |
| [zune-image](https://github.com/etemesi254/zune-image) | Rust | MIT or Apache 2.0 | Very fast JPEG and PNG with SIMD | 1.1, 1.3 |
| [fast_image_resize](https://github.com/Cykooz/fast_image_resize) | Rust | MIT or Apache 2.0 | SIMD resize kernels | 1.2 |
| [libspng](https://github.com/randy408/libspng) | C | BSD-2 | Modern, simple PNG reader and writer | 1.1 |
| [LodePNG](https://github.com/lvandeve/lodepng) | C/C++ | zlib | Complete PNG encoder | 1.1, 1.4 |
| [QOI reference](https://github.com/phoboslab/qoi) | C | MIT | The whole QOI format | 1.5 |
| [Pfim](https://github.com/nickbabcock/Pfim) | C# | MIT | Managed TGA and DDS readers | 1.5 |
| [Go image packages](https://github.com/golang/go/tree/master/src/image) | Go | BSD-3 | Readable PNG, JPEG and GIF codecs | 1.1, 1.3, 1.4 |
| [Pillow](https://github.com/python-pillow/Pillow) | C and Python | HPND | Resampling and color operations | 1.2 |

Specifications, textbooks and papers are always fine. Code under GPL, LGPL or AGPL, and code with no license, is never used.
