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

The time-image jpeg-encode-* scenarios have a cjpeg side instead (libjpeg-turbo's cjpeg from Alpine's libjpeg-turbo-utils, a
black-box reference, never linked): the benchmark photo is written as PPM once and three times stacked, cjpeg encodes each with
the scenario's settings (-quality 75 -sample 2x2; -quality 90 -sample 1x1 [-optimize]), and the difference over two encodes of
the same pixels drops start-up out. cjpeg's count includes reading the PPM rows, which Egami's (from a raster) does not, so the
ratio is slightly in cjpeg's disfavour. The same is run with JSIMD_FORCENONE=1, which shows whether the build's SIMD is on.
  python tools/video/count-instructions.py --mode time-image jpeg-encode-q75-420

The JPEG XL modes (roadmap 2.4.11) count per image and per megapixel against libjxl 0.12.0's djxl and cjxl, run in
egami-jxlref-perf:2.9.3 (egami-jxlref:2.9.3 from the 2.9.3 references with Linux perf added; built here when missing):
  - time-jxl decodes a .jxl file: Egami's --time-jxl against djxl --disable_output --num_threads=0 --num_reps=A and B.
  - time-jxl-encode encodes a PNG at --jxl-setting (d1e7, d1e7p for progressive, ll-e3 for lossless): Egami's
    --time-jxl-encode against cjxl --num_threads=0 --num_reps=A and B with the same distance, effort and progressive flag.
Both default to runs 2,6 (encodes are slow); --full-opts gives the steadier counts.
  python tools/video/count-instructions.py --mode time-jxl --full-opts .cache/jxl-measure/kodim05-d1e7.jxl
  python tools/video/count-instructions.py --mode time-jxl-encode --jxl-setting d1e7 --full-opts .cache/jxl-measure/kodim05.png
"""
import argparse
import os
import subprocess
import sys

IMAGE = "egami-perf-counters:2"
DOCKERFILE = "FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine\nRUN apk add --no-cache perf ffmpeg libjpeg-turbo-utils\n"
BENCH = "benchmarks/Egami.Video.Benchmarks/bin/Release/net10.0"
IMAGE_BENCH = "benchmarks/Egami.Benchmarks/bin/Release/net10.0"
IMAGE_MODES = {"time-image", "time-vp8", "time-av1", "time-jxl", "time-jxl-encode"}
JXL_MODES = {"time-jxl", "time-jxl-encode"}
JXL_IMAGE = "egami-jxlref-perf:2.9.3"
JXL_DOCKERFILE = "FROM egami-jxlref:2.9.3\nRUN apt-get update && apt-get install -y --no-install-recommends linux-perf && rm -rf /var/lib/apt/lists/*\n"
AUDIO_MODES = {"time-opus"}
# The cjpeg settings of each time-image JPEG encode scenario.
CJPEG = {
    "jpeg-encode-q75-420": "-quality 75 -sample 2x2",
    "jpeg-encode-q75-420-rgba": "-quality 75 -sample 2x2",
    "jpeg-encode-q90-444": "-quality 90 -sample 1x1",
    "jpeg-encode-q90-444-optimized": "-quality 90 -sample 1x1 -optimize",
}
PPM_DIR = ".cache/jpeg-encode"
EVENTS = ["instructions:u", "branches:u", "branch-misses:u", "L1-dcache-load-misses:u", "cycles:u"]
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")


def ensure_image(image=IMAGE, dockerfile=DOCKERFILE):
    have = subprocess.run(["docker", "images", "-q", image], capture_output=True, text=True, env=ENV).stdout.strip()
    if not have:
        subprocess.run(["docker", "build", "-t", image, "-"], input=dockerfile, text=True, env=ENV, check=True)


def counters(command, pgo, env="", image=IMAGE):
    """Runs one command under perf stat in the image and returns {event: count}."""
    env += "" if pgo else "export DOTNET_TieredPGO=0; "
    script = f"{env}perf stat -x, -e {','.join(EVENTS)} -o /tmp/s {command} > /dev/null 2>&1; cat /tmp/s"
    # A build inside the repo runs from /work, so files it finds by walking up to Egami.slnx (.cache) are there.
    inside = os.path.relpath(os.path.abspath(BENCH_DIR), os.getcwd()).replace("\\", "/")
    bench = f"/work/{inside}" if not inside.startswith("..") else "/bench"
    script = script.replace("/bench/", f"{bench}/")
    out = subprocess.run(["docker", "run", "--rm", "--privileged", "-v", f"{os.getcwd()}:/work", "-v", f"{os.path.abspath(BENCH_DIR)}:/bench",
                          image, "sh", "-c", script], capture_output=True, text=True, env=ENV, check=True).stdout
    values = {}
    for line in out.splitlines():
        parts = line.split(",")
        # Some perf builds print an event without its :u modifier.
        event = parts[2] if len(parts) > 2 and parts[2] in EVENTS else parts[2] + ":u" if len(parts) > 2 else ""
        if event in EVENTS:
            values[event] = float(parts[0]) if parts[0].replace(".", "").isdigit() else float("nan")
    return values


def frames(clip, audio=False):
    """Counts the clip's video frames, or its first audio track's packets."""
    kind, count = ("a:0", "packets") if audio else ("v:0", "frames")
    out = subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/work", IMAGE, "ffprobe", "-v", "error", f"-count_{count}",
                          "-select_streams", kind, "-show_entries", f"stream=nb_read_{count}", "-of", "csv=p=0", f"/work/{clip}"],
                         capture_output=True, text=True, env=ENV, check=True).stdout
    return int(out.strip().splitlines()[0])


def photo_ppms():
    """Writes the benchmark photo as PPM, once and three times stacked, unless there already; returns their paths."""
    paths = [f"{PPM_DIR}/photo-x{k}.ppm" for k in (1, 3)]
    os.makedirs(PPM_DIR, exist_ok=True)
    for k, path in zip((1, 3), paths):
        if not os.path.exists(path):
            subprocess.run(["dotnet", f"{BENCH_DIR}/Egami.Benchmarks.dll", "--time-image", f"--write-ppm={path}", f"--copies={k}"], check=True)
    return paths


def per_frame(low, high, count):
    return {e: (high[e] - low[e]) / count for e in EVENTS}


def cjxl_options(setting):
    """Gives cjxl's options for a --jxl-setting: d1e7 (lossy), d1e7p (progressive) or ll-e3 (lossless)."""
    if setting.startswith("ll-e"):
        return f"-d 0 -e {setting[4:]}"
    distance, effort = setting.rstrip("p")[1:].split("e")
    return f"-d {distance} -e {effort}" + (" -p" if setting.endswith("p") else "")


def megapixels(clip):
    """Gets the image's size in megapixels from jxlinfo (a .jxl) or the PNG header."""
    if clip.endswith(".png"):
        with open(clip, "rb") as f:
            head = f.read(24)
        return int.from_bytes(head[16:20], "big") * int.from_bytes(head[20:24], "big") / 1e6
    out = subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/work", JXL_IMAGE, "jxlinfo", f"/work/{clip}"],
                         capture_output=True, text=True, env=ENV, check=True).stdout
    size = next(word for line in out.splitlines() if line.startswith("JPEG XL image") for word in line.replace(",", " ").split() if "x" in word and word[0].isdigit())
    w, h = size.split("x")
    return int(w) * int(h) / 1e6


def jxl(args, clip, dll):
    """The JPEG XL modes: Egami's benchmark against djxl or cjxl, per image and per megapixel."""
    ensure_image(JXL_IMAGE, JXL_DOCKERFILE)
    a, b = (int(x) for x in (args.runs or "2,6").split(","))
    tiering = "export DOTNET_TieredCompilation=0; " if args.full_opts else ""
    encode = args.mode == "time-jxl-encode"
    setting = f" --setting={args.jxl_setting}" if encode else ""
    egami = [counters(f"dotnet /bench/{dll} --{args.mode}{setting} --one-thread --runs={r} /work/{clip}", args.pgo, tiering) for r in (a, b)]
    rows = {"Egami": per_frame(egami[0], egami[1], b - a)}
    name = "cjxl" if encode else "djxl"
    if not args.egami_only:
        if encode:
            command = f"cjxl {cjxl_options(args.jxl_setting)} --num_threads=0 --quiet /work/{clip} /tmp/out.jxl --num_reps="
        else:
            command = f"djxl /work/{clip} --disable_output --num_threads=0 --num_reps="
        runs = [counters(f"{command}{r}", True, image=JXL_IMAGE) for r in (a, b)]
        rows[name] = per_frame(runs[0], runs[1], b - a)

    mp = megapixels(clip)
    what = f"{clip} {args.jxl_setting}" if encode else clip
    print(f"{what}: {mp:.2f} MP, per megapixel (user mode)")
    print(f"  {'':8} {'instructions':>13} {'branches':>10} {'br. misses':>10} {'L1 misses':>10} {'cycles':>10} {'IPC':>5}")
    for key, v in rows.items():
        print(f"  {key:8} {v['instructions:u'] / mp / 1e6:12.2f}M {v['branches:u'] / mp / 1e6:9.2f}M {v['branch-misses:u'] / mp / 1e3:9.0f}k "
              f"{v['L1-dcache-load-misses:u'] / mp / 1e3:9.0f}k {v['cycles:u'] / mp / 1e6:9.2f}M {v['instructions:u'] / v['cycles:u']:5.2f}")
    if name in rows:
        e, f = rows["Egami"], rows[name]
        print(f"  Egami/{name}: {e['instructions:u'] / f['instructions:u']:.2f}x instructions, {e['cycles:u'] / f['cycles:u']:.2f}x cycles, "
              f"{e['branch-misses:u'] / f['branch-misses:u']:.2f}x branch misses, {e['L1-dcache-load-misses:u'] / f['L1-dcache-load-misses:u']:.2f}x L1 misses")


def main():
    global BENCH_DIR
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("clip", help="clip path relative to the repo root")
    parser.add_argument("--mode", default="time-h264", help="the benchmark project's timing mode, without dashes")
    parser.add_argument("--bench", help="folder of the built benchmark project (default: the mode's project in Release)")
    parser.add_argument("--runs", help="the two run counts A,B whose difference is measured (default 8,24; 20,60 for time-image, "
                        "whose few operations still find the JIT tiering up at 8)")
    parser.add_argument("--pgo", action="store_true", help="keep dynamic PGO on (counts then vary by about 4%%)")
    parser.add_argument("--full-opts", action="store_true",
                        help="compile Egami fully optimized at once (no tiering): steady counts, where a hot loop otherwise "
                             "lands in one of several code versions from run to run")
    parser.add_argument("--egami-only", action="store_true", help="skip FFmpeg and cjpeg")
    parser.add_argument("--ffmpeg-args", default="", help="extra ffmpeg input options, such as -c:v libdav1d")
    parser.add_argument("--jxl-setting", default="d1e7", help="time-jxl-encode's setting: d1e7, d1e7p (progressive) or ll-e3 (lossless)")
    args = parser.parse_args()
    image_mode, audio = args.mode in IMAGE_MODES, args.mode in AUDIO_MODES
    BENCH_DIR = args.bench or (IMAGE_BENCH if image_mode else BENCH)
    dll = "Egami.Benchmarks.dll" if image_mode else "Egami.Video.Benchmarks.dll"
    clip = args.clip.replace("\\", "/")
    ensure_image()
    if args.mode in JXL_MODES:
        return jxl(args, clip, dll)
    scenario = args.mode == "time-image"
    n = 1 if scenario else frames(clip, audio)
    unit = "operation" if scenario else "packet" if audio else "frame"
    a, b = (int(x) for x in (args.runs or ("20,60" if scenario else "8,24")).split(","))
    target = clip if scenario else f"/work/{clip}"
    tiering = "export DOTNET_TieredCompilation=0; " if args.full_opts else ""
    egami = [counters(f"dotnet /bench/{dll} --{args.mode} --one-thread --runs={r} {target}", args.pgo, tiering) for r in (a, b)]
    rows = {"Egami": per_frame(egami[0], egami[1], n * (b - a))}
    if not args.egami_only and scenario and clip in CJPEG:
        ppms = photo_ppms()
        for name, env in (("cjpeg", ""), ("cjpeg no SIMD", "export JSIMD_FORCENONE=1; ")):
            runs = [counters(f"cjpeg {CJPEG[clip]} -outfile /dev/null /work/{ppm}", True, env) for ppm in ppms]
            rows[name] = per_frame(runs[0], runs[1], 2)
    if not args.egami_only and not scenario:
        limit = "-frames:a" if audio else "-frames:v"
        ffmpeg = [counters(f"ffmpeg -v error -threads 1 {args.ffmpeg_args} -i /work/{clip} {limit} {k} -f null -", True) for k in (20, n)]
        rows["FFmpeg"] = per_frame(ffmpeg[0], ffmpeg[1], n - 20)

    print(f"{clip}: {n} {unit}{'' if n == 1 else 's'}, per {unit} (user mode)")
    print(f"  {'':12} {'instructions':>13} {'branches':>10} {'br. misses':>10} {'L1 misses':>10} {'cycles':>10} {'IPC':>5}")
    for name, v in rows.items():
        print(f"  {name:12} {v['instructions:u'] / 1e6:12.2f}M {v['branches:u'] / 1e6:9.2f}M {v['branch-misses:u'] / 1e3:9.0f}k "
              f"{v['L1-dcache-load-misses:u'] / 1e3:9.0f}k {v['cycles:u'] / 1e6:9.2f}M {v['instructions:u'] / v['cycles:u']:5.2f}")
    for other in ("FFmpeg", "cjpeg", "cjpeg no SIMD"):
        if other in rows:
            e, f = rows["Egami"], rows[other]
            print(f"  Egami/{other}: {e['instructions:u'] / f['instructions:u']:.2f}x instructions, {e['branches:u'] / f['branches:u']:.2f}x branches, "
                  f"{e['cycles:u'] / f['cycles:u']:.2f}x cycles")


if __name__ == "__main__":
    sys.exit(main())
