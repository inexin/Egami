# FFmpeg and aac-enc (the Fraunhofer FDK AAC library's example encoder, from Debian non-free) as black-box reference tools:
# they make the V5.10 AAC test clips. Neither is linked into Egami.Video, and their source is never read.
FROM debian:trixie-slim
RUN sed -i 's/^Components: main$/Components: main non-free/' /etc/apt/sources.list.d/debian.sources \
    && apt-get update && apt-get install -y --no-install-recommends ffmpeg aac-enc python3 && rm -rf /var/lib/apt/lists/*
WORKDIR /work
