# libvpx 1.14.1 (BSD-3, Google patent grant) built from source as the VP9 encoder reference for Egami.Video V5.3: vpxenc is the
# optimized build (SIMD, threads, 8 bit) that the compression and speed gates compare against, vpxenc-hbd the same with high
# bit depth for the 10 and 12-bit comparisons, and vpxdec the decoder that checks every stream Egami's encoder writes. FFmpeg (run
# only as a black-box tool, never read) measures PSNR and SSIM.
FROM alpine@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc
RUN apk add --no-cache build-base git yasm nasm perl bash diffutils python3 py3-numpy ffmpeg
RUN git clone --depth 1 --branch v1.14.1 https://chromium.googlesource.com/webm/libvpx /libvpx-src \
    && mkdir /build && cd /build && /libvpx-src/configure --disable-vp8 --enable-vp9 --disable-unit-tests --disable-docs \
    && make -j8 && cp vpxenc /usr/local/bin/vpxenc && cp vpxdec /usr/local/bin/vpxdec \
    && mkdir /build-hbd && cd /build-hbd && /libvpx-src/configure --disable-vp8 --enable-vp9 --enable-vp9-highbitdepth \
        --disable-unit-tests --disable-docs \
    && make -j8 && cp vpxenc /usr/local/bin/vpxenc-hbd && cp vpxdec /usr/local/bin/vpxdec-hbd \
    && rm -rf /build /build-hbd /libvpx-src
WORKDIR /work
