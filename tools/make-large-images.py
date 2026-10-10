"""Builds the Phase 2.2 large test images in .cache/large with ImageMagick and libjpeg-turbo's cjpeg in Docker.

Streaming decode is gated on files too big to commit, so they are generated the same way on every machine:
  png-20000.png             20,000 x 20,000 RGB PNG (the thumbnail memory gate; Load alone needs 1.2 GB as Rgb8)
  jpeg-8000.jpg             8,000 x 8,000 baseline JPEG, quality 90, 4:2:0, no restart markers
  jpeg-8000-restart.jpg     the same with a restart marker after every MCU row (the region speed gate)
  gif-10.gif, gif-500.gif   480 x 270 animations of 10 and 500 frames (held memory flat from 10 to 500 frames)

Content is the benchmark photo (BenchImages.Photo, written by the benchmark project as PNG) tiled or turned, so the files
compress like photos rather than like flat colour.

Usage, from the repo root with Docker running:
  python tools/make-large-images.py
The script runs alpine:3.20 and installs imagemagick and libjpeg-turbo-utils inside it with apk. The files are not committed.
Then report what 1.0 Load holds at its peak:
  dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --peak .cache/large/*
"""
import os
import subprocess

IMAGE = "alpine:3.20"
PACKAGES = "imagemagick libjpeg-turbo-utils"
TARGET = ".cache/large"

# ImageMagick holds a 20,000 x 20,000 image at 16 bits per sample (3.2 GB), so it may spill to its disk cache.
LIMITS = "-limit memory 2GiB -limit map 4GiB -limit disk 32GiB -limit width 64KP -limit height 64KP -limit area 1GP"

STEPS = [
    f"magick {LIMITS} -size 20000x20000 tile:/work/photo.png -depth 8 -define png:compression-level=6 /work/png-20000.png",
    f"magick {LIMITS} -size 8000x8000 tile:/work/photo.png -depth 8 /work/tile-8000.ppm",
    "cjpeg -quality 90 -sample 2x2 -outfile /work/jpeg-8000.jpg /work/tile-8000.ppm",
    "cjpeg -quality 90 -sample 2x2 -restart 1 -outfile /work/jpeg-8000-restart.jpg /work/tile-8000.ppm",
    "rm /work/tile-8000.ppm",
]

# Each frame is the photo turned a little further, so every frame differs from the one before.
for frames in (10, 500):
    STEPS.append(
        f"magick /work/photo.png -resize 480x270! -duplicate {frames - 1} -virtual-pixel edge "
        f"-distort SRT '%[fx:t*0.72]' -delay 4 -loop 0 /work/gif-{frames}.gif"
    )

STEPS.append("rm /work/photo.png")

FILES = ["png-20000.png", "jpeg-8000.jpg", "jpeg-8000-restart.jpg", "gif-10.gif", "gif-500.gif"]


def main():
    os.makedirs(TARGET, exist_ok=True)
    png = os.path.join(TARGET, "photo.png")
    subprocess.run(["dotnet", "run", "-c", "Release", "--project", "benchmarks/Egami.Benchmarks", "--", "--write-photo", png], check=True)
    script = f"apk add --no-cache {PACKAGES} > /dev/null && " + " && ".join(STEPS)
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(TARGET)}:/work", IMAGE, "sh", "-c", script], check=True)
    for name in FILES:
        print(f"{name}: {os.path.getsize(os.path.join(TARGET, name)):,} bytes")


if __name__ == "__main__":
    main()
