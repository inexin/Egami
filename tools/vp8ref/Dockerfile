# libvpx 1.14.1 (BSD-3, Google patent grant) built from source as the VP8 reference for Phase 2.3.5: vpxdec gives the expected
# frames, vpxenc and FFmpeg (run only as a black-box tool, never read) make the generated clips, and vp8dump (vp8dump.c, built
# against libvpx's decoder internals) prints each frame's header fields and each macroblock's modes and motion vectors.
# The generic build is plain C, so vp8dump sees the same code paths the reference uses; vpxdec-fast is the SIMD build, for timing.
FROM alpine@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc
RUN apk add --no-cache build-base git yasm perl bash diffutils python3 ffmpeg
RUN git clone --depth 1 --branch v1.14.1 https://chromium.googlesource.com/webm/libvpx /libvpx-src \
    && cp -r /libvpx-src /libvpx-fast \
    && printf '%s\n' 'void *vp8dump_pbi(vpx_codec_ctx_t *ctx) {' \
        '  return ctx->priv ? ((vpx_codec_alg_priv_t *)ctx->priv)->yv12_frame_buffers.pbi[0] : 0;' '}' \
        >> /libvpx-src/vp8/vp8_dx_iface.c \
    && mkdir /libvpx && cd /libvpx && /libvpx-src/configure --disable-vp9 --disable-unit-tests --disable-docs --enable-vp8 \
        --disable-multithread --target=generic-gnu \
    && make -j8 && cp vpxdec vpxenc /usr/local/bin/ \
    && cd /libvpx-fast && ./configure --disable-vp9 --disable-unit-tests --disable-docs --disable-vp8-encoder \
    && make -j8 && cp vpxdec /usr/local/bin/vpxdec-fast
COPY vp8dump.c /vp8dump.c
RUN gcc -O1 -std=gnu99 -I/libvpx -I/libvpx-src -o /usr/local/bin/vp8dump /vp8dump.c /libvpx/libvpx.a -lm
WORKDIR /work
