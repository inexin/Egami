# libaom 3.15.2 (BSD-2, AOM patent license) and SVT-AV1 3.0.2 (BSD-3-Clause-Clear, AOM patent license) built from source as the
# AV1 encoder references for Egami.Video V5.4: aomenc is the optimized build (SIMD, threads, high bit depth) that the compression
# and speed gates compare against, SvtAv1EncApp the encoder whose preset is recorded next to them, and aomdec decodes both
# encoders' streams for the metrics. FFmpeg (run only as a black-box tool, never read) measures SSIM.
FROM alpine@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc
RUN apk add --no-cache build-base cmake samurai git yasm nasm perl bash python3 py3-numpy ffmpeg
RUN git clone --depth 1 --branch v3.15.2 https://aomedia.googlesource.com/aom /aom-src \
    && cmake -S /aom-src -B /aom-build -G Ninja -DCMAKE_BUILD_TYPE=Release -DENABLE_TESTS=0 -DENABLE_DOCS=0 -DENABLE_EXAMPLES=0 \
        -DENABLE_TOOLS=0 \
    && ninja -C /aom-build && cp /aom-build/aomenc /aom-build/aomdec /usr/local/bin/ \
    && rm -rf /aom-build /aom-src
RUN git clone --depth 1 --branch v3.0.2 https://gitlab.com/AOMediaCodec/SVT-AV1.git /svt-src \
    && cmake -S /svt-src -B /svt-build -G Ninja -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=OFF -DBUILD_DEC=OFF -DBUILD_TESTING=OFF \
    && ninja -C /svt-build && cp /svt-src/Bin/Release/SvtAv1EncApp /usr/local/bin/ \
    && rm -rf /svt-build /svt-src
RUN aomenc --help > /dev/null && aomdec --help > /dev/null; SvtAv1EncApp --version
WORKDIR /work
