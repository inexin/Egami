# Reference tools for Phase 2.9.4 (HEIC): HM 18.0, the HEVC reference software (BSD 3-clause, ITU/ISO/IEC; no patent rights
# granted), built from source as a decoder, a decoder that writes HM's syntax trace and an encoder; and, run as black-box tools
# only, libheif 1.23.4's heif-dec, heif-info and heif-enc (LGPL-3) with its libde265, x265, kvazaar and JPEG plugins, libde265
# 1.0.15's dec265 (LGPL-3), x265 4.1 (GPL-2) and kvazaar 2.3.1 (BSD 3-clause) from Debian; ExifTool. Built by
# tools/make-heic-references.py. The sources of libheif, libde265 and x265 are never fetched or read.
FROM debian@sha256:913f6706df59a68922d1dd08f78c2476560a8d367897200a6005b00e5f67c2d5
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake git ca-certificates python3 python3-numpy libimage-exiftool-perl \
        libheif-examples=1.23.4-1~deb13u1 libheif-plugin-libde265=1.23.4-1~deb13u1 libheif-plugin-x265=1.23.4-1~deb13u1 \
        libheif-plugin-kvazaar=1.23.4-1~deb13u1 libde265-examples=1.0.15-1+deb13u2 x265=4.1-2 kvazaar=2.3.1-2 \
    && rm -rf /var/lib/apt/lists/*
# HM at HM-18.0 (fb4486d5), with its warnings-as-errors turned off (gcc 14 warns on HM's code). TAppDecoderTrace is built with
# ENC_DEC_TRACE and DEC_NUH_TRACE: it writes TraceDec.txt, every syntax element HM parses with its value, NAL unit headers too.
RUN git clone -q https://vcgit.hhi.fraunhofer.de/jvet/HM.git /src/hm \
    && git -C /src/hm checkout -q fb4486d5ab5d0cd3b6a71659c7d5eb4509f2a4ce \
    && sed -i 's/bb_enable_warnings( gcc warnings-as-errors/bb_enable_warnings( gcc/' /src/hm/CMakeLists.txt \
    && cmake -S /src/hm -B /src/hm/build -DCMAKE_BUILD_TYPE=Release \
    && make -C /src/hm/build -j"$(nproc)" TAppDecoder TAppEncoder \
    && cp "$(find /src/hm/bin -name TAppDecoder -type f)" "$(find /src/hm/bin -name TAppEncoder -type f)" /usr/local/bin/ \
    && cp -r /src/hm/cfg /usr/local/share/hm-cfg && rm -rf /src/hm/build /src/hm/bin /src/hm/lib
RUN sed -i 's/#define DEC_NUH_TRACE  *0/#define DEC_NUH_TRACE 1/' /src/hm/source/Lib/TLibCommon/TypeDef.h \
    && cmake -S /src/hm -B /src/hm/trace -DCMAKE_BUILD_TYPE=Release -DCMAKE_CXX_FLAGS=-DENC_DEC_TRACE=1 \
    && make -C /src/hm/trace -j"$(nproc)" TAppDecoder \
    && cp "$(find /src/hm/bin -name TAppDecoder -type f)" /usr/local/bin/TAppDecoderTrace \
    && rm -rf /src/hm/trace /src/hm/bin /src/hm/lib
# libheif's JPEG encoder and decoder plugins (libjpeg-turbo), for JPEG-coded HEIF items.
RUN apt-get update && apt-get install -y --no-install-recommends libheif-plugin-jpegenc=1.23.4-1~deb13u1 \
        libheif-plugin-jpegdec=1.23.4-1~deb13u1 && rm -rf /var/lib/apt/lists/*
RUN ls /usr/local/bin && heif-info --version && heif-dec --version && x265 --version 2>&1 | head -3; exiftool -ver
WORKDIR /work
