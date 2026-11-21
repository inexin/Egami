# The HEVC encoder references for Egami.Video V8.1 (HEVC encoder Build Plan tasks 4-6 and 13). x265 (GPL) and FFmpeg (LGPL/GPL)
# come as Alpine's binary packages and are run only as black-box tools, never read: x265 is the encoder the compression and speed
# gates compare against, FFmpeg decodes every stream Egami's encoder writes and measures SSIM. The HM reference decoder
# (TAppDecoder, HM 18.0, BSD 3-Clause) is built from source and run as a decoder. perf counts cycles and instructions.
FROM alpine@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc
RUN apk add --no-cache build-base cmake git bash python3 py3-numpy ffmpeg x265 perf
RUN git clone --depth 1 --branch HM-18.0 https://vcgit.hhi.fraunhofer.de/jvet/HM.git /hm-src \
    && cmake -S /hm-src -B /hm-src/build -DCMAKE_BUILD_TYPE=Release -DHIGH_BITDEPTH=ON -DCMAKE_CXX_FLAGS="-Wno-error -w" > /dev/null \
    && cmake --build /hm-src/build --target TAppDecoder -j8 > /dev/null \
    && find /hm-src -type f -name 'TAppDecoder*' -perm -u+x -exec cp {} /usr/local/bin/TAppDecoder \; -quit \
    && rm -rf /hm-src
RUN x265 --version 2>&1 | head -2 && ffmpeg -version | head -1 && test -x /usr/local/bin/TAppDecoder
WORKDIR /work
