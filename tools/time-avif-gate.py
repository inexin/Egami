"""The Phase 2.9.2 speed gate (design question 18): Egami's AVIF load (AV1 decode and YUV to RGB) within 2 times libavif's with
dav1d, threads matched: one thread, and all cores.

libavif runs in the egami-avifref:2.9.2 image (tools/avifref/avifbench.c: avifDecoderReadMemory and avifImageYUVToRGB, from
memory, best of ten after three warm-up runs) and Egami in the benchmark project (dotnet run -c Release --project
benchmarks/Egami.Benchmarks -- --time-avif). Both are timed in rounds that alternate between them, so a busy machine slows both
alike; the best of the rounds counts. The files are the largest corpus images and, made here once into .cache/avif/speed, the
benchmark photo (4000 x 3000) encoded by avifenc at 8 and 10 bits, 4:2:0 and 4:4:4, with tiles, and with film grain.

Usage, from the repo root with Docker running and the benchmarks built in Release:
  python tools/time-avif-gate.py [rounds]
"""
import os
import re
import subprocess
import sys

IMAGE = "egami-avifref:2.9.2"
GATE = 2.0
SPEED = ".cache/avif/speed"
ENCODES = {
    # name: avifenc options (the photo is 4000 x 3000, 8-bit RGBA without transparency)
    "photo-420-q60.avif": ["-d", "8", "-y", "420", "-q", "60", "-s", "6", "--ignore-icc"],
    "photo-420-10bit-tiles.avif": ["-d", "10", "-y", "420", "-q", "60", "-s", "6", "--autotiling"],
    "photo-444-q80.avif": ["-d", "8", "-y", "444", "-q", "80", "-s", "6"],
    "photo-420-grain.avif": ["-d", "8", "-y", "420", "-q", "50", "-s", "6", "-a", "denoise-noise-level=20"],
}
FILES = [
    "tests/images/avif/duck-420-q60.avif",
    "tests/images/avif/camera-nikon-d300-rec2020-12bit.avif",
    *[f"{SPEED}/{name}" for name in ENCODES],
]
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")


def docker(*args):
    # .cache/avif may be a link to another checkout's, which Docker does not follow: mount its target too.
    cache = ["-v", f"{os.path.realpath('.cache/avif')}:/work/.cache/avif"]
    return subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/work", *cache, "-w", "/work", IMAGE, *args],
                          capture_output=True, text=True, env=ENV, check=True).stdout


def make_files():
    os.makedirs(SPEED, exist_ok=True)
    photo = f"{SPEED}/photo.png"
    if not os.path.exists(photo):
        subprocess.run(["dotnet", "run", "-c", "Release", "--no-build", "--project", "benchmarks/Egami.Benchmarks", "--", "--write-photo",
                        photo], check=True)
    for name, options in ENCODES.items():
        if not os.path.exists(f"{SPEED}/{name}"):
            docker("avifenc", "-j", "all", *options, photo, f"{SPEED}/{name}")
            print("made", name, flush=True)


def libavif(path, threads):
    return float(re.search(r": ([\d.]+) ms", docker("avifbench", path, str(threads))).group(1))


def egami(files):
    out = subprocess.run(["dotnet", "run", "-c", "Release", "--no-build", "--project", "benchmarks/Egami.Benchmarks", "--", "--time-avif",
                          *files], capture_output=True, text=True, check=True).stdout
    times = {}
    for line in out.splitlines():
        match = re.search(r"^(.+): ([\d.,]+) ms an image on (\d+) thread", line)
        if match:
            times.setdefault(match.group(1), {})[int(match.group(3))] = float(match.group(2).replace(",", "."))
    return times


def main():
    rounds = int(sys.argv[1]) if len(sys.argv) > 1 else 3
    make_files()
    cores = os.cpu_count()
    files = [f for f in FILES if os.path.exists(f)]
    best = {}
    for _ in range(rounds):
        ours = egami(files)
        for path in files:
            for threads in (1, cores):
                theirs = libavif(path, threads)
                key = (path, threads)
                old = best.get(key, (float("inf"), float("inf")))
                best[key] = (min(old[0], ours[path][threads]), min(old[1], theirs))

    passed = True
    for (path, threads), (ours, theirs) in best.items():
        ratio = ours / theirs
        passed &= ratio <= GATE
        name = os.path.basename(path)
        print(f"{name:40} {threads:2} thread(s): Egami {ours:8.1f} ms, libavif {theirs:8.1f} ms, {ratio:5.2f}x {'pass' if ratio <= GATE else 'FAIL'}")
    print("gate", "passes" if passed else "fails")


if __name__ == "__main__":
    main()
