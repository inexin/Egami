"""The V5.3 VP9 encoder gates (V5 design Q11 and Q12): Egami's BD-rate on PSNR-Y and SSIM against libvpx VP9 --good at the
cpu-used whose encode time matches Egami's (at most +15 %), and Egami's encode time at its default effort against libvpx at the
cpu-used whose BD-rate matches (at most 2 times), at 1080p on one thread and on all cores.

Everything runs in Docker on the same machine: libvpx 1.14.1's vpxenc (SIMD, threads; egami-vp9encref:v5.3 from
tools/video/vp9encref/Dockerfile) as one-pass VBR with 25 frames of lag and alternate references (--good --cpu-used=N
--end-usage=vbr --lag-in-frames=25 --auto-alt-ref=1), and Egami through the benchmark project's Release build (--encode-vp9,
constant quality, 25 frames of lookahead and alternate references by default) on .NET 10 (egami-dotnet-perf:2.4.5). FFmpeg in
the libvpx image measures SSIM; PSNR-Y is the mean of the frames' own. libvpx encodes at the bitrates Egami's qualities gave,
so the two curves cover the same range. The BD-rate is a cubic fit of log rate over the metric, Egami's against libvpx's.

Times are each encoder's whole run for its frames (Egami's frames are read into memory first, libvpx reads the y4m as it
goes, which costs little next to encoding); runs alternate between the two and the best of --rounds counts, as other work on
the shared machine slows both alike. The BD runs only measure sizes and quality, so they run --jobs at a time.

The clips are Xiph's derf clips (crowd_run, ducks_take_off, park_joy, old_town_cross; 50 fps), copied to .cache/vp9enc/clips as
<name>-720.y4m and <name>-1080.y4m (from .cache/video/derf, which tools/video/make-vp8-corpus.py fetches, or --clips-from).

Usage, from the repo root with Docker running and the benchmarks built in Release
(dotnet build -c Release benchmarks/Egami.Video.Benchmarks):
  python tools/video/time-vp9-encoder.py bd [--size 720] [--frames 60] [--effort 4] [--cpu 0,1,2,3,4,5] [--clips a,b]
  python tools/video/time-vp9-encoder.py time [--size 1080] [--frames 30] [--effort 4] [--cpu 0,1,2,3,4,5] [--threads 1,24]
  python tools/video/time-vp9-encoder.py report     (the gate table from the last bd and time runs)
"""
import argparse
import concurrent.futures
import json
import os
import re
import shutil
import subprocess
import time

import numpy as np

OUT = ".cache/vp9enc"
CLIPS = ["crowd_run", "ducks_take_off", "park_joy", "old_town_cross"]
QUALITIES = [30, 45, 60, 75]
VPX_IMAGE = "egami-vp9encref:v5.3"
DOTNET_IMAGE = "egami-dotnet-perf:2.4.5"
BENCH = "benchmarks/Egami.Video.Benchmarks/bin/Release/net10.0/Egami.Video.Benchmarks.dll"
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")
ROOT = os.path.abspath(".")
GATE_BD = 15.0
GATE_TIME = 2.0


def docker(image, command, cpus=None):
    args = ["docker", "run", "--rm", "-v", f"{ROOT}:/work", "-w", "/work"]
    if cpus is not None:
        args += ["--cpus", str(cpus)]
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
    out = docker(VPX_IMAGE, f"ffmpeg -hide_banner -f rawvideo -pix_fmt yuv420p -s {w}x{h} -r {fps} -i {decoded_yuv} -i {src} "
                            f"-frames:v {len(dec)} -lavfi '[0:v][1:v]ssim' -f null - 2>&1 | grep -o 'Y:[0-9.]*' | tail -1")
    ssim = float(out.stdout.strip().split(":")[1]) if out.stdout.strip() else float("nan")
    return float(np.mean(psnrs)), ssim


def egami(src, frames, quality, effort, threads, tag):
    out = f"{OUT}/runs/egami-{tag}"
    cmd = (f"dotnet {BENCH} --encode-vp9 {src} --frames={frames} --quality={quality} --effort={effort} --threads={threads} "
           f"--out={out}.ivf --decoded={out}.yuv")
    result = docker(DOTNET_IMAGE, cmd)
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)
    data = json.loads(result.stdout.strip().splitlines()[-1])
    return out, data["ms"], data["bytes"]


def libvpx(src, frames, kbps, cpu, threads, tag):
    out = f"{OUT}/runs/vpx-{tag}"
    tiles = "--tile-columns=6 --row-mt=1" if threads > 1 else "--tile-columns=0"
    cmd = ("python3 -c \"import subprocess,time,sys;s=time.perf_counter();"
           f"r=subprocess.run(['vpxenc','--ivf','--good','--cpu-used={cpu}','--threads={threads}',*'{tiles}'.split(),'--limit={frames}',"
           f"'--lag-in-frames=25','--auto-alt-ref=1','--end-usage=vbr','--target-bitrate={int(kbps)}','-q','-o','{out}.ivf','{src}']);"
           "print((time.perf_counter()-s)*1000)\" && vpxdec --i420 --rawvideo -o " + out + ".yuv " + out + ".ivf")
    result = docker(VPX_IMAGE, cmd)
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
    cpus = [int(c) for c in args.cpu.split(",")]
    results = {"size": args.size, "frames": args.frames, "effort": args.effort, "clips": {}}

    def egami_job(clip, q):
        src = clip_path(clip, args.size)
        out, ms, size = egami(src, args.frames, q, args.effort, 1, f"{clip}-{args.size}-q{q}-e{args.effort}")
        _, _, fps = y4m_info(src)
        psnr, ssim = metrics(src, out + ".yuv", args.frames)
        return clip, ("egami", q), {"kbps": size * 8 * fps / args.frames / 1000, "psnr": psnr, "ssim": ssim, "ms": ms}

    def vpx_job(clip, cpu, kbps, i):
        src = clip_path(clip, args.size)
        out, ms, size = libvpx(src, args.frames, kbps, cpu, 1, f"{clip}-{args.size}-c{cpu}-{i}")
        _, _, fps = y4m_info(src)
        psnr, ssim = metrics(src, out + ".yuv", args.frames)
        return clip, ("vpx", cpu, i), {"kbps": size * 8 * fps / args.frames / 1000, "psnr": psnr, "ssim": ssim, "ms": ms}

    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        egami_results = list(pool.map(lambda job: egami_job(*job), [(c, q) for c in clips for q in QUALITIES]))
        for clip, key, value in egami_results:
            results["clips"].setdefault(clip, {"egami": [], "vpx": {}})["egami"].append(value)
        jobs = []
        for clip in clips:
            for cpu in cpus:
                for i, point in enumerate(results["clips"][clip]["egami"]):
                    jobs.append((clip, cpu, point["kbps"], i))
        for clip, key, value in pool.map(lambda job: vpx_job(*job), jobs):
            results["clips"][clip]["vpx"].setdefault(str(key[1]), []).append(value)

    with open(f"{OUT}/bd-{args.size}-e{args.effort}.json", "w") as f:
        json.dump(results, f, indent=1)
    report_bd(results)


def report_bd(results):
    print(f"BD-rate of Egami (effort {results['effort']}) against libvpx --good, {results['size']}p, {results['frames']} frames:")
    for clip, data in results["clips"].items():
        e = sorted(data["egami"], key=lambda p: p["kbps"])
        line = f"  {clip:16s}"
        for cpu, points in sorted(data["vpx"].items(), key=lambda kv: int(kv[0])):
            v = sorted(points, key=lambda p: p["kbps"])
            psnr = bd_rate([p["kbps"] for p in v], [p["psnr"] for p in v], [p["kbps"] for p in e], [p["psnr"] for p in e])
            ssim = bd_rate([p["kbps"] for p in v], [p["ssim"] for p in v], [p["kbps"] for p in e], [p["ssim"] for p in e])
            line += f"  cpu{cpu}: {psnr:+6.1f}% / {ssim:+6.1f}%"
        print(line)


def run_time(args):
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    clips = args.clips.split(",")
    cpus = [int(c) for c in args.cpu.split(",")]
    threads = [int(t) for t in args.threads.split(",")]
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
                    _, ms, _ = libvpx(src, args.frames, kbps, cpu, t, f"time-{clip}-c{cpu}-t{t}")
                    key = f"{clip}|vpx{cpu}|{t}"
                    best[key] = min(best.get(key, float("inf")), ms)
    results = {"size": args.size, "frames": args.frames, "effort": args.effort, "times": best}
    with open(f"{OUT}/time-{args.size}-e{args.effort}.json", "w") as f:
        json.dump(results, f, indent=1)
    for key, ms in sorted(best.items()):
        print(f"  {key:40s} {ms / args.frames:8.1f} ms a frame")


def report(args):
    bd_file = f"{OUT}/bd-{args.bd_size}-e{args.effort}.json"
    time_file = f"{OUT}/time-{args.size}-e{args.effort}.json"
    with open(bd_file) as f:
        bd = json.load(f)
    with open(time_file) as f:
        times = json.load(f)
    report_bd(bd)
    t = times["times"]
    for threads in sorted({int(k.split("|")[2]) for k in t}):
        egami_ms = np.mean([v for k, v in t.items() if k.split("|")[1] == "egami" and int(k.split("|")[2]) == threads])
        vpx = {int(k.split("|")[1][3:]): [] for k in t if k.split("|")[1].startswith("vpx")}
        for k, v in t.items():
            if k.split("|")[1].startswith("vpx") and int(k.split("|")[2]) == threads:
                vpx[int(k.split("|")[1][3:])].append(v)
        print(f"{threads} thread(s), {times['size']}p: Egami effort {times['effort']} {egami_ms / times['frames']:.1f} ms a frame")
        for cpu, values in sorted(vpx.items()):
            ms = np.mean(values) if values else float("nan")
            print(f"  libvpx cpu-used {cpu}: {ms / times['frames']:7.1f} ms a frame, Egami / libvpx {egami_ms / ms:5.2f}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["bd", "time", "report"])
    parser.add_argument("--size", default=None)
    parser.add_argument("--bd-size", default="720")
    parser.add_argument("--frames", type=int, default=None)
    parser.add_argument("--effort", type=int, default=4)
    parser.add_argument("--quality", type=int, default=60)
    parser.add_argument("--cpu", default="0,1,2,3,4,5")
    parser.add_argument("--threads", default=f"1,{os.cpu_count()}")
    parser.add_argument("--clips", default=",".join(CLIPS))
    parser.add_argument("--rounds", type=int, default=2)
    parser.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) // 3))
    parser.add_argument("--clips-from", default=".cache/video/derf")
    args = parser.parse_args()
    if args.mode == "bd":
        args.size = args.size or "720"
        args.frames = args.frames or 60
        ensure_clips(args.size, args.clips_from)
        run_bd(args)
    elif args.mode == "time":
        args.size = args.size or "1080"
        args.frames = args.frames or 30
        ensure_clips(args.size, args.clips_from)
        run_time(args)
    else:
        args.size = args.size or "1080"
        report(args)


if __name__ == "__main__":
    main()
