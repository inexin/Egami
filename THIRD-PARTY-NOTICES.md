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
