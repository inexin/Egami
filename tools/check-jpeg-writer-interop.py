"""Checks the Phase 2.4.3 JPEG writer against libjpeg-turbo, ImageMagick and Pillow: CMYK, YCCK, 12-bit and progressive files
decode in each to within 1 level of what Egami reads back, and progressive files are within 1 % of cjpeg -progressive's size at
the same quality and sampling.

Runs the JpegWriterInteropFiles test with EGAMI_INTEROP_DIR=.cache/jpeg-interop, which writes the files and, next to each,
Egami's decode as 8-bit RGB (.rgb), inks (.cmyk) and 12-bit samples scaled to 16 bits (.wide); then, in Docker (alpine:3.20,
libjpeg-turbo 3.0, ImageMagick 7.1, Pillow), compares:
  djpeg -pnm       8-bit RGB or gray against .rgb; 12-bit files (maxval 4095) against .wide narrowed to 12 bits. djpeg turns
                   CMYK into RGB with the same plain formula Egami uses.
  magick           inks (cmyk:) for CMYK and YCCK files, RGB otherwise; 16-bit output for 12-bit files
  Pillow           inks for CMYK and YCCK (Pillow inverts Adobe CMYK as Photoshop expects), RGB or L otherwise
Known reader gaps are reported, not failed: Pillow reads no 12-bit JPEG, and Alpine's ImageMagick is built without it.

Usage, from the repo root with Docker running:
  python tools/check-jpeg-writer-interop.py
"""
import json
import os
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow imagemagick libjpeg-turbo-utils"
OUT = ".cache/jpeg-interop"

KNOWN = {"pillow-12": "Pillow reads no 12-bit JPEG", "magick-12": "Alpine's ImageMagick has no 12-bit JPEG"}


def parse_pnm(data):
    fields = []
    i = 0
    while len(fields) < 4:
        while data[i:i + 1].isspace():
            i += 1
        if data[i:i + 1] == b"#":
            while data[i:i + 1] != b"\n":
                i += 1
            continue
        j = i
        while not data[j:j + 1].isspace():
            j += 1
        fields.append(data[i:j])
        i = j
    return fields[0], int(fields[3]), data[i + 1:]


def samples(data, wide, big_endian):
    if not wide:
        return list(data)
    order = "big" if big_endian else "little"
    return [int.from_bytes(data[k:k + 2], order) for k in range(0, len(data), 2)]


def worst(a, b):
    if len(a) != len(b):
        return None
    return max((abs(x - y) for x, y in zip(a, b)), default=0)


def gray_to_rgb(values):
    return [v for v in values for _ in range(3)]


def check_file(name):
    path = os.path.join(OUT, name)
    twelve = name.startswith("p12-")
    cmyk = os.path.exists(path + ".cmyk")
    rgb = list(open(path + ".rgb", "rb").read())
    inks = list(open(path + ".cmyk", "rb").read()) if cmyk else None
    wide = samples(open(path + ".wide", "rb").read(), True, False) if twelve else None
    narrowed = [(v * 4095 + 32767) // 65535 for v in wide] if twelve else None
    results = []
    failures = 0

    # djpeg
    out = subprocess.run(["djpeg", "-pnm", path], capture_output=True)
    if out.returncode != 0:
        results.append("djpeg: unreadable")
        failures += 1
    else:
        magic, maxval, pixels = parse_pnm(out.stdout)
        values = samples(pixels, maxval > 255, True)
        if twelve and not cmyk:
            expected = narrowed
        elif twelve:
            # djpeg's CMYK to RGB at 12 bits; Egami's 8-bit RGB is the same formula, so compare at 8 bits.
            values = [(v * 255 + 2047) // 4095 for v in values]
            expected = rgb
        else:
            expected = rgb
        if magic == b"P5":
            values = gray_to_rgb(values) if not twelve else values
        d = worst(values, expected)
        ok = d is not None and d <= 1
        failures += 0 if ok else 1
        results.append(f"djpeg {'ok' if ok else 'DIFFERS'} ({d})")

    # ImageMagick
    if cmyk:
        args, expected, depth = ["-depth", "8", "cmyk:/tmp/m.raw"], inks, 8
    elif twelve:
        args, expected, depth = ["-depth", "16", "-endian", "LSB", ("gray:" if "gray" in name else "rgb:") + "/tmp/m.raw"], wide, 16
    else:
        args, expected, depth = ["-depth", "8", "rgb:/tmp/m.raw"], rgb, 8
    out = subprocess.run(["magick", path] + args, capture_output=True, text=True)
    if out.returncode != 0 and twelve and "precision 12" in out.stderr:
        results.append("magick: " + KNOWN["magick-12"] + " (known)")
    elif out.returncode != 0:
        results.append("magick: unreadable " + out.stderr.strip()[:120])
        failures += 1
    else:
        values = samples(open("/tmp/m.raw", "rb").read(), depth == 16, False)
        d = worst(values, expected)
        limit = 17 if depth == 16 else 1
        ok = d is not None and d <= limit
        failures += 0 if ok else 1
        results.append(f"magick {'ok' if ok else 'DIFFERS'} ({d})")

    # Pillow
    from PIL import Image
    try:
        with Image.open(path) as im:
            im.load()
            if cmyk:
                values, expected = list(im.convert("CMYK").tobytes()), inks
            else:
                values, expected = list(im.convert("RGB").tobytes()), rgb
        d = worst(values, expected)
        ok = d is not None and d <= 1
        failures += 0 if ok else 1
        results.append(f"pillow {'ok' if ok else 'DIFFERS'} ({d})")
    except Exception:
        if twelve:
            results.append("pillow: " + KNOWN["pillow-12"] + " (known)")
        else:
            results.append("pillow: unreadable")
            failures += 1
    return failures, results


def check_size(name):
    path = os.path.join(OUT, name)
    settings = json.load(open(path[:-4] + ".json"))
    args = ["cjpeg", "-progressive", "-quality", str(settings["quality"]), "-sample", settings["sample"]]
    if settings["gray"]:
        args.append("-grayscale")
    theirs = subprocess.run(args + [os.path.join(OUT, settings["source"])], capture_output=True, check=True).stdout
    mine = open(path, "rb").read()
    ratio = len(mine) / len(theirs)
    ok = ratio <= 1.01
    same = " (same bytes)" if mine == theirs else ""
    return (0 if ok else 1), f"size {len(mine):,} vs cjpeg -progressive {len(theirs):,} ({(ratio - 1) * 100:+.2f} %){same} {'ok' if ok else 'TOO LARGE'}"


def inside():
    failures = 0
    for name in sorted(f for f in os.listdir(OUT) if f.endswith(".jpg")):
        bad, results = check_file(name)
        if name.startswith("prog-"):
            more, text = check_size(name)
            bad += more
            results.append(text)
        failures += bad
        print(f"{name:26} " + ", ".join(results))
    print("all readers agree" if failures == 0 else f"{failures} failures")
    sys.exit(1 if failures else 0)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return
    env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(OUT))
    subprocess.run(["dotnet", "test", "tests/Egami.Tests", "-c", "Release", "--filter", "FullyQualifiedName~JpegWriterInteropFiles"],
                   check=True, env=env, stdout=subprocess.DEVNULL)
    repo = os.getcwd()
    result = subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                             f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/check-jpeg-writer-interop.py --inside"])
    sys.exit(result.returncode)


if __name__ == "__main__":
    main()
