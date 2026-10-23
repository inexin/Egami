"""Makes the subtitle track corpus (Video V1.2 tasks 8-10): the subtitle files in tests/video/corpus/subtitles muxed into
Matroska, WebM and MP4 by FFmpeg, MKVToolNix and GPAC MP4Box, into tests/video/corpus/subtitles/tracks (checked in). The tests
read each file back to the cues of the file it was made from. The tools run only as black-box programs; their source is never
read.

Usage, from the repo root:
  docker build -t egami-video-tools tools/video/docker
  docker build -t egami-video-mp4:4.3 tools/video/docker-mp4
  python tools/video/make-subtitle-tracks.py           in Docker
  python tools/video/make-subtitle-tracks.py --local   with ffmpeg, mkvmerge and MP4Box on PATH (the checked-in files were made
                                                       this way with FFmpeg 6.1.1, mkvmerge 82.0 and MP4Box 2.2.1 on Ubuntu 24.04)
"""
import os
import shutil
import subprocess
import sys
import tempfile

SOURCE = os.path.abspath("tests/video/corpus/subtitles")
OUT = os.path.join(SOURCE, "tracks")
TOOLS = "egami-video-tools"
MP4 = "egami-video-mp4:4.3"

# name: (image, shell command run in /work, which holds the source files). FFmpeg copies the text where it can, so the blocks
# are the source cues as FFmpeg's demuxers read them. FFmpeg 6.1 reads no cues from a WebVTT file with REGION or STYLE blocks,
# so it gets cues.vtt: features.vtt from its first NOTE on, under a plain WEBVTT line.
CLIPS = {
    "ffmpeg-srt.mkv": (MP4, "ffmpeg -v error -i clean.srt -c:s copy -f matroska {out}"),
    "ffmpeg-ass.mkv": (MP4, "ffmpeg -v error -i features.ass -c:s copy -f matroska {out}"),
    "ffmpeg-vtt.mkv": (MP4, "ffmpeg -v error -i cues.vtt -c:s copy -f matroska {out}"),
    "ffmpeg-vtt.webm": (MP4, "ffmpeg -v error -f lavfi -i color=c=black:s=64x64:r=10:d=12 -i cues.vtt -map 0 -map 1 -c:v libvpx-vp9 -c:s copy {out}"),
    "ffmpeg-movtext.mp4": (MP4, "ffmpeg -v error -i clean.srt -c:s mov_text {out}"),
    "mp4box-movtext.mp4": (MP4, "MP4Box -quiet -add clean.srt:fmt=tx3g -new {out}"),
    "mp4box-wvtt.mp4": (MP4, "MP4Box -quiet -add clean.vtt -new {out}"),
    "mkvmerge-srt.mkv": (TOOLS, "mkvmerge -q -o {out} clean.srt"),
    "mkvmerge-vtt.mkv": (TOOLS, "mkvmerge -q -o {out} clean.vtt"),
    "mkvmerge-ass.mkv": (TOOLS, "mkvmerge -q -o {out} features.ass"),
}


def main():
    local = "--local" in sys.argv
    os.makedirs(OUT, exist_ok=True)
    with tempfile.TemporaryDirectory() as work:
        for name in os.listdir(SOURCE):
            if os.path.isfile(os.path.join(SOURCE, name)):
                shutil.copy(os.path.join(SOURCE, name), work)
        with open(os.path.join(SOURCE, "features.vtt"), encoding="utf-8", newline="") as f:
            features = f.read()
        with open(os.path.join(work, "cues.vtt"), "w", encoding="utf-8", newline="") as f:
            f.write("WEBVTT\n\n" + features[features.index("NOTE"):])
        # A plain WebVTT version of clean.srt for the tools that take only simple WebVTT.
        subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", "clean.srt", "clean.vtt"] if local else
                       ["docker", "run", "--rm", "-v", f"{work}:/work", MP4, "ffmpeg", "-v", "error", "-y", "-i", "clean.srt", "clean.vtt"],
                       cwd=work, check=True)
        for name, (image, command) in CLIPS.items():
            script = command.format(out=name)
            if local:
                subprocess.run(["sh", "-c", script], cwd=work, check=True)
            else:
                subprocess.run(["docker", "run", "--rm", "-v", f"{work}:/work", image, "sh", "-c", script], check=True)
            shutil.copy(os.path.join(work, name), os.path.join(OUT, name))
            print(name)
        for name in ("clean.vtt", "cues.vtt"):
            shutil.copy(os.path.join(work, name), os.path.join(OUT, name))


if __name__ == "__main__":
    main()
