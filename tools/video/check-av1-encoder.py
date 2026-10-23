"""Checks that dav1d and libaom decode Egami.Video's AV1 encoder output exactly as Egami does.

Runs the Av1EncoderInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which writes IVF files from Egami.Video's AV1 encoder
through VideoWriter to .cache/interop/av1video (every effort, the hidden alternate references and their pyramid shown with
show_existing_frame, real time, the bitrate modes, keyframes at scene cuts and the interval, the extreme quantizers, error
resilience, odd sizes down to 1x1, a frame wide enough for several tiles, Main, High and Professional profiles at 8, 10 and 12
bits, and HDR metadata OBUs), each <name>.ivf next to <name>.yuv, the shown frames Egami's decoder makes of it as planar samples
(16-bit little-endian above 8 bits), and <name>.txt, its width, height, bit depth and chroma format. Each stream is also encoded
at one thread and must give the same bytes. Then, in Docker (egami-avifref:2.9.2 from tools/avifref/Dockerfile: dav1d 1.5.4 and
libaom 3.15.2), decodes each stream with dav1d and aomdec and requires the same bytes.

Usage, from the repo root with Docker running:
  python tools/video/check-av1-encoder.py [--no-test] [--only name,name]
"""
import os
import subprocess
import sys

IMAGE = "egami-avifref:2.9.2"
OUT = ".cache/interop/av1video"


def inside():
    failures, checked = [], 0
    for file in sorted(f for f in os.listdir(OUT) if f.endswith(".ivf")):
        name = file[:-4]
        base = os.path.join(OUT, name)
        with open(base + ".yuv", "rb") as f:
            expected = f.read()
        for decoder, command in (("dav1d", ["dav1d", "-q", "-i", base + ".ivf", "-o", "/tmp/out.yuv"]),
                                 ("aomdec", ["aomdec", "--rawvideo", "-o", "/tmp/out.yuv", base + ".ivf"])):
            result = subprocess.run(command, capture_output=True, text=True)
            if result.returncode != 0:
                failures.append(f"{name}: {decoder} cannot decode it: {result.stderr.strip()}")
                continue
            with open("/tmp/out.yuv", "rb") as f:
                got = f.read()
            if got != expected:
                first = next((i for i in range(min(len(got), len(expected))) if got[i] != expected[i]), min(len(got), len(expected)))
                failures.append(f"{name}: {decoder} gives {len(got)} bytes, Egami {len(expected)}; first difference at byte {first}")
        checked += 1
    for failure in failures:
        print("FAIL", failure)
    print(f"Checked {checked} streams with dav1d and aomdec; {len(failures)} failed.")
    return 1 if failures or checked == 0 else 0


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        return inside()
    if "--no-test" not in sys.argv:
        env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(".cache/interop"))
        if "--only" in sys.argv:
            env["EGAMI_INTEROP_ONLY"] = sys.argv[sys.argv.index("--only") + 1]
        subprocess.run(["dotnet", "test", "tests/Egami.Video.Tests", "-c", "Release", "--filter",
                        "FullyQualifiedName~Av1EncoderInteropFiles"], env=env, check=True)
    root = os.path.abspath(".")
    return subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/work", "-w", "/work", IMAGE, "python3",
                           "tools/video/check-av1-encoder.py", "--inside"], env=dict(os.environ, MSYS_NO_PATHCONV="1")).returncode


if __name__ == "__main__":
    sys.exit(main())
