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
(`src/Egami/Formats/Jxl/JxlHeaders.cs`), its box rules (`JxlContainer.cs`), and its ICC header prediction from the bytes
already decoded (`JxlIcc.cs`). libjxl is distributed under the BSD 3-clause
license:

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
upsampling weights) and the XYB conversion (`JxlColor.cs`) from its jxl-vardct, jxl-render, jxl-image and jxl-color crates. It is distributed under the MIT license (or Apache-2.0); the MIT
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
