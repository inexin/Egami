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
