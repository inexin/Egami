"""The Phase 2.9.3 speed gate (design question 16): Egami's JPEG XL decode time within 2 times djxl's (libjxl 0.12.0, frames
not written), threads matched: one thread, and all cores.

djxl runs in the egami-jxlref:2.9.3 image and Egami's decoder in the benchmark project (dotnet run -c Release --project
benchmarks/Egami.Benchmarks -- --time-jxl). Both are timed in rounds that alternate between them, best of five within a round,
so a busy machine slows both alike; the best of the rounds counts. The files are the corpus's largest of each kind and, when
.cache/jxl holds the conformance suite, its large lossy and recompressed-JPEG cases.

Usage, from the repo root with Docker running and the benchmarks built in Release:
  python tools/time-jxl-gate.py [rounds]
"""
import os
import re
import subprocess
import sys

IMAGE = "egami-jxlref:2.9.3"
GATE = 2.0
FILES = [
    "tests/images/jxl/duck-lossless-e7.jxl",
    "tests/images/jxl/duck-d1.jxl",
    "tests/images/jxl/egret-d1.jxl",
    "tests/images/jxl/jpeg-egret-420.jxl",
    "tests/images/jxl/duck-16bit-lossless.jxl",
    ".cache/jxl/conformance/testcases/bike/input.jxl",
    ".cache/jxl/conformance/testcases/progressive/input.jxl",
    ".cache/jxl/conformance/testcases/cafe/input.jxl",
    ".cache/jxl/conformance/testcases/lz77_flower/input.jxl",
]
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")


def djxl(path, threads):
    # --num_threads=0 decodes on the calling thread alone.
    # .cache may be a link to another checkout's, which Docker does not follow: mount its target too.
    cache = ["-v", f"{os.path.realpath('.cache/jxl')}:/work/.cache/jxl"] if os.path.isdir(".cache/jxl") else []
    out = subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/work", *cache, "-w", "/work", IMAGE, "djxl", path, "--disable_output",
                          "--num_reps=5", f"--num_threads={0 if threads == 1 else threads}"], capture_output=True, text=True, env=ENV,
                         check=True)
    text = out.stdout + out.stderr
    size = re.search(r"(\d+) x (\d+)", text)
    rates = [float(v) for v in re.findall(r"([\d.]+) MP/s", text)]
    bracket = re.search(r"\[([\d.]+), ([\d.]+)\]", text)
    best = float(bracket.group(2)) if bracket else max(rates)
    return int(size.group(1)) * int(size.group(2)) / 1e6 / best * 1000


def egami(files):
    out = subprocess.run(["dotnet", "run", "-c", "Release", "--no-build", "--project", "benchmarks/Egami.Benchmarks", "--", "--time-jxl",
                          *files], capture_output=True, text=True, check=True).stdout
    times = {}
    for line in out.splitlines():
        match = re.search(r"^(.+): ([\d.,]+) ms an image on (\d+) thread", line)
        if match:
            times.setdefault(match.group(1), {})[int(match.group(3))] = float(match.group(2).replace(",", "."))
    return times


def main():
    rounds = int(sys.argv[1]) if len(sys.argv) > 1 else 3
    cores = os.cpu_count()
    files = [f for f in FILES if os.path.exists(f)]
    best = {}
    for _ in range(rounds):
        ours = egami(files)
        for path in files:
            mine = ours[path]
            for threads in (1, cores):
                theirs = djxl(path, threads)
                key = (path, threads)
                old = best.get(key, (float("inf"), float("inf")))
                best[key] = (min(old[0], mine[threads]), min(old[1], theirs))

    passed = True
    for (path, threads), (ours, theirs) in best.items():
        ratio = ours / theirs
        passed &= ratio <= GATE
        name = path.replace(".cache/jxl/conformance/testcases/", "conformance/").replace("tests/images/jxl/", "")
        print(f"{name:34} {threads:2} thread(s): Egami {ours:8.1f} ms, djxl {theirs:8.1f} ms, {ratio:5.2f}x {'pass' if ratio <= GATE else 'FAIL'}")
    print("gate", "passes" if passed else "fails")


if __name__ == "__main__":
    main()
