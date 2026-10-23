"""The V5.4 AV1 encoder gates (V5 design Q11 and Q12): Egami's BD-rate on PSNR-Y and SSIM against libaom --good at the cpu-used
whose encode time matches Egami's (at most +15 %), with SVT-AV1's presets recorded next to it; Egami's encode time at its default
effort against libaom at the cpu-used whose BD-rate matches (at most 2 times), at 1080p on one thread and on all cores; and the
rate control gate: one and two-pass bitrate within 5 % of the target over a clip, constant bitrate never emptying the buffer.

Everything runs in Docker on the same machine: libaom 3.15.2's aomenc (SIMD, threads, row-mt; egami-av1encref:v5.4 from
tools/video/av1encref/Dockerfile) as one-pass VBR with 25 frames of lag and alternate references (--good --passes=1
--cpu-used=N --end-usage=vbr --lag-in-frames=25 --auto-alt-ref=1), SVT-AV1 3.0.2's SvtAv1EncApp in VBR (--rc 1) at its presets,
and Egami through the benchmark project's Release build (--encode-av1, constant quality, 25 frames of lookahead, hidden
alternate references and their pyramid by default) on .NET 10 (egami-dotnet-perf:2.4.5). aomdec decodes libaom's and SVT-AV1's
streams; FFmpeg in the libaom image measures SSIM; PSNR-Y is the mean of the frames' own. The reference encoders encode at the
bitrates Egami's qualities gave, so the curves cover the same range. The BD-rate is a cubic fit of log rate over the metric.

Times are each encoder's whole run for its frames (Egami's frames are read into memory first, aomenc reads the y4m as it goes,
which costs little next to encoding); runs alternate between the encoders and the best of --rounds counts, as other work on the
shared machine slows both alike. The BD runs only measure sizes and quality, so they run --jobs at a time.

The clips are Xiph's derf clips (crowd_run, ducks_take_off, park_joy, old_town_cross; 50 fps), copied to .cache/av1enc/clips as
<name>-720.y4m and <name>-1080.y4m (from .cache/video/derf, which tools/video/make-vp8-corpus.py fetches, or --clips-from).

Usage, from the repo root with Docker running and the benchmarks built in Release
(dotnet build -c Release benchmarks/Egami.Video.Benchmarks):
  python tools/video/time-av1-encoder.py bd [--size 720] [--frames 60] [--effort 4] [--cpu 0,1,2,3,4,5,6] [--svt 4,6,8] [--clips a,b]
  python tools/video/time-av1-encoder.py time [--size 1080] [--frames 10] [--effort 4] [--cpu 0,1,2,3,4,5,6] [--threads 1,24]
  python tools/video/time-av1-encoder.py rc [--size 720] [--frames 100] [--effort 2] [--kbps 1500]
  python tools/video/time-av1-encoder.py report     (the gate table from the last bd and time runs)
"""
import argparse
import concurrent.futures
import json
import os
import shutil
import subprocess

import numpy as np

OUT = ".cache/av1enc"
CLIPS = ["crowd_run", "ducks_take_off", "park_joy", "old_town_cross"]
QUALITIES = [30, 45, 60, 75]
AOM_IMAGE = "egami-av1encref:v5.4"
DOTNET_IMAGE = "egami-dotnet-perf:2.4.5"
BENCH = "benchmarks/Egami.Video.Benchmarks/bin/Release/net10.0/Egami.Video.Benchmarks.dll"
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")
ROOT = os.path.abspath(".")
GATE_BD = 15.0
GATE_TIME = 2.0
GATE_RATE = 5.0


def docker(image, command):
    args = ["docker", "run", "--rm", "-v", f"{ROOT}:/work", "-w", "/work"]
    return subprocess.run(args + [image, "sh", "-c", command], capture_output=True, text=True, env=ENV)


def clip_path(name, size):
    return f"{OUT}/clips/{name}-{size}.y4m"


def ensure_clips(size, source):
    os.makedirs(f"{OUT}/clips", exist_ok=True)
    for name in CLIPS:
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


def metrics(src, decoded_yuv, frames):
    """Mean PSNR-Y of the frames, and SSIM-Y from FFmpeg."""
    w, h, ref = y4m_frames(src, frames)
    fps = y4m_info(src)[2]
    dec = np.fromfile(decoded_yuv, np.uint8).reshape(-1, w * h * 3 // 2)[:frames]
    psnrs = []
    for a, b in zip(ref, dec):
        mse = ((a[:w * h].astype(np.int32) - b[:w * h].astype(np.int32)) ** 2).mean()
        psnrs.append(10 * np.log10(255 * 255 / max(mse, 1e-9)))
    out = docker(AOM_IMAGE, f"ffmpeg -hide_banner -f rawvideo -pix_fmt yuv420p -s {w}x{h} -r {fps} -i {decoded_yuv} -i {src} "
                            f"-frames:v {len(dec)} -lavfi '[0:v][1:v]ssim' -f null - 2>&1 | grep -o 'Y:[0-9.]*' | tail -1")
    ssim = float(out.stdout.strip().split(":")[1]) if out.stdout.strip() else float("nan")
    return float(np.mean(psnrs)), ssim


def egami(src, frames, quality, effort, threads, tag, extra=""):
    out = f"{OUT}/runs/egami-{tag}"
    q = f"--quality={quality}" if quality is not None else ""
    cmd = (f"dotnet {BENCH} --encode-av1 {src} --frames={frames} {q} --effort={effort} --threads={threads} {extra} "
           f"--out={out}.ivf --decoded={out}.yuv")
    result = docker(DOTNET_IMAGE, cmd)
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)
    data = json.loads(result.stdout.strip().splitlines()[-1])
    return out, data["ms"], data["bytes"]


def timed(command):
    """A shell command run under a timer that prints its milliseconds first."""
    return ("python3 -c \"import subprocess,time,sys;s=time.perf_counter();"
            f"r=subprocess.run(sys.argv[1:]);print((time.perf_counter()-s)*1000);sys.exit(r.returncode)\" {command}")


def libaom(src, frames, kbps, cpu, threads, tag):
    out = f"{OUT}/runs/aom-{tag}"
    cmd = (timed(f"aomenc --ivf --good --passes=1 --cpu-used={cpu} --threads={threads} --row-mt={1 if threads > 1 else 0} "
                 f"--limit={frames} --lag-in-frames=25 --auto-alt-ref=1 --end-usage=vbr --target-bitrate={int(kbps)} -q "
                 f"-o {out}.ivf {src}")
           + f" && aomdec --rawvideo -o {out}.yuv {out}.ivf")
    result = docker(AOM_IMAGE, cmd)
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)
    ms = float(result.stdout.strip().splitlines()[0])
    return out, ms, os.path.getsize(out + ".ivf")


def svt(src, frames, kbps, preset, threads, tag):
    out = f"{OUT}/runs/svt-{tag}"
    lp = 1 if threads == 1 else 0
    cmd = (timed(f"SvtAv1EncApp -i {src} -b {out}.ivf -n {frames} --preset {preset} --rc 1 --tbr {max(1, int(kbps))} --lp {lp} "
                 f"--progress 0")
           + f" && aomdec --rawvideo -o {out}.yuv {out}.ivf")
    result = docker(AOM_IMAGE, cmd)
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)
    ms = float(result.stdout.strip().splitlines()[0])
    return out, ms, os.path.getsize(out + ".ivf")


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
    cpus = [int(c) for c in args.cpu.split(",")] if args.cpu else []
    presets = [int(p) for p in args.svt.split(",")] if args.svt else []
    path = f"{OUT}/bd-{args.size}-e{args.effort}.json"
    results = {"size": args.size, "frames": args.frames, "effort": args.effort, "clips": {}}
    if os.path.exists(path) and args.reuse:
        with open(path) as f:
            results = json.load(f)

    def egami_job(clip, q):
        src = clip_path(clip, args.size)
        out, ms, size = egami(src, args.frames, q, args.effort, 1, f"{clip}-{args.size}-q{q}-e{args.effort}")
        _, _, fps = y4m_info(src)
        psnr, ssim = metrics(src, out + ".yuv", args.frames)
        return clip, q, {"q": q, "kbps": size * 8 * fps / args.frames / 1000, "psnr": psnr, "ssim": ssim, "ms": ms}

    def ref_job(kind, clip, setting, kbps, i):
        src = clip_path(clip, args.size)
        encode = libaom if kind == "aom" else svt
        out, ms, size = encode(src, args.frames, kbps, setting, 1, f"{clip}-{args.size}-{setting}-{i}")
        _, _, fps = y4m_info(src)
        psnr, ssim = metrics(src, out + ".yuv", args.frames)
        return kind, clip, setting, {"kbps": size * 8 * fps / args.frames / 1000, "psnr": psnr, "ssim": ssim, "ms": ms}

    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        missing = [(c, q) for c in clips for q in QUALITIES
                   if not any(p.get("q") == q for p in results["clips"].get(c, {}).get("egami", []))]
        for clip, q, value in pool.map(lambda job: egami_job(*job), missing):
            results["clips"].setdefault(clip, {"egami": [], "aom": {}, "svt": {}})["egami"].append(value)
        jobs = []
        for clip in clips:
            data = results["clips"][clip]
            data.setdefault("aom", {})
            data.setdefault("svt", {})
            points = sorted(data["egami"], key=lambda p: p["q"])
            for kind, settings in (("aom", cpus), ("svt", presets)):
                for setting in settings:
                    if str(setting) in data[kind]:
                        continue
                    for i, point in enumerate(points):
                        jobs.append((kind, clip, setting, point["kbps"], i))
        for kind, clip, setting, value in pool.map(lambda job: ref_job(*job), jobs):
            results["clips"][clip][kind].setdefault(str(setting), []).append(value)

    with open(path, "w") as f:
        json.dump(results, f, indent=1)
    report_bd(results)


def report_bd(results):
    print(f"BD-rate of Egami (effort {results['effort']}) against libaom --good and SVT-AV1, {results['size']}p, {results['frames']} frames"
          " (PSNR-Y / SSIM; time a frame at one thread):")
    for clip, data in results["clips"].items():
        e = sorted(data["egami"], key=lambda p: p["kbps"])
        print(f"  {clip}: Egami {np.mean([p['ms'] for p in e]) / results['frames'] / 1000:.2f} s a frame")
        for kind, name in (("aom", "libaom cpu-used"), ("svt", "SVT-AV1 preset")):
            for setting, points in sorted(data.get(kind, {}).items(), key=lambda kv: int(kv[0])):
                v = sorted(points, key=lambda p: p["kbps"])
                psnr = bd_rate([p["kbps"] for p in v], [p["psnr"] for p in v], [p["kbps"] for p in e], [p["psnr"] for p in e])
                ssim = bd_rate([p["kbps"] for p in v], [p["ssim"] for p in v], [p["kbps"] for p in e], [p["ssim"] for p in e])
                ms = np.mean([p["ms"] for p in v]) / results["frames"] / 1000
                print(f"    {name} {setting:>2}: {psnr:+6.1f}% / {ssim:+6.1f}%   {ms:6.2f} s a frame")


def run_time(args):
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    clips = args.clips.split(",")
    cpus = [int(c) for c in args.cpu.split(",")]
    threads = [int(t) for t in args.threads.split(",")]
    path = f"{OUT}/time-{args.size}-e{args.effort}.json"
    best = {}
    for _ in range(args.rounds):
        for clip in clips:
            src = clip_path(clip, args.size)
            for t in threads:
                _, ms, size = egami(src, args.frames, args.quality, args.effort, t, f"time-{clip}-e{args.effort}-t{t}")
                key = f"{clip}|egami|{t}"
                best[key] = min(best.get(key, float("inf")), ms)
                _, _, fps = y4m_info(src)
                kbps = size * 8 * fps / args.frames / 1000
                for cpu in cpus:
                    _, ms, _ = libaom(src, args.frames, kbps, cpu, t, f"time-{clip}-c{cpu}-t{t}")
                    key = f"{clip}|aom{cpu}|{t}"
                    best[key] = min(best.get(key, float("inf")), ms)
                print(f"  {clip} {t} thread(s): " + ", ".join(f"{k.split('|')[1]} {v / args.frames / 1000:.2f} s"
                                                          for k, v in best.items() if k.startswith(clip) and k.endswith(f"|{t}")), flush=True)
    results = {"size": args.size, "frames": args.frames, "effort": args.effort, "times": best}
    with open(path, "w") as f:
        json.dump(results, f, indent=1)
    for key, ms in sorted(best.items()):
        print(f"  {key:40s} {ms / args.frames / 1000:8.2f} s a frame")


def run_rc(args):
    """One-pass and two-pass bitrate against the target, and constant bitrate's buffer, from the packet sizes."""
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    rows = []
    for clip in args.clips.split(","):
        src = clip_path(clip, args.size)
        _, _, fps = y4m_info(src)
        for mode, extra in (("vbr", f"--bitrate={args.kbps * 1000}"), ("two-pass", f"--bitrate={args.kbps * 1000} --two-pass"),
                            ("cbr", f"--bitrate={args.kbps * 1000} --cbr --realtime")):
            out, _, size = egami(src, args.frames, None, args.effort, os.cpu_count(), f"rc-{clip}-{mode}", extra)
            kbps = size * 8 * fps / args.frames / 1000
            row = {"clip": clip, "mode": mode, "kbps": kbps, "off": (kbps / args.kbps - 1) * 100}
            if mode == "cbr":
                # The leaky bucket the encoder keeps: one second of the bitrate, starting three quarters full.
                buffer = args.kbps * 1000.0
                level, lowest = buffer * 0.75, buffer
                for packet in ivf_sizes(out + ".ivf"):
                    level = min(buffer, level + args.kbps * 1000.0 / fps) - packet * 8
                    lowest = min(lowest, level)
                row["lowest"] = lowest / buffer * 100
            rows.append(row)
            print(f"  {clip:16s} {mode:9s} {kbps:8.1f} kbit/s, {row['off']:+5.1f} %"
                  + (f", buffer never below {row['lowest']:.0f} %" if "lowest" in row else ""), flush=True)
    with open(f"{OUT}/rc-{args.size}.json", "w") as f:
        json.dump({"kbps": args.kbps, "frames": args.frames, "rows": rows}, f, indent=1)


def ivf_sizes(path):
    with open(path, "rb") as f:
        data = f.read()
    at, sizes = 32, []
    while at + 12 <= len(data):
        size = int.from_bytes(data[at:at + 4], "little")
        sizes.append(size)
        at += 12 + size
    return sizes


def report(args):
    with open(f"{OUT}/bd-{args.bd_size}-e{args.effort}.json") as f:
        bd = json.load(f)
    report_bd(bd)
    time_file = f"{OUT}/time-{args.size}-e{args.effort}.json"
    if not os.path.exists(time_file):
        return
    with open(time_file) as f:
        times = json.load(f)
    t = times["times"]
    for threads in sorted({int(k.split("|")[2]) for k in t}):
        egami_ms = np.mean([v for k, v in t.items() if k.split("|")[1] == "egami" and int(k.split("|")[2]) == threads])
        aom = {}
        for k, v in t.items():
            if k.split("|")[1].startswith("aom") and int(k.split("|")[2]) == threads:
                aom.setdefault(int(k.split("|")[1][3:]), []).append(v)
        print(f"{threads} thread(s), {times['size']}p: Egami effort {times['effort']} {egami_ms / times['frames'] / 1000:.2f} s a frame")
        for cpu, values in sorted(aom.items()):
            ms = np.mean(values)
            print(f"  libaom cpu-used {cpu}: {ms / times['frames'] / 1000:7.2f} s a frame, Egami / libaom {egami_ms / ms:6.2f}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["bd", "time", "rc", "report"])
    parser.add_argument("--size", default=None)
    parser.add_argument("--bd-size", default="720")
    parser.add_argument("--frames", type=int, default=None)
    parser.add_argument("--effort", type=int, default=4)
    parser.add_argument("--quality", type=int, default=60)
    parser.add_argument("--cpu", default="0,1,2,3,4,5,6")
    parser.add_argument("--svt", default="")
    parser.add_argument("--kbps", type=int, default=1500)
    parser.add_argument("--threads", default=f"1,{os.cpu_count()}")
    parser.add_argument("--clips", default=",".join(CLIPS))
    parser.add_argument("--rounds", type=int, default=2)
    parser.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) // 3))
    parser.add_argument("--reuse", action="store_true", help="keep the runs already in the bd file and add only what is missing")
    parser.add_argument("--clips-from", default=".cache/video/derf")
    args = parser.parse_args()
    if args.mode == "bd":
        args.size = args.size or "720"
        args.frames = args.frames or 60
        ensure_clips(args.size, args.clips_from)
        run_bd(args)
    elif args.mode == "time":
        args.size = args.size or "1080"
        args.frames = args.frames or 10
        ensure_clips(args.size, args.clips_from)
        run_time(args)
    elif args.mode == "rc":
        args.size = args.size or "720"
        args.frames = args.frames or 100
        ensure_clips(args.size, args.clips_from)
        run_rc(args)
    else:
        args.size = args.size or "1080"
        report(args)


if __name__ == "__main__":
    main()
