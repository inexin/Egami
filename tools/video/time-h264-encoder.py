"""The V7.2 H.264 encoder gates (H.264 Encoder Design task 12): Egami's BD-rate on PSNR-Y and SSIM against x264 --preset medium
(at most +15 %), with x264's other presets recorded next to it so the preset whose time matches Egami's shows too; Egami's
encode time at its default effort against x264 medium (at most 2 times), on one thread and on all cores, in wall time and in
CPU cycles; and the rate control gate: one and two-pass bitrate within 5 % of the target over a clip, constant bitrate never
emptying the buffer.

Everything runs in Docker on the same machine: x264 0.164 (Alpine's binary package, run only as a black-box encoder) and FFmpeg
6.1 in egami-h264encref:7.2 from tools/video/h264encref/Dockerfile, and Egami through the benchmark project's Release build
(--encode-h264) on .NET 10 (egami-dotnet-perf:2.4.5). For the BD-rate both are tuned for PSNR (x264 --tune psnr, Egami
without adaptive quantization) and use their own defaults otherwise; x264 at CRF 21, 26, 31 and 36, Egami at Quality 52, 43, 34
and 25, which cover about the same range. FFmpeg decodes every stream and measures SSIM; PSNR-Y is the mean of the frames' own.
The BD-rate is a cubic fit of log rate over the metric.

Times are each encoder's whole run for its frames (Egami's frames are read into memory first, x264 reads the y4m as it goes,
which costs little next to encoding); x264 encodes at the bitrate Egami's quality gave, runs alternate between the encoders and
the best of --rounds counts, as other work on the shared machine slows both alike. Cycles are user-mode hardware counts from
Linux perf, which barely move when the machine is busy: Egami's at --runs 1 and 3 (egami-perf-counters:2, the image
tools/video/count-instructions.py builds), x264's at --frames N and 2N, and the difference over the extra frames, so start-up and
the JIT drop out. Egami runs on the default runtime, tiered PGO on, as users get it; --no-pgo turns it off (DOTNET_TieredPGO=0),
as this mode did before the H.264 Encoder Speed Build Plan (decision Q4).

The clips are Xiph's derf clips (crowd_run, ducks_take_off, park_joy, old_town_cross; 50 fps), copied to .cache/h264enc/clips
as <name>-<size>.y4m from .cache/video/derf (egami-compare's video/native/vp8-encode-gate.py fetches them) or --clips-from.

Usage, from the repo root with Docker running and the benchmarks built in Release
(dotnet build -c Release benchmarks/Egami.Video.Benchmarks):
  python tools/video/time-h264-encoder.py bd [--size 720] [--frames 60] [--effort 4] [--presets ultrafast,...,slow] [--clips a,b]
  python tools/video/time-h264-encoder.py time [--size 720] [--frames 30] [--effort 4] [--presets medium] [--threads 1,24]
  python tools/video/time-h264-encoder.py cycles [--size 720] [--frames 20] [--effort 4] [--presets medium] [--no-pgo]
  python tools/video/time-h264-encoder.py rc [--size 720] [--frames 120] [--effort 2] [--kbps 2500]   (one file per target)
  python tools/video/time-h264-encoder.py report     (the gate table from the last bd, time, cycles and rc runs)
"""
import argparse
import concurrent.futures
import json
import os
import shutil
import subprocess

import numpy as np

OUT = ".cache/h264enc"
CLIP_DIR = ".cache/h264enc/clips"
CLIPS = ["crowd_run", "ducks_take_off", "park_joy", "old_town_cross"]
QUALITIES = [52, 43, 34, 25]
CRFS = [21, 26, 31, 36]
PRESETS = "ultrafast,superfast,veryfast,faster,fast,medium,slow"
REF_IMAGE = "egami-h264encref:7.2"
DOTNET_IMAGE = "egami-dotnet-perf:2.4.5"
PERF_IMAGE = "egami-perf-counters:2"
PERF_DOCKERFILE = "FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine\nRUN apk add --no-cache perf ffmpeg libjpeg-turbo-utils\n"
BENCH = "benchmarks/Egami.Video.Benchmarks/bin/Release/net10.0/Egami.Video.Benchmarks.dll"
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")
ROOT = os.path.abspath(".")
GATE_BD = 15.0
GATE_TIME = 2.0
GATE_RATE = 5.0


def docker(image, command, privileged=False):
    args = ["docker", "run", "--rm"] + (["--privileged"] if privileged else []) + ["-v", f"{ROOT}:/work", "-w", "/work"]
    return subprocess.run(args + [image, "sh", "-c", command], capture_output=True, text=True, env=ENV)


def checked(result):
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)
    return result


def clip_path(name, size):
    return f"{CLIP_DIR}/{name}-{size}.y4m"


def ensure_clips(clips, size, source):
    os.makedirs(CLIP_DIR, exist_ok=True)
    for name in clips:
        path = clip_path(name, size)
        if not os.path.exists(path):
            shutil.copy(os.path.join(source, f"{name}-{size}.y4m"), path)


def y4m_info(path):
    with open(path, "rb") as f:
        header = f.readline().decode().split()
    w = int(next(h for h in header if h.startswith("W"))[1:])
    h = int(next(h for h in header if h.startswith("H"))[1:])
    num, den = (int(v) for v in next(x for x in header if x.startswith("F"))[1:].split(":"))
    return w, h, num / den


def y4m_frames(path, count):
    w, h, _ = y4m_info(path)
    size = w * h * 3 // 2
    frames = []
    with open(path, "rb") as f:
        f.readline()
        for _ in range(count):
            if not f.readline():
                break
            frames.append(np.frombuffer(f.read(size), np.uint8))
    return w, h, frames


def decode(stream):
    """Decodes an Annex B stream with FFmpeg to <stream>.yuv."""
    checked(docker(REF_IMAGE, f"ffmpeg -v error -y -i {stream} -f rawvideo -pix_fmt yuv420p {stream}.yuv"))
    return stream + ".yuv"


def metrics(src, stream, frames):
    """Mean PSNR-Y of the frames, and SSIM-Y from FFmpeg."""
    decoded = decode(stream)
    w, h, ref = y4m_frames(src, frames)
    fps = y4m_info(src)[2]
    dec = np.fromfile(decoded, np.uint8).reshape(-1, w * h * 3 // 2)[:frames]
    psnrs = []
    for a, b in zip(ref, dec):
        mse = ((a[:w * h].astype(np.int32) - b[:w * h].astype(np.int32)) ** 2).mean()
        psnrs.append(10 * np.log10(255 * 255 / max(mse, 1e-9)))
    out = docker(REF_IMAGE, f"ffmpeg -hide_banner -f rawvideo -pix_fmt yuv420p -s {w}x{h} -r {fps} -i {decoded} -i {src} "
                            f"-frames:v {len(dec)} -lavfi '[0:v][1:v]ssim' -f null - 2>&1 | grep -o 'Y:[0-9.]*' | tail -1")
    ssim = float(out.stdout.strip().split(":")[1]) if out.stdout.strip() else float("nan")
    os.remove(decoded)
    return float(np.mean(psnrs)), ssim


def egami(src, frames, quality, effort, threads, tag, extra="--no-aq"):
    out = f"{OUT}/runs/egami-{tag}.h264"
    q = f"--quality={quality}" if quality is not None else ""
    cmd = f"dotnet {BENCH} --encode-h264 {src} --frames={frames} {q} --effort={effort} --threads={threads} {extra} --out={out}"
    data = json.loads(checked(docker(DOTNET_IMAGE, cmd)).stdout.strip().splitlines()[-1])
    return out, data["ms"], data["bytes"]


def timed(command):
    """A shell command run under a timer that prints its milliseconds first."""
    return ("python3 -c \"import subprocess,time,sys;s=time.perf_counter();"
            f"r=subprocess.run(sys.argv[1:]);print((time.perf_counter()-s)*1000);sys.exit(r.returncode)\" {command}")


def x264_command(src, frames, preset, rate, threads, out):
    return (f"x264 --quiet --preset {preset} --tune psnr {rate} --threads {threads} --frames {frames} -o {out} {src}")


def x264(src, frames, preset, rate, threads, tag):
    out = f"{OUT}/runs/x264-{tag}.h264"
    result = checked(docker(REF_IMAGE, timed(x264_command(src, frames, preset, rate, threads, out))))
    return out, float(result.stdout.strip().splitlines()[0]), os.path.getsize(out)


def bd_rate(r1, m1, r2, m2):
    """Bjontegaard rate difference of curve 2 against curve 1 in percent: cubic fit of log rate over the metric."""
    order = min(3, len(r1) - 1, len(r2) - 1)
    c1, c2 = np.polyfit(m1, np.log(r1), order), np.polyfit(m2, np.log(r2), order)
    lo, hi = max(min(m1), min(m2)), min(max(m1), max(m2))
    if hi <= lo:
        return float("nan")
    i1 = np.polyval(np.polyint(c1), hi) - np.polyval(np.polyint(c1), lo)
    i2 = np.polyval(np.polyint(c2), hi) - np.polyval(np.polyint(c2), lo)
    return float((np.exp((i2 - i1) / (hi - lo)) - 1) * 100)


def run_bd(args):
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    clips = args.clips.split(",")
    presets = args.presets.split(",")
    path = f"{OUT}/bd-{args.size}-e{args.effort}.json"
    results = {"size": args.size, "frames": args.frames, "effort": args.effort, "clips": {}}
    if os.path.exists(path) and args.reuse:
        with open(path) as f:
            results = json.load(f)

    def egami_job(clip, q):
        src = clip_path(clip, args.size)
        out, ms, size = egami(src, args.frames, q, args.effort, 1, f"{clip}-{args.size}-q{q}-e{args.effort}")
        fps = y4m_info(src)[2]
        psnr, ssim = metrics(src, out, args.frames)
        return clip, {"q": q, "kbps": size * 8 * fps / args.frames / 1000, "psnr": psnr, "ssim": ssim, "ms": ms}

    def x264_job(clip, preset, crf):
        src = clip_path(clip, args.size)
        out, ms, size = x264(src, args.frames, preset, f"--crf {crf}", 1, f"{clip}-{args.size}-{preset}-crf{crf}")
        fps = y4m_info(src)[2]
        psnr, ssim = metrics(src, out, args.frames)
        return clip, preset, {"crf": crf, "kbps": size * 8 * fps / args.frames / 1000, "psnr": psnr, "ssim": ssim, "ms": ms}

    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        missing = [(c, q) for c in clips for q in QUALITIES
                   if not any(p.get("q") == q for p in results["clips"].get(c, {}).get("egami", []))]
        for clip, value in pool.map(lambda job: egami_job(*job), missing):
            results["clips"].setdefault(clip, {"egami": [], "x264": {}})["egami"].append(value)
        jobs = [(c, p, crf) for c in clips for p in presets for crf in CRFS
                if not any(v.get("crf") == crf for v in results["clips"][c].setdefault("x264", {}).get(p, []))]
        for clip, preset, value in pool.map(lambda job: x264_job(*job), jobs):
            results["clips"][clip]["x264"].setdefault(preset, []).append(value)

    with open(path, "w") as f:
        json.dump(results, f, indent=1)
    report_bd(results)


def bd_against(data, preset):
    e = sorted(data["egami"], key=lambda p: p["kbps"])
    v = sorted(data["x264"][preset], key=lambda p: p["kbps"])
    psnr = bd_rate([p["kbps"] for p in v], [p["psnr"] for p in v], [p["kbps"] for p in e], [p["psnr"] for p in e])
    ssim = bd_rate([p["kbps"] for p in v], [p["ssim"] for p in v], [p["kbps"] for p in e], [p["ssim"] for p in e])
    return psnr, ssim


def report_bd(results):
    frames = results["frames"]
    print(f"BD-rate of Egami (effort {results['effort']}) against x264 presets, {results['size']}p, {frames} frames"
          " (PSNR-Y / SSIM; ms a frame at one thread, run --jobs at a time):")
    order = PRESETS.split(",") + ["slower", "veryslow", "placebo"]
    worst = []
    for clip, data in results["clips"].items():
        egami_ms = np.mean([p["ms"] for p in data["egami"]]) / frames
        print(f"  {clip}: Egami {egami_ms:.1f} ms a frame")
        for preset in sorted(data["x264"], key=lambda p: order.index(p) if p in order else 99):
            psnr, ssim = bd_against(data, preset)
            ms = np.mean([p["ms"] for p in data["x264"][preset]]) / frames
            print(f"    x264 {preset:10s}: {psnr:+6.1f}% / {ssim:+6.1f}%   {ms:6.1f} ms a frame")
            if preset == "medium":
                worst.append((psnr, clip))
    if worst:
        mean = np.mean([w[0] for w in worst])
        top = max(worst)
        verdict = "met" if top[0] <= GATE_BD else "not met"
        print(f"  BD-rate gate (PSNR-Y against x264 medium at most +{GATE_BD:.0f} % on every clip): {verdict}; mean {mean:+.1f} %,"
              f" worst {top[1]} {top[0]:+.1f} %")


def run_time(args):
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    clips = args.clips.split(",")
    presets = args.presets.split(",")
    threads = [int(t) for t in args.threads.split(",")]
    best = {}
    for _ in range(args.rounds):
        for clip in clips:
            src = clip_path(clip, args.size)
            fps = y4m_info(src)[2]
            for t in threads:
                _, ms, size = egami(src, args.frames, args.quality, args.effort, t, f"time-{clip}-e{args.effort}-t{t}", "")
                key = f"{clip}|egami|{t}"
                best[key] = min(best.get(key, float("inf")), ms)
                kbps = size * 8 * fps / args.frames / 1000
                for preset in presets:
                    _, ms, _ = x264(src, args.frames, preset, f"--bitrate {int(kbps)}", t, f"time-{clip}-{preset}-t{t}")
                    key = f"{clip}|{preset}|{t}"
                    best[key] = min(best.get(key, float("inf")), ms)
                print(f"  {clip} {t} thread(s): " + ", ".join(f"{k.split('|')[1]} {v / args.frames:.1f} ms"
                                                          for k, v in best.items() if k.startswith(clip + "|") and k.endswith(f"|{t}")), flush=True)
    with open(f"{OUT}/time-{args.size}-e{args.effort}.json", "w") as f:
        json.dump({"size": args.size, "frames": args.frames, "effort": args.effort, "quality": args.quality, "times": best}, f, indent=1)
    report_time(args.size, args.effort)


def report_time(size, effort):
    path = f"{OUT}/time-{size}-e{effort}.json"
    if not os.path.exists(path):
        return
    with open(path) as f:
        times = json.load(f)
    t, frames = times["times"], times["frames"]
    for threads in sorted({int(k.split("|")[2]) for k in t}):
        rows = {k.split("|")[1]: [] for k in t if int(k.split("|")[2]) == threads}
        for k, v in t.items():
            if int(k.split("|")[2]) == threads:
                rows[k.split("|")[1]].append(v)
        egami_ms = np.mean(rows.pop("egami"))
        print(f"{threads} thread(s), {times['size']}p, {frames} frames: Egami effort {times['effort']} {egami_ms / frames:.1f} ms a frame")
        for preset, values in rows.items():
            ratio = egami_ms / np.mean(values)
            gate = f" (gate at most {GATE_TIME:.0f}: {'met' if ratio <= GATE_TIME else 'not met'})" if preset == "medium" else ""
            print(f"  x264 {preset:10s}: {np.mean(values) / frames:7.1f} ms a frame, Egami / x264 {ratio:6.2f}{gate}")


def ensure_perf_image():
    if not subprocess.run(["docker", "images", "-q", PERF_IMAGE], capture_output=True, text=True, env=ENV).stdout.strip():
        subprocess.run(["docker", "build", "-t", PERF_IMAGE, "-"], input=PERF_DOCKERFILE, text=True, env=ENV, check=True)


def counters(image, command, env=""):
    """User-mode cycles and instructions of a command, from perf stat."""
    script = f"{env}perf stat -x, -e cycles:u,instructions:u -o /tmp/s {command} > /dev/null 2>&1; cat /tmp/s"
    out = checked(docker(image, script, privileged=True)).stdout
    values = {}
    for line in out.splitlines():
        parts = line.split(",")
        if len(parts) > 2 and parts[0].replace(".", "").isdigit():
            values[parts[2].split(":")[0]] = float(parts[0])
    return values


def run_cycles(args):
    """Cycles a frame at one thread: Egami at --runs 1 and 3, x264 at N and 2N frames, the difference over the extra frames."""
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    ensure_perf_image()
    rows = {}
    for clip in args.clips.split(","):
        src = clip_path(clip, args.size)
        fps = y4m_info(src)[2]
        base = f"dotnet {BENCH} --encode-h264 {src} --frames={args.frames} --quality={args.quality} --effort={args.effort} --threads=1"
        a, b = (counters(PERF_IMAGE, f"{base} --runs={r}", "export DOTNET_TieredPGO=0; " if args.no_pgo else "") for r in (1, 3))
        row = {"egami": {k: (b[k] - a[k]) / (2 * args.frames) for k in a}}
        _, _, size = egami(src, args.frames, args.quality, args.effort, 1, f"cycles-{clip}", "")
        kbps = size * 8 * fps / args.frames / 1000
        for preset in args.presets.split(","):
            out = f"{OUT}/runs/x264-cycles.h264"
            a, b = (counters(REF_IMAGE, x264_command(src, n, preset, f"--bitrate {int(kbps)}", 1, out)) for n in (args.frames, 2 * args.frames))
            row[preset] = {k: (b[k] - a[k]) / args.frames for k in a}
        rows[clip] = row
        print(f"  {clip}: " + ", ".join(f"{k} {v['cycles'] / 1e6:.1f}M cycles {v['instructions'] / 1e6:.1f}M instructions" for k, v in row.items()), flush=True)
    with open(f"{OUT}/cycles-{args.size}-e{args.effort}.json", "w") as f:
        json.dump({"size": args.size, "frames": args.frames, "effort": args.effort, "pgo": not args.no_pgo, "rows": rows}, f, indent=1)
    report_cycles(args.size, args.effort)


def report_cycles(size, effort):
    path = f"{OUT}/cycles-{size}-e{effort}.json"
    if not os.path.exists(path):
        return
    with open(path) as f:
        data = json.load(f)
    pgo = "on" if data.get("pgo", False) else "off"
    print(f"Cycles a frame at one thread, {data['size']}p, Egami effort {data['effort']}, tiered PGO {pgo} (user mode, perf):")
    for clip, row in data["rows"].items():
        egami_c = row["egami"]["cycles"]
        others = ", ".join(f"x264 {k} {v['cycles'] / 1e6:.1f}M (Egami / x264 {egami_c / v['cycles']:.1f})" for k, v in row.items() if k != "egami")
        print(f"  {clip}: Egami {egami_c / 1e6:.1f}M; {others}")


def access_units(path):
    """The sizes of an Annex B stream's access units: each starts at a parameter set, SEI or AUD before a slice, or at a slice whose
    first_mb_in_slice is 0 (its first payload bit is 1)."""
    with open(path, "rb") as f:
        data = f.read()
    starts, at = [], data.find(b"\x00\x00\x01")
    while at >= 0:
        starts.append(at + 3)
        at = data.find(b"\x00\x00\x01", at + 3)
    sizes, current, opened = [], 0, False
    for i, start in enumerate(starts):
        end = starts[i + 1] - 3 if i + 1 < len(starts) else len(data)
        nal = data[start] & 31
        new = nal in (6, 7, 8, 9) and opened or nal in (1, 5) and data[start + 1] & 0x80 and opened
        if new:
            sizes.append(current)
            current, opened = 0, False
        current += end - start + 3
        opened = opened or nal in (1, 5)
    sizes.append(current)
    return sizes


def run_rc(args):
    """One-pass and two-pass bitrate against the target, and constant bitrate's buffer, from the access unit sizes."""
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    bits = args.kbps * 1000
    modes = (("one-pass", f"--bitrate={bits}"), ("two-pass", f"--bitrate={bits} --two-pass"),
             ("cbr", f"--bitrate={bits} --cbr --realtime"))

    def job(clip, mode, extra):
        src = clip_path(clip, args.size)
        fps = y4m_info(src)[2]
        out, _, size = egami(src, args.frames, None, args.effort, 1, f"rc-{clip}-{args.kbps}-{mode}", extra)
        kbps = size * 8 * fps / args.frames / 1000
        row = {"clip": clip, "mode": mode, "kbps": kbps, "off": (kbps / args.kbps - 1) * 100}
        if mode == "cbr":
            # The encoder's leaky bucket: one second of the bitrate, starting three quarters full, drained a frame at a time.
            level, lowest = bits * 0.75, float(bits)
            for unit in access_units(out):
                level = min(bits, level + bits / fps) - unit * 8
                lowest = min(lowest, level)
            row["lowest"] = lowest / bits * 100
        return row

    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        rows = list(pool.map(lambda j: job(*j), [(c, m, e) for c in args.clips.split(",") for m, e in modes]))
    with open(f"{OUT}/rc-{args.size}-{args.kbps}.json", "w") as f:
        json.dump({"kbps": args.kbps, "frames": args.frames, "effort": args.effort, "rows": rows}, f, indent=1)
    report_rc(args.size, args.kbps)


def report_rc(size, kbps):
    path = f"{OUT}/rc-{size}-{kbps}.json"
    if not os.path.exists(path):
        return
    with open(path) as f:
        data = json.load(f)
    print(f"Rate control, {size}p, {data['frames']} frames, target {data['kbps']} kbit/s, effort {data['effort']}:")
    ok = True
    for row in data["rows"]:
        ok &= abs(row["off"]) <= GATE_RATE and row.get("lowest", 0) >= 0
        print(f"  {row['clip']:16s} {row['mode']:9s} {row['kbps']:8.1f} kbit/s, {row['off']:+5.1f} %"
              + (f", buffer never below {row['lowest']:.0f} %" if "lowest" in row else ""))
    print(f"  Rate gate (within {GATE_RATE:.0f} %, buffer never empty): {'met' if ok else 'not met'}")


def report(args):
    bd = f"{OUT}/bd-{args.size}-e{args.effort}.json"
    if os.path.exists(bd):
        with open(bd) as f:
            report_bd(json.load(f))
    report_time(args.size, args.effort)
    report_cycles(args.size, args.effort)
    for name in sorted(f for f in os.listdir(OUT) if f.startswith(f"rc-{args.size}-") and f.endswith(".json")):
        report_rc(args.size, int(name[len(f"rc-{args.size}-"):-5]))


def main():
    global OUT, BENCH
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["bd", "time", "cycles", "rc", "report"])
    parser.add_argument("--size", default="720")
    parser.add_argument("--frames", type=int, default=None)
    parser.add_argument("--effort", type=int, default=4)
    parser.add_argument("--quality", type=int, default=50)
    parser.add_argument("--presets", default=None)
    parser.add_argument("--kbps", type=int, default=2500)
    parser.add_argument("--threads", default=f"1,{os.cpu_count()}")
    parser.add_argument("--clips", default=",".join(CLIPS))
    parser.add_argument("--rounds", type=int, default=2)
    parser.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) // 3))
    parser.add_argument("--reuse", action="store_true", help="keep the runs already in the bd file and add only what is missing")
    parser.add_argument("--no-pgo", action="store_true", help="cycles: Egami with tiered PGO off (the default runtime has it on)")
    parser.add_argument("--clips-from", default=".cache/video/derf")
    parser.add_argument("--out", default=OUT, help="where runs and results go (clips stay in .cache/h264enc/clips)")
    parser.add_argument("--bench", default=BENCH, help="the benchmark build to time Egami with, inside the repo")
    args = parser.parse_args()
    OUT = args.out
    BENCH = args.bench
    if args.mode != "report":
        ensure_clips(args.clips.split(","), args.size, args.clips_from)
    if args.mode == "bd":
        args.frames = args.frames or 60
        args.presets = args.presets or PRESETS
        run_bd(args)
    elif args.mode == "time":
        args.frames = args.frames or 30
        args.presets = args.presets or "medium"
        run_time(args)
    elif args.mode == "cycles":
        args.frames = args.frames or 20
        args.presets = args.presets or "medium"
        run_cycles(args)
    elif args.mode == "rc":
        args.frames = args.frames or 120
        run_rc(args)
    else:
        report(args)


if __name__ == "__main__":
    main()
