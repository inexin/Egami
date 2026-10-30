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
  python tools/video/count-instructions.py --mode time-opus tests/video/corpus/audio/opus/opus-hybrid.opus --ffmpeg-args "-c:a libopus"
  python tools/video/count-instructions.py --mode time-image jpeg-decode-420                (an Egami.Benchmarks image scenario)
The timing mode must accept --one-thread and --runs=N (H264Timing does). time-image, time-vp8 and time-av1 run from
Egami.Benchmarks, the rest from Egami.Video.Benchmarks. time-opus counts per packet of the first audio track; time-image counts
per operation (its "clip" is the scenario name, see --time-image --list) and has no FFmpeg side.
"""
import argparse
import os
import subprocess
import sys

IMAGE = "egami-perf-counters:1"
DOCKERFILE = "FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine\nRUN apk add --no-cache perf ffmpeg\n"
BENCH = "benchmarks/Egami.Video.Benchmarks/bin/Release/net10.0"
IMAGE_BENCH = "benchmarks/Egami.Benchmarks/bin/Release/net10.0"
IMAGE_MODES = {"time-image", "time-vp8", "time-av1"}
AUDIO_MODES = {"time-opus"}
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
    # A build inside the repo runs from /work, so files it finds by walking up to Egami.slnx (.cache) are there.
    inside = os.path.relpath(os.path.abspath(BENCH_DIR), os.getcwd()).replace("\\", "/")
    bench = f"/work/{inside}" if not inside.startswith("..") else "/bench"
    script = script.replace("/bench/", f"{bench}/")
    out = subprocess.run(["docker", "run", "--rm", "--privileged", "-v", f"{os.getcwd()}:/work", "-v", f"{os.path.abspath(BENCH_DIR)}:/bench",
                          IMAGE, "sh", "-c", script], capture_output=True, text=True, env=ENV, check=True).stdout
    values = {}
    for line in out.splitlines():
        parts = line.split(",")
        if len(parts) > 2 and parts[2] in EVENTS:
            values[parts[2]] = float(parts[0]) if parts[0].replace(".", "").isdigit() else float("nan")
    return values


def frames(clip, audio=False):
    """Counts the clip's video frames, or its first audio track's packets."""
    kind, count = ("a:0", "packets") if audio else ("v:0", "frames")
    out = subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/work", IMAGE, "ffprobe", "-v", "error", f"-count_{count}",
                          "-select_streams", kind, "-show_entries", f"stream=nb_read_{count}", "-of", "csv=p=0", f"/work/{clip}"],
                         capture_output=True, text=True, env=ENV, check=True).stdout
    return int(out.strip().splitlines()[0])


def per_frame(low, high, count):
    return {e: (high[e] - low[e]) / count for e in EVENTS}


def main():
    global BENCH_DIR
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("clip", help="clip path relative to the repo root")
    parser.add_argument("--mode", default="time-h264", help="the benchmark project's timing mode, without dashes")
    parser.add_argument("--bench", help="folder of the built benchmark project (default: the mode's project in Release)")
    parser.add_argument("--runs", default="8,24", help="the two run counts A,B whose difference is measured")
    parser.add_argument("--pgo", action="store_true", help="keep dynamic PGO on (counts then vary by about 4%%)")
    parser.add_argument("--egami-only", action="store_true", help="skip FFmpeg")
    parser.add_argument("--ffmpeg-args", default="", help="extra ffmpeg input options, such as -c:v libdav1d")
    args = parser.parse_args()
    image_mode, audio = args.mode in IMAGE_MODES, args.mode in AUDIO_MODES
    BENCH_DIR = args.bench or (IMAGE_BENCH if image_mode else BENCH)
    dll = "Egami.Benchmarks.dll" if image_mode else "Egami.Video.Benchmarks.dll"
    clip = args.clip.replace("\\", "/")
    ensure_image()
    scenario = args.mode == "time-image"
    n = 1 if scenario else frames(clip, audio)
    unit = "operation" if scenario else "packet" if audio else "frame"
    a, b = (int(x) for x in args.runs.split(","))
    target = clip if scenario else f"/work/{clip}"
    egami = [counters(f"dotnet /bench/{dll} --{args.mode} --one-thread --runs={r} {target}", args.pgo) for r in (a, b)]
    rows = {"Egami": per_frame(egami[0], egami[1], n * (b - a))}
    if not args.egami_only and not scenario:
        limit = "-frames:a" if audio else "-frames:v"
        ffmpeg = [counters(f"ffmpeg -v error -threads 1 {args.ffmpeg_args} -i /work/{clip} {limit} {k} -f null -", True) for k in (20, n)]
        rows["FFmpeg"] = per_frame(ffmpeg[0], ffmpeg[1], n - 20)

    print(f"{clip}: {n} {unit}{'' if n == 1 else 's'}, per {unit} (user mode)")
    print(f"  {'':12} {'instructions':>13} {'branches':>10} {'br. misses':>10} {'L1 misses':>10} {'cycles':>10} {'IPC':>5}")
    for name, v in rows.items():
        print(f"  {name:12} {v['instructions:u'] / 1e6:12.2f}M {v['branches:u'] / 1e6:9.2f}M {v['branch-misses:u'] / 1e3:9.0f}k "
              f"{v['L1-dcache-load-misses:u'] / 1e3:9.0f}k {v['cycles:u'] / 1e6:9.2f}M {v['instructions:u'] / v['cycles:u']:5.2f}")
    if "FFmpeg" in rows:
        e, f = rows["Egami"], rows["FFmpeg"]
        print(f"  Egami/FFmpeg: {e['instructions:u'] / f['instructions:u']:.2f}x instructions, {e['branches:u'] / f['branches:u']:.2f}x branches, "
              f"{e['cycles:u'] / f['cycles:u']:.2f}x cycles")


if __name__ == "__main__":
    sys.exit(main())
