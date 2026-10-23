# FFmpeg, GPAC (MP4Box) and Bento4 (mp4info) as black-box reference tools for MP4 and QuickTime (V4.1, V4.3): they make test
# clips and check our output. None is linked or read; all run only inside this image.
FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg gpac python3 git cmake g++ make ca-certificates && rm -rf /var/lib/apt/lists/*
RUN git clone --depth 1 --branch v1.6.0-641 https://github.com/axiomatic-systems/Bento4.git /opt/bento4-src \
    && cmake -S /opt/bento4-src -B /opt/bento4-build -DCMAKE_BUILD_TYPE=Release \
    && cmake --build /opt/bento4-build --target mp4info -j 4 \
    && cp /opt/bento4-build/mp4info /usr/local/bin/ && rm -rf /opt/bento4-src /opt/bento4-build
WORKDIR /work
