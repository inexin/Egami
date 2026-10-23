# Apple's reference ALAC codec (Apache-2.0, https://github.com/macosforge/alac) built with Egami's small driver alacref.cpp,
# plus FFmpeg: tools/video/make-alac-references.py encodes the gate signals with it, and Egami.Video's ALAC encoder must
# write the same packets. Egami.Video's ALAC codec is translated from this source (see THIRD-PARTY-NOTICES.md).
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates git build-essential ffmpeg python3 \
    && rm -rf /var/lib/apt/lists/*
COPY alacref.cpp build.sh /alacref/
RUN sh /alacref/build.sh /opt && cp /opt/alacref /usr/local/bin/
WORKDIR /work
