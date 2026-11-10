# FFmpeg, aac-enc (the Fraunhofer FDK AAC library's example encoder, from Debian non-free) and AtomicParsley (to write iTunes
# tags) as black-box reference tools:
# they make the V5.10 AAC test clips. None is linked into Egami.Video, and their source is never read.
FROM debian:trixie-slim
RUN sed -i 's/^Components: main$/Components: main non-free/' /etc/apt/sources.list.d/debian.sources \
    && apt-get update && apt-get install -y --no-install-recommends ffmpeg aac-enc atomicparsley python3 && rm -rf /var/lib/apt/lists/*
WORKDIR /work
