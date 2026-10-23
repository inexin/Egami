"""Checks that dav1d and libaom decode the AV1 stream encoder's output exactly as Egami decodes it.

Runs the Av1EncoderInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which writes Egami's AV1 streams to .cache/interop/av1,
each <name>.ivf next to the frames Egami's decoder shows from it (<name>.yuv) and its shape (<name>.txt); then, in Docker
(egami-avifref:2.9.2: dav1d 1.5.4, libaom 3.15.2), checks that dav1d and aomdec decode each .ivf to exactly those frames.

Usage, from the repo root with Docker running:
  python tools/check-av1-encoder.py
"""
import os
import subprocess
import sys

IMAGE = "egami-avifref:2.9.2"
OUT = ".cache/interop/av1"


def frame_sizes(width, height, depth, chroma):
    """The bytes of one frame's luma and of its two chroma planes, as the decoders write raw video."""
    size = 2 if int(depth) > 8 else 1
    w, h = int(width), int(height)
    cw, ch = {"Yuv420": ((w + 1) // 2, (h + 1) // 2), "Yuv422": ((w + 1) // 2, h), "Yuv444": (w, h), "Yuv400": (0, 0)}[chroma]
    return w * h * size, cw * ch * size


def luma_only(data, luma, chroma_420):
    """The luma planes of raw frames that also carry two 4:2:0 chroma planes, as aomdec writes a monochrome stream."""
    step = luma + 2 * chroma_420
    return b"".join(data[i:i + luma] for i in range(0, len(data), step))


def inside():
    failures = []
    checked = 0
    for name in sorted(os.listdir(OUT)):
        if not name.endswith(".ivf"):
            continue
        base = os.path.join(OUT, name[:-4])
        width, height, depth, chroma = open(base + ".txt").read().split()
        expected = open(base + ".yuv", "rb").read()
        luma, _ = frame_sizes(width, height, depth, chroma)
        result = subprocess.run(["dav1d", "-q", "-i", base + ".ivf", "-o", "/tmp/dav1d.yuv"], capture_output=True, text=True)
        if result.returncode != 0:
            failures.append(f"{name}: dav1d cannot decode it: {result.stderr.strip()}")
        elif open("/tmp/dav1d.yuv", "rb").read() != expected:
            failures.append(f"{name}: dav1d's frames differ from Egami's")
        result = subprocess.run(["aomdec", "--rawvideo", "-o", "/tmp/aom.yuv", base + ".ivf"], capture_output=True, text=True)
        if result.returncode != 0:
            failures.append(f"{name}: aomdec cannot decode it: {result.stderr.strip()}")
        else:
            got = open("/tmp/aom.yuv", "rb").read()
            if chroma == "Yuv400" and len(got) != len(expected):
                _, chroma_420 = frame_sizes(width, height, depth, "Yuv420")
                got = luma_only(got, luma, chroma_420)
            if got != expected:
                failures.append(f"{name}: aomdec's frames differ from Egami's ({len(got)} bytes, {len(expected)} expected)")
        checked += 1
    for failure in failures:
        print("FAIL", failure)
    print(f"Checked {checked} streams; {len(failures)} failed.")
    sys.exit(1 if failures else 0)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(".cache/interop"))
    subprocess.run(["dotnet", "build", "tests/Egami.Tests", "-c", "Release", "-v", "q", "-nologo"], check=True)
    exe = os.path.abspath(os.path.join("tests", "Egami.Tests", "bin", "Release", "net10.0", "Egami.Tests"))
    subprocess.run([exe, "-class", "Egami.Tests.Formats.Av1.Av1EncoderInteropFiles"], check=True, env=env)
    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "python3", "tools/check-av1-encoder.py",
                    "--inside"], check=True, env=dict(os.environ, MSYS_NO_PATHCONV="1"))


if __name__ == "__main__":
    main()
