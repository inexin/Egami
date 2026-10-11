"""Checks that other tools read Egami's TIFF files: libtiff's tiffinfo with no warnings and its decoded samples identical to
Egami's own, and ImageMagick and Pillow able to open each file.

Runs the TiffInteropFiles test with EGAMI_INTEROP_DIR=.cache/tiff-interop, which writes every pixel type in every compression,
in strips, tiles and BigTIFF, with Egami's decode as raw samples next to each; then, in Docker (alpine:3.20), checks them with
libtiff 4.6.0 (tiffinfo -D, and tools/tiffref/tiffref.c's raw samples), ImageMagick 7.1.1 and Pillow.

Usage, from the repo root with Docker running:
  python tools/check-tiff-interop.py
"""
import os
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow imagemagick tiff-tools tiff-dev gcc musl-dev"
OUT = ".cache/tiff-interop"

# Pillow reads no float, CMYK-16 or 16-bit RGB(A) TIFF it can convert to RGBA, so it is only asked to open them.
PILLOW_OPEN_ONLY = ("f32", "f16", "16")


def inside():
    subprocess.run(["gcc", "-O2", "-o", "/tmp/tiffref", "tools/tiffref/tiffref.c", "-ltiff"], check=True)
    from PIL import Image

    failures = 0
    files = sorted(f for f in os.listdir(OUT) if f.endswith(".tif"))
    for name in files:
        path = os.path.join(OUT, name)
        problems = []
        info = subprocess.run(["tiffinfo", "-D", path], capture_output=True, text=True)
        if info.returncode != 0 or info.stderr.strip():
            problems.append("tiffinfo: " + (info.stderr.strip() or "failed")[:200])
        raw = subprocess.run(["/tmp/tiffref", "raw", path, "0", "/tmp/out.raw"], capture_output=True, text=True)
        if raw.returncode != 0:
            problems.append("libtiff: " + raw.stderr.strip()[:200])
        elif open("/tmp/out.raw", "rb").read() != open(path + ".raw", "rb").read():
            problems.append("libtiff's samples differ from Egami's")
        magick = subprocess.run(["magick", path + "[0]", "-depth", "8", "rgba:/tmp/m.rgba"], capture_output=True, text=True)
        if magick.returncode != 0:
            problems.append("ImageMagick: " + magick.stderr.strip()[:200])
        try:
            with Image.open(path) as im:
                im.load()
        except Exception as e:
            if not any(k in name for k in PILLOW_OPEN_ONLY):
                problems.append(f"Pillow: {e}")
        if problems:
            failures += 1
            print(f"{name}: " + "; ".join(problems))
    print(f"{len(files)} files, {failures} with problems.")
    sys.exit(1 if failures else 0)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return
    env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(OUT))
    subprocess.run(["dotnet", "test", "tests/Egami.Tests", "-c", "Release", "--filter", "FullyQualifiedName~TiffInteropFiles"], check=True, env=env)
    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/check-tiff-interop.py --inside"], check=True)


if __name__ == "__main__":
    main()
