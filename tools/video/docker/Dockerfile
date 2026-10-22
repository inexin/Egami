# FFmpeg, MKVToolNix and mkvalidator as black-box reference tools (never linked): they make test clips and check our output.
# mkvalidator (BSD-3, Matroska foundation-source) is built from source since Debian does not package it.
FROM debian:bookworm-slim AS mkvalidator
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates git cmake make gcc g++ && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch mkvalidator-0.6.0 https://github.com/Matroska-Org/foundation-source /src \
    && cmake -S /src -B /build -DCMAKE_BUILD_TYPE=Release \
    && cmake --build /build --target mkvalidator -j4 \
    && find /build -name mkvalidator -type f -perm -u+x -exec cp {} /mkvalidator \;

FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg mkvtoolnix python3 && rm -rf /var/lib/apt/lists/*
COPY --from=mkvalidator /mkvalidator /usr/local/bin/mkvalidator
WORKDIR /work
