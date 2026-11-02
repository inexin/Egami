"""The Video 1.2.0 deinterlace gates (design D8), with FFmpeg's yadif run as a black box in Docker:

1. quality: a synthetic set of progressive clips at 50 frames a second (FFmpeg's testsrc2 and mandelbrot sources, and a pan
   over a photo), woven two pictures to a frame into 25i YUV4MPEG2 clips, top field first. Each clip is deinterlaced at the
   frame rate and at the field rate by egami-video (encode --deinterlace) and by yadif (mode send_frame and send_field), and
   every output picture is measured against the progressive picture it was woven from: the PSNR over all planes, averaged
   over the clip. The gate: Egami's PSNR is at least yadif's minus 0.3 dB on every clip and rate.
2. speed: the time per frame of 1080i adaptive deinterlacing on one thread, Egami's from the benchmark project
   (--time-deinterlace, frames held in memory) and yadif's from ffmpeg -benchmark with -threads 1 and -filter_threads 1, less
   the same line without the filter. The gate: Egami within 2x of yadif.

Usage, from the repo root with Docker running and the CLI and benchmarks built in Release:
  dotnet build -c Release src/Egami.Video.Cli
  dotnet build -c Release benchmarks/Egami.Video.Benchmarks
  python tools/video/check-deinterlace.py              both gates
  python tools/video/check-deinterlace.py --quality    the PSNR gate only
  python tools/video/check-deinterlace.py --speed      the speed gate only
  python tools/video/check-deinterlace.py --frames <interlaced file> [--at 10]
                                                       PNGs of six frames of a real interlaced file (H.264 and HEVC too)
                                                       for a look by eye: woven, Egami adaptive, bob and adaptive at the
                                                       field rate, and yadif
The clips go to .cache/video/deinterlace; results.json there holds the numbers.
"""
import json
import os
import subprocess
import sys

import numpy as np

IMAGE = "egami-video-tools"
OUT = ".cache/video/deinterlace"
CLI = os.path.join("src", "Egami.Video.Cli", "bin", "Release", "net10.0", "Egami.Video.Cli.dll")
BENCH = os.path.join("benchmarks", "Egami.Video.Benchmarks", "bin", "Release", "net10.0", "Egami.Video.Benchmarks.dll")
PHOTO = "tests/images/jpeg/egret-420.jpg"
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")

# name: (size, woven frames, lavfi source or None for the photo pan)
CLIPS = {
    "sd-testsrc2": ("720x576", 24, "testsrc2=size=720x576:rate=50"),
    "sd-mandelbrot": ("720x576", 24, "mandelbrot=size=720x576:rate=50"),
    "sd-pan": ("720x576", 24, None),
    "fhd-testsrc2": ("1920x1080", 16, "testsrc2=size=1920x1080:rate=50"),
    "fhd-pan": ("1920x1080", 16, None),
}
GATE_DB = 0.3
GATE_SPEED = 2.0


def docker(command):
    return subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(OUT)}:/work", "-v", f"{os.path.abspath('tests/images')}:/images:ro",
                           IMAGE, "sh", "-c", command], check=True, capture_output=True, text=True, env=ENV)


def egami(*args):
    return subprocess.run(["dotnet", CLI, *args], check=True, capture_output=True, text=True)


def planes(size):
    w, h = (int(v) for v in size.split("x"))
    return w, h, [(w, h), (w // 2, h // 2), (w // 2, h // 2)]


def read_raw(path, size):
    """Frames of 8-bit 4:2:0 raw video as lists of three plane arrays."""
    w, h, shapes = planes(size)
    frame = sum(pw * ph for pw, ph in shapes)
    data = np.fromfile(path, dtype=np.uint8)
    frames = []
    for i in range(len(data) // frame):
        chunk, offset, planes_ = data[i * frame:(i + 1) * frame], 0, []
        for pw, ph in shapes:
            planes_.append(chunk[offset:offset + pw * ph].reshape(ph, pw))
            offset += pw * ph
        frames.append(planes_)
    return frames


def read_y4m(path):
    with open(path, "rb") as f:
        data = f.read()
    header_end = data.index(b"\n")
    tags = data[:header_end].split()
    w = int(next(t for t in tags if t.startswith(b"W"))[1:])
    h = int(next(t for t in tags if t.startswith(b"H"))[1:])
    _, _, shapes = planes(f"{w}x{h}")
    frames, pos = [], header_end + 1
    while pos < len(data):
        pos = data.index(b"\n", pos) + 1
        planes_ = []
        for pw, ph in shapes:
            planes_.append(np.frombuffer(data, dtype=np.uint8, count=pw * ph, offset=pos).reshape(ph, pw))
            pos += pw * ph
        frames.append(planes_)
    return frames


def write_y4m(path, frames, size, rate, order):
    w, h, _ = planes(size)
    with open(path, "wb") as f:
        f.write(f"YUV4MPEG2 W{w} H{h} F{rate}:1 I{order} A1:1 C420mpeg2\n".encode())
        for frame in frames:
            f.write(b"FRAME\n")
            for plane in frame:
                f.write(np.ascontiguousarray(plane).tobytes())


def weave(first, second):
    """Frame from two pictures: the first one's top field, the second one's bottom field, in every plane."""
    woven = []
    for a, b in zip(first, second):
        plane = a.copy()
        plane[1::2] = b[1::2]
        woven.append(plane)
    return woven


def psnr(a, b):
    squares = sum(float(np.sum((x.astype(np.int32) - y.astype(np.int32)) ** 2)) for x, y in zip(a, b))
    count = sum(x.size for x in a)
    mse = squares / count
    return 100.0 if mse == 0 else 10 * np.log10(255 * 255 / mse)


def make(name, size, frames, source):
    woven = os.path.join(OUT, name + ".y4m")
    progressive = os.path.join(OUT, name + "-progressive.yuv")
    if not os.path.exists(progressive):
        count = 2 * frames
        if source is None:
            # A pan across the photo, scaled up so the crop window moves over it: 3 pixels across and 1 down a picture.
            w, h, _ = planes(size)
            docker(f"ffmpeg -y -v error -nostdin -loop 1 -framerate 50 -i /images/{PHOTO.removeprefix('tests/images/')} "
                   f"-vf \"scale={2 * w}:{2 * h},crop={w}:{h}:'n*3':'n*1',format=yuv420p\" -frames:v {count} -f rawvideo {name}-progressive.yuv")
        else:
            docker(f"ffmpeg -y -v error -nostdin -f lavfi -i {source} -frames:v {count} -pix_fmt yuv420p -f rawvideo {name}-progressive.yuv")
    if not os.path.exists(woven):
        pictures = read_raw(progressive, size)
        write_y4m(woven, [weave(pictures[2 * n], pictures[2 * n + 1]) for n in range(len(pictures) // 2)], size, 25, "t")
    return woven, read_raw(progressive, size)


def quality():
    results, failures = {}, []
    for name, (size, frames, source) in CLIPS.items():
        woven, pictures = make(name, size, frames, source)
        entry = {}
        for rate, mode in (("frame", "send_frame"), ("field", "send_field")):
            ours = os.path.join(OUT, f"{name}-egami-{rate}.y4m")
            theirs = f"{name}-yadif-{rate}.yuv"
            egami("encode", woven, "-o", ours, "--deinterlace", "adaptive", "--deinterlace-rate", rate, "--deinterlace-frames", "all")
            docker(f"ffmpeg -y -v error -nostdin -i {name}.y4m -vf yadif=mode={mode}:parity=tff:deint=all -f rawvideo -pix_fmt yuv420p {theirs}")
            ours_frames = read_y4m(ours)
            theirs_frames = read_raw(os.path.join(OUT, theirs), size)
            # At the frame rate output n is picture 2n, the first field's time; at the field rate output t is picture t.
            truth = pictures[::2] if rate == "frame" else pictures
            woven_frames = read_y4m(woven)
            ours_db = float(np.mean([psnr(o, t) for o, t in zip(ours_frames, truth)]))
            theirs_db = float(np.mean([psnr(o, t) for o, t in zip(theirs_frames, truth)]))
            woven_db = float(np.mean([psnr(woven_frames[i // (1 if rate == "frame" else 2)], t) for i, t in enumerate(truth)]))
            entry[rate] = {"egami": round(ours_db, 2), "yadif": round(theirs_db, 2), "woven": round(woven_db, 2),
                           "frames": [len(ours_frames), len(theirs_frames), len(truth)]}
            ok = ours_db >= theirs_db - GATE_DB and len(ours_frames) == len(truth)
            print(f"{name} {rate}: Egami {ours_db:.2f} dB, yadif {theirs_db:.2f} dB, woven {woven_db:.2f} dB {'ok' if ok else 'FAIL'}")
            if not ok:
                failures.append(f"{name} {rate}")
        results[name] = entry
    return results, failures


def speed():
    name = "fhd-testsrc2"
    size, frames, source = CLIPS[name]
    woven, _ = make(name, size, frames, source)
    base = "ffmpeg -hide_banner -nostats -nostdin -benchmark -threads 1 -filter_threads 1 -i /work/fhd-testsrc2.y4m"

    def rtime(command):
        out = docker(command)
        text = out.stdout + out.stderr
        return float(text.split("rtime=")[1].split("s")[0])

    plain = min(rtime(f"{base} -f null -") for _ in range(5))
    filtered = min(rtime(f"{base} -vf yadif=deint=all -f null -") for _ in range(5))
    yadif_ms = (filtered - plain) * 1000 / frames
    out = subprocess.run(["dotnet", BENCH, "--time-deinterlace", woven], check=True, capture_output=True, text=True).stdout
    print(out.strip())
    egami_ms = float(out.split(" ms a frame")[0].split()[-1])
    ok = egami_ms <= GATE_SPEED * yadif_ms
    print(f"1080i adaptive, one thread: Egami {egami_ms:.2f} ms a frame, yadif {yadif_ms:.2f} ms ({egami_ms / yadif_ms:.2f}x) {'ok' if ok else 'FAIL'}")
    return {"egami_ms": round(egami_ms, 2), "yadif_ms": round(yadif_ms, 2), "ratio": round(egami_ms / yadif_ms, 2)}, [] if ok else ["speed"]


def frames(path, at):
    """A real file's frames from a time as PNGs, for Lars to look at: Egami's from the benchmark project (which decodes H.264
    and HEVC too), yadif's from FFmpeg in Docker."""
    folder = os.path.join(OUT, "look")
    os.makedirs(folder, exist_ok=True)
    stem = os.path.splitext(os.path.basename(path))[0]
    out = subprocess.run(["dotnet", BENCH, "--look-deinterlace", path, f"--at={at}", "--count=6", f"--out={folder}"], check=True, capture_output=True, text=True).stdout
    print(out.strip())
    local = os.path.abspath(path)
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.dirname(local)}:/in:ro", "-v", f"{os.path.abspath(folder)}:/work", IMAGE, "sh", "-c",
                    f"ffmpeg -y -v error -nostdin -ss {at} -i '/in/{os.path.basename(local)}' -vf yadif=deint=all -frames:v 6 -start_number 0 {stem}-yadif-%02d.png"],
                   check=True, env=ENV)
    print(f"wrote {folder}: {stem}-woven, -adaptive, -bob, -adaptive-field and -yadif frames")


def main():
    os.makedirs(OUT, exist_ok=True)
    if "--frames" in sys.argv:
        at = float(sys.argv[sys.argv.index("--at") + 1]) if "--at" in sys.argv else 10.0
        frames(sys.argv[sys.argv.index("--frames") + 1], at)
        return
    results, failures = {}, []
    if "--speed" not in sys.argv:
        results["quality"], failed = quality()
        failures += failed
    if "--quality" not in sys.argv:
        results["speed"], failed = speed()
        failures += failed
    with open(os.path.join(OUT, "results.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump(results, f, indent=1)
    if failures:
        print("FAILED: " + ", ".join(failures))
        sys.exit(1)
    print("all gates passed")


if __name__ == "__main__":
    main()
