"""Checks that dav1d and libavif decode Egami's AVIF files to what Egami decodes them to.

Runs the AvifInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which writes Egami's AVIF files to .cache/interop/avif, each
<name>.avif next to its color item's AV1 stream (<name>.obu), the planes Egami decodes from it (<name>.yuv) and the RGBA Egami
loads (<name>.rgba, size and depth in <name>.txt); then, in Docker (egami-avifref:2.9.2: dav1d 1.5.4, libavif 1.4.2), checks:
  - dav1d decodes each .obu to exactly Egami's planes (the AV1 encoder writes what dav1d reads);
  - avifdec decodes each .avif to exactly Egami's RGBA (the container is read alike, and Egami's AVIF reader converts as
    libavif does), and avifitems lists its items without complaint;
  - avifgainmaputil applies the gain map of each file named gainmap.

Usage, from the repo root with Docker running:
  python tools/check-avif-interop.py
"""
import os
import struct
import subprocess
import sys
import zlib

IMAGE = "egami-avifref:2.9.2"
OUT = ".cache/interop/avif"


def read_png(path):
    """A small PNG reader: non-interlaced gray, gray-alpha, RGB and RGBA at 8 or 16 bits, as RGBA bytes (big-endian at 16)."""
    data = open(path, "rb").read()
    at = 8
    idat = b""
    while at < len(data):
        length, kind = struct.unpack(">I4s", data[at:at + 8])
        body = data[at + 8:at + 8 + length]
        if kind == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", body)
            assert interlace == 0
        elif kind == b"IDAT":
            idat += body
        at += 12 + length
    channels = {0: 1, 2: 3, 4: 2, 6: 4}[color]
    bpp = channels * depth // 8
    stride = width * bpp
    raw = zlib.decompress(idat)
    rows = []
    previous = bytearray(stride)
    for y in range(height):
        f = raw[y * (stride + 1)]
        line = bytearray(raw[y * (stride + 1) + 1:(y + 1) * (stride + 1)])
        for i in range(stride):
            a = line[i - bpp] if i >= bpp else 0
            b = previous[i]
            c = previous[i - bpp] if i >= bpp else 0
            if f == 1:
                line[i] = (line[i] + a) & 255
            elif f == 2:
                line[i] = (line[i] + b) & 255
            elif f == 3:
                line[i] = (line[i] + ((a + b) >> 1)) & 255
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(bytes(line))
        previous = line
    size = depth // 8
    opaque = b"\xff" * size
    out = bytearray()
    for line in rows:
        for x in range(width):
            px = line[x * bpp:(x + 1) * bpp]
            s = [px[i * size:(i + 1) * size] for i in range(channels)]
            if channels == 1:
                out += s[0] * 3 + opaque
            elif channels == 2:
                out += s[0] * 3 + s[1]
            elif channels == 3:
                out += s[0] + s[1] + s[2] + opaque
            else:
                out += s[0] + s[1] + s[2] + s[3]
    return width, height, bytes(out)


def inside():
    failures = []
    checked = 0
    for name in sorted(os.listdir(OUT)):
        if not name.endswith(".avif"):
            continue
        base = os.path.join(OUT, name[:-5])
        width, height, depth, kind = open(base + ".txt").read().split()
        # dav1d's section 5 demuxer splits temporal units at temporal delimiters, which an AVIF item has none of.
        with open("/tmp/in.obu", "wb") as f:
            f.write(bytes([0x12, 0x00]) + open(base + ".obu", "rb").read())
        result = subprocess.run(["dav1d", "-q", "-i", "/tmp/in.obu", "--demuxer", "section5", "-o", "/tmp/out.yuv"],
                                capture_output=True, text=True)
        if result.returncode != 0:
            failures.append(f"{name}: dav1d cannot decode it: {result.stderr.strip()}")
            continue
        if open("/tmp/out.yuv", "rb").read() != open(base + ".yuv", "rb").read():
            failures.append(f"{name}: dav1d's planes differ from Egami's")
            continue
        result = subprocess.run(["avifdec", "-d", depth, base + ".avif", "/tmp/out.png"], capture_output=True, text=True)
        if result.returncode != 0:
            failures.append(f"{name}: avifdec cannot decode it: {result.stdout.strip()} {result.stderr.strip()}")
            continue
        w, h, rgba = read_png("/tmp/out.png")
        expected = open(base + ".rgba", "rb").read()
        if (w, h) != (int(width), int(height)):
            failures.append(f"{name}: avifdec gives {w} x {h}, not {width} x {height}")
        elif rgba != expected:
            first = next(i for i in range(len(rgba)) if rgba[i] != expected[i])
            failures.append(f"{name}: avifdec's pixels differ from Egami's at byte {first}")
        result = subprocess.run(["avifitems", base + ".avif"], capture_output=True, text=True)
        if result.returncode != 0:
            failures.append(f"{name}: avifitems fails: {result.stdout.strip()} {result.stderr.strip()}")
        if "gainmap" in name:
            # libavif finds the gain map, reads its metadata and applies it.
            result = subprocess.run(["avifgainmaputil", "tonemap", "--headroom", "2", base + ".avif", "/tmp/tonemapped.png"],
                                    capture_output=True, text=True)
            if result.returncode != 0:
                failures.append(f"{name}: avifgainmaputil cannot apply its gain map: {result.stdout.strip()} {result.stderr.strip()}")
        checked += 1
    for failure in failures:
        print("FAIL", failure)
    print(f"Checked {checked} files; {len(failures)} failed.")
    sys.exit(1 if failures else 0)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(".cache/interop"))
    subprocess.run(["dotnet", "build", "tests/Egami.Tests", "-c", "Release", "-v", "q", "-nologo"], check=True)
    exe = os.path.abspath(os.path.join("tests", "Egami.Tests", "bin", "Release", "net10.0", "Egami.Tests"))
    subprocess.run([exe, "-class", "Egami.Tests.Formats.Avif.AvifInteropFiles"], check=True, env=env)
    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "python3", "tools/check-avif-interop.py",
                    "--inside"], check=True, env=dict(os.environ, MSYS_NO_PATHCONV="1"))


if __name__ == "__main__":
    main()
