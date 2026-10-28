"""The V6 speed gate's FFmpeg side, and the clips it times: 1080p FFV1 version 3 clips (16 slices, slice CRCs, every frame a
keyframe) of testsrc2 with temporal noise, so they code like camera footage, in .cache/video/ffv1/large; and FFmpeg's decode time
per frame for each, the best of three runs at one thread and at all cores (-threads 0), written to timing.json. FFmpeg runs only
as a black box.

Egami's side: dotnet run -c Release --project benchmarks/Egami.Video.Benchmarks -- --time-ffv1 .cache/video/ffv1/large/*.mkv

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
FRAMES = 30
SOURCE = "testsrc2=size=1920x1080:rate=25"

# name: (pixel format, coder)
CLIPS = {
    "fhd-420-8": ("yuv420p", "range_tab"),
    "fhd-420-10": ("yuv420p10le", "range_tab"),
    "fhd-422-10": ("yuv422p10le", "range_tab"),
    "fhd-420-8-rice": ("yuv420p", "rice"),
    "fhd-rgb-8": ("bgr0", "range_tab"),
}


def run(command, local):
    if local:
        return subprocess.run(["sh", "-c", f"cd {OUT} && {command}"], check=True, capture_output=True, text=True)
    return subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(OUT)}:/work", IMAGE, "sh", "-c", command],
                          check=True, capture_output=True, text=True)


def main():
    local = "--local" in sys.argv
    os.makedirs(OUT, exist_ok=True)
    timing = {}
    for name, (fmt, coder) in CLIPS.items():
        file = name + ".mkv"
        if not os.path.exists(os.path.join(OUT, file)):
            run(f"ffmpeg -y -v error -nostdin -f lavfi -i {SOURCE} -frames:v {FRAMES} -vf noise=alls=8:allf=t,format={fmt} "
                f"-c:v ffv1 -level 3 -coder {coder} -slices 16 -slicecrc 1 -g 1 {file}", local)
        timing[name] = {}
        for threads in (1, 0):
            best = min(bench(file, threads, local) for _ in range(3))
            timing[name]["1" if threads == 1 else "all"] = round(best * 1000 / FRAMES, 2)
        print(name, timing[name])
    with open(os.path.join(OUT, "timing.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(timing, f, indent=1)


def bench(file, threads, local):
    result = run(f"ffmpeg -hide_banner -nostats -nostdin -benchmark -threads {threads} -i {file} -f null -", local)
    return float(re.search(r"rtime=([0-9.]+)s", result.stdout + result.stderr).group(1))


if __name__ == "__main__":
    main()
