"""Makes the step 2.4.8 counting clips: the same five seconds of a film (720p, 120 frames) encoded as VP8, VP9, AV1 and HEVC, and
thirty seconds of its sound as Opus, into .cache/counts, with FFmpeg (libvpx, libaom, libx265, libopus) in the image
count-instructions.py uses. One source for every codec, so their instruction ratios to the reference decoders compare.

Usage, from the repo root with Docker running:
  python tools/video/make-count-clips.py ../egami-compare/.cache/scenes/tos.mov [--start 60]
Then, for example:
  python tools/video/count-instructions.py --mode time-vp9 .cache/counts/vp9.ivf --ffmpeg-args "-c:v libvpx-vp9"
"""
import argparse
import os
import subprocess
import sys

IMAGE = "egami-perf-counters:1"
OUT = ".cache/counts"
VIDEO = "-vf scale=1280:720:flags=lanczos,format=yuv420p -frames:v 120 -an"
CLIPS = {
    "vp8.ivf": "-c:v libvpx -b:v 2M -g 120",
    "vp9.ivf": "-c:v libvpx-vp9 -crf 32 -b:v 0 -row-mt 1 -g 120",
    "av1.ivf": "-c:v libaom-av1 -crf 32 -cpu-used 6 -row-mt 1 -g 120",
    "hevc.h265": "-c:v libx265 -crf 26 -preset medium -x265-params keyint=120:log-level=error",
}


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("film", help="the source film")
    parser.add_argument("--start", type=float, default=60, help="where the clips start, in seconds")
    args = parser.parse_args()
    os.makedirs(OUT, exist_ok=True)
    film = os.path.abspath(args.film)
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    base = ["docker", "run", "--rm", "-v", f"{os.getcwd()}:/work", "-v", f"{os.path.dirname(film)}:/in", IMAGE]
    source = f"-ss {args.start} -i /in/{os.path.basename(film)}"
    for name, codec in CLIPS.items():
        subprocess.run(base + ["sh", "-c", f"ffmpeg -v error -y {source} {VIDEO} {codec} /work/{OUT}/{name}"], env=env, check=True)
        print(f"{OUT}/{name}")
    subprocess.run(base + ["sh", "-c", f"ffmpeg -v error -y {source} -t 30 -vn -ac 2 -c:a libopus -b:a 128k /work/{OUT}/opus.opus"],
                   env=env, check=True)
    print(f"{OUT}/opus.opus")


if __name__ == "__main__":
    sys.exit(main())
