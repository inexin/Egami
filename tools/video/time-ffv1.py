"""The V6 speed and size gates' FFmpeg side, and the clips they use: FFV1 version 3 clips (16 slices, slice CRCs, every frame a
keyframe, the range coder with a custom table and the small context model unless named) of testsrc2 with temporal noise, so they
code like camera footage, at 1080p (30 frames) and 2160p (10 frames), in .cache/video/ffv1/large. For each, FFmpeg's decode
time per frame and encode time per frame (from the decoded raw frames), the best of three runs at one thread and at all cores
(-threads 0), and the coded size, written to timing.json. FFmpeg runs only as a black box.

Egami's side, with the same settings:
  dotnet run -c Release --project benchmarks/Egami.Video.Benchmarks -- --time-ffv1 .cache/video/ffv1/large/*.mkv
  dotnet run -c Release --project benchmarks/Egami.Video.Benchmarks -- --encode-ffv1 <clip> --slices=16 --context=small [--coder=rice] --threads=N

Usage, from the repo root:
  python tools/video/time-ffv1.py            make the clips if missing and time FFmpeg
  python tools/video/time-ffv1.py --local    run the ffmpeg on the PATH instead of Docker
"""
import json
import os
import re
import subprocess
import sys

IMAGE = "egami-video-tools"
OUT = ".cache/video/ffv1/large"

# name: (size, frames, pixel format, coder)
CLIPS = {
    "fhd-420-8": ("1920x1080", 30, "yuv420p", "range_tab"),
    "fhd-420-10": ("1920x1080", 30, "yuv420p10le", "range_tab"),
    "fhd-422-10": ("1920x1080", 30, "yuv422p10le", "range_tab"),
    "fhd-420-8-rice": ("1920x1080", 30, "yuv420p", "rice"),
    "fhd-rgb-8": ("1920x1080", 30, "bgr0", "range_tab"),
    "uhd-420-8": ("3840x2160", 10, "yuv420p", "range_tab"),
    "uhd-420-10": ("3840x2160", 10, "yuv420p10le", "range_tab"),
}


def run(command, local):
    if local:
        return subprocess.run(["sh", "-c", f"cd {OUT} && {command}"], check=True, capture_output=True, text=True)
    return subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(OUT)}:/work", IMAGE, "sh", "-c", command],
                          check=True, capture_output=True, text=True)


def bench(command, local):
    result = run(f"ffmpeg -hide_banner -nostats -nostdin -benchmark {command} -f null -", local)
    return float(re.search(r"rtime=([0-9.]+)s", result.stdout + result.stderr).group(1))


def main():
    local = "--local" in sys.argv
    os.makedirs(OUT, exist_ok=True)
    timing = {}
    for name, (size, frames, fmt, coder) in CLIPS.items():
        file, raw = name + ".mkv", name + ".raw"
        settings = f"-c:v ffv1 -level 3 -coder {coder} -context 0 -slices 16 -slicecrc 1 -g 1"
        if not os.path.exists(os.path.join(OUT, file)):
            run(f"ffmpeg -y -v error -nostdin -f lavfi -i testsrc2=size={size}:rate=25 -frames:v {frames} "
                f"-vf noise=alls=8:allf=t,format={fmt} {settings} {file}", local)
        if not os.path.exists(os.path.join(OUT, raw)):
            run(f"ffmpeg -y -v error -nostdin -i {file} -f rawvideo {raw}", local)
        sizes = run(f"ffprobe -v error -show_entries packet=size -of csv=p=0 {file}", local).stdout.split()
        entry = {"bytes": sum(int(s) for s in sizes)}
        for threads in (1, 0):
            label = "1" if threads == 1 else "all"
            entry[f"decode_{label}"] = round(min(bench(f"-threads {threads} -i {file}", local) for _ in range(3)) * 1000 / frames, 2)
            source = f"-f rawvideo -pix_fmt {fmt} -s {size} -r 25 -i {raw}"
            entry[f"encode_{label}"] = round(min(bench(f"{source} -threads {threads} {settings}", local) for _ in range(3)) * 1000 / frames, 2)
        timing[name] = entry
        print(name, entry)
    with open(os.path.join(OUT, "timing.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(timing, f, indent=1)


if __name__ == "__main__":
    main()
