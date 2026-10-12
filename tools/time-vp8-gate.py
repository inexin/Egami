"""The Phase 2.3.5 speed gate (V2 design Q9): Egami's VP8 decode time per packet within 1.5 times libvpx's, at one thread and at
all cores, on the 720p and 1080p clips that tools/make-vp8-references.py --large makes.

vpxdec (libvpx 1.14.1's SIMD build, frames not written) runs in the egami-vp8ref Docker image and Egami's decoder in the
benchmark project (dotnet run -c Release --project benchmarks/Egami.Benchmarks -- --time-vp8). Both are timed in rounds that
alternate between them, best of five within a round, so a busy machine slows both alike; the best of the rounds counts.

Usage, from the repo root with Docker running and the benchmarks built in Release:
  python tools/time-vp8-gate.py [rounds]
"""
import os
import re
import subprocess
import sys

IMAGE = "egami-vp8ref:2.3.5"
CLIPS = ["720p", "1080p", "1080p-1part"]
LARGE = ".cache/vp8/large"
GATE = 1.5


def vpxdec(clip, threads):
    best = None
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    for _ in range(5):
        out = subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/work", "-w", "/work", IMAGE, "vpxdec-fast", "--noblit",
                              "--summary", "-t", str(threads), f"{LARGE}/{clip}.ivf"], capture_output=True, text=True, env=env, check=True)
        match = re.search(r"(\d+) decoded frames/\d+ showed frames in (\d+) us", out.stdout + out.stderr)
        ms = int(match.group(2)) / 1000 / int(match.group(1))
        best = ms if best is None else min(best, ms)
    return best


def egami(clip):
    out = subprocess.run(["dotnet", "run", "-c", "Release", "--no-build", "--project", "benchmarks/Egami.Benchmarks", "--", "--time-vp8",
                          f"{LARGE}/{clip}.ivf"], capture_output=True, text=True, check=True).stdout
    times = {}
    for line in out.splitlines():
        match = re.search(r": ([\d.,]+) ms a packet on (\d+) thread", line)
        if match:
            times[int(match.group(2))] = float(match.group(1).replace(",", "."))
    return times


def main():
    rounds = int(sys.argv[1]) if len(sys.argv) > 1 else 3
    cores = os.cpu_count()
    best = {}
    for _ in range(rounds):
        for clip in CLIPS:
            ours = egami(clip)
            for threads in (1, cores):
                theirs = vpxdec(clip, threads)
                key = (clip, threads)
                old = best.get(key, (float("inf"), float("inf")))
                best[key] = (min(old[0], ours[threads]), min(old[1], theirs))

    passed = True
    for (clip, threads), (ours, theirs) in best.items():
        ratio = ours / theirs
        passed &= ratio <= GATE
        print(f"{clip:12} {threads:2} thread(s): Egami {ours:6.2f} ms, libvpx {theirs:6.2f} ms, {ratio:4.2f}x {'pass' if ratio <= GATE else 'FAIL'}")
    print("gate", "passes" if passed else "fails")


if __name__ == "__main__":
    main()
