"""Checks that libvpx decodes the VP9 encoder's output exactly as Egami does.

Runs the Vp9EncoderInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which writes streams from Egami's VP9 encoder to
.cache/interop/vp9 (every effort, hidden frames with compound prediction, superframes from the public encoder, keyframes
mid-stream, error resilience, saved probability contexts, lossless and the extreme quantizers, adaptive quantization by
segments, loop filter levels, odd sizes down to 1x1, tile columns, and from 5.3 task 14 on the 10 and 12-bit, 4:2:2 and 4:4:4
profiles and alpha streams), each <name>.ivf next to <name>.yuv, the shown frames Egami's decoder makes of it as packed planes
(16-bit little-endian samples above 8 bits). Then, in Docker (egami-vp9encref:v5.3, libvpx 1.14.1 from
tools/video/vp9encref/Dockerfile), decodes each stream with vpxdec and requires the same bytes.

Usage, from the repo root with Docker running:
  docker build -t egami-vp9encref:v5.3 tools/video/vp9encref
  python tools/video/check-vp9-encoder.py [--no-test]
"""
import os
import subprocess
import sys

IMAGE = "egami-vp9encref:v5.3"
OUT = ".cache/interop/vp9"


def inside():
    failures, checked = [], 0
    for file in sorted(f for f in os.listdir(OUT) if f.endswith(".ivf")):
        name = file[:-4]
        decoded = f"/tmp/{name}.yuv"
        result = subprocess.run(["vpxdec-hbd", "--rawvideo", "--output-bit-depth=0", "-o", decoded, os.path.join(OUT, file)],
                                capture_output=True)
        if result.returncode != 0:
            result = subprocess.run(["vpxdec-hbd", "--rawvideo", "-o", decoded, os.path.join(OUT, file)], capture_output=True)
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
    if "--no-test" not in sys.argv:
        env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(".cache/interop"))
        subprocess.run(["dotnet", "test", "tests/Egami.Video.Tests", "--filter", "FullyQualifiedName~Vp9EncoderInteropFiles"], env=env,
                       check=True)
    root = os.path.abspath(".")
    return subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/work", "-w", "/work", IMAGE,
                           "python3", "tools/video/check-vp9-encoder.py", "--inside"]).returncode


if __name__ == "__main__":
    sys.exit(main())
