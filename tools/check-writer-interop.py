"""Checks that other tools read Egami's BMP, TGA, QOI and PNM files, and its interlaced and banded PNG files, to the pixels Egami
reads back.

Runs the WriterInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which writes every writer layout and, next to each
file, Egami's own decode as raw RGBA; then, in Docker (alpine:3.20), reads each file with Pillow, ImageMagick, Netpbm (PNM
only) and qoiconv (QOI only, when .cache/qoi/qoi.h is there) and compares. 16-bit PNM and PNG files may differ by 1 level, since the
tools narrow 16 bits to 8 their own way. Known tool failures are reported, not failed: neither Pillow nor
ImageMagick 7.1 reads 2-bit BMP, Pillow reads no PAM, and Pillow clips 16-bit gray. Egami's own reader covers 2-bit BMP.

Usage, from the repo root with Docker running:
  python tools/check-writer-interop.py
"""
import gzip
import os
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow imagemagick netpbm gcc musl-dev"
OUT = ".cache/interop"

# Readers known not to read a file (neither Pillow nor ImageMagick 7.1 reads 2-bit BMP; Pillow reads no PAM) or to read it
# wrongly (Pillow clips 16-bit gray when converting to RGBA); they are reported but do not fail the check.
KNOWN_FAILURES = {("bmp-2.bmp", "pillow"), ("bmp-2.bmp", "magick"), ("pnm-rgba.pam", "pillow"), ("pnm-gray-alpha.pam", "pillow"),
                  ("pnm-rgb.pam", "pillow"), ("pnm-gray16-plain.pgm", "pillow"), ("pnm-gray16-raw.pgm", "pillow")}


def decode(path, tool):
    from PIL import Image
    if tool == "pillow":
        with Image.open(path) as im:
            return im.convert("RGBA").tobytes()
    if tool == "magick":
        subprocess.run(["magick", path + "[0]", "-depth", "8", "rgba:/tmp/out.rgba"], check=True, capture_output=True)
        return open("/tmp/out.rgba", "rb").read()
    if tool == "netpbm":
        with open(path, "rb") as source, open("/tmp/out.pam", "wb") as out:
            subprocess.run(["pamdepth", "255"], stdin=source, stdout=out, check=True, capture_output=False, stderr=subprocess.DEVNULL)
        with open("/tmp/out.pam", "rb") as source, open("/tmp/out.png", "wb") as out:
            subprocess.run(["pamtopng"], stdin=source, stdout=out, check=True, stderr=subprocess.DEVNULL)
        with Image.open("/tmp/out.png") as im:
            return im.convert("RGBA").tobytes()
    if tool == "qoiconv":
        subprocess.run(["/tmp/qoiconv", path, "/tmp/out.pam"], check=True)
        data = open("/tmp/out.pam", "rb").read()
        return data.partition(b"ENDHDR\n")[2]
    raise ValueError(tool)


def inside():
    if os.path.exists(".cache/qoi/qoi.h"):
        # The same minimal qoiconv the reference script builds.
        source = open("tools/make-simple-references.py").read()
        start = source.index("QOICONV = r'''") + len("QOICONV = r'''")
        with open("/tmp/qoiconv.c", "w") as f:
            f.write(source[start:source.index("'''", start)])
        subprocess.run(["gcc", "-O2", "-I.cache/qoi", "-o", "/tmp/qoiconv", "/tmp/qoiconv.c", "-lm"], check=True)

    failures = 0
    for name in sorted(os.listdir(OUT)):
        if name.endswith(".rgba"):
            continue
        path = os.path.join(OUT, name)
        expected = open(path + ".rgba", "rb").read()
        tools = ["pillow", "magick"]
        if name.startswith("pnm"):
            tools.append("netpbm")
        if name.startswith("qoi") and os.path.exists("/tmp/qoiconv"):
            tools.append("qoiconv")
        tolerance = 1 if "16" in name else 0
        results = []
        for tool in tools:
            try:
                actual = decode(path, tool)
            except Exception:  # Reported below.
                results.append(f"{tool}: unreadable")
                if (name, tool) not in KNOWN_FAILURES:
                    failures += 1
                continue
            if len(actual) != len(expected):
                results.append(f"{tool}: size differs")
                failures += 1
                continue
            largest = max((abs(a - b) for a, b in zip(actual, expected)), default=0)
            ok = largest <= tolerance
            failures += 0 if ok or (name, tool) in KNOWN_FAILURES else 1
            results.append(f"{tool}: {'ok' if ok else 'differs, known' if (name, tool) in KNOWN_FAILURES else 'DIFFERS'} (largest {largest})")
        print(f"{name:24} " + ", ".join(results))
    print("all readers agree" if failures == 0 else f"{failures} failures")
    sys.exit(1 if failures else 0)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(OUT))
    subprocess.run(["dotnet", "test", "tests/Egami.Tests", "-c", "Release", "--filter", "FullyQualifiedName~WriterInteropFiles"],
                   check=True, env=env, stdout=subprocess.DEVNULL)
    repo = os.getcwd()
    result = subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                             f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/check-writer-interop.py --inside"])
    sys.exit(result.returncode)


if __name__ == "__main__":
    main()
