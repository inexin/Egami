# Third-party notices

Egami is licensed under the MIT license (see LICENSE).

This file lists every piece of outside work that Egami's source was closely translated from or modeled on, with its license
notice.

## Independent JPEG Group

This software is based in part on the work of the Independent JPEG Group.

Egami's JPEG reader and writer are written from ITU-T T.81 and the published DCT papers, but are made to produce the same
output as libjpeg-turbo, which builds on the IJG's libjpeg: its accurate and scaled IDCT constants, color conversion and
downsampling rounding, and quantization scheme were studied for that purpose. libjpeg is distributed under the IJG license,
which asks derived works to carry the sentence above.

## RFC 6386 (VP8) tables, transcribed through TwelveMonkeys

Egami's VP8 decoder is written from RFC 6386. Its constant tables (the default and update probabilities of the coefficient
tokens, the 4 × 4 intra mode probabilities and the quantizer step tables, in `src/Egami/Formats/Webp/Vp8Tables.cs`) are the RFC's
data, transcribed from the copy in TwelveMonkeys' VP8 decoder (`imageio-webp`, `vp8/Globals.java`), which is distributed under
the BSD 3-clause license:

    Copyright (c) 2017, Brooss, Harald Kuhr
    All rights reserved.

    Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
    following conditions are met:

    * Redistributions of source code must retain the above copyright notice, this list of conditions and the following
      disclaimer.
    * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
      disclaimer in the documentation and/or other materials provided with the distribution.
    * Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products
      derived from this software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
    INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
    SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
    SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
    WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
    OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

The RFC's own code components are licensed by the IETF Trust under the Simplified BSD License.

## libwebp

Egami's VP8 encoder and lossy alpha writer (`src/Egami/Formats/Webp/Vp8Encoder*.cs`, `Vp8EncDsp.cs`, `Vp8Costs.cs`,
`WebpAlphaEncoder.cs`), its sharp RGB to YUV (`WebpSharpYuv.cs`) and its composition of animation frames (`WebpAnimation.cs`)
follow libwebp's design: its segment analysis, quality curve, quantizer bias and sharpening, rate-distortion mode choice and
trellis, token probability choice, alpha level reduction, sharp YUV iteration and WebPAnimDecoder's blending and key frames,
with some of its constants. The code is Egami's own, written against libwebp 1.3.2 (`src/enc`, `src/dsp`, `sharpyuv`,
`src/demux/anim_decode.c`), which is distributed under the BSD 3-clause license:

    Copyright (c) 2010, Google Inc. All rights reserved.

    Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
    following conditions are met:

      * Redistributions of source code must retain the above copyright notice, this list of conditions and the following
        disclaimer.

      * Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
        disclaimer in the documentation and/or other materials provided with the distribution.

      * Neither the name of Google nor the names of its contributors may be used to endorse or promote products derived from
        this software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
    INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
    SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
    SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
    WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
    OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## libtiff

<https://gitlab.com/libtiff/libtiff>

`src/Egami/Formats/Tiff/LogLuvTable.cs` is the LogLuv24 chromaticity table of libtiff's `uvcode.h` (generated by Greg Ward
Larson, SGI, 1997), by way of Lars's ClassicMac. The rest of Egami's TIFF reader and writer follows the TIFF specification and
Adobe's technical notes, with libtiff as a behavioural reference.

```
Copyright (c) 1988-1997 Sam Leffler
Copyright (c) 1991-1997 Silicon Graphics, Inc.

Permission to use, copy, modify, distribute, and sell this software and
its documentation for any purpose is hereby granted without fee, provided
that (i) the above copyright notices and this permission notice appear in
all copies of the software and related documentation, and (ii) the names of
Sam Leffler and Silicon Graphics may not be used in any advertising or
publicity relating to the software without the specific, prior written
permission of Sam Leffler and Silicon Graphics.

THE SOFTWARE IS PROVIDED "AS-IS" AND WITHOUT WARRANTY OF ANY KIND,
EXPRESS, IMPLIED OR OTHERWISE, INCLUDING WITHOUT LIMITATION, ANY
WARRANTY OF MERCHANTABILITY OR FITNESS FOR A PARTICULAR PURPOSE.

IN NO EVENT SHALL SAM LEFFLER OR SILICON GRAPHICS BE LIABLE FOR
ANY SPECIAL, INCIDENTAL, INDIRECT OR CONSEQUENTIAL DAMAGES OF ANY KIND,
OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS,
WHETHER OR NOT ADVISED OF THE POSSIBILITY OF DAMAGE, AND ON ANY THEORY OF
LIABILITY, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE
OF THIS SOFTWARE.
```

## ImageMagick distortions

Egami's built-in warps (`src/Egami/Operations/Warps.cs`: Barrel, BarrelInverse, Polar, DePolar, Arc, Swirl, Implode and Wave) are
defined to give the same mapping as ImageMagick's `-distort`, `-swirl`, `-implode` and `-wave`, so ImageMagick's documentation
describes their parameters. Their formulas, defaults and best-fit canvas sizes were taken from ImageMagick's documentation and
studied in its source (`MagickCore/distort.c`, `MagickCore/visual-effects.c`) for that purpose. ImageMagick is distributed under
the ImageMagick License (https://imagemagick.org/script/license.php):

    Copyright @ 1999 ImageMagick Studio LLC, a non-profit organization dedicated to making software imaging solutions freely
    available.

    Licensed under the ImageMagick License (the "License"); you may not use this file except in compliance with the License.
    You may obtain a copy of the License at https://imagemagick.org/script/license.php

    Unless required by applicable law or agreed to in writing, software distributed under the License is distributed on an
    "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the specific
    language governing permissions and limitations under the License.

## Skia magnifier

`Warps.Magnifier` gives the same mapping as Skia's magnifier image filter, whose weighting was studied in Skia's
`sk_magnifier` shader (`src/sksl/sksl_rt_shader.sksl`) and `SkMagnifierImageFilter.cpp`. Skia is distributed under the BSD
3-clause license:

    Copyright (c) 2011 Google Inc. All rights reserved.

    Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
    following conditions are met: redistributions of source code must retain the above copyright notice, this list of
    conditions and the following disclaimer; redistributions in binary form must reproduce the above copyright notice, this
    list of conditions and the following disclaimer in the documentation and/or other materials provided with the
    distribution; neither the name of Google Inc. nor the names of its contributors may be used to endorse or promote products
    derived from this software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
    INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
    EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF
    USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
    LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
    OF THE POSSIBILITY OF SUCH DAMAGE.

## Unicode Character Database

Egami.Fonts' Unicode tables (`src/Egami.Fonts/Unicode/*.g.cs`) are generated from the Unicode Character Database 17.0, and its
tests read Unicode's conformance files (`tests/reference/unicode`). They are distributed under the Unicode License v3:

    UNICODE LICENSE V3

    COPYRIGHT AND PERMISSION NOTICE

    Copyright (c) 1991-2025 Unicode, Inc.

    NOTICE TO USER: Carefully read the following legal agreement. BY DOWNLOADING, INSTALLING, COPYING OR OTHERWISE USING DATA
    FILES, AND/OR SOFTWARE, YOU UNEQUIVOCALLY ACCEPT, AND AGREE TO BE BOUND BY, ALL OF THE TERMS AND CONDITIONS OF THIS
    AGREEMENT. IF YOU DO NOT AGREE, DO NOT DOWNLOAD, INSTALL, COPY, DISTRIBUTE OR USE THE DATA FILES OR SOFTWARE.

    Permission is hereby granted, free of charge, to any person obtaining a copy of data files and any associated documentation
    (the "Data Files") or software and any associated documentation (the "Software") to deal in the Data Files or Software
    without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, and/or sell
    copies of the Data Files or Software, and to permit persons to whom the Data Files or Software are furnished to do so,
    provided that either (a) this copyright and permission notice appear with all copies of the Data Files or Software, or (b)
    this copyright and permission notice appear in associated Documentation.

    THE DATA FILES AND SOFTWARE ARE PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT
    LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT OF THIRD PARTY RIGHTS.

    IN NO EVENT SHALL THE COPYRIGHT HOLDER OR HOLDERS INCLUDED IN THIS NOTICE BE LIABLE FOR ANY CLAIM, OR ANY SPECIAL INDIRECT
    OR CONSEQUENTIAL DAMAGES, OR ANY DAMAGES WHATSOEVER RESULTING FROM LOSS OF USE, DATA OR PROFITS, WHETHER IN AN ACTION OF
    CONTRACT, NEGLIGENCE OR OTHER TORTIOUS ACTION, ARISING OUT OF OR IN CONNECTION WITH THE USE OR PERFORMANCE OF THE DATA FILES
    OR SOFTWARE.

    Except as contained in this notice, the name of a copyright holder shall not be used in advertising or otherwise to promote
    the sale, use or other dealings in these Data Files or Software without prior written authorization of the copyright
    holder.

## HarfBuzz

Egami.Fonts' shaping engine (`src/Egami.Fonts/Shaping`) is translated in large part from HarfBuzz 14.5.1: the buffer and
its cluster and glyph-flag rules, the GSUB and GPOS lookup application, the shape plan and feature map, normalization, the
fallback positioning, the layout table checks, hb-subset's GSUB glyph closure and the language tag table (generated by
`tools/make-language-tags.py` from HarfBuzz's `hb-ot-tag-table.hh`), so that Egami shapes text exactly as hb-shape does.
HarfBuzz is distributed under the "Old MIT" license:

    HarfBuzz is licensed under the so-called "Old MIT" license.  Details follow.
    For parts of HarfBuzz that are licensed under different licenses see individual
    files names COPYING in subdirectories where applicable.

    Copyright © 2010-2022  Google, Inc.
    Copyright © 2015-2020  Ebrahim Byagowi
    Copyright © 2019,2020  Facebook, Inc.
    Copyright © 2012,2015  Mozilla Foundation
    Copyright © 2011  Codethink Limited
    Copyright © 2008,2010  Nokia Corporation and/or its subsidiary(-ies)
    Copyright © 2009  Keith Stribley
    Copyright © 2011  Martin Hosken and SIL International
    Copyright © 2007  Chris Wilson
    Copyright © 2005,2006,2020,2021,2022,2023  Behdad Esfahbod
    Copyright © 2004,2007,2008,2009,2010,2013,2021,2022,2023  Red Hat, Inc.
    Copyright © 1998-2005  David Turner and Werner Lemberg
    Copyright © 2016  Igalia S.L.
    Copyright © 2022  Matthias Clasen
    Copyright © 2018,2021  Khaled Hosny
    Copyright © 2018,2019,2020  Adobe, Inc
    Copyright © 2013-2015  Alexei Podtelezhnikov

    For full copyright notices consult the individual files in the package.


    Permission is hereby granted, without written agreement and without
    license or royalty fees, to use, copy, modify, and distribute this
    software and its documentation for any purpose, provided that the
    above copyright notice and the following two paragraphs appear in
    all copies of this software.

    IN NO EVENT SHALL THE COPYRIGHT HOLDER BE LIABLE TO ANY PARTY FOR
    DIRECT, INDIRECT, SPECIAL, INCIDENTAL, OR CONSEQUENTIAL DAMAGES
    ARISING OUT OF THE USE OF THIS SOFTWARE AND ITS DOCUMENTATION, EVEN
    IF THE COPYRIGHT HOLDER HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH
    DAMAGE.

    THE COPYRIGHT HOLDER SPECIFICALLY DISCLAIMS ANY WARRANTIES, INCLUDING,
    BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
    FITNESS FOR A PARTICULAR PURPOSE.  THE SOFTWARE PROVIDED HEREUNDER IS
    ON AN "AS IS" BASIS, AND THE COPYRIGHT HOLDER HAS NO OBLIGATION TO
    PROVIDE MAINTENANCE, SUPPORT, UPDATES, ENHANCEMENTS, OR MODIFICATIONS.

## OpenEXR

Egami's OpenEXR reader and writer are written from the OpenEXR file layout documentation, but closely follow OpenEXR's
decompressors so the pixels match its decode bit for bit: the PIZ wavelet and Huffman coder, PXR24, the B44 and B44A block
packing with their log table, DWAA and DWAB (the channel rules, zigzag order, AVX-ordered inverse DCT and the nonlinear
lookup), and RgbaInputFile's luminance-chroma to RGB filter and saturation fix were studied in OpenEXR 3.5.2. OpenEXR is
distributed under the BSD 3-clause license:

    Copyright (c) Contributors to the OpenEXR Project. All rights reserved.

    Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
    following conditions are met: redistributions of source code must retain the above copyright notice, this list of
    conditions and the following disclaimer; redistributions in binary form must reproduce the above copyright notice, this
    list of conditions and the following disclaimer in the documentation and/or other materials provided with the
    distribution; neither the name of the copyright holder nor the names of its contributors may be used to endorse or
    promote products derived from this software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
    INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
    EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF
    USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
    LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
    OF THE POSSIBILITY OF SUCH DAMAGE.

## libultrahdr

Egami's gain map code is written from ISO 21496-1, Adobe's Gain Map Specification 1.0 and Android's Ultra HDR format, with
these details taken from libultrahdr 2.0.2 so files and renditions match it: the headroom formula for Apple's maker note tags
33 and 48, the corner-aligned map sampling, the gain limits and dark-pixel cap when making a map, and the encoder defaults.
libultrahdr is distributed under the Apache License, Version 2.0 (https://www.apache.org/licenses/LICENSE-2.0):

    Copyright 2022 The Android Open Source Project

    Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the
    License. Unless required by applicable law or agreed to in writing, software distributed under the License is distributed
    on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the
    specific language governing permissions and limitations under the License.

## GS1 Barcode Syntax Dictionary

`src/Egami.Barcodes/Gs1Data/gs1-syntax-dictionary.txt`, embedded in Egami.Barcodes.dll, is the GS1 Barcode Syntax Dictionary
release 2026-01-27 ([gs1/gs1-syntax-dictionary](https://github.com/gs1/gs1-syntax-dictionary)), unchanged. Egami.Barcodes reads
it to check GS1 element strings: each Application Identifier's data components, lengths and content checks. It is distributed
under the Apache License, Version 2.0 (https://www.apache.org/licenses/LICENSE-2.0):

    Copyright (c) 2020-2021 BWIPP project. https://bwipp.terryburton.co.uk/
    Copyright (c) 2020-2021 Zint Project. https://zint.org.uk/
    Copyright (c) 2021-2025 GS1 AISBL

    Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the
    License. Unless required by applicable law or agreed to in writing, software distributed under the License is distributed
    on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the
    specific language governing permissions and limitations under the License.

## ZXing

Egami's Aztec Code encoder (`src/Egami.Barcodes/Matrix/AztecEncoder.cs`) is written from ISO/IEC 24778. Its layout of the
data layers, the mapping that skips the reference grid of full-range symbols, the placement of the mode message and the
bit stuffing follow the structure of ZXing's Aztec encoder (`com.google.zxing.aztec.encoder.Encoder`), and its search over mode
states for the fewest bits takes the approach of ZXing's `HighLevelEncoder`. The barcode reader's binarizers
(`src/Egami.Barcodes/Reading/Images.cs`) take the approach of ZXing's `GlobalHistogramBinarizer` (a threshold at the deepest
valley between the two main histogram peaks) and `HybridBinarizer` (8 x 8 blocks thresholded by the mean of the 5 x 5 blocks
around them). The QR Code detector (`src/Egami.Barcodes/Reading/QrDetector.cs`) follows the method of ZXing's
`FinderPatternFinder` and `Detector`: finder patterns by 1:1:3:1:1 runs checked across both axes, the size from the finders'
distances, and the alignment pattern searched around the point 3 modules in from the extrapolated bottom-right corner. ZXing is distributed under the Apache License,
Version 2.0 (https://www.apache.org/licenses/LICENSE-2.0):

    Copyright 2013 ZXing authors

    Licensed under the Apache License, Version 2.0 (the "License"); you may not use this file except in compliance with the
    License. Unless required by applicable law or agreed to in writing, software distributed under the License is distributed
    on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied. See the License for the
    specific language governing permissions and limitations under the License.

## zint

Egami.Barcodes' encoders are written from the ISO/IEC barcode standards. These tables, which the standards print, were
transcribed through zint's backend library (zint 2.16.0, BSD-3), whose frontends and GUI (GPL) are not used:

- QR Code capacities: codewords per version, data codewords and error correction blocks per version and level
  (ISO/IEC 18004 Tables 1, 7 and 9; zint `backend/qr.h`), in `src/Egami.Barcodes/Matrix/QrTables.cs`.
- Data Matrix symbol sizes and C40 and Text character tables (ISO/IEC 16022 Table 7 and Annex C, ISO/IEC 21471; zint
  `backend/dmatrix.h`), in `src/Egami.Barcodes/Matrix/DataMatrixTables.cs`.
- PDF417 symbol character patterns for clusters 0, 3 and 6 (ISO/IEC 15438 Annex A; zint `backend/pdf417_tabs.h`), in
  `src/Egami.Barcodes/Matrix/Pdf417Tables.cs`.

Egami's Data Matrix encodation (`src/Egami.Barcodes/Matrix/DataMatrixEncoder.cs`) is translated from zint's minimal
encodation in `backend/dmatrix.c` (itself based on Alex Geller's algorithm), with its end-of-data rules, padding, block
interleaving and 144 x 144 placement, so that Egami's symbols equal zint's.

zint's backend is distributed under the BSD 3-clause license:

    libzint - the open source barcode library
    Copyright (C) 2008-2024 Robin Stuart <rstuart114@gmail.com>
    Copyright (C) 2006 Kentaro Fukuchi <fukuchi@megaui.net>

    Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
    following conditions are met:

    1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following
       disclaimer.
    2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following
       disclaimer in the documentation and/or other materials provided with the distribution.
    3. Neither the name of the project nor the names of its contributors may be used to endorse or promote products derived
       from this software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
    INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
    SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
    LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
    CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
    SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## resvg

Egami.Svg is written from the SVG 1.1 and SVG 2 specifications, with resvg (linebender/resvg 0.48.1) as its model, as
Phase 2.10's question 22 allows: where SVG leaves a rule open, Egami.Svg follows resvg's usvg crate, and some of its code is
translated from it closely: marker vertices and their angles (`src/Egami.Svg/Resolving/SvgMarkerVertices.cs`, from
`usvg/src/parser/marker.rs`), and text layout: white space processing, character positions, chunks, clusters, spacing,
anchors, baselines, decorations and text on a path (`src/Egami.Svg/Text`, from `usvg/src/parser/svgtree/text.rs`,
`usvg/src/parser/text.rs` and `usvg/src/text/layout.rs`). resvg is distributed under the Apache License 2.0 or the MIT license, at the user's option;
Egami uses it under the MIT license:

    Copyright 2017 the Resvg Authors

    Permission is hereby granted, free of charge, to any
    person obtaining a copy of this software and associated
    documentation files (the "Software"), to deal in the
    Software without restriction, including without
    limitation the rights to use, copy, modify, merge,
    publish, distribute, sublicense, and/or sell copies of
    the Software, and to permit persons to whom the Software
    is furnished to do so, subject to the following
    conditions:

    The above copyright notice and this permission notice
    shall be included in all copies or substantial portions
    of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF
    ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED
    TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A
    PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT
    SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
    CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
    OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR
    IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
    DEALINGS IN THE SOFTWARE.

## libjxl

Egami's JPEG XL reader is written from ISO/IEC 18181-1 and 18181-2, with libjxl 0.12.0 read so its headers match the reference
decoder field for field: the order and conditions of the SizeHeader, ImageMetadata, ExtraChannelInfo, ColorEncoding,
CustomTransformData, FrameHeader, BlendingInfo, Passes and LoopFilter fields, the frame and LF group counts behind the TOC
(`src/Egami/Formats/Jxl/JxlHeaders.cs`), its box rules (`JxlContainer.cs`), its ICC header prediction from the bytes
already decoded (`JxlIcc.cs`), and its rendering order and blending: the stage order of its render pipeline (patches and
splines before color upsampling, noise after, reference frames saved before or after the color transform) in
`JxlImageDecoder.cs`, its patch and frame blending and alpha compositing (`JxlBlend.cs`), its spot color mixing and alpha unpremultiplying with their smallest alpha (`JxlImageDecoder.cs`), and its conversion of XYB output into an enumerated color space: linear sRGB to XYZ D50 and on to the target's primaries, the sRGB luminances for gray, a gamma curve that sends samples at or below 10^-5 to 0, the PQ scaling by intensity_target and the HLG inverse OOTF (`JxlColor.cs`), the layout of its jhgm gain map bundle (`JxlGainMap.cs`), and, for speed, its high-precision sRGB curve and its fast power function (FastLog2f and FastPow2f) for the BT.709, gamma and DCI curves (`JxlColor.cs`, `JxlFastMath.cs`). Egami's lossless JPEG XL encoder is translated from
libjxl 0.12.0's encoder, so its files match cjxl's choices: the entropy coder's histogram normalization and description,
clustering, context map, hybrid integer choice and RLE (enc_ans, enc_cluster, enc_context_map, enc_lz77; `JxlEntropyEncoder.cs`),
the MA tree learning with its sample deduplication and property quantization (enc_ma, enc_encoding; `JxlTreeLearner.cs`), the
choices of palettes, RCTs, fixed trees and settings at each effort (enc_modular, enc_palette, enc_rct; `JxlModularEncoder.cs`),
the fast lossless encoder of effort 1 with its fixed tree, prefix codes and code-length search (enc_fast_lossless;
`JxlFastLossless.cs`), the ICC profile transform (enc_icc_codec; `JxlIccEncoder.cs`) and, for the lossy encoder (2.9.7), the forward XYB: linear sRGB
through the opsin absorbance matrix and bias, cone responses clamped at zero, cube root (enc_xyb, cms/opsin_params; `JxlXyb.cs`), and its VarDCT frame: the
quantizer's global scale and DC quantizer (quantizer, InitialQuantDC in enc_adaptive_quantization), the per-effort heuristics
(enc_heuristics, enc_frame's chromacity and loop filter settings), coefficient quantization with its thresholds and the Y round
trip (enc_group), the LF and HF metadata streams and their trees (enc_modular, enc_cache), coefficient orders and tokens
(enc_coeff_order, enc_entropy_coder; `JxlVarDctEncoder.cs`), the per-block quantization adjustments (AdjustQuantBlockAC,
AdjustQuantField), the adaptive quantization map with its masks and the inverse Gaborish (enc_adaptive_quantization,
enc_gaborish, the Symmetric5 convolution; `JxlAdaptiveQuantization.cs`) the chroma-from-luma search (enc_chroma_from_luma;
`JxlChromaFromLuma.cs`), the block-size search with its entropy estimate and merges (enc_ac_strategy; `JxlAcStrategySearch.cs`), the EPF
sharpness search (ComputeARHeuristics in enc_heuristics) and the patch dictionary: text-like patches, Gaussian dot detection, the bin
packing and the reference frame's quantization (enc_patch_dictionary, enc_detect_dots, enc_dot_dictionary, enc_linalg, enc_modular's
XYB factors; `JxlPatchSearch.cs`), the photon noise model (enc_photon_noise), the 2× downsampling at low bitrates with its sharper
kernel (DownsampleImage2_Sharper in enc_heuristics, enc_frame's resampling point) and the pixel-based chroma matrix scales
(enc_frame's PixelStatsForChromacityAdjustment), SimplifyInvisible (enc_frame) and lossy Modular extra channels: the forward squeeze
with its default steps and their cut, and the quantization tables (modular/transform/enc_squeeze, enc_modular), and the progressive
mode: the HF split into passes and the LF frame's settings (enc_progressive_split, enc_frame's SetProgressiveMode, enc_cache), and the
streaming mode: when a frame goes in chunks (CanDoStreamingEncoding), the chunk border, the first chunk's global state, the coefficient
orders kept across chunks, the HF histogram budget with its fixed flat histogram, unused histograms removed, and every symbol counted
in the Modular distributions (enc_frame, enc_ans, enc_coeff_order; `JxlVarDctChunked.cs`). Its JPEG recompression is translated
from libjxl 0.12.0 too: the JPEG reader with its padding bits, reset points and extra zero runs (jpeg/enc_jpeg_data_reader;
`JxlJpegReader.cs`), and the jbrd box, chroma from luma search, DC contexts, coefficient orders and coefficient tokens
(jpeg/enc_jpeg_data, jpeg/jpeg_data, enc_frame, enc_coeff_order, enc_entropy_coder; `JxlJpegEncoder.cs`). libjxl is
distributed under the BSD 3-clause license:

    Copyright (c) the JPEG XL Project Authors. All rights reserved.

    Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
    following conditions are met: redistributions of source code must retain the above copyright notice, this list of
    conditions and the following disclaimer; redistributions in binary form must reproduce the above copyright notice, this
    list of conditions and the following disclaimer in the documentation and/or other materials provided with the
    distribution; neither the name of the copyright holder nor the names of its contributors may be used to endorse or
    promote products derived from this software without specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
    INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
    EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF
    USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
    LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED
    OF THE POSSIBILITY OF SUCH DAMAGE.

## jxl-oxide

jxl-oxide 0.12.6, a JPEG XL decoder in Rust, was read beside libjxl for the same header layouts and its bit reader's U32, U64
and F16 fields (`src/Egami/Formats/Jxl/JxlBitReader.cs`); Egami's entropy decoder (`JxlEntropy.cs`: ANS with its alias
tables, prefix codes, context maps, hybrid integers, LZ77 and permutations) and compressed ICC decoding (`JxlIcc.cs`) are
translated from its jxl-coding and jxl-color crates, and the Modular decoder (`JxlModular.cs`, `JxlModularImage.cs`,
`JxlModularDecoder.cs`, `JxlTransforms.cs`, `JxlFrameDecoder.cs`: MA trees, the predictors and the self-correcting predictor's
error bookkeeping, RCT, palette with its delta palette and implicit colours, squeeze and its tendency term, and the split of
channels into global, LF group and pass group streams) from its jxl-modular and jxl-frame crates, and the VarDCT decoder
(`JxlVarDctHeaders.cs`, `JxlDequantMatrices.cs`, `JxlVarDct.cs`, `JxlDct.cs`: block contexts, coefficient orders, the default
and coded dequantization matrices, HF coefficient contexts, chroma from luma, adaptive LF smoothing, the DCTs and the DCT2, DCT4,
Hornuss, DCT4x8 and AFV transforms with the AFV basis), the restoration filters and upsampling (`JxlFilters.cs`, with the default
upsampling weights) the XYB conversion (`JxlColor.cs`) and the patches, splines and noise (`JxlFeatures.cs`: the patch dictionary and patch
blending, spline dequantization, Catmull-Rom sampling and drawing with its error function approximation, and the noise
generator, filter and strength table) from its jxl-vardct, jxl-frame, jxl-render, jxl-image and jxl-color crates; the JPEG
bitstream reconstruction (`JxlJpegReconstruction.cs`: the jbrd header, its marker, table and scan bookkeeping, the integer chroma
from luma, Huffman table building and the sequential and progressive scan writers) is translated from its jxl-jbr crate. It is distributed under the MIT license (or Apache-2.0); the MIT
terms:

    Copyright (c) Wonwoo Choi and the jxl-oxide contributors

    Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation
    files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy,
    modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the
    Software is furnished to do so, subject to the following conditions:

    The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
    Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
    WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
    COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE,
    ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

## libaom

Egami's AV1 decoder (`src/Egami/Formats/Av1`) is translated from libaom 3.15.2, the Alliance for Open Media's reference
decoder: the arithmetic decoder of `aom_dsp/entdec.c` (`Av1SymbolDecoder.cs`); the default CDFs of `av1/common/entropymode.c`,
`token_cdfs.h` and `entropymv.c` and its block, transform, scan, quantizer and quantizer matrix tables from `common_data.h`,
`scan.c`, `quant_common.c` and `txb_common.c`, which `tools/make-av1-tables.py` generates into `Av1DefaultCdfs.cs` and
`Av1Tables.Data.cs` (`Av1Tables.cs`, `Av1FrameContext.cs`); and the tile syntax of `av1/decoder/decodeframe.c`, `decodemv.c`,
`decodetxb.c` and `detokenize.c` with the contexts of `av1/common/av1_common_int.h`, `blockd.h`, `pred_common.c`,
`txb_common.h` and `entropymode.c`, the intra block copy vector search of `mvref_common.c` and the loop restoration unit layout
of `restoration.c` (`Av1Frame.cs`, `Av1TileParser*.cs`); the intra predictors, edge filters and upsampling of
`av1/common/reconintra.c` and `aom_dsp/intrapred.c` with their tables, chroma from luma (`cfl.c`) and the intra block copy
prediction of `reconinter.c` and `convolve.c` (`Av1Reconstructor*.cs`); and the inverse transforms of `av1_inv_txfm2d.c` and
`idct.c` (`Av1InverseTransform.cs`), whose one-dimensional transforms `av1_inv_txfm1d.c` `tools/make-av1-tables.py` translates
into `Av1InverseTransform.OneD.cs`; the deblocking filter of `av1_loopfilter.c` and `aom_dsp/loopfilter.c`
(`Av1LoopFilter.cs`), CDEF from `cdef.c` and `cdef_block.c` (`Av1Cdef.cs`), superres upscaling from `resize.c` with its
normative filter (`Av1Superres.cs`), loop restoration from `restoration.c` and the Wiener convolution of `convolve.c`
(`Av1Restoration.cs`), and the Gaussian sequence of `av1/decoder/grain_synthesis.c` (`Av1FilmGrainSynthesis.cs`). Its inter
frames take the inter frame header, short reference signaling and global motion of `decodeframe.c` and `mvref_common.c`
(`Av1FrameHeader.cs`); the inter mode info, reference frame contexts, reference vector stack, temporal candidates, warp samples and
overlappable neighbours of `decodemv.c`, `mvref_common.c`, `pred_common.c`, `blockd.h`, `obmc.h` and `mv.h`
(`Av1TileParser.Inter.cs`); the motion field projection and saved vectors of `mvref_common.c` (`Av1MotionField.cs`); the warp
fit, shear decomposition and warp filter of `warped_motion.c` (`Av1Warp.cs`); and the interpolation, warp, wedge codebook, OBMC,
inter-intra and distance weight tables of `filter.h`, `warped_motion.c`, `reconinter.c` and `common_data.h`, which
`tools/make-av1-tables.py` generates into `Av1Tables.Data.cs`. Egami's AV1 encoder takes from libaom 3.15.2 too: the arithmetic encoder of `aom_dsp/entenc.c` (`Av1SymbolEncoder.cs`), the forward transforms
of `av1/encoder/av1_fwd_txfm2d.c` and the Walsh-Hadamard transform of `hybrid_fwd_txfm.c` (`Av1ForwardTransform.cs`), whose
one-dimensional transforms `av1_fwd_txfm1d.c` `tools/make-av1-tables.py` translates into `Av1ForwardTransform.OneD.cs` and
`Av1ForwardTransform.Vector.cs`, the tile syntax its `bitstream.c` and `encodetxb.c` write (`Av1TileWriter.cs`), the quantizer to
qindex table of `av1/encoder/av1_quantize.c` (`AvifWriter.cs`), and the encoder's choices: the rate tables and rate-distortion
cost of `rd.c` and `cost.c` (`Av1SymbolSink.cs`), the trellis quantization and coefficient rates of `txb_rdopt.c`
(`Av1Trellis.cs`, `Av1TileEncoder.Rate.cs`), tune IQ's quantizer, quantization matrix, sharpness and SSIM rdmult choices of
`av1_quantize.c`, `quant_common.h`, `picklpf.c` and `encoder_utils.c` (`Av1Encoder.cs`), Variance Boost from `allintra_vis.c`
and `aq_variance.c` (`Av1VarianceBoost.cs`), the deblocking level guess of `picklpf.c` and the CDEF strength search of
`pickcdef.c` (`Av1FilterSearch.cs`), the screen content detection of `encoder.c` (`Av1ScreenContent.cs`), the palette search of
`palette.c` (`Av1TileEncoder.Palette.cs`), and the intra block copy search and vector checks of `rdopt.c` and `mvref_common.h`
(`Av1TileEncoder.Intrabc.cs`). The inter encoder (2.9.9) writes the inter mode info its `bitstream.c` writes
(`Av1TileWriter.Inter.cs`, sharing the derivations of `mvref_common.c` and `pred_common.c` with the decoder in
`Av1TileState.Inter.cs`), and follows the structure of its inter mode search and motion search (`rdopt.c`, `mcomp.c`;
`Av1TileEncoder.Inter.cs`, `Av1MotionSearch.cs`) and of its global motion estimation (`global_motion.c`, `ransac.c`;
`Av1GlobalMotion.cs`). libaom is distributed under the BSD 2-clause license:

    Copyright (c) 2016, Alliance for Open Media. All rights reserved.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions
    are met:

    1. Redistributions of source code must retain the above copyright
       notice, this list of conditions and the following disclaimer.

    2. Redistributions in binary form must reproduce the above copyright
       notice, this list of conditions and the following disclaimer in
       the documentation and/or other materials provided with the
       distribution.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
    "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
    LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS
    FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE
    COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT,
    INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING,
    BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
    LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
    CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT
    LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
    ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
    POSSIBILITY OF SUCH DAMAGE.

and with the Alliance for Open Media Patent License 1.0, which covers implementations of AV1:

    Alliance for Open Media Patent License 1.0

    1. License Terms.

    1.1. Patent License. Subject to the terms and conditions of this License, each
         Licensor, on behalf of itself and successors in interest and assigns,
         grants Licensee a non-sublicensable, perpetual, worldwide, non-exclusive,
         no-charge, royalty-free, irrevocable (except as expressly stated in this
         License) patent license to its Necessary Claims to make, use, sell, offer
         for sale, import or distribute any Implementation.

    1.2. Conditions.

    1.2.1. Availability. As a condition to the grant of rights to Licensee to make,
           sell, offer for sale, import or distribute an Implementation under
           Section 1.1, Licensee must make its Necessary Claims available under
           this License, and must reproduce this License with any Implementation
           as follows:

           a. For distribution in source code, by including this License in the
              root directory of the source code with its Implementation.

           b. For distribution in any other form (including binary, object form,
              and/or hardware description code (e.g., HDL, RTL, Gate Level Netlist,
              GDSII, etc.)), by including this License in the documentation, legal
              notices, and/or other written materials provided with the
              Implementation.

    1.2.2. Additional Conditions. This license is directly from Licensor to
           Licensee.  Licensee acknowledges as a condition of benefiting from it
           that no rights from Licensor are received from suppliers, distributors,
           or otherwise in connection with this License.

    1.3. Defensive Termination. If any Licensee, its Affiliates, or its agents
         initiates patent litigation or files, maintains, or voluntarily
         participates in a lawsuit against another entity or any person asserting
         that any Implementation infringes Necessary Claims, any patent licenses
         granted under this License directly to the Licensee are immediately
         terminated as of the date of the initiation of action unless 1) that suit
         was in response to a corresponding suit regarding an Implementation first
         brought against an initiating entity, or 2) that suit was brought to
         enforce the terms of this License (including intervention in a third-party
         action by a Licensee).

    1.4. Disclaimers. The Reference Implementation and Specification are provided
         "AS IS" and without warranty. The entire risk as to implementing or
         otherwise using the Reference Implementation or Specification is assumed
         by the implementer and user. Licensor expressly disclaims any warranties
         (express, implied, or otherwise), including implied warranties of
         merchantability, non-infringement, fitness for a particular purpose, or
         title, related to the material. IN NO EVENT WILL LICENSOR BE LIABLE TO
         ANY OTHER PARTY FOR LOST PROFITS OR ANY FORM OF INDIRECT, SPECIAL,
         INCIDENTAL, OR CONSEQUENTIAL DAMAGES OF ANY CHARACTER FROM ANY CAUSES OF
         ACTION OF ANY KIND WITH RESPECT TO THIS LICENSE, WHETHER BASED ON BREACH
         OF CONTRACT, TORT (INCLUDING NEGLIGENCE), OR OTHERWISE, AND WHETHER OR
         NOT THE OTHER PARTY HAS BEEN ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

    2. Definitions.

    2.1. Affiliate.  "Affiliate" means an entity that directly or indirectly
         Controls, is Controlled by, or is under common Control of that party.

    2.2. Control. "Control" means direct or indirect control of more than 50% of
         the voting power to elect directors of that corporation, or for any other
         entity, the power to direct management of such entity.

    2.3. Decoder.  "Decoder" means any decoder that conforms fully with all
         non-optional portions of the Specification.

    2.4. Encoder.  "Encoder" means any encoder that produces a bitstream that can
         be decoded by a Decoder only to the extent it produces such a bitstream.

    2.5. Final Deliverable.  "Final Deliverable" means the final version of a
         deliverable approved by the Alliance for Open Media as a Final
         Deliverable.

    2.6. Implementation.  "Implementation" means any implementation, including the
         Reference Implementation, that is an Encoder and/or a Decoder. An
         Implementation also includes components of an Implementation only to the
         extent they are used as part of an Implementation.

    2.7. License. "License" means this license.

    2.8. Licensee. "Licensee" means any person or entity who exercises patent
         rights granted under this License.

    2.9. Licensor.  "Licensor" means (i) any Licensee that makes, sells, offers
         for sale, imports or distributes any Implementation, or (ii) a person
         or entity that has a licensing obligation to the Implementation as a
         result of its membership and/or participation in the Alliance for Open
         Media working group that developed the Specification.

    2.10. Necessary Claims.  "Necessary Claims" means all claims of patents or
          patent applications, (a) that currently or at any time in the future,
          are owned or controlled by the Licensor, and (b) (i) would be an
          Essential Claim as defined by the W3C Policy as of February 5, 2004
          (https://www.w3.org/Consortium/Patent-Policy-20040205/#def-essential)
          as if the Specification was a W3C Recommendation; or (ii) are infringed
          by the Reference Implementation.

    2.11. Reference Implementation. "Reference Implementation" means an Encoder
          and/or Decoder released by the Alliance for Open Media as a Final
          Deliverable.

    2.12. Specification. "Specification" means the specification designated by
          the Alliance for Open Media as a Final Deliverable for which this
          License was issued.

## dav1d

Where libaom rejects a stream that dav1d 1.5.4 decodes, Egami's AV1 decoder follows dav1d (`src/decode.c`, `src/recon_tmpl.c`),
whose output its tests hold it to: a segment id past the last active one becomes 0, a coefficient's Golomb code may run to 32
bits, and an intra block copy vector that reaches outside the decoded area is moved back inside it (`Av1TileParser*.cs`,
`Av1SymbolDecoder.cs`). Film grain synthesis is translated from its `src/filmgrain_tmpl.c` and `src/fg_apply_tmpl.c`
(`Av1FilmGrainSynthesis.cs`). dav1d is distributed under the BSD 2-clause license:

    Copyright © 2018-2025, VideoLAN and dav1d authors
    All rights reserved.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions are met:

    1. Redistributions of source code must retain the above copyright notice, this
       list of conditions and the following disclaimer.

    2. Redistributions in binary form must reproduce the above copyright notice,
       this list of conditions and the following disclaimer in the documentation
       and/or other materials provided with the distribution.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
    ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
    WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR
    ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
    (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
    LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
    ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
    (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
    SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## HM (HEVC Test Model)

Egami.Heic's HEVC decoder is written from ITU-T H.265 and held bit-exact to HM 18.0, the reference decoder. Its CABAC engine
(`HevcCabac.cs`) follows the structure of HM's `TDecBinCABAC`, reading whole bytes ahead, so that its byte positions after a
terminating bin match HM's. HM is distributed under the BSD license:

    The copyright in this software is being made available under the BSD
    License, included below. This software may be subject to other third party
    and contributor rights, including patent rights, and no such rights are
    granted under this license.

    Copyright (c) 2010-2022, ITU/ISO/IEC
    All rights reserved.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions are met:

     * Redistributions of source code must retain the above copyright notice,
       this list of conditions and the following disclaimer.
     * Redistributions in binary form must reproduce the above copyright notice,
       this list of conditions and the following disclaimer in the documentation
       and/or other materials provided with the distribution.
     * Neither the name of the ITU/ISO/IEC nor the names of its contributors may
       be used to endorse or promote products derived from this software without
       specific prior written permission.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
    AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
    IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
    ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS
    BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
    CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
    SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
    INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
    CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
    ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF
    THE POSSIBILITY OF SUCH DAMAGE.

## libavif

The HEIF image layer converts decoded planes to RGB as libavif 1.4.2 does without libyuv, so Load equals avifdec sample for
sample: the lookup tables, Kr and Kb (derived from the primaries for matrix code 12), bilinear chroma upsampling and alpha
unpremultiplication of its `src/reformat.c`, `src/alpha.c` and `src/colr.c` (`HeifRgbConverter.cs`). The AVIF writer lays out
its files as libavif's `src/write.c` does and maps quality to libaom's quantizer with the tune IQ table of `src/codec_aom.c`
(`AvifWriter.cs`). libavif is distributed under the BSD 2-clause license:

    Copyright 2019 Joe Drago. All rights reserved.

    Redistribution and use in source and binary forms, with or without
    modification, are permitted provided that the following conditions are met:

    1. Redistributions of source code must retain the above copyright notice, this
    list of conditions and the following disclaimer.

    2. Redistributions in binary form must reproduce the above copyright notice,
    this list of conditions and the following disclaimer in the documentation
    and/or other materials provided with the distribution.

    THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
    AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
    IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
    DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE
    FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
    DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
    SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER
    CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
    OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
    OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## FOGRA39L print profile

`src/Egami/Colors/Profiles/FOGRA39L_coated.icc`, embedded in Egami as `ColorProfile.Fogra39`, is shipped unchanged from the CTAN
colorprofiles package (version 20181105, <https://ctan.org/pkg/colorprofiles>). Peter Selinger built it with colord's
cd-create-profile (colord 1.4.3) from colord's `FOGRA39L_coated.iccprofile.xml`, which holds Fogra's FOGRA39L characterization
data (<https://fogra.org>) and ArgyllCMS profiling settings and is marked CC0; he waived his rights to the ICC file, and the
profile states "This profile is free of known copyright restrictions". The CTAN package lists it under the zlib/libpng license:

    FOGRA39L_coated
    Copyright:  Kai-Uwe Behrmann <www.behrmann.name>
                Marti Maria <www.littlecms.com>
                Photogamut <www.photogamut.org>
                Graeme Gill <www.argyllcms.com>
                ColorSolutions <www.basICColor.com>

    This software is provided 'as-is', without any express or implied warranty. In no event will the authors be held liable
    for any damages arising from the use of this software.

    Permission is granted to anyone to use this software for any purpose, including commercial applications, and to alter it
    and redistribute it freely, subject to the following restrictions:

      1. The origin of this software must not be misrepresented; you must not claim that you wrote the original software. If
         you use this software in a product, an acknowledgment in the product documentation would be appreciated but is not
         required.

      2. Altered source versions must be plainly marked as such, and must not be misrepresented as being the original
         software.

      3. This notice may not be removed or altered from any source distribution.

## Liberation, Carlito and Caladea fonts

`src/Egami.Fonts.Substitutes/Fonts/*.ttf`, embedded in Egami.Fonts.Substitutes.dll, are unchanged releases of open fonts
metric-compatible with the fonts documents name most, used in place of fonts a machine does not have:

| Fonts | Release | Licence file |
| --- | --- | --- |
| Liberation Sans, Serif and Mono (regular, bold, italic, bold italic) | liberation-fonts-ttf 2.1.5, [liberationfonts/liberation-fonts](https://github.com/liberationfonts/liberation-fonts) | `Liberation-OFL.txt` |
| Carlito (four styles) | [google/fonts](https://github.com/google/fonts/tree/main/ofl/carlito) | `Carlito-OFL.txt` |
| Caladea (four styles) | [google/fonts](https://github.com/google/fonts/tree/main/ofl/caladea) | `Caladea-OFL.txt` |

All are licensed under the SIL Open Font License 1.1. The licence files, with each font's copyright and reserved font names,
are in the same folder, embedded in Egami.Fonts.Substitutes.dll and packed with the package under `licenses/`.

## libvpx

Egami.Video's VP9 decoder (`src/Egami.Video/Codecs/Vp9`) is translated from libvpx 1.14.1, the WebM project's reference
codec, so that its frames equal libvpx's sample for sample: the uncompressed and compressed headers, reference slots and
superframes of `vp9/decoder/vp9_decoder.c`, `vp9_decodeframe.c` and `vp9/vp9_dx_iface.c` (`Vp9FrameHeader.cs`,
`Vp9Decoder*.cs`, `Vp9StreamDecoder.cs`); the boolean decoder of `vpx_dsp/bitreader.c` (`Vp9BoolDecoder.cs`); mode info, motion
vector candidates and contexts from `vp9_decodemv.c`, `vp9_mvref_common.h` and `vp9_pred_common.c`; coefficient tokens from
`vp9_detokenize.c`; probability updates and backward adaptation from `vp9_dsubexp.c`, `vp9_entropy.c`, `vp9_entropymode.c`,
`vp9_entropymv.c` and `vpx_dsp/prob.c` (`Vp9FrameContext.cs`, `Vp9Decoder.Probabilities.cs`); intra prediction from
`vp9_reconintra.c` and `vpx_dsp/intrapred.c` (`Vp9Intra.cs`); inter prediction and scaled references from `vp9_reconinter.c`,
`vp9_scale.c` and `vpx_dsp/vpx_convolve.c` (`Vp9Decoder.Inter.cs`, `Vp9Convolve.cs`, `Vp9ScaleFactors.cs`); the inverse
transforms of `vp9_idct.c` and `vpx_dsp/inv_txfm.c` (`Vp9Transform.cs`, with the one-dimensional transforms translated by
`tools/make-vp9-transforms.py` into `Vp9Transform.OneD.cs`); and the loop filter of `vp9_loopfilter.c` and
`vpx_dsp/loopfilter.c` (`Vp9LoopFilter.cs`). `tools/make-vp9-tables.py` generates the default probabilities, scans,
quantizer tables, interpolation kernels and lookups of `vp9/common` into `Vp9Tables.Data.cs`. `tools/vp9ref/vp9dump.c`, a test
tool built against libvpx in Docker, is Egami.Video's own.

```
Copyright (c) 2010, The WebM Project authors. All rights reserved.

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions are
met:

  * Redistributions of source code must retain the above copyright
    notice, this list of conditions and the following disclaimer.

  * Redistributions in binary form must reproduce the above copyright
    notice, this list of conditions and the following disclaimer in
    the documentation and/or other materials provided with the
    distribution.

  * Neither the name of Google, nor the WebM Project, nor the names
    of its contributors may be used to endorse or promote products
    derived from this software without specific prior written
    permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
"AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```

```
Additional IP Rights Grant (Patents)
------------------------------------

"These implementations" means the copyrightable works that implement the WebM
codecs distributed by Google as part of the WebM Project.

Google hereby grants to you a perpetual, worldwide, non-exclusive, no-charge,
royalty-free, irrevocable (except as stated in this section) patent license to
make, have made, use, offer to sell, sell, import, transfer, and otherwise
run, modify and propagate the contents of these implementations of WebM, where
such license applies only to those patent claims, both currently owned by
Google and acquired in the future, licensable by Google that are necessarily
infringed by these implementations of WebM. This grant does not include claims
that would be infringed only as a consequence of further modification of these
implementations. If you or your agent or exclusive licensee institute or order
or agree to the institution of patent litigation or any other patent
enforcement activity against any entity (including a cross-claim or
counterclaim in a lawsuit) alleging that any of these implementations of WebM
or any code incorporated within any of these implementations of WebM
constitute direct or contributory patent infringement, or inducement of
patent infringement, then any patent rights granted to you under this License
for these implementations of WebM shall terminate as of the date such
litigation is filed.
```

## libopus

Egami.Video's Opus decoder (`src/Egami.Video/Audio/Codecs/Opus`) is translated from libopus 1.5.2, the Opus reference
implementation whose decoder RFC 6716 makes normative: the range decoder of `celt/entdec.c` and `celt/entcode.c`
(`OpusRangeDecoder.cs`); packet parsing from `src/opus.c` and `src/opus_decoder.c` (`OpusPacket.cs`); the decoder of
`src/opus_decoder.c`, with mode transitions, redundancy frames and in-band FEC (`OpusStreamDecoder.cs`); the multistream
decoder of `src/opus_multistream_decoder.c` and `src/opus_multistream.c` (`OpusMultistreamDecoder.cs`); the SILK decoder of
`silk/` (`Silk/`, its tables generated by `tools/video/make-silk-tables.py`); and the CELT decoder of `celt/` in its floating-point build (`Celt/`, its tables generated by
`tools/video/make-celt-tables.py`). The Opus conformance measure of
`src/opus_compare.c` is translated into the tests (`tests/Egami.Video.Tests/Audio/OpusCompare.cs`).

Its Opus encoder, in the same folder, is translated from the same release's floating-point build: the range encoder of
`celt/entenc.c` (`OpusRangeEncoder.cs`); the repacketizer of `src/repacketizer.c` (`OpusRepacketizer.cs`); the encoder of
`src/opus_encoder.c`, with mode, bandwidth and bitrate decisions, FEC, DTX and multi-frame packets (`OpusEncoder.cs`); the
tonality and speech/music analysis of `src/analysis.c` and `src/mlp.c`, with the MLP weights of `src/mlp_data.c` and the
tables of `src/analysis.c` (`OpusTonalityAnalysis.cs`, `OpusAnalysisInfo.cs`, `OpusAnalysisTables.Data.cs`, generated by
`tools/video/make-opus-analysis-tables.py`); the multistream and surround encoder of `src/opus_multistream_encoder.c`
(`OpusMultistreamEncoder.cs`); the SILK encoder of `silk/` and `silk/float/` (`Silk/`); and the CELT encoder of
`celt/celt_encoder.c`, `celt/bands.c`, `celt/quant_bands.c`, `celt/vq.c`, `celt/cwrs.c`, `celt/rate.c`, `celt/mdct.c`,
`celt/celt_lpc.c` and `celt/pitch.c` (`Celt/`). The encoder tests' reference streams are made by libopus itself
(`tools/video/opusref/make-opus-encoder-reference.c`).

```
Copyright 2001-2023 Xiph.Org, Skype Limited, Octasic,
                    Jean-Marc Valin, Timothy B. Terriberry,
                    CSIRO, Gregory Maxwell, Mark Borgerding,
                    Erik de Castro Lopo, Mozilla, Amazon

Redistribution and use in source and binary forms, with or without
modification, are permitted provided that the following conditions
are met:

- Redistributions of source code must retain the above copyright
notice, this list of conditions and the following disclaimer.

- Redistributions in binary form must reproduce the above copyright
notice, this list of conditions and the following disclaimer in the
documentation and/or other materials provided with the distribution.

- Neither the name of Internet Society, IETF or IETF Trust, nor the
names of specific contributors, may be used to endorse or promote
products derived from this software without specific prior written
permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
``AS IS'' AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER
OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL,
EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO,
PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR
PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF
LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING
NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

Opus is subject to the royalty-free patent licenses which are
specified at:

Xiph.Org Foundation:
https://datatracker.ietf.org/ipr/1524/

Microsoft Corporation:
https://datatracker.ietf.org/ipr/1914/

Broadcom Corporation:
https://datatracker.ietf.org/ipr/1526/
```

## minimp3

Egami.Video's MPEG audio decoder (`src/Egami.Video/Audio/Codecs/Mp3`) is translated from minimp3 by Lion (lieff): the bit
reader, side information, scale factors, Huffman decoding, stereo processing, reordering, antialias, IMDCT and the Layer I and
II paths of `minimp3.h` in its scalar build (`Mp3FrameDecoder.cs`), its tables (`Mp3Tables.cs`), the synthesis filterbank
(`Mp3Synthesis.cs`) and its frame search (`MpegAudioSync.cs`). minimp3 is dedicated to the public domain under CC0 1.0
Universal, which asks for no notice; it is listed here as a courtesy.

```
To the extent possible under law, the author(s) have dedicated all copyright and related and neighboring rights to this
software to the public domain worldwide. This software is distributed without any warranty.
See <http://creativecommons.org/publicdomain/zero/1.0/>.
```

## OpenH264

Egami.Video.H264's CABAC context initialisation values and CAVLC code tables (`src/Egami.Video.H264/Codecs/H264/H264Tables.Data.cs`)
are transcribed from OpenH264 2.4.1 (`codec/common/src/common_tables.cpp` and `codec/encoder/core/src/encoder_data_tables.cpp`)
by `tools/video/make-h264-tables.py`. The decoder itself is written from ITU-T H.264. OpenH264 is distributed under the
BSD 2-Clause license:

```
Copyright (c) 2013, Cisco Systems
All rights reserved.

Redistribution and use in source and binary forms, with or without modification,
are permitted provided that the following conditions are met:

* Redistributions of source code must retain the above copyright notice, this
  list of conditions and the following disclaimer.

* Redistributions in binary form must reproduce the above copyright notice, this
  list of conditions and the following disclaimer in the documentation and/or
  other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR
ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON
ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
```
