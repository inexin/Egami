"""Checks the Phase 2.4.3 JPEG writer against libjpeg-turbo, ImageMagick and Pillow: CMYK, YCCK, 12-bit and progressive files
decode in each to within 1 level of what Egami reads back, and progressive files are within 1 % of cjpeg -progressive's size at
the same quality and sampling. Arithmetic-coded files (ac-, SOF9 and SOF10) decode in djpeg to exactly the pixels of their
Huffman twin (.huffman, written with the same options), as well as within 1 level of Egami's decode of that twin, and the size
cases are within 0.5 % of cjpeg -arithmetic at the same settings. Lossless files (ll-, SOF3) decode in djpeg to exactly their
samples (.src.pnm, low point-transform bits cleared) and are within 1 % of cjpeg -lossless at the same predictor, point
transform, precision and restart interval; arithmetic lossless files (lla-, SOF11, which libjpeg-turbo cannot read) decode
exactly in Thomas Richter's libjpeg, run as a binary in the egami-jpeg-depth image of make-jpeg-depth-references.py.

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

KNOWN = {"djpeg-precision": "libjpeg-turbo 3.0 reads 8, 12 and 16-bit lossless only; checked in Richter's libjpeg instead",
         "richter-pt": "Richter's libjpeg starts the prediction at 2^(P-1), not T.81's 2^(P-Pt-1), with a point transform; "
                       "checked against its decode of the file's SOF3 twin instead",
         "pillow-12": "Pillow reads no 12-bit JPEG", "magick-12": "Alpine's ImageMagick has no 12-bit JPEG",
         "pillow-ac": "Pillow fails on some arithmetic files, cjpeg -arithmetic's 4:4:4 ones too"}


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
    twelve = "p12-" in name
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
        if os.path.exists(path + ".huffman"):
            twin = subprocess.run(["djpeg", "-pnm", path + ".huffman"], capture_output=True)
            same = twin.returncode == 0 and twin.stdout == out.stdout
            failures += 0 if same else 1
            results.append("same pixels as Huffman twin" if same else "DIFFERS FROM HUFFMAN TWIN")

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
        elif name.startswith("ac-"):
            results.append("pillow: " + KNOWN["pillow-ac"] + " (known)")
        else:
            results.append("pillow: unreadable")
            failures += 1
    return failures, results


def check_size(name):
    path = os.path.join(OUT, name)
    settings = json.load(open(path[:-4] + ".json"))
    modes = (["-arithmetic"] if settings["arithmetic"] else []) + (["-progressive"] if settings["progressive"] else [])
    args = ["cjpeg"] + modes + ["-quality", str(settings["quality"]), "-sample", settings["sample"]]
    if settings["restart"]:
        args += ["-restart", f"{settings['restart']}b"]
    if settings["gray"]:
        args.append("-grayscale")
    theirs = subprocess.run(args + [os.path.join(OUT, settings["source"])], capture_output=True, check=True).stdout
    mine = open(path, "rb").read()
    ratio = len(mine) / len(theirs)
    ok = ratio <= 1 + settings["limit"] / 100
    same = " (same bytes)" if mine == theirs else ""
    return (0 if ok else 1), f"size {len(mine):,} vs cjpeg {' '.join(modes)} {len(theirs):,} ({(ratio - 1) * 100:+.2f} %){same} {'ok' if ok else 'TOO LARGE'}"


def expected_samples(name, pt):
    magic, maxval, pixels = parse_pnm(open(os.path.join(OUT, name[:-4] + ".src.pnm"), "rb").read())
    return [(v >> pt) << pt for v in samples(pixels, maxval > 255, True)], maxval


def check_lossless(name, decoder):
    path = os.path.join(OUT, name)
    settings = json.load(open(path[:-4] + ".json"))
    expected, maxval = expected_samples(name, settings["pt"])
    if decoder == "djpeg" and settings["precision"] not in (8, 12, 16):
        return 0, ["djpeg: " + KNOWN["djpeg-precision"] + " (known)"]
    if decoder == "djpeg":
        out = subprocess.run(["djpeg", "-pnm", path], capture_output=True)
        data = out.stdout if out.returncode == 0 else None
    else:
        out = subprocess.run(["/src/libjpeg/jpeg", path, "/tmp/lossless.pnm"], capture_output=True)
        data = open("/tmp/lossless.pnm", "rb").read() if out.returncode == 0 else None
    if data is None:
        return 1, [f"{decoder}: unreadable " + out.stderr.decode(errors="replace").strip()[:120]]
    magic, theirs_max, pixels = parse_pnm(data)
    values = samples(pixels, theirs_max > 255, True)
    if decoder == "richter" and settings["pt"] > 0:
        twin = subprocess.run(["/src/libjpeg/jpeg", path[:-4] + ".sof3", "/tmp/twin.pnm"], capture_output=True)
        same = twin.returncode == 0 and open("/tmp/twin.pnm", "rb").read() == data
        return (0 if same else 1), ["richter: " + KNOWN["richter-pt"] + " (known); " + ("same as the SOF3 twin" if same else "DIFFERS FROM THE SOF3 TWIN")]
    same = values == expected and theirs_max == maxval
    results = [f"{decoder} {'exact' if same else 'DIFFERS'} ({settings['precision']} bits, maxval {theirs_max})"]
    failures = 0 if same else 1
    if decoder == "djpeg" and settings["predictor"] is not None:
        args = ["cjpeg", "-lossless", f"{settings['predictor']},{settings['pt']}", "-precision", str(settings["precision"])]
        if settings["restart"]:
            args += ["-restart", f"{settings['restart']}b"]
        if not settings["gray"]:
            args.append("-rgb")
        theirs = subprocess.run(args + [path[:-4] + ".src.pnm"], capture_output=True, check=True).stdout
        mine = os.path.getsize(path)
        ratio = mine / len(theirs)
        ok = ratio <= 1.01
        failures += 0 if ok else 1
        results.append(f"size {mine:,} vs cjpeg -lossless {settings['predictor']},{settings['pt']} {len(theirs):,} ({(ratio - 1) * 100:+.2f} %) {'ok' if ok else 'TOO LARGE'}")
    return failures, results


def richter():
    failures = 0
    for name in sorted(f for f in os.listdir(OUT) if f.endswith(".jpg") and (f.startswith("lla-") or f.startswith("ll-gray2") or f.startswith("ll-gray5"))):
        bad, results = check_lossless(name, "richter")
        failures += bad
        print(f"{name:28} " + ", ".join(results))
    sys.exit(1 if failures else 0)


def inside():
    failures = 0
    for name in sorted(f for f in os.listdir(OUT) if f.endswith(".jpg")):
        if name.startswith("lla-"):
            continue
        if name.startswith("ll-"):
            bad, results = check_lossless(name, "djpeg")
            failures += bad
            print(f"{name:28} " + ", ".join(results))
            continue
        bad, results = check_file(name)
        if os.path.exists(os.path.join(OUT, name[:-4] + ".json")):
            more, text = check_size(name)
            bad += more
            results.append(text)
        failures += bad
        print(f"{name:28} " + ", ".join(results))
    print("all readers agree" if failures == 0 else f"{failures} failures")
    sys.exit(1 if failures else 0)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return
    if len(sys.argv) > 1 and sys.argv[1] == "--richter":
        richter()
        return
    env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(OUT))
    subprocess.run(["dotnet", "test", "tests/Egami.Tests", "-c", "Release", "--filter", "FullyQualifiedName~JpegWriterInteropFiles"],
                   check=True, env=env, stdout=subprocess.DEVNULL)
    repo = os.getcwd()
    result = subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                             f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/check-jpeg-writer-interop.py --inside"])

    # SOF11 in Richter's libjpeg, in the image tools/make-jpeg-depth-references.py builds (built here if it is missing).
    sys.path.insert(0, os.path.join(repo, "tools"))
    depth = __import__("make-jpeg-depth-references")
    if subprocess.run(["docker", "image", "inspect", depth.IMAGE], capture_output=True).returncode != 0:
        subprocess.run(["docker", "build", "-t", depth.IMAGE, "-"], input=depth.DOCKERFILE, text=True, check=True)
    arithmetic = subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", depth.IMAGE, "python3",
                                 "tools/check-jpeg-writer-interop.py", "--richter"])
    sys.exit(result.returncode or arithmetic.returncode)


if __name__ == "__main__":
    main()
