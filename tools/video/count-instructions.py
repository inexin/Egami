"""Counts the instructions, branches and cycles a decoder spends per frame, for Egami.Video against FFmpeg, from the CPU's
hardware counters (Linux perf in Docker, user mode only). Instruction counts barely move when the machine is busy, so they show
1% changes that wall-clock timing cannot, and they tell executing more instructions apart from waiting on memory (IPC).

Both sides are measured as differences, so start-up, file reading and JIT warm-up drop out:
  - Egami.Video: the benchmark project's timing mode run with --runs=A and --runs=B on one thread; the frames of B - A runs.
    Dynamic PGO is off by default (DOTNET_TieredPGO=0): with it on, the instruction count of the same build moves by about 4%
    from process to process, as the JIT's profile-driven decisions differ.
  - FFmpeg: `ffmpeg -threads 1 -i clip -f null -` decoding all frames and decoding the first 20; the frames in between.
FFmpeg comes from Alpine's package in the image, a build that differs from other FFmpeg builds in absolute speed; the
instruction ratio is the reliable part.

Usage, from the repo root with Docker running and the benchmarks built in Release:
  python tools/video/count-instructions.py --mode time-h264 .cache/video/h264/large/1080p.h264
  python tools/video/count-instructions.py --mode time-h264 --egami-only clip.h264       (Egami only, for A/B of builds)
  python tools/video/count-instructions.py --bench other/bin/folder ...                   (another build of the benchmarks)
The timing mode must accept --one-thread and --runs=N (H264Timing does).
"""
import argparse
import os
import subprocess
import sys

IMAGE = "egami-perf-counters:1"
DOCKERFILE = "FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine\nRUN apk add --no-cache perf ffmpeg\n"
BENCH = "benchmarks/Egami.Video.Benchmarks/bin/Release/net10.0"
EVENTS = ["instructions:u", "branches:u", "branch-misses:u", "L1-dcache-load-misses:u", "cycles:u"]
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")


def ensure_image():
    have = subprocess.run(["docker", "images", "-q", IMAGE], capture_output=True, text=True, env=ENV).stdout.strip()
    if not have:
        subprocess.run(["docker", "build", "-t", IMAGE, "-"], input=DOCKERFILE, text=True, env=ENV, check=True)


def counters(command, pgo):
    """Runs one command under perf stat in the image and returns {event: count}."""
    env = "" if pgo else "export DOTNET_TieredPGO=0; "
    script = f"{env}perf stat -x, -e {','.join(EVENTS)} -o /tmp/s {command} > /dev/null 2>&1; cat /tmp/s"
    out = subprocess.run(["docker", "run", "--rm", "--privileged", "-v", f"{os.getcwd()}:/work", "-v", f"{os.path.abspath(BENCH_DIR)}:/bench",
                          IMAGE, "sh", "-c", script], capture_output=True, text=True, env=ENV, check=True).stdout
    values = {}
    for line in out.splitlines():
        parts = line.split(",")
        if len(parts) > 2 and parts[2] in EVENTS:
            values[parts[2]] = float(parts[0]) if parts[0].replace(".", "").isdigit() else float("nan")
    return values


def frames(clip):
    out = subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/work", IMAGE, "ffprobe", "-v", "error", "-count_frames",
                          "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames", "-of", "csv=p=0", f"/work/{clip}"],
                         capture_output=True, text=True, env=ENV, check=True).stdout
    return int(out.strip().splitlines()[0])


def per_frame(low, high, count):
    return {e: (high[e] - low[e]) / count for e in EVENTS}


def main():
    global BENCH_DIR
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("clip", help="clip path relative to the repo root")
    parser.add_argument("--mode", default="time-h264", help="the benchmark project's timing mode, without dashes")
    parser.add_argument("--bench", default=BENCH, help="folder of the built benchmark project")
    parser.add_argument("--runs", default="8,24", help="the two run counts A,B whose difference is measured")
    parser.add_argument("--pgo", action="store_true", help="keep dynamic PGO on (counts then vary by about 4%%)")
    parser.add_argument("--egami-only", action="store_true", help="skip FFmpeg")
    parser.add_argument("--ffmpeg-args", default="", help="extra ffmpeg input options, such as -c:v libdav1d")
    args = parser.parse_args()
    BENCH_DIR = args.bench
    clip = args.clip.replace("\\", "/")
    ensure_image()
    n = frames(clip)
    a, b = (int(x) for x in args.runs.split(","))
    egami = [counters(f"dotnet /bench/Egami.Video.Benchmarks.dll --{args.mode} --one-thread --runs={r} /work/{clip}", args.pgo) for r in (a, b)]
    rows = {"Egami.Video": per_frame(egami[0], egami[1], n * (b - a))}
    if not args.egami_only:
        ffmpeg = [counters(f"ffmpeg -v error -threads 1 {args.ffmpeg_args} -i /work/{clip} -frames:v {k} -f null -", True) for k in (20, n)]
        rows["FFmpeg"] = per_frame(ffmpeg[0], ffmpeg[1], n - 20)

    print(f"{clip}: {n} frames, per frame (user mode)")
    print(f"  {'':12} {'instructions':>13} {'branches':>10} {'br. misses':>10} {'L1 misses':>10} {'cycles':>10} {'IPC':>5}")
    for name, v in rows.items():
        print(f"  {name:12} {v['instructions:u'] / 1e6:12.2f}M {v['branches:u'] / 1e6:9.2f}M {v['branch-misses:u'] / 1e3:9.0f}k "
              f"{v['L1-dcache-load-misses:u'] / 1e3:9.0f}k {v['cycles:u'] / 1e6:9.2f}M {v['instructions:u'] / v['cycles:u']:5.2f}")
    if "FFmpeg" in rows:
        e, f = rows["Egami.Video"], rows["FFmpeg"]
        print(f"  Egami/FFmpeg: {e['instructions:u'] / f['instructions:u']:.2f}x instructions, {e['branches:u'] / f['branches:u']:.2f}x branches, "
              f"{e['cycles:u'] / f['cycles:u']:.2f}x cycles")


if __name__ == "__main__":
    sys.exit(main())
