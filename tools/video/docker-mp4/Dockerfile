# FFmpeg and GPAC (MP4Box) as black-box reference tools for MP4 and QuickTime (V4.1): they make test clips and check our
# output. Neither is linked or read; both run only inside this image.
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg gpac python3 && rm -rf /var/lib/apt/lists/*
WORKDIR /work
