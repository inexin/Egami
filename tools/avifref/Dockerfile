# Reference tools for Phase 2.9.2 (AVIF): dav1d 1.5.4 (BSD-2), libaom 3.15.2 (BSD-2, AOM patent license) and libavif 1.4.2
# (BSD-2) built from source, with SVT-AV1 and rav1e from Debian as extra encoders for corpus variety; ExifTool. libavif is built
# without libyuv, so avifdec converts YUV to RGB with its own exact code. Built by tools/make-avif-references.py; the tools run as
# oracles only.
FROM debian@sha256:913f6706df59a68922d1dd08f78c2476560a8d367897200a6005b00e5f67c2d5
RUN apt-get update && apt-get install -y --no-install-recommends \
        build-essential cmake meson ninja-build nasm git ca-certificates pkg-config python3 python3-numpy \
        libpng-dev libjpeg62-turbo-dev zlib1g-dev libxml2-dev libsvtav1enc-dev librav1e-dev libimage-exiftool-perl \
    && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch 1.5.4 https://code.videolan.org/videolan/dav1d.git /src/dav1d \
    && meson setup /src/dav1d/build /src/dav1d --buildtype=release -Denable_tools=true -Denable_tests=false \
    && ninja -C /src/dav1d/build install && rm -rf /src/dav1d/build
RUN git clone --depth 1 --branch v3.15.2 https://aomedia.googlesource.com/aom /src/aom \
    && cmake -S /src/aom -B /src/aom/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=1 -DENABLE_TESTS=0 \
        -DENABLE_DOCS=0 -DENABLE_EXAMPLES=1 -DENABLE_TOOLS=0 \
    && ninja -C /src/aom/build install && rm -rf /src/aom/build
RUN git clone --depth 1 --branch v1.4.2 https://github.com/AOMediaCodec/libavif /src/libavif \
    && cmake -S /src/libavif -B /src/libavif/build -G Ninja -DCMAKE_BUILD_TYPE=Release -DAVIF_BUILD_APPS=ON \
        -DAVIF_CODEC_DAV1D=SYSTEM -DAVIF_CODEC_AOM=SYSTEM -DAVIF_CODEC_SVT=SYSTEM -DAVIF_CODEC_RAV1E=SYSTEM \
        -DAVIF_LIBYUV=OFF -DAVIF_JPEG=SYSTEM -DAVIF_ZLIBPNG=SYSTEM -DAVIF_LIBXML2=SYSTEM -DAVIF_LIBSHARPYUV=OFF \
    && ninja -C /src/libavif/build install && rm -rf /src/libavif/build && ldconfig
RUN dav1d --version && aomenc --help > /dev/null; avifdec --version && avifenc --version && exiftool -ver
# avifitems prints libavif's item model (its read.c, compiled in) for comparing item lists.
COPY avifitems.c /src/libavif/apps/avifitems.c
RUN cd /src/libavif/src && gcc -O1 -I/src/libavif/include -o /usr/local/bin/avifitems /src/libavif/apps/avifitems.c         $(ls *.c | grep -v -e '^read.c$' -e '^codec_' -e '^scale.c$') -lavif -lm
# av1headers prints dav1d's sequence and first frame headers (its public API) for comparing header readers.
COPY av1headers.c /src/av1headers.c
RUN gcc -O1 -o /usr/local/bin/av1headers /src/av1headers.c -ldav1d
# aominspect is libaom's inspect example (CONFIG_INSPECTION): each frame's mode info (block sizes, modes, transforms, skip,
# CDEF, segment ids, palette, intra block copy) as JSON, the block syntax trace the AV1 decoder is compared with.
RUN cmake -S /src/aom -B /src/aom/inspect -G Ninja -DCMAKE_BUILD_TYPE=Release -DCONFIG_INSPECTION=1 -DCONFIG_ACCOUNTING=1         -DCONFIG_AV1_ENCODER=0 -DENABLE_EXAMPLES=1 -DENABLE_TESTS=0 -DENABLE_DOCS=0 -DENABLE_TOOLS=0     && ninja -C /src/aom/inspect inspect && cp /src/aom/inspect/inspect /usr/local/bin/aominspect && rm -rf /src/aom/inspect
# avifbench times libavif's decode (dav1d) and YUV to RGB conversion from memory, for the speed gate (tools/time-avif-gate.py).
COPY avifbench.c /src/avifbench.c
RUN gcc -O2 -o /usr/local/bin/avifbench /src/avifbench.c -lavif
# libheif's heif-dec 1.23.4 (LGPL) from Debian, run as a black box only, decodes the overlay (iovl) files libavif cannot.
RUN apt-get update && apt-get install -y --no-install-recommends libheif-examples libheif-plugin-dav1d \
    && rm -rf /var/lib/apt/lists/* && heif-dec --version
WORKDIR /work
