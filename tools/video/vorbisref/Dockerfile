# libvorbis (BSD-3) and FFmpeg as black-box reference tools: vorbisref.c, built here against libvorbisfile, writes the float
# samples libvorbis decodes (ov_read_float), which Egami.Video's Vorbis decoder must match within 2^-16 of full scale; FFmpeg
# and vorbis-tools make the test clips.
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends gcc libc6-dev libvorbis-dev libogg-dev vorbis-tools ffmpeg python3 \
    && rm -rf /var/lib/apt/lists/*
COPY vorbisref.c /src/vorbisref.c
RUN gcc -O2 -o /usr/local/bin/vorbisref /src/vorbisref.c -lvorbisfile -lvorbis -logg -lm
WORKDIR /work
