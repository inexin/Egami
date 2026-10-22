# libopus 1.5.2 (BSD-3) built from source for opus_demo and opus_compare, plus opus-tools and FFmpeg as black-box reference
# tools: they decode the RFC 8251 test vectors, compare Egami.Video's output with opus_compare, and make Ogg and WebM test clips.
# Egami.Video's Opus decoder is translated from this libopus release (see THIRD-PARTY-NOTICES.md).
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates git build-essential cmake \
        ffmpeg opus-tools python3 curl && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch v1.5.2 https://github.com/xiph/opus /opus \
    && cmake -S /opus -B /build -DCMAKE_BUILD_TYPE=Release -DOPUS_BUILD_PROGRAMS=ON -DOPUS_DRED=OFF -DOPUS_DEEP_PLC=OFF > /dev/null \
    && cmake --build /build -j4 --target opus_demo opus_compare > /dev/null \
    && cp /build/opus_demo /build/opus_compare /usr/local/bin/
WORKDIR /work
