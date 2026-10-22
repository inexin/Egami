# libvpx 1.14.1 (BSD-3, Google patent grant) built from source as the VP9 reference for Egami.Video V3. vpxdec gives the expected
# frames, vpxenc, FFmpeg and mkvmerge (run only as black-box tools, never read) make the generated clips and probe the containers, and
# vp9dump (vp9dump.c, built against libvpx's decoder internals) prints what libvpx read from every frame: its header fields, its
# probabilities after adaptation, every block's modes and motion vectors, and the MD5 of the frame.
# The reference build is plain C (generic target, one thread, high bit depth on), so vp9dump sees the code paths the reference
# uses. vpxdec-fast is the SIMD build without high bit depth (the 8-bit speed gate) and vpxdec-fast-hbd the SIMD build with it.
FROM alpine@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc
RUN apk add --no-cache build-base git yasm nasm perl bash diffutils python3 ffmpeg
RUN git clone --depth 1 --branch v1.14.1 https://chromium.googlesource.com/webm/libvpx /libvpx-src \
    && cp -r /libvpx-src /libvpx-fast && cp -r /libvpx-src /libvpx-fast-hbd \
    && sed -i 's|^  vp9_decode_frame(pbi, source, source + size, psource);|&\n  if (vp9dump_hook) vp9dump_hook(pbi);|' \
        /libvpx-src/vp9/decoder/vp9_decoder.c \
    && sed -i 's|^int vp9_receive_compressed_data(|void vp9dump_hook(VP9Decoder *pbi) __attribute__((weak));\n&|' \
        /libvpx-src/vp9/decoder/vp9_decoder.c \
    && grep -q 'vp9dump_hook(pbi)' /libvpx-src/vp9/decoder/vp9_decoder.c \
    && mkdir /libvpx && cd /libvpx && /libvpx-src/configure --disable-vp8 --enable-vp9 --enable-vp9-highbitdepth \
        --disable-unit-tests --disable-docs --disable-multithread --target=generic-gnu \
    && make -j8 && cp vpxdec vpxenc /usr/local/bin/ \
    && cd /libvpx-fast && ./configure --disable-vp8 --disable-vp9-encoder --disable-unit-tests --disable-docs \
    && make -j8 && cp vpxdec /usr/local/bin/vpxdec-fast \
    && cd /libvpx-fast-hbd && ./configure --disable-vp8 --disable-vp9-encoder --enable-vp9-highbitdepth --disable-unit-tests \
        --disable-docs && make -j8 && cp vpxdec /usr/local/bin/vpxdec-fast-hbd
COPY vp9dump.c /vp9dump.c
RUN gcc -O1 -std=gnu99 -I/libvpx -I/libvpx-src -o /usr/local/bin/vp9dump /vp9dump.c /libvpx-src/md5_utils.c /libvpx/libvpx.a -lm \
        -lpthread
# mkvmerge writes the HDR10 clip's mastering display and light level metadata into the Matroska Colour element.
RUN apk add --no-cache mkvtoolnix
WORKDIR /work
