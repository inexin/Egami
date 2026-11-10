# The H.264 encoder references for Egami.Video V7.2 (H.264 Encoder Design task 12). x264 (GPL) and FFmpeg (LGPL/GPL) come as
# Alpine's binary packages and are run only as black-box tools, never read: x264 is the encoder the compression and speed gates
# compare against, FFmpeg decodes every stream Egami's encoder writes and measures SSIM. The JM reference decoder (ldecod,
# JM 19.0) is built from source and run only as a decoder; its source is not read. perf counts cycles for the speed gate.
FROM alpine@sha256:d9e853e87e55526f6b2917df91a2115c36dd7c696a35be12163d44e6e2a4b6bc
RUN apk add --no-cache build-base git bash python3 py3-numpy ffmpeg x264 perf
RUN git clone --depth 1 --branch JM-19.0 https://vcgit.hhi.fraunhofer.de/jvet/JM.git /jm-src \
    && make -C /jm-src/ldecod -j8 CC="gcc -w -fcommon" \
    && find /jm-src -type f -name 'ldecod*' -perm -u+x -exec cp {} /usr/local/bin/ldecod \; -quit \
    && rm -rf /jm-src
RUN x264 --version | head -1 && ffmpeg -version | head -1 && test -x /usr/local/bin/ldecod
WORKDIR /work
