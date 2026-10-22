"""Checks that libvpx decodes the VP8 stream encoder's output exactly as Egami does.

Runs the Vp8EncoderInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which writes streams from Vp8StreamEncoder to
.cache/interop/vp8 (every effort, hidden alternate frames, golden frames and reference copies, sign biases, eight token
partitions without probability carry-over, key frames mid-stream, the extreme quantizers, odd sizes), each <name>.ivf next to
<name>.yuv, the shown frames Egami's decoder makes of it as packed I420. Then, in Docker (egami-vp8ref:2.3.5, libvpx 1.14.1 from
tools/vp8ref/Dockerfile), decodes each stream with vpxdec and requires the same bytes.

Usage, from the repo root with Docker running:
  docker build -t egami-vp8ref:2.3.5 tools/vp8ref
  python tools/check-vp8-encoder.py
"""
import os
import subprocess
import sys

IMAGE = "egami-vp8ref:2.3.5"
OUT = ".cache/interop/vp8"


def inside():
    failures, checked = [], 0
    for file in sorted(f for f in os.listdir(OUT) if f.endswith(".ivf")):
        name = file[:-4]
        decoded = f"/tmp/{name}.yuv"
        result = subprocess.run(["vpxdec", "--i420", "--rawvideo", "-o", decoded, os.path.join(OUT, file)], capture_output=True)
        if result.returncode != 0:
            failures.append(f"{name}: vpxdec failed: {result.stderr.decode().strip()}")
            continue
        with open(decoded, "rb") as f:
            theirs = f.read()
        with open(os.path.join(OUT, name + ".yuv"), "rb") as f:
            ours = f.read()
        checked += 1
        if ours != theirs:
            first = next((i for i in range(min(len(ours), len(theirs))) if ours[i] != theirs[i]), min(len(ours), len(theirs)))
            failures.append(f"{name}: {len(theirs)} bytes from vpxdec, {len(ours)} from Egami, first difference at byte {first}")
    for failure in failures:
        print(failure)
    print(f"{checked} streams decoded, {len(failures)} failed")
    return 1 if failures or checked == 0 else 0


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        return inside()
    env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(".cache/interop"))
    subprocess.run(["dotnet", "test", "tests/Egami.Tests", "--filter", "FullyQualifiedName~Vp8EncoderInteropFiles"], env=env, check=True)
    root = os.path.abspath(".")
    return subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/work", "-w", "/work", IMAGE,
                           "python3", "tools/check-vp8-encoder.py", "--inside"]).returncode


if __name__ == "__main__":
    sys.exit(main())
