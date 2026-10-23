"""The 5.2 VP8 encoder gate (V5 design Q11 and Q12): Vp8StreamEncoder's encode time per frame at an effort within 2 times
libvpx's at the matched setting (--good --cpu-used=1), at one thread and at all cores, while its BD-rate on PSNR-Y stays within
+10 % of libvpx's.

Both encode the same raw 4:2:0 clips at fixed quantizers, the first frame a key frame and every other frame an inter frame, no
alt-ref and no lag: Egami through the benchmark project (--encode-vp8), libvpx through FFmpeg's libvpx (ffmpeg with libvpx on
the PATH, -deadline good -cpu-used 1 -qmin q -qmax q). The quantizers are matched pairs on the two scales (Egami's 0 to 127
index, libvpx's 0 to 63): 16/8, 32/16, 52/26 and 76/38. Both streams are decoded by FFmpeg for the PSNR-Y (the mean of the
frames'), and the BD-rate (cubic fit of log rate over PSNR) is Egami's against libvpx at one thread; libvpx's own all-core
output, which loses compression to its threads, is reported too. Times are the encoder's alone for Egami and FFmpeg's whole
run for libvpx (reading raw frames costs little next to encoding); runs alternate between the two and the best of the rounds
counts, since other work on the machine slows both alike.

The clips are made from the repo's photos and FFmpeg's test pattern (.cache/vp8/encode, 60 frames at 1920 x 1080):
  egret-1080p     zooming into egret.png, as tools/make-vp8-references.py --large makes its 1080p clip
  duck-1080p      panning over duck.png
  testsrc2-1080p  FFmpeg's testsrc2 pattern

Usage, from the repo root with the benchmarks built in Release (dotnet build -c Release benchmarks/Egami.Benchmarks):
  python tools/time-vp8-encoder.py [--efforts 4,6] [--threads 1,24] [--frames 60] [--rounds 2] [--clips egret,duck]
                                   [--size 1920x1080] [--no-libvpx]
"""
import argparse
import json
import os
import re
import subprocess
import time

import numpy as np

OUT = ".cache/vp8/encode"
BENCH = "benchmarks/Egami.Benchmarks/bin/Release/net10.0/Egami.Benchmarks.dll"
OURS_Q = [16, 32, 52, 76]
VPX_Q = [8, 16, 26, 38]
SOURCES = {
    "egret": "-loop 1 -i tests/images/ops/egret.png -vf scale={big}:-2,zoompan=z='1+0.01*on':x='iw/2-iw/zoom/2':y='ih/2-ih/zoom/2':d=1:s={w}x{h}",
    "duck": "-loop 1 -i tests/images/ops/duck.png -vf scale={big}:-2,crop={w}:{h}:x='min(iw-{w},n*9)':y='min(ih-{h},n*6)'",
    "testsrc2": "-f lavfi -i testsrc2=size={w}x{h}:rate=30",
}
GATE_TIME = 2.0
GATE_RATE = 10.0


def clip_path(name, w, h):
    return f"{OUT}/{name}-{h}p.yuv" if (w, h) in ((1920, 1080), (1280, 720)) else f"{OUT}/{name}-{w}x{h}.yuv"


def make_clip(name, w, h, frames):
    path = clip_path(name, w, h)
    size = w * h * 3 // 2
    if os.path.exists(path) and os.path.getsize(path) >= size * frames:
        return path
    os.makedirs(OUT, exist_ok=True)
    source = SOURCES[name].format(w=w, h=h, big=int(w * 1.5) // 2 * 2)
    subprocess.run(f"ffmpeg -v error -y {source} -frames:v {max(frames, 60)} -pix_fmt yuv420p -f rawvideo {path}", shell=True, check=True)
    return path


def psnr(src, ivf, w, h, frames):
    dec = subprocess.run(["ffmpeg", "-v", "error", "-i", ivf, "-fps_mode", "passthrough", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-"],
                         capture_output=True, check=True).stdout
    fs = w * h * 3 // 2
    with open(src, "rb") as f:
        a = np.frombuffer(f.read(fs * frames), np.uint8).reshape(frames, fs)[:, :w * h].astype(np.int32)
    b = np.frombuffer(dec, np.uint8).reshape(-1, fs)[:frames, :w * h].astype(np.int32)
    mse = ((a - b) ** 2).mean(axis=1)
    return float(np.mean(10 * np.log10(255 * 255 / np.maximum(mse, 1e-9))))


def egami(src, w, h, frames, q, effort, threads):
    out = f"{OUT}/egami-{os.path.basename(src)[:-4]}-{q}-{effort}-{threads}.ivf"
    text = subprocess.run(["dotnet", BENCH, "--encode-vp8", src, str(w), str(h), str(frames), str(q), str(effort), str(threads), out],
                          capture_output=True, text=True, check=True).stdout
    ms = float(re.search(r"([\d.]+) ms a frame", text).group(1))
    return out, ms


def libvpx(src, w, h, frames, q, threads):
    out = f"{OUT}/libvpx-{os.path.basename(src)[:-4]}-{q}-{threads}.ivf"
    start = time.perf_counter()
    subprocess.run(["ffmpeg", "-v", "error", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-s", f"{w}x{h}", "-r", "30", "-i", src,
                    "-frames:v", str(frames), "-c:v", "libvpx", "-deadline", "good", "-cpu-used", "1", "-qmin", str(q), "-qmax", str(q),
                    "-b:v", "1000M", "-auto-alt-ref", "0", "-lag-in-frames", "0", "-g", "9999", "-threads", str(threads), "-y", out], check=True)
    return out, (time.perf_counter() - start) * 1000 / frames


def bd_rate(r1, p1, r2, p2):
    """Bjontegaard rate difference of curve 2 against curve 1 in percent: cubic fit of log rate over PSNR."""
    c1, c2 = np.polyfit(p1, np.log(r1), 3), np.polyfit(p2, np.log(r2), 3)
    lo, hi = max(min(p1), min(p2)), min(max(p1), max(p2))
    i1 = np.polyval(np.polyint(c1), hi) - np.polyval(np.polyint(c1), lo)
    i2 = np.polyval(np.polyint(c2), hi) - np.polyval(np.polyint(c2), lo)
    return (np.exp((i2 - i1) / (hi - lo)) - 1) * 100


def main():
    cores = os.cpu_count()
    parser = argparse.ArgumentParser()
    parser.add_argument("--efforts", default="4")
    parser.add_argument("--threads", default=f"1,{cores}")
    parser.add_argument("--frames", type=int, default=60)
    parser.add_argument("--rounds", type=int, default=2)
    parser.add_argument("--clips", default="egret,duck,testsrc2")
    parser.add_argument("--size", default="1920x1080")
    parser.add_argument("--no-libvpx", action="store_true", help="reuse libvpx's results from the last run (.cache/vp8/encode/libvpx.json)")
    args = parser.parse_args()
    w, h = (int(v) for v in args.size.split("x"))
    efforts = [int(e) for e in args.efforts.split(",")]
    threads = [int(t) for t in args.threads.split(",")]
    clips = args.clips.split(",")
    cache_file = f"{OUT}/libvpx.json"
    cache = {}
    if args.no_libvpx and os.path.exists(cache_file):
        with open(cache_file) as f:
            cache = json.load(f)

    ours, theirs = {}, {}
    for _ in range(args.rounds):
        for clip in clips:
            src = make_clip(clip, w, h, args.frames)
            for t in threads:
                for i, q in enumerate(OURS_Q):
                    key = f"{clip}|{w}x{h}|{args.frames}|{VPX_Q[i]}|{t}"
                    if not args.no_libvpx or key not in cache:
                        out, ms = libvpx(src, w, h, args.frames, VPX_Q[i], t)
                        old = theirs.get(key)
                        theirs[key] = (out, min(ms, old[1]) if old else ms)
                    for effort in efforts:
                        out, ms = egami(src, w, h, args.frames, q, effort, t)
                        old = ours.get((clip, effort, t, q))
                        ours[(clip, effort, t, q)] = (out, min(ms, old[1]) if old else ms)

    for key, (out, ms) in theirs.items():
        clip, _, frames, _, _ = key.split("|")
        cache[key] = {"kbps": os.path.getsize(out) * 8 * 30 / int(frames) / 1000, "psnr": psnr(clip_path(clip, w, h), out, w, h, int(frames)), "ms": ms}
    with open(cache_file, "w") as f:
        json.dump(cache, f, indent=1)

    passed = True
    for clip in clips:
        src = clip_path(clip, w, h)
        vpx = {t: [cache[f"{clip}|{w}x{h}|{args.frames}|{q}|{t}"] for q in VPX_Q] for t in threads}
        base = vpx[1] if 1 in vpx else vpx[threads[0]]
        for t in threads:
            vt = np.mean([x["ms"] for x in vpx[t]])
            loss = bd_rate([x["kbps"] for x in base], [x["psnr"] for x in base], [x["kbps"] for x in vpx[t]], [x["psnr"] for x in vpx[t]])
            print(f"{clip} {w}x{h}, {t} thread(s): libvpx cpu-used 1 {vt:7.1f} ms a frame{f', BD-rate {loss:+.1f}% against its one thread' if t != 1 else ''}")
            for effort in efforts:
                runs = [ours[(clip, effort, t, q)] for q in OURS_Q]
                rates = [os.path.getsize(out) * 8 * 30 / args.frames / 1000 for out, _ in runs]
                psnrs = [psnr(src, out, w, h, args.frames) for out, _ in runs]
                ms = np.mean([m for _, m in runs])
                rate = bd_rate([x["kbps"] for x in base], [x["psnr"] for x in base], rates, psnrs)
                ok = ms / vt <= GATE_TIME and rate <= GATE_RATE
                passed &= ok
                print(f"  effort {effort:2}: {ms:7.1f} ms a frame, {ms / vt:4.2f}x libvpx, BD-rate {rate:+5.1f}%  {'pass' if ok else 'FAIL'}"
                      f"   ({', '.join(f'{r:.0f} kbps {p:.2f} dB' for r, p in zip(rates, psnrs))})")
    print("gate", "passes" if passed else "fails", f"(time at most {GATE_TIME}x libvpx's, BD-rate at most +{GATE_RATE:.0f}%)")


if __name__ == "__main__":
    main()
