# Benchmarks

How fast Egami is next to ImageSharp, SkiaSharp and Magick.NET on the same tasks and inputs. This page has 260 comparisons
(each case against each library timed on it). Egami is faster in 234 of them, within 5% in 16, and more than 5% slower in 10,
led by lossless WebP at effort 4 on screen content, where SkiaSharp's native libwebp is about 10 times faster. Against
ImageSharp, Egami is typically 1.5 to 3 times faster.

[Performance](performance.md) has the tips that keep your own code fast and the benchmarks that ship with Egami.

## Summary

Each library has its own column with its typical time relative to Egami's on the same task and input (the geometric mean over
the cases). Egami is the 1.00× baseline: 2.00× means that library takes twice as long, and below 1.00× it is faster than Egami.
An empty cell means that library was not measured for the area. Cases counts the scenarios in the area; a
scenario timed against two libraries makes two comparisons. Areas marked Oct 30 were rerun
on 30 October 2026; the rest come from runs between 9 and 22 October.

| Area | Cases | Egami | ImageSharp | SkiaSharp | Magick.NET |
| --- | --- | --- | --- | --- | --- |
| JPEG decode (Oct 30) | 24 | 1.00× | 1.47× | 2.11× |  |
| JPEG encode (Oct 30) | 4 | 1.00× | 1.41× |  |  |
| JPEG probe, EXIF and thumbnail | 6 | 1.00× | 3.03× |  |  |
| PNG decode (Oct 30) | 6 | 1.00× | 2.27× | 2.09× |  |
| PNG probe | 6 | 1.00× | 38.09× | 1.44× |  |
| PNG encode (Oct 30) | 8 | 1.00× | 2.48× | 2.74× |  |
| WebP lossless decode (Oct 30) | 2 | 1.00× | 3.50× | 1.53× |  |
| WebP lossy decode (Oct 30) | 2 | 1.00× | 1.61× | 1.01× |  |
| WebP lossy encode, effort 4 (Oct 30) | 2 | 1.00× | 3.48× | 0.99× |  |
| WebP lossless encode, effort 0 (Oct 30) | 2 | 1.00× | 7.34× | 1.37× |  |
| WebP lossless encode, effort 4 (Oct 30) | 2 | 1.00× | 2.03× | 0.31× |  |
| WebP at matched file size (Oct 30) | 4 | 1.00× |  | 1.62× |  |
| Simple formats decode | 2 | 1.00× | 1.46× |  |  |
| Simple formats encode (BMP, PNM, QOI, TGA) | 18 | 1.00× | 3.25× |  |  |
| GIF and APNG | 24 | 1.00× | 2.57× |  |  |
| Reduced-size decode | 12 | 1.00× | 1.09× |  |  |
| Operations (resize, blur, rotate, fill and more) | 36 | 1.00× | 3.39× |  |  |
| Blend modes (DrawImage) | 18 | 1.00× | 1.43× | 3.36× |  |
| Compositor blend | 3 | 1.00× | 1.06× |  |  |
| Scoped operations (rectangle, mask) | 4 | 1.00× | 5.40× |  | 35.46× |
| Pixel conversion | 18 | 1.00× | 2.43× |  |  |
| Color space conversion | 2 | 1.00× |  | 1.68× |  |
| Channel extract, combine, shuffle | 6 | 1.00× |  |  | 5.44× |
| Color keying | 10 | 1.00× |  |  | 10.82× |

## Where Egami is slower

The one large gap left is lossless WebP at effort 4 on screen content, about 10× slower than libwebp; at effort 0 Egami matches libwebp there and writes a smaller file. The rest are within 15% or single cases.

| Case | Egami | ImageSharp | SkiaSharp |
| --- | --- | --- | --- |
| WebP lossless encode, effort 4, screenshot, all cores (Oct 30) | 118 ms | 208 ms | 11.9 ms |
| Pixel conversion, Rgba8 to GrayAlpha16 | 8.95 ms | 2.80 ms |  |
| Reduced-size decode, TGA 32-bit, all cores | 8.03 ms | 5.91 ms |  |
| DrawImage DestinationIn, 1 thread | 615.8 µs | 524.1 µs | 1.22 ms |
| WebP lossless encode, matched size, screenshot, 1 thread (Oct 30) | 14.1 ms |  | 12.0 ms |
| Reduced-size decode, TGA 32-bit, 1 thread | 7.60 ms | 6.68 ms |  |
| Pixel conversion, Argb8 to Rgba8 | 97.1 µs | 86.2 µs |  |
| Reduced-size decode, PNG screenshot, all cores | 7.29 ms | 6.56 ms |  |
| WebP lossless encode, effort 4, photo, all cores (Oct 30) | 1,039 ms | 2,437 ms | 964 ms |
| WebP lossy decode, photo, all cores (Oct 30) | 23.7 ms | 40.5 ms | 22.1 ms |

At 1 thread, lossless WebP at effort 4 on the photo is also about 2.3× slower than libwebp (Egami 2,283 ms).

The JPEG quality 90 case is not like for like: Egami writes optimized Huffman tables there, ImageSharp does not.

## How it was measured

The numbers come from a comparison harness kept apart from the Egami repository, so that Egami itself never references the
other libraries. Most areas were run between 9 and 22 October 2026 against Egami's main branch on that day. JPEG and PNG were
rerun on 30 October against Egami cd0e6b41 (decode, after the speed work in #208) and 35d45808 (encode); WebP was rerun the same
evening against 6b8b07b9. Compare libraries within one run only: absolute times differ a little between runs.

- **Machine:** AMD Ryzen 9 3900X, 12 cores and 24 threads, Windows 10 22H2, .NET 10.0.12 x64 (AVX2).
- **Libraries:** ImageSharp 3.1.12, SkiaSharp (native Skia, libpng and libwebp), Magick.NET Q16 (native ImageMagick).
- **Job:** BenchmarkDotNet ShortRun: 3 warmups and 3 iterations. Error bars are wide on some rows, so treat differences under
  about 10% as a tie.
- **Threads:** "1 thread" sets Egami's `MaxParallelism` and ImageSharp's `MaxDegreeOfParallelism` to 1; "All cores" leaves them
  at the processor count. SkiaSharp and Magick.NET choose their own threading.
- **Left out:** drawing, text, SVG, barcode and video speed are timed separately and are not on this page.

## Full results

Every comparison row, codecs first, then operations. Times are BenchmarkDotNet means; the last columns are the other library's
time divided by Egami's.

### JPEG decode

Rerun Oct 30 against Egami cd0e6b41, the JPEG and PNG speed work merged as #208. Compare libraries within a run only: absolute times differ between runs for all libraries. Quarter and eighth size use each library's reduced decode; SkiaSharp was timed at full size only.

| Case | File | Threads | Egami | ImageSharp | SkiaSharp | ImageSharp ÷ Egami | SkiaSharp ÷ Egami |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Full size | 420 | All cores | 33.83 ms | 61.16 ms | 93.74 ms | 1.81× | 2.77× |
| Full size, fast upsampling | 420 | All cores | 31.53 ms |  |  |  |  |
| Quarter size | 420 | All cores | 40.11 ms | 45.34 ms |  | 1.13× |  |
| Eighth size | 420 | All cores | 29.47 ms | 37.85 ms |  | 1.28× |  |
| Full size | 420 | 1 thread | 48.73 ms | 60.86 ms | 93.27 ms | 1.25× | 1.91× |
| Full size, fast upsampling | 420 | 1 thread | 49.72 ms |  |  |  |  |
| Quarter size | 420 | 1 thread | 39.99 ms | 45.86 ms |  | 1.15× |  |
| Eighth size | 420 | 1 thread | 30.54 ms | 37.50 ms |  | 1.23× |  |
| Full size | 444 | All cores | 36.85 ms | 78.44 ms | 106 ms | 2.13× | 2.88× |
| Full size, fast upsampling | 444 | All cores | 38.62 ms |  |  |  |  |
| Quarter size | 444 | All cores | 47.81 ms | 59.70 ms |  | 1.25× |  |
| Eighth size | 444 | All cores | 39.80 ms | 42.43 ms |  | 1.07× |  |
| Full size | 444 | 1 thread | 62.58 ms | 78.11 ms | 107 ms | 1.25× | 1.71× |
| Full size, fast upsampling | 444 | 1 thread | 60.74 ms |  |  |  |  |
| Quarter size | 444 | 1 thread | 49.65 ms | 61.57 ms |  | 1.24× |  |
| Eighth size | 444 | 1 thread | 37.87 ms | 42.52 ms |  | 1.12× |  |
| Full size | gray | All cores | 29.24 ms | 48.04 ms | 73.14 ms | 1.64× | 2.50× |
| Full size, fast upsampling | gray | All cores | 28.09 ms |  |  |  |  |
| Quarter size | gray | All cores | 32.88 ms | 38.63 ms |  | 1.17× |  |
| Eighth size | gray | All cores | 27.73 ms | 32.27 ms |  | 1.16× |  |
| Full size | gray | 1 thread | 36.22 ms | 47.41 ms | 71.50 ms | 1.31× | 1.97× |
| Full size, fast upsampling | gray | 1 thread | 36.11 ms |  |  |  |  |
| Quarter size | gray | 1 thread | 32.50 ms | 38.27 ms |  | 1.18× |  |
| Eighth size | gray | 1 thread | 27.34 ms | 33.87 ms |  | 1.24× |  |
| Full size | progressive | All cores | 67.38 ms | 148 ms | 168 ms | 2.20× | 2.50× |
| Full size, fast upsampling | progressive | All cores | 67.27 ms |  |  |  |  |
| Quarter size | progressive | All cores | 84.55 ms | 128 ms |  | 1.52× |  |
| Eighth size | progressive | All cores | 18.32 ms | 127 ms |  | 6.95× |  |
| Full size | progressive | 1 thread | 136 ms | 147 ms | 168 ms | 1.09× | 1.24× |
| Full size, fast upsampling | progressive | 1 thread | 136 ms |  |  |  |  |
| Quarter size | progressive | 1 thread | 130 ms | 129 ms |  | 0.99× |  |
| Eighth size | progressive | 1 thread | 28.43 ms | 119 ms |  | 4.19× |  |

### JPEG encode

Rerun Oct 30 against Egami main 35d45808. Quality 90 is now timed both plain, like ImageSharp, and with Egami's optimized Huffman tables, which ImageSharp does not offer.

| Case | Threads | Egami | ImageSharp | ImageSharp ÷ Egami |
| --- | --- | --- | --- | --- |
| Quality 75, half chroma | All cores | 46.81 ms | 67.60 ms | 1.44× |
| Quality 90, full chroma | All cores | 57.06 ms | 117 ms | 2.05× |
| Quality 90, full chroma, optimized | All cores | 150 ms |  |  |
| Quality 75, half chroma | 1 thread | 53.34 ms | 67.40 ms | 1.26× |
| Quality 90, full chroma | 1 thread | 110 ms | 117 ms | 1.06× |
| Quality 90, full chroma, optimized | 1 thread | 152 ms |  |  |

### JPEG probe, EXIF and thumbnail

Run Oct 9.

| Case | Threads | Egami | ImageSharp | ImageSharp ÷ Egami |
| --- | --- | --- | --- | --- |
| Probe 50 files | All cores | 390.9 µs | 824.3 µs | 2.11× |
| EXIF 50 files | All cores | 497.2 µs | 5.73 ms | 11.52× |
| Thumbnail | All cores | 32.72 ms | 38.71 ms | 1.18× |
| Probe 50 files | 1 thread | 393.9 µs | 832.1 µs | 2.11× |
| EXIF 50 files | 1 thread | 474.7 µs | 5.13 ms | 10.80× |
| Thumbnail | 1 thread | 32.41 ms | 38.53 ms | 1.19× |

### PNG decode and probe

Decode rerun Oct 30 against Egami cd0e6b41 (#208); the probe rows are from Oct 18. Egami's probe reads only the header; ImageSharp's reads further into the file.

| Method | Scenario | Egami | ImageSharp | SkiaSharp | ImageSharp ÷ Egami | SkiaSharp ÷ Egami |
| --- | --- | --- | --- | --- | --- | --- |
| Decode | Adam7-RGBA8 | 29.15 ms | 41.88 ms | 58.24 ms | 1.44× | 2.00× |
| Decode | Gray1 | 5.45 ms | 23.74 ms | 20.64 ms | 4.36× | 3.79× |
| Decode | Palette8 | 1.26 ms | 8.15 ms | 2.51 ms | 6.48× | 1.99× |
| Decode | Photo-RGBA8 | 71.18 ms | 102 ms | 103 ms | 1.43× | 1.45× |
| Decode | RGB16 | 49.91 ms | 74.05 ms | 61.60 ms | 1.48× | 1.23× |
| Decode | Screenshot-RGB8 | 1.09 ms | 1.75 ms | 3.33 ms | 1.61× | 3.06× |
| Probe (Oct 18) | Adam7-RGBA8 | 2.0 µs | 483.7 µs | 2.3 µs | 241.53× | 1.13× |
| Probe (Oct 18) | Gray1 | 880 ns | 2.9 µs | 2.1 µs | 3.33× | 2.36× |
| Probe (Oct 18) | Palette8 | 2.1 µs | 4.0 µs | 2.3 µs | 1.94× | 1.11× |
| Probe (Oct 18) | Photo-RGBA8 | 1.8 µs | 1.67 ms | 1.7 µs | 939.16× | 0.95× |
| Probe (Oct 18) | RGB16 | 1.6 µs | 793.2 µs | 1.7 µs | 497.18× | 1.08× |
| Probe (Oct 18) | Screenshot-RGB8 | 1.0 µs | 4.4 µs | 3.0 µs | 4.19× | 2.89× |

### PNG encode

Rerun Oct 30 against Egami main 35d45808.

| Scenario | Level | Egami | ImageSharp | SkiaSharp | ImageSharp ÷ Egami | SkiaSharp ÷ Egami |
| --- | --- | --- | --- | --- | --- | --- |
| Photo-RGBA8 | 1 | 305 ms | 905 ms | 900 ms | 2.97× | 2.95× |
| Photo-RGBA8 | 5 | 1,118 ms | 1,898 ms | 1,670 ms | 1.70× | 1.49× |
| Photo-RGBA8 | 6 | 2,213 ms | 4,694 ms | 3,679 ms | 2.12× | 1.66× |
| Photo-RGBA8 | 9 | 3,057 ms | 14,567 ms | 7,481 ms | 4.76× | 2.45× |
| Screenshot-RGB8 | 1 | 18.25 ms | 33.55 ms | 67.46 ms | 1.84× | 3.70× |
| Screenshot-RGB8 | 5 | 16.31 ms | 49.85 ms | 84.68 ms | 3.06× | 5.19× |
| Screenshot-RGB8 | 6 | 16.75 ms | 52.64 ms | 94.48 ms | 3.14× | 5.64× |
| Screenshot-RGB8 | 9 | 79.55 ms | 127 ms | 129 ms | 1.59× | 1.62× |

### WebP lossy decode

Rerun Oct 30 against Egami main 6b8b07b9 (ShortRun). All cores; 1 thread was within a few percent.

| Picture | Egami | ImageSharp | SkiaSharp | ImageSharp ÷ Egami | SkiaSharp ÷ Egami |
| --- | --- | --- | --- | --- | --- |
| photo | 23.7 ms | 40.5 ms | 22.1 ms | 1.71× | 0.93× |
| screenshot | 24.2 ms | 36.6 ms | 26.5 ms | 1.51× | 1.10× |

### WebP lossy encode

Rerun Oct 30 against Egami main 6b8b07b9. Quality 75, effort 4, all cores; 1 thread was within a few percent. SkiaSharp runs native libwebp.

| Picture | Egami | ImageSharp | SkiaSharp | ImageSharp ÷ Egami | SkiaSharp ÷ Egami |
| --- | --- | --- | --- | --- | --- |
| photo | 248 ms | 884 ms | 241 ms | 3.56× | 0.97× |
| screenshot | 167 ms | 567 ms | 169 ms | 3.40× | 1.01× |

### WebP lossless encode

Rerun Oct 30 against Egami main 6b8b07b9, all cores. SkiaSharp runs libwebp at its own default, so its time is the same in both effort rows; Egami's effort sets how hard it searches. At 1 thread Egami takes 671 ms (effort 0) and 2,283 ms (effort 4) on the photo and 127 ms on the screenshot at effort 4.

| Picture | Egami effort | Egami | ImageSharp | SkiaSharp | ImageSharp ÷ Egami | SkiaSharp ÷ Egami |
| --- | --- | --- | --- | --- | --- | --- |
| photo | 0 | 537 ms | 1,612 ms | 961 ms | 3.00× | 1.79× |
| screenshot | 0 | 11.3 ms | 203 ms | 11.8 ms | 17.96× | 1.04× |
| photo | 4 | 1,039 ms | 2,437 ms | 964 ms | 2.35× | 0.93× |
| screenshot | 4 | 118 ms | 208 ms | 11.9 ms | 1.76× | 0.10× |

### WebP lossless decode

Run Oct 30 against Egami main 6b8b07b9, all cores.

| Picture | Egami | ImageSharp | SkiaSharp | ImageSharp ÷ Egami | SkiaSharp ÷ Egami |
| --- | --- | --- | --- | --- | --- |
| photo | 38.4 ms | 95.6 ms | 45.5 ms | 2.49× | 1.18× |
| screenshot | 1.71 ms | 8.40 ms | 3.37 ms | 4.91× | 1.97× |

### WebP at matched file size

Run Oct 30, 1 thread. SkiaSharp (libwebp at its defaults) against Egami at the lowest effort whose file is no larger than libwebp's. ImageSharp is not in this suite.

| Case | Egami effort | Egami size | SkiaSharp size | Egami | SkiaSharp | SkiaSharp ÷ Egami |
| --- | --- | --- | --- | --- | --- | --- |
| photo, lossless | 0 | 2,474,194 bytes | 2,931,846 bytes | 683 ms | 1,154 ms | 1.69× |
| photo, lossy q75 | 0 | 134,692 bytes | 192,232 bytes | 48.9 ms | 245 ms | 5.01× |
| screenshot, lossless | 0 | 2,564 bytes | 3,522 bytes | 14.1 ms | 12.0 ms | 0.85× |
| screenshot, lossy q75 | 4 | 366,354 bytes | 374,590 bytes | 171.5 ms | 169.0 ms | 0.99× |

### Simple formats decode

Run Oct 9.

| Threads | Egami | ImageSharp | ImageSharp ÷ Egami |
| --- | --- | --- | --- |
| All cores | 709.5 µs | 1.03 ms | 1.45× |
| 1 thread | 688.3 µs | 1.02 ms | 1.48× |

### Simple formats encode (BMP, PNM, QOI, TGA)

Run Oct 9. Times were the same at 1 thread and all cores, so all cores is shown.

| Case | Egami | ImageSharp | ImageSharp ÷ Egami |
| --- | --- | --- | --- |
| BMP 24-bit | 464.6 µs | 730.3 µs | 1.57× |
| BMP 32-bit | 180.7 µs | 937.7 µs | 5.19× |
| TGA 32-bit RLE | 3.62 ms | 109 ms | 29.99× |
| TGA 32-bit raw | 293.7 µs | 954.9 µs | 3.25× |
| QOI photo | 14.42 ms | 63.58 ms | 4.41× |
| QOI screenshot | 3.05 ms | 14.04 ms | 4.61× |
| PNM P6 | 196.2 µs | 369.3 µs | 1.88× |
| PNM P5 wide | 184.8 µs | 221.4 µs | 1.20× |
| PNM P3 | 4.03 ms | 4.32 ms | 1.07× |

### GIF and APNG

Run Oct 18.

| Case | Threads | Egami | ImageSharp | ImageSharp ÷ Egami |
| --- | --- | --- | --- | --- |
| GIF decode, animated | All cores | 38.85 ms | 54.56 ms | 1.40× |
| GIF decode, animated | 1 thread | 38.22 ms | 54.72 ms | 1.43× |
| GIF decode, still | All cores | 12.34 ms | 17.36 ms | 1.41× |
| GIF decode, still | 1 thread | 12.58 ms | 17.37 ms | 1.38× |
| GIF decode, first frame | All cores | 734.6 µs | 1.16 ms | 1.57× |
| GIF decode, first frame | 1 thread | 748.6 µs | 1.17 ms | 1.57× |
| GIF encode, photo dithered | All cores | 98.17 ms | 212 ms | 2.16× |
| GIF encode, photo dithered | 1 thread | 81.64 ms | 203 ms | 2.49× |
| GIF encode, photo undithered | All cores | 46.91 ms | 91.07 ms | 1.94× |
| GIF encode, photo undithered | 1 thread | 44.30 ms | 78.00 ms | 1.76× |
| GIF encode, exact colors | All cores | 54.79 ms | 97.90 ms | 1.79× |
| GIF encode, exact colors | 1 thread | 43.99 ms | 88.56 ms | 2.01× |
| GIF encode, animated, global palette | All cores | 193 ms | 342 ms | 1.77× |
| GIF encode, animated, global palette | 1 thread | 253 ms | 343 ms | 1.36× |
| GIF encode, animated, palette per frame | All cores | 128 ms | 2,898 ms | 22.73× |
| GIF encode, animated, palette per frame | 1 thread | 370 ms | 2,888 ms | 7.81× |
| APNG decode | All cores | 27.28 ms | 50.23 ms | 1.84× |
| APNG decode | 1 thread | 26.11 ms | 48.86 ms | 1.87× |
| APNG encode | All cores | 172 ms | 385 ms | 2.25× |
| APNG encode | 1 thread | 157 ms | 298 ms | 1.90× |
| Animated thumbnail | All cores | 92.64 ms | 185 ms | 2.00× |
| Animated thumbnail | 1 thread | 176 ms | 195 ms | 1.11× |
| Probe | All cores | 162.3 µs | 4.40 ms | 27.11× |
| Probe | 1 thread | 159.0 µs | 4.37 ms | 27.46× |

### Reduced-size decode

Run Oct 11.

| Scenario | Threads | Egami | ImageSharp | ImageSharp ÷ Egami |
| --- | --- | --- | --- | --- |
| bmp-24 | All cores | 5.67 ms | 5.38 ms | 0.95× |
| bmp-24 | 1 thread | 5.49 ms | 5.55 ms | 1.01× |
| jpeg-photo | All cores | 41.87 ms | 47.98 ms | 1.15× |
| jpeg-photo | 1 thread | 43.61 ms | 55.08 ms | 1.26× |
| png-photo | All cores | 126 ms | 178 ms | 1.41× |
| png-photo | 1 thread | 126 ms | 138 ms | 1.09× |
| png-screenshot | All cores | 7.29 ms | 6.56 ms | 0.90× |
| png-screenshot | 1 thread | 7.30 ms | 7.38 ms | 1.01× |
| qoi-rgb | All cores | 21.47 ms | 32.41 ms | 1.51× |
| qoi-rgb | 1 thread | 21.13 ms | 31.65 ms | 1.50× |
| tga-32 | All cores | 8.03 ms | 5.91 ms | 0.74× |
| tga-32 | 1 thread | 7.60 ms | 6.68 ms | 0.88× |

### Operations (resize, blur, rotate, fill and more)

Run Oct 18.

| Scenario | Threads | Egami | ImageSharp | ImageSharp ÷ Egami |
| --- | --- | --- | --- | --- |
| Blur, radius 25 | All cores | 48.10 ms | 79.65 ms | 1.66× |
| Blur, radius 25 | 1 thread | 119 ms | 1,183 ms | 9.97× |
| Blur, radius 3 | All cores | 2.48 ms | 9.78 ms | 3.94× |
| Blur, radius 3 | 1 thread | 19.44 ms | 280 ms | 14.42× |
| Color chain | All cores | 4.44 ms | 8.84 ms | 1.99× |
| Color chain | 1 thread | 34.99 ms | 80.51 ms | 2.30× |
| DrawImage | All cores | 106.4 µs | 305.3 µs | 2.87× |
| DrawImage | 1 thread | 362.9 µs | 571.2 µs | 1.57× |
| Fill | All cores | 5.11 ms | 20.01 ms | 3.92× |
| Fill | 1 thread | 15.92 ms | 20.77 ms | 1.30× |
| Flip horizontal | All cores | 2.06 ms | 2.57 ms | 1.25× |
| Flip horizontal | 1 thread | 4.43 ms | 4.41 ms | 1.00× |
| Resize, bicubic | All cores | 3.22 ms | 31.80 ms | 9.86× |
| Resize, bicubic | 1 thread | 23.21 ms | 30.76 ms | 1.33× |
| Resize, bilinear | All cores | 2.75 ms | 24.16 ms | 8.79× |
| Resize, bilinear | 1 thread | 15.43 ms | 23.45 ms | 1.52× |
| Resize, box | All cores | 2.00 ms | 21.44 ms | 10.73× |
| Resize, box | 1 thread | 12.39 ms | 20.02 ms | 1.62× |
| Resize, Lanczos3 | All cores | 4.81 ms | 38.51 ms | 8.01× |
| Resize, Lanczos3 | 1 thread | 33.38 ms | 40.60 ms | 1.22× |
| Resize, linear light | All cores | 7.59 ms | 96.50 ms | 12.72× |
| Resize, linear light | 1 thread | 67.99 ms | 89.25 ms | 1.31× |
| Resize, Mitchell | All cores | 3.64 ms | 33.80 ms | 9.29× |
| Resize, Mitchell | 1 thread | 23.99 ms | 32.71 ms | 1.36× |
| Resize, nearest | All cores | 116.1 µs | 293.5 µs | 2.53× |
| Resize, nearest | 1 thread | 446.2 µs | 810.3 µs | 1.82× |
| Rotate 15° | All cores | 4.85 ms | 82.14 ms | 16.92× |
| Rotate 15° | 1 thread | 47.15 ms | 1,053 ms | 22.33× |
| Rotate 90° | All cores | 9.18 ms | 20.23 ms | 2.20× |
| Rotate 90° | 1 thread | 17.64 ms | 153 ms | 8.68× |
| Sharpen | All cores | 1.87 ms | 5.01 ms | 2.68× |
| Sharpen | 1 thread | 14.52 ms | 96.69 ms | 6.66× |
| Thumbnail | All cores | 115 ms | 142 ms | 1.24× |
| Thumbnail | 1 thread | 116 ms | 143 ms | 1.23× |
| Upscale | All cores | 2.46 ms | 26.98 ms | 10.96× |
| Upscale | 1 thread | 16.45 ms | 26.32 ms | 1.60× |

ImageSharp's resize times barely change between 1 thread and all cores in this run, so most of the all-cores resize gap (8 to 13×) comes from Egami's parallel resize.

### Blend modes (DrawImage)

Run Oct 11. ImageSharp has no ColorDodge, SoftLight, Hue or Luminosity mode.

| Threads | Mode | Egami | ImageSharp | SkiaSharp | ImageSharp ÷ Egami | SkiaSharp ÷ Egami |
| --- | --- | --- | --- | --- | --- | --- |
| All cores | SourceOver | 240.9 µs | 302.0 µs | 1.25 ms | 1.25× | 5.17× |
| All cores | DestinationIn | 178.1 µs | 274.1 µs | 1.29 ms | 1.54× | 7.27× |
| All cores | Multiply | 149.7 µs | 284.0 µs | 1.40 ms | 1.90× | 9.35× |
| All cores | Screen | 154.0 µs | 278.3 µs | 1.38 ms | 1.81× | 8.99× |
| All cores | Overlay | 165.8 µs | 361.3 µs | 1.52 ms | 2.18× | 9.19× |
| All cores | ColorDodge | 1.04 ms |  | 2.38 ms |  | 2.30× |
| All cores | SoftLight | 400.8 µs |  | 3.15 ms |  | 7.86× |
| All cores | Hue | 736.5 µs |  | 3.74 ms |  | 5.08× |
| All cores | Luminosity | 580.2 µs |  | 3.39 ms |  | 5.85× |
| 1 thread | SourceOver | 350.0 µs | 612.0 µs | 1.03 ms | 1.75× | 2.93× |
| 1 thread | DestinationIn | 615.8 µs | 524.1 µs | 1.22 ms | 0.85× | 1.97× |
| 1 thread | Multiply | 580.9 µs | 638.4 µs | 1.05 ms | 1.10× | 1.80× |
| 1 thread | Screen | 576.0 µs | 678.2 µs | 1.15 ms | 1.18× | 1.99× |
| 1 thread | Overlay | 585.3 µs | 764.0 µs | 1.39 ms | 1.31× | 2.38× |
| 1 thread | ColorDodge | 2.98 ms |  | 2.99 ms |  | 1.00× |
| 1 thread | SoftLight | 2.00 ms |  | 3.70 ms |  | 1.84× |
| 1 thread | Hue | 2.79 ms |  | 4.19 ms |  | 1.50× |
| 1 thread | Luminosity | 2.65 ms |  | 3.61 ms |  | 1.36× |

### Compositor blend

Run Oct 11.

| Mode | Egami | ImageSharp | ImageSharp ÷ Egami |
| --- | --- | --- | --- |
| SourceOver | 8.56 ms | 8.79 ms | 1.03× |
| Multiply | 8.57 ms | 8.78 ms | 1.02× |
| Screen | 7.89 ms | 8.97 ms | 1.14× |

### Scoped operations (rectangle, mask)

Run Oct 11. Egami (whole image) is Egami's plain run over the full image, for reference. Rectangle blur is compared with ImageSharp, the mask with Magick.NET's write mask.

| Scenario | Threads | Egami | Egami (whole image) | ImageSharp | Magick.NET | Other ÷ Egami |
| --- | --- | --- | --- | --- | --- | --- |
| Blur inside a rectangle | All cores | 2.70 ms | 19.52 ms | 7.35 ms |  | 2.72× |
| Blur inside a rectangle | 1 thread | 13.20 ms | 123 ms | 142 ms |  | 10.72× |
| Color change inside a mask | All cores | 15.48 ms | 8.97 ms |  | 1,420 ms | 91.71× |
| Color change inside a mask | 1 thread | 94.28 ms | 57.92 ms |  | 1,293 ms | 13.71× |

### Pixel conversion

Run Oct 10. Converting one image to and from Rgba8 (ImageSharp's Rgba32).

| Pixel type | Direction | Egami | ImageSharp | ImageSharp ÷ Egami |
| --- | --- | --- | --- | --- |
| Abgr8 | From Rgba8 | 90.6 µs | 96.2 µs | 1.06× |
| Abgr8 | To Rgba8 | 89.8 µs | 86.0 µs | 0.96× |
| Alpha8 | From Rgba8 | 471.1 µs | 1.66 ms | 3.53× |
| Alpha8 | To Rgba8 | 795.1 µs | 6.36 ms | 8.00× |
| Argb8 | From Rgba8 | 89.2 µs | 87.9 µs | 0.99× |
| Argb8 | To Rgba8 | 97.1 µs | 86.2 µs | 0.89× |
| Bgr565 | From Rgba8 | 429.3 µs | 6.46 ms | 15.05× |
| Bgr565 | To Rgba8 | 414.0 µs | 7.28 ms | 17.58× |
| Bgra4444 | From Rgba8 | 4.54 ms | 7.02 ms | 1.55× |
| Bgra4444 | To Rgba8 | 2.08 ms | 7.34 ms | 3.53× |
| GrayAlpha16 | From Rgba8 | 8.95 ms | 2.80 ms | 0.31× |
| GrayAlpha16 | To Rgba8 | 1.65 ms | 1.59 ms | 0.97× |
| Rgb10A2 | From Rgba8 | 4.52 ms | 6.84 ms | 1.51× |
| Rgb10A2 | To Rgba8 | 4.52 ms | 9.71 ms | 2.15× |
| Rgb16 | From Rgba8 | 323.4 µs | 1.34 ms | 4.15× |
| Rgb16 | To Rgba8 | 819.6 µs | 1.83 ms | 2.23× |
| RgbaF16 | From Rgba8 | 1.96 ms | 9.93 ms | 5.07× |
| RgbaF16 | To Rgba8 | 2.85 ms | 14.58 ms | 5.11× |

### Color space conversion

Run Oct 10.

| Case | Egami | SkiaSharp | SkiaSharp ÷ Egami |
| --- | --- | --- | --- |
| Display P3 to sRGB | 142 ms | 255 ms | 1.79× |
| Adobe RGB to sRGB | 141 ms | 223 ms | 1.58× |

### Channel extract, combine, shuffle

Run Oct 11.

| Scenario | Threads | Egami | Magick.NET | Magick.NET ÷ Egami |
| --- | --- | --- | --- | --- |
| Combine | All cores | 129 ms | 177 ms | 1.37× |
| Combine | 1 thread | 117 ms | 204 ms | 1.75× |
| Extract | All cores | 79.08 ms | 156 ms | 1.98× |
| Extract | 1 thread | 77.61 ms | 161 ms | 2.07× |
| Shuffle | All cores | 3.34 ms | 550 ms | 164.63× |
| Shuffle | 1 thread | 34.36 ms | 548 ms | 15.95× |

### Color keying

Run Oct 11.

| Scenario | Threads | Egami | Magick.NET | Magick.NET ÷ Egami |
| --- | --- | --- | --- | --- |
| Flood fill | All cores | 232 ms | 250 ms | 1.08× |
| Flood fill | 1 thread | 224 ms | 252 ms | 1.12× |
| Replace color | All cores | 6.70 ms | 378 ms | 56.40× |
| Replace color | 1 thread | 54.39 ms | 388 ms | 7.14× |
| Stack mean | All cores | 13.72 ms | 482 ms | 35.13× |
| Stack mean | 1 thread | 131 ms | 438 ms | 3.34× |
| Stack median | All cores | 28.45 ms | 2,537 ms | 89.19× |
| Stack median | 1 thread | 259 ms | 2,506 ms | 9.68× |
| Make color transparent | All cores | 6.75 ms | 409 ms | 60.61× |
| Make color transparent | 1 thread | 56.23 ms | 412 ms | 7.33× |
