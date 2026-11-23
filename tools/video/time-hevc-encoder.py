"""The HEVC encoder's measurements (HEVC encoder Build Plan task 4; the gates of task 13 build on them): Egami's BD-rate on PSNR-Y
and SSIM against x265 --preset medium and against Egami's own H.264 encoder at effort 4; Egami's encode time at one thread and
on all cores; CPU cycles and instructions a frame against x265 medium matched by quality; the counting build's work counters;
and the rate control check (one and two-pass bitrate within 5 % of the target, constant bitrate never emptying the buffer).

Everything runs in Docker on the same machine: x265 3.6 (Alpine's binary package, run only as a black-box encoder), FFmpeg and
the HM 18.0 decoder in egami-hevcencref:8.1 from tools/video/hevcencref/Dockerfile, and Egami through the benchmark project's
Release build (--encode-hevc, --encode-h264) on .NET 10 (egami-dotnet-perf:2.4.5; cycles in egami-perf-counters:2, the image
tools/video/count-instructions.py builds). For the PSNR BD-rate both encoders are tuned for PSNR (x265 --tune psnr, Egami
without adaptive quantization); SSIM is measured with Egami's adaptive quantization on (its default) against the same x265
runs. x265 codes at CRF 22, 27, 32 and 37 and Egami at Quality 52, 43, 34 and 25, whose QPs (51 - 0.56 Quality) are the same
numbers. FFmpeg decodes every stream and measures SSIM-Y; PSNR-Y is the mean of the frames' own. The BD-rate is a cubic fit of
log rate over the metric.

x265 runs on one thread with --pools 1 --frame-threads 1 --no-wpp (its lookahead runs on that thread too). --x265 names the
x265 configurations: a preset, or a preset and more options after a colon ("medium:--bframes 0" measures x265 without B
pictures).

Cycles and instructions are user-mode counts from Linux perf: Egami's at --runs 1 and 3 and x265's at N and 2N frames, the
difference over the extra frames, so start-up and the JIT drop out; each count is the least of --repeats runs. x265 is matched to
Egami by quality: a secant search on x265's CRF finds the one that gives Egami's mean PSNR-Y on the same frames to within
0.02 dB. Egami runs on the default runtime, tiered PGO on.

instr runs the counting build (-p:EgamiHevcStats=true, built into .cache/hevcenc/stats-build) and prints its counters a frame by
picture type: they depend only on the input and the options, so they compare between runs and machines where times do not.

The clips are Xiph's derf clips at 720p, 50 fps: the four busy ones of the H.264 gates (crowd_run, ducks_take_off, park_joy,
old_town_cross) and with --wide the four calm ones the H.264 compression rounds added (johnny, kristen_sara, pedestrian_area,
rush_hour). Their first --clip-frames frames are copied to .cache/hevcenc/clips from --clips-from (.cache/video/derf, which
egami-compare's video/native/vp8-encode-gate.py fetches).

Usage, from the repo root with Docker running and the benchmarks built in Release
(dotnet build -c Release benchmarks/Egami.Video.Benchmarks):
  python tools/video/time-hevc-encoder.py bd [--frames 30] [--effort 4] [--x265 medium,...] [--clips a,b | --wide] [--no-h264]
  python tools/video/time-hevc-encoder.py time [--frames 20] [--effort 4] [--threads 1,24]
  python tools/video/time-hevc-encoder.py cycles [--frames 10] [--effort 4] [--quality 50,34] [--repeats 3]
  python tools/video/time-hevc-encoder.py instr [--frames 10] [--effort 4] [--quality 50]
  python tools/video/time-hevc-encoder.py rc [--frames 120] [--effort 2] [--kbps 2000]
  python tools/video/time-hevc-encoder.py report     (the tables of the last bd, time, cycles, instr and rc runs)
"""
import argparse
import concurrent.futures
import json
import os
import subprocess

import numpy as np

OUT = ".cache/hevcenc"
CLIP_DIR = ".cache/hevcenc/clips"
CLIPS = ["crowd_run", "ducks_take_off", "park_joy", "old_town_cross"]
WIDE_CLIPS = CLIPS + ["johnny", "kristen_sara", "pedestrian_area", "rush_hour"]
QUALITIES = [52, 43, 34, 25]
CRFS = [22, 27, 32, 37]
REF_IMAGE = "egami-hevcencref:8.1"
DOTNET_IMAGE = "egami-dotnet-perf:2.4.5"
PERF_IMAGE = "egami-perf-counters:2"
PERF_DOCKERFILE = "FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine\nRUN apk add --no-cache perf ffmpeg libjpeg-turbo-utils\n"
BENCH = "benchmarks/Egami.Video.Benchmarks/bin/Release/net10.0/Egami.Video.Benchmarks.dll"
STATS_BUILD = ".cache/hevcenc/stats-build"
STATS_BENCH = f"{STATS_BUILD}/bin/Egami.Video.Benchmarks/release/Egami.Video.Benchmarks.dll"
X265_ONE_THREAD = "--pools 1 --frame-threads 1 --no-wpp"
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")
ROOT = os.path.abspath(".")
GATE_RATE = 5.0
REPEATS = 1


def docker(image, command, privileged=False):
    args = ["docker", "run", "--rm"] + (["--privileged"] if privileged else []) + ["-v", f"{ROOT}:/work", "-w", "/work"]
    return subprocess.run(args + [image, "sh", "-c", command], capture_output=True, text=True, env=ENV)


def checked(result):
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)
    return result


def clip_path(name):
    return f"{CLIP_DIR}/{name}-720.y4m"


def ensure_clips(clips, source, frames):
    """Copies the first frames of each clip (whole frames, after the y4m header) into the clip folder."""
    os.makedirs(CLIP_DIR, exist_ok=True)
    for name in clips:
        path = clip_path(name)
        if os.path.exists(path) and y4m_count(path) >= frames:
            continue
        with open(os.path.join(source, f"{name}-720.y4m"), "rb") as f, open(path, "wb") as out:
            header = f.readline()
            out.write(header)
            w, h = y4m_size(header)
            for _ in range(frames):
                marker = f.readline()
                if not marker:
                    break
                out.write(marker)
                out.write(f.read(w * h * 3 // 2))


def y4m_size(header):
    fields = header.decode().split()
    return int(next(x for x in fields if x.startswith("W"))[1:]), int(next(x for x in fields if x.startswith("H"))[1:])


def y4m_info(path):
    with open(path, "rb") as f:
        header = f.readline()
    w, h = y4m_size(header)
    num, den = (int(v) for v in next(x for x in header.decode().split() if x.startswith("F"))[1:].split(":"))
    return w, h, num / den


def y4m_count(path):
    w, h, _ = y4m_info(path)
    with open(path, "rb") as f:
        header = len(f.readline())
    return (os.path.getsize(path) - header) // (w * h * 3 // 2 + 6)


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
    """Decodes a raw stream (Annex B) with FFmpeg to <stream>.yuv, 8-bit 4:2:0."""
    checked(docker(REF_IMAGE, f"ffmpeg -v error -y -i {stream} -f rawvideo -pix_fmt yuv420p {stream}.yuv"))
    return stream + ".yuv"


def psnr_list(ref, dec, w, h):
    out = []
    for a, b in zip(ref, dec):
        mse = ((a[:w * h].astype(np.int32) - b[:w * h].astype(np.int32)) ** 2).mean()
        out.append(10 * np.log10(255 * 255 / max(mse, 1e-9)))
    return out


def metrics(src, stream, frames, ssim=True):
    """Mean PSNR-Y of the frames, and SSIM-Y from FFmpeg."""
    decoded = decode(stream)
    w, h, ref = y4m_frames(src, frames)
    fps = y4m_info(src)[2]
    dec = np.fromfile(decoded, np.uint8).reshape(-1, w * h * 3 // 2)[:frames]
    psnr = float(np.mean(psnr_list(ref, dec, w, h)))
    value = float("nan")
    if ssim:
        out = docker(REF_IMAGE, f"ffmpeg -hide_banner -f rawvideo -pix_fmt yuv420p -s {w}x{h} -r {fps} -i {decoded} -i {src} "
                                f"-frames:v {len(dec)} -lavfi '[0:v][1:v]ssim' -f null - 2>&1 | grep -o 'Y:[0-9.]*' | tail -1")
        value = float(out.stdout.strip().split(":")[1]) if out.stdout.strip() else float("nan")
    os.remove(decoded)
    return psnr, value


def egami(codec, src, frames, quality, effort, threads, tag, extra="--no-aq", bench=None):
    """An Egami encode through the benchmark: its stream, milliseconds and bytes (and the JSON's other fields)."""
    out = f"{OUT}/runs/egami-{codec}-{tag}.{codec}"
    q = f"--quality={quality}" if quality is not None else ""
    cmd = (f"dotnet {bench or BENCH} --encode-{codec} {src} --frames={frames} {q} --effort={effort} --threads={threads} {extra} "
           f"--out={out}")
    data = json.loads(checked(docker(DOTNET_IMAGE, cmd)).stdout.strip().splitlines()[-1])
    return out, data


def x265_command(src, frames, config, rate, out, threads=1):
    preset, _, more = config.partition(":")
    threading = X265_ONE_THREAD if threads == 1 else ""
    return f"x265 --log-level error --preset {preset} --tune psnr {more} {rate} {threading} --frames {frames} -o {out} --input {src}"


def timed(command):
    """A shell command run under a timer that prints its milliseconds first."""
    return ("python3 -c \"import subprocess,time,sys;s=time.perf_counter();"
            f"r=subprocess.run(sys.argv[1:]);print((time.perf_counter()-s)*1000);sys.exit(r.returncode)\" {command}")


def x265(src, frames, config, rate, tag, threads=1):
    out = f"{OUT}/runs/x265-{tag}.h265"
    result = checked(docker(REF_IMAGE, timed(x265_command(src, frames, config, rate, out, threads))))
    return out, float(result.stdout.strip().splitlines()[0]), os.path.getsize(out)


def x265_psnr(src, frames, config, crf):
    out = f"{OUT}/runs/x265-crf-search-{os.path.basename(src)}.h265"
    checked(docker(REF_IMAGE, x265_command(src, frames, config, f"--crf {crf:.2f}", out, threads=0)))
    return metrics(src, out, frames, ssim=False)[0]


def matched_crf(src, frames, config, psnr):
    """The CRF at which x265 reaches a mean PSNR-Y on the frames: a secant search on CRF, which x265 takes in fractions."""
    points = [(c, x265_psnr(src, frames, config, c)) for c in (24.0, 32.0)]
    for _ in range(8):
        (c0, p0), (c1, p1) = sorted(points, key=lambda p: abs(p[1] - psnr))[:2]
        if abs(p0 - psnr) <= 0.02 or p1 == p0:
            break
        c = min(51.0, max(0.0, c0 + (psnr - p0) * (c1 - c0) / (p1 - p0)))
        if any(abs(c - q) < 0.005 for q, _ in points):
            break
        points.append((c, x265_psnr(src, frames, config, c)))
    return min(points, key=lambda p: abs(p[1] - psnr))


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


def bd_of(base, test, metric):
    b = sorted(base, key=lambda p: p["kbps"])
    t = sorted(test, key=lambda p: p["kbps"])
    return bd_rate([p["kbps"] for p in b], [p[metric] for p in b], [p["kbps"] for p in t], [p[metric] for p in t])


def run_bd(args):
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    clips = args.clips.split(",")
    configs = args.x265.split(",")
    path = f"{OUT}/bd-e{args.effort}.json"
    results = {"frames": args.frames, "effort": args.effort, "clips": {}}
    if os.path.exists(path) and args.reuse:
        with open(path) as f:
            results = json.load(f)

    def egami_job(codec, clip, q, aq):
        src = clip_path(clip)
        extra = ("" if aq else "--no-aq") + (f" --inter={args.inter}" if args.inter and codec == "hevc" else "")
        extra += f" --intra={args.intra}" if args.intra and codec == "hevc" else ""
        out, data = egami(codec, src, args.frames, q, args.effort if codec == "hevc" else 4, 1,
                          f"{clip}-q{q}-e{args.effort}{'-aq' if aq else ''}", extra)
        psnr, ssim = metrics(src, out, args.frames)
        fps = y4m_info(src)[2]
        key = ("egami" if codec == "hevc" else "h264") + ("_aq" if aq else "")
        return clip, key, {"q": q, "kbps": data["bytes"] * 8 * fps / args.frames / 1000, "psnr": psnr, "ssim": ssim, "ms": data["ms"]}

    def x265_job(clip, config, crf):
        src = clip_path(clip)
        out, ms, size = x265(src, args.frames, config, f"--crf {crf}", f"{clip}-{config.replace(':', '').replace(' ', '')}-crf{crf}")
        psnr, ssim = metrics(src, out, args.frames)
        fps = y4m_info(src)[2]
        return clip, config, {"crf": crf, "kbps": size * 8 * fps / args.frames / 1000, "psnr": psnr, "ssim": ssim, "ms": ms}

    codecs = ["hevc"] + ([] if args.no_h264 else ["h264"])
    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        jobs = [(codec, c, q, aq) for c in clips for codec in codecs for aq in (False, True) for q in QUALITIES
                if not any(p.get("q") == q for p in results["clips"].get(c, {}).get(
                    ("egami" if codec == "hevc" else "h264") + ("_aq" if aq else ""), []))]
        for clip, key, value in pool.map(lambda job: egami_job(*job), jobs):
            results["clips"].setdefault(clip, {"x265": {}}).setdefault(key, []).append(value)
        jobs = [(c, x, crf) for c in clips for x in configs for crf in CRFS
                if not any(v.get("crf") == crf for v in results["clips"].setdefault(c, {"x265": {}})["x265"].get(x, []))]
        for clip, config, value in pool.map(lambda job: x265_job(*job), jobs):
            results["clips"][clip]["x265"].setdefault(config, []).append(value)

    with open(path, "w") as f:
        json.dump(results, f, indent=1)
    report_bd(results)


def report_bd(results):
    frames = results["frames"]
    print(f"BD-rate of Egami HEVC (effort {results['effort']}), 720p, {frames} frames; PSNR-Y with adaptive quantization off, "
          "SSIM-Y with it on (ms a frame at one thread):")
    rows = []
    for clip, data in results["clips"].items():
        if "egami" not in data:
            continue
        egami_ms = np.mean([p["ms"] for p in data["egami"]]) / frames
        cells = {"clip": clip, "ms": egami_ms}
        for config, runs in data["x265"].items():
            cells[f"x265 {config} PSNR"] = bd_of(runs, data["egami"], "psnr")
            if "egami_aq" in data:
                cells[f"x265 {config} SSIM"] = bd_of(runs, data["egami_aq"], "ssim")
            cells[f"x265 {config} ms"] = np.mean([p["ms"] for p in runs]) / frames
        if "h264" in data:
            cells["H.264 e4 PSNR"] = bd_of(data["h264"], data["egami"], "psnr")
            if "h264_aq" in data and "egami_aq" in data:
                cells["H.264 e4 SSIM"] = bd_of(data["h264_aq"], data["egami_aq"], "ssim")
        rows.append(cells)
    if not rows:
        return
    columns = [k for k in rows[0] if k not in ("clip", "ms") and not k.endswith(" ms")]
    print("| Clip | Egami ms/frame | " + " | ".join(columns) + " |")
    print("|---|---:|" + "---:|" * len(columns))
    for row in rows:
        print(f"| {row['clip']} | {row['ms']:.0f} | " + " | ".join(f"{row.get(c, float('nan')):+.1f} %" for c in columns) + " |")
    print(f"| Mean of {len(rows)} | | " + " | ".join(f"{np.nanmean([r.get(c, np.nan) for r in rows]):+.1f} %" for c in columns) + " |")
    for config in sorted({k[5:-3] for r in rows for k in r if k.startswith("x265 ") and k.endswith(" ms")}):
        print(f"  x265 {config}: {np.mean([r[f'x265 {config} ms'] for r in rows]):.0f} ms a frame (bd runs, --jobs at a time)")


def run_time(args):
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    threads = [int(t) for t in args.threads.split(",")]
    config = args.x265.split(",")[0]
    best, crfs = {}, {}
    for _ in range(args.rounds):
        for clip in args.clips.split(","):
            src = clip_path(clip)
            for t in threads:
                out, data = egami("hevc", src, args.frames, args.quality, args.effort, t, f"time-{clip}-t{t}")
                key = f"{clip}|egami|{t}"
                best[key] = min(best.get(key, float("inf")), data["ms"])
                if clip not in crfs:
                    crfs[clip] = matched_crf(src, args.frames, config, metrics(src, out, args.frames, ssim=False)[0])[0]
                _, ms, _ = x265(src, args.frames, config, f"--crf {crfs[clip]:.2f}", f"time-{clip}-t{t}", t)
                key = f"{clip}|x265|{t}"
                best[key] = min(best.get(key, float("inf")), ms)
                print(f"  {clip} {t} thread(s): Egami {best[f'{clip}|egami|{t}'] / args.frames:.1f} ms, x265 {config} "
                      f"{best[f'{clip}|x265|{t}'] / args.frames:.1f} ms a frame", flush=True)
    with open(f"{OUT}/time-e{args.effort}.json", "w") as f:
        json.dump({"frames": args.frames, "effort": args.effort, "quality": args.quality, "x265": config, "times": best}, f, indent=1)
    report_time(args.effort)


def report_time(effort):
    path = f"{OUT}/time-e{effort}.json"
    if not os.path.exists(path):
        return
    with open(path) as f:
        data = json.load(f)
    t, frames = data["times"], data["frames"]
    print(f"Wall time, 720p, {frames} frames, Egami effort {data['effort']} quality {data['quality']}, x265 {data['x265']} at Egami's PSNR-Y:")
    for threads in sorted({int(k.split("|")[2]) for k in t}):
        for clip in sorted({k.split("|")[0] for k in t}):
            e, x = t.get(f"{clip}|egami|{threads}"), t.get(f"{clip}|x265|{threads}")
            if e and x:
                print(f"  {threads:2d} thread(s) {clip:16s}: Egami {e / frames:7.1f} ms, x265 {x / frames:7.1f} ms a frame, ratio {e / x:5.2f}")


def ensure_perf_image():
    if not subprocess.run(["docker", "images", "-q", PERF_IMAGE], capture_output=True, text=True, env=ENV).stdout.strip():
        subprocess.run(["docker", "build", "-t", PERF_IMAGE, "-"], input=PERF_DOCKERFILE, text=True, env=ENV, check=True)


def counters(image, command, env=""):
    """User-mode cycles and instructions of a command, from perf stat: the least of REPEATS runs of each."""
    script = f"{env}perf stat -x, -e cycles:u,instructions:u -o /tmp/s {command} > /dev/null 2>&1; cat /tmp/s"
    values = {}
    for _ in range(REPEATS):
        out = checked(docker(image, script, privileged=True)).stdout
        for line in out.splitlines():
            parts = line.split(",")
            if len(parts) > 2 and parts[0].replace(".", "").isdigit():
                key = parts[2].split(":")[0]
                values[key] = min(values.get(key, float("inf")), float(parts[0]))
    return values


def run_cycles(args):
    """Cycles and instructions a frame at one thread: Egami at --runs 1 and 3, x265 at N and 2N frames, the difference over the
    extra frames; x265 at the CRF that gives Egami's PSNR-Y on the N frames; Egami's H.264 at effort 4 and the same quality beside."""
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    ensure_perf_image()
    global REPEATS
    REPEATS = args.repeats
    configs = args.x265.split(",")
    for quality in (int(q) for q in str(args.quality).split(",")):
        rows = {}
        for clip in args.clips.split(","):
            src = clip_path(clip)
            row = {}
            for codec, effort in (("hevc", args.effort), ("h264", 4)):
                if codec == "h264" and args.no_h264:
                    continue
                base = (f"dotnet {BENCH} --encode-{codec} {src} --frames={args.frames} --quality={quality} --effort={effort} --threads=1 --no-aq")
                a, b = (counters(PERF_IMAGE, f"{base} --runs={r}") for r in (1, 3))
                name = "egami" if codec == "hevc" else "h264"
                row[name] = {k: (b[k] - a[k]) / (2 * args.frames) for k in a}
                out, _ = egami(codec, src, args.frames, quality, effort, 1, f"cycles-{clip}")
                row[name]["psnr"] = metrics(src, out, args.frames, ssim=False)[0]
            for config in configs:
                crf, psnr = matched_crf(src, args.frames, config, row["egami"]["psnr"])
                out = f"{OUT}/runs/x265-cycles.h265"
                a, b = (counters(REF_IMAGE, x265_command(src, n, config, f"--crf {crf:.2f}", out)) for n in (args.frames, 2 * args.frames))
                row[f"x265 {config}"] = {k: (b[k] - a[k]) / args.frames for k in a}
                row[f"x265 {config}"].update(crf=crf, psnr=psnr)
            rows[clip] = row
            print(f"  {clip} q{quality}: " + ", ".join(f"{k} {v['cycles'] / 1e6:.1f}M cycles {v['instructions'] / 1e6:.1f}M instr "
                                                         f"{v['psnr']:.2f} dB" for k, v in row.items()), flush=True)
        with open(f"{OUT}/cycles-e{args.effort}-q{quality}.json", "w") as f:
            json.dump({"frames": args.frames, "effort": args.effort, "quality": quality, "rows": rows}, f, indent=1)
        report_cycles(args.effort, quality)


def report_cycles(effort, quality):
    path = f"{OUT}/cycles-e{effort}-q{quality}.json"
    if not os.path.exists(path):
        return
    with open(path) as f:
        data = json.load(f)
    print(f"Cycles and instructions a frame at one thread, 720p, Egami HEVC effort {data['effort']} quality {quality}, adaptive "
          "quantization off, tiered PGO on (user mode, perf; x265 --tune psnr at Egami's PSNR-Y; H.264 effort 4 at the same quality):")
    others = [k for k in next(iter(data["rows"].values())) if k != "egami"]
    print("| Clip | Egami M cycles | Egami M instr | " + " | ".join(f"vs {k} cycles | vs {k} instr" for k in others) + " |")
    print("|---|---:|---:|" + "---:|---:|" * len(others))
    ratios = {k: [] for k in others}
    for clip, row in data["rows"].items():
        e = row["egami"]
        cells = []
        for k in others:
            ratios[k].append((e["cycles"] / row[k]["cycles"], e["instructions"] / row[k]["instructions"]))
            cells.append(f"{ratios[k][-1][0]:.2f} | {ratios[k][-1][1]:.2f}")
        print(f"| {clip} | {e['cycles'] / 1e6:.0f} | {e['instructions'] / 1e6:.0f} | " + " | ".join(cells) + " |")
    print("| Mean | | | " + " | ".join(f"{np.mean([r[0] for r in v]):.2f} | {np.mean([r[1] for r in v]):.2f}" for v in ratios.values()) + " |")


def ensure_stats_build():
    subprocess.run(["dotnet", "build", "-c", "Release", "-p:EgamiHevcStats=true", "--artifacts-path", STATS_BUILD, "-v", "q", "-nologo",
                    "benchmarks/Egami.Video.Benchmarks"], check=True)


def run_instr(args):
    """The counting build's work counters a frame, by picture type, and the picture bits and coding unit shares."""
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    ensure_stats_build()
    rows = {}
    for clip in args.clips.split(","):
        _, data = egami("hevc", clip_path(clip), args.frames, args.quality, args.effort, 1, f"instr-{clip}", "--no-aq", bench=STATS_BENCH)
        if "stats" not in data:
            raise RuntimeError(f"{STATS_BENCH} does not count: rebuild it with -p:EgamiHevcStats=true")
        rows[clip] = {"stats": data["stats"], "pictures": data["pictures"], "bytes": data["bytes"]}
    with open(f"{OUT}/instr-e{args.effort}-q{args.quality}.json", "w") as f:
        json.dump({"frames": args.frames, "effort": args.effort, "quality": args.quality, "rows": rows}, f, indent=1)
    report_instr(args.effort, args.quality)


def report_instr(effort, quality):
    path = f"{OUT}/instr-e{effort}-q{quality}.json"
    if not os.path.exists(path):
        return
    with open(path) as f:
        data = json.load(f)
    print(f"Work counters of the counting build, 720p, {data['frames']} frames, effort {data['effort']} quality {quality}, "
          "adaptive quantization off; per picture of each type:")
    for clip, row in data["rows"].items():
        pictures = row["pictures"]
        print(f"  {clip}: {row['bytes'] * 8 / data['frames'] / 1000:.1f} kbit a frame")
        for kind, counts in pictures.items():
            if counts["Pictures"]:
                n = counts["Pictures"]
                shares = ", ".join(f"{k} {counts[k] / n:.0f}" for k in ("Skip", "Merge", "Inter", "Intra"))
                print(f"    {kind:4s} {n:3d} pictures, {counts['Bits'] / n / 1000:7.1f} kbit, 8x8 blocks: {shares}")
        picture_counts = {"P": pictures["P"]["Pictures"], "B": pictures["Bref"]["Pictures"] + pictures["B"]["Pictures"], "I": pictures["I"]["Pictures"]}
        for kind, stats in row["stats"].items():
            n = picture_counts[kind]
            if n:
                print(f"    {kind} a picture: " + ", ".join(f"{k} {v / n:.0f}" for k, v in stats.items() if v))


def access_units(path):
    """The sizes of an Annex B HEVC stream's access units: each starts at a VPS, SPS, PPS, AUD or prefix SEI before a slice, or at
    a slice whose first_slice_segment_in_pic_flag is set."""
    with open(path, "rb") as f:
        data = f.read()
    starts, at = [], data.find(b"\x00\x00\x01")
    while at >= 0:
        starts.append(at + 3)
        at = data.find(b"\x00\x00\x01", at + 3)
    sizes, current, opened = [], 0, False
    for i, start in enumerate(starts):
        end = starts[i + 1] - 3 if i + 1 < len(starts) else len(data)
        nal = (data[start] >> 1) & 63
        new = opened and (nal in (32, 33, 34, 35, 39) or (nal < 32 and data[start + 2] & 0x80))
        if new:
            sizes.append(current)
            current, opened = 0, False
        current += end - start + 3
        opened = opened or nal < 32
    sizes.append(current)
    return sizes


def run_rc(args):
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    bits = args.kbps * 1000
    modes = (("one-pass", f"--bitrate={bits}"), ("two-pass", f"--bitrate={bits} --two-pass"), ("cbr", f"--bitrate={bits} --cbr --realtime"))

    def job(clip, mode, extra):
        src = clip_path(clip)
        fps = y4m_info(src)[2]
        out, data = egami("hevc", src, args.frames, None, args.effort, 1, f"rc-{clip}-{args.kbps}-{mode}", extra)
        kbps = data["bytes"] * 8 * fps / args.frames / 1000
        row = {"clip": clip, "mode": mode, "kbps": kbps, "off": (kbps / args.kbps - 1) * 100}
        if mode == "cbr":
            level, lowest = bits * 0.75, float(bits)
            for unit in access_units(out):
                level = min(bits, level + bits / fps) - unit * 8
                lowest = min(lowest, level)
            row["lowest"] = lowest / bits * 100
        return row

    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        rows = list(pool.map(lambda j: job(*j), [(c, m, e) for c in args.clips.split(",") for m, e in modes]))
    with open(f"{OUT}/rc-{args.kbps}.json", "w") as f:
        json.dump({"kbps": args.kbps, "frames": args.frames, "effort": args.effort, "rows": rows}, f, indent=1)
    report_rc(args.kbps)


def report_rc(kbps):
    path = f"{OUT}/rc-{kbps}.json"
    if not os.path.exists(path):
        return
    with open(path) as f:
        data = json.load(f)
    print(f"Rate control, 720p, {data['frames']} frames, target {data['kbps']} kbit/s, effort {data['effort']}:")
    ok = True
    for row in data["rows"]:
        ok &= abs(row["off"]) <= GATE_RATE and row.get("lowest", 0) >= 0
        print(f"  {row['clip']:16s} {row['mode']:9s} {row['kbps']:8.1f} kbit/s, {row['off']:+5.1f} %"
              + (f", buffer never below {row['lowest']:.0f} %" if "lowest" in row else ""))
    print(f"  Rate check (within {GATE_RATE:.0f} %, buffer never empty): {'met' if ok else 'not met'}")


def report(args):
    path = f"{OUT}/bd-e{args.effort}.json"
    if os.path.exists(path):
        with open(path) as f:
            report_bd(json.load(f))
    report_time(args.effort)
    for name in sorted(os.listdir(OUT)):
        if name.startswith(f"cycles-e{args.effort}-q") and name.endswith(".json"):
            report_cycles(args.effort, int(name[len(f"cycles-e{args.effort}-q"):-5]))
        if name.startswith(f"instr-e{args.effort}-q") and name.endswith(".json"):
            report_instr(args.effort, int(name[len(f"instr-e{args.effort}-q"):-5]))
        if name.startswith("rc-") and name.endswith(".json"):
            report_rc(int(name[3:-5]))


def main():
    global OUT, BENCH
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["bd", "time", "cycles", "instr", "rc", "report"])
    parser.add_argument("--frames", type=int, default=None)
    parser.add_argument("--effort", type=int, default=4)
    parser.add_argument("--quality", default=None, help="time and instr: one quality (50); cycles: a list (50,34)")
    parser.add_argument("--x265", default="medium", help="x265 configurations: presets, each with options after a colon")
    parser.add_argument("--kbps", type=int, default=2000)
    parser.add_argument("--threads", default=f"1,{os.cpu_count()}")
    parser.add_argument("--clips", default=",".join(CLIPS))
    parser.add_argument("--wide", action="store_true", help="the eight clips of the H.264 compression rounds (four busy, four calm)")
    parser.add_argument("--clip-frames", type=int, default=120, help="frames copied from each source clip")
    parser.add_argument("--rounds", type=int, default=2)
    parser.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) // 3))
    parser.add_argument("--reuse", action="store_true", help="bd: keep the runs already in the bd file and add only what is missing")
    parser.add_argument("--repeats", type=int, default=3, help="cycles: runs of each count, the least kept")
    parser.add_argument("--no-h264", action="store_true", help="bd and cycles: leave out Egami's H.264 encoder")
    parser.add_argument("--intra", default=None, help="bd: change Egami's intra profile (Name:value,...), to measure one setting")
    parser.add_argument("--inter", default=None, help="bd: change Egami's inter profile (Name:value,...), to measure one setting")
    parser.add_argument("--clips-from", default=".cache/video/derf")
    parser.add_argument("--out", default=OUT, help="where runs and results go (clips stay in .cache/hevcenc/clips)")
    parser.add_argument("--bench", default=BENCH, help="the benchmark build to time Egami with, inside the repo")
    args = parser.parse_args()
    OUT = args.out
    BENCH = args.bench
    if args.wide:
        args.clips = ",".join(WIDE_CLIPS)
    if args.mode != "report":
        ensure_clips(args.clips.split(","), args.clips_from, args.clip_frames)
    if args.mode == "bd":
        args.frames = args.frames or 30
        run_bd(args)
    elif args.mode == "time":
        args.frames = args.frames or 20
        args.quality = int(args.quality or 50)
        run_time(args)
    elif args.mode == "cycles":
        args.frames = args.frames or 10
        args.quality = args.quality or "50,34"
        run_cycles(args)
    elif args.mode == "instr":
        args.frames = args.frames or 10
        args.quality = int(args.quality or 50)
        run_instr(args)
    elif args.mode == "rc":
        args.frames = args.frames or 120
        run_rc(args)
    else:
        report(args)


if __name__ == "__main__":
    main()
