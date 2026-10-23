"""Compares reading a subtitle file and writing it as SRT with FFmpeg (Video V1.2 task 14). The benchmark program writes three
files of 20,000 cues (SRT, WebVTT, ASS) into .cache/video/subtitle-timing and times Egami.Video in-process, the best of five
after a warm-up; FFmpeg runs `ffmpeg -i <file> -f srt` five times per file, and its time with a one-cue file is taken off as
start-up. FFmpeg is run as a tool only.

Usage, from the repo root:
  docker build -t egami-video-tools tools/video/docker
  python tools/video/time-subtitles.py           FFmpeg in Docker
  python tools/video/time-subtitles.py --local   ffmpeg on PATH
"""
import os
import subprocess
import sys
import time

OUT = os.path.abspath(".cache/video/subtitle-timing")
LOCAL = "--local" in sys.argv
BENCH = ["dotnet", "run", "-c", "Release", "--project", "benchmarks/Egami.Video.Benchmarks", "--", "--time-subtitles"]
FILES = ["cues-20000.srt", "cues-20000.vtt", "cues-20000.ass"]


def ffmpeg(name):
    args = ["ffmpeg", "-v", "error", "-y", "-i", name, "-f", "srt", os.path.splitext(name)[0] + ".ffmpeg.srt"]
    command = args if LOCAL else ["docker", "run", "--rm", "-v", f"{OUT}:/work", "-w", "/work", "egami-video-tools"] + args
    best = None
    for _ in range(5):
        start = time.perf_counter()
        subprocess.run(command, cwd=OUT, check=True)
        elapsed = (time.perf_counter() - start) * 1000
        best = elapsed if best is None else min(best, elapsed)
    return best


def main():
    subprocess.run(BENCH + ["--write", OUT], check=True, capture_output=True)
    with open(os.path.join(OUT, "one.srt"), "w", newline="\n") as file:
        file.write("1\n00:00:01,000 --> 00:00:02,000\nOne\n")
    startup = ffmpeg("one.srt")
    egami = {}
    output = subprocess.run(BENCH + [os.path.join(OUT, f) for f in FILES], check=True, capture_output=True, text=True).stdout
    for line in output.splitlines():
        name, ms = line.split("\t")
        egami[name] = float(ms.split()[0])
    print(f"FFmpeg start-up (one cue): {startup:.1f} ms, taken off FFmpeg's times")
    print(f"{'file':<18}{'Egami.Video':>14}{'FFmpeg':>12}{'ratio':>8}")
    for name in FILES:
        theirs = max(ffmpeg(name) - startup, 0.1)
        print(f"{name:<18}{egami[name]:>11.1f} ms{theirs:>9.1f} ms{theirs / egami[name]:>7.2f}x")


if __name__ == "__main__":
    main()
