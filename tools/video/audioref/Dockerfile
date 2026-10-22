# FFmpeg, the flac command-line tool and SoX as black-box reference tools (never linked, their source never read): they make
# the V5.0 audio test clips, decode the FLAC references and check Egami.Video's FLAC files with flac -t.
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends ffmpeg flac sox python3 && rm -rf /var/lib/apt/lists/*
WORKDIR /work
