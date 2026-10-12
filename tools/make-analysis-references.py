"""Builds the Phase 2.5.5 analysis corpus in tests/images/analysis and its references in tests/reference/analysis, with
ImageMagick and Python imagehash in Docker.

Corpus (licenses in tests/images/LICENSES), made here from the 1.2 public-domain photos (tests/images/ops) or generated:
  duck.png, egret.png       copies of the 1.2 photos (duck has an alpha channel, all opaque)
  duck-q75.png              duck through JPEG quality 75, as 8-bit RGB
  duck-half.png             duck resized to 50% and back, for hash robustness
  duck-bright.png           duck 3% brighter, for hash robustness
  egret-q75.png             egret through JPEG quality 75
  tiny-a.png, tiny-b.png    4 x 4 8-bit RGB with hand-picked values, for the metric math
  grad16-a.png, grad16-b.png  96 x 64 16-bit RGB gradients, b with small noise
  alpha-a.png, alpha-b.png  64 x 48 8-bit RGBA with an alpha ramp, b with small changes and other hidden colors under alpha 0
  gray8.png                 96 x 64 8-bit gray
  almost-gray.png           96 x 64 RGB whose channels differ by at most 3 levels
  mosaic.png                64 x 64 RGB of 37 flat colors
  dups.gif, zero.gif, allzero.gif  48 x 32 animations with repeated frames and zero delays, for RemoveDups and RemoveZero
  base.gif, over.gif        a 4-frame 48 x 32 base and a 4-frame 20 x 16 overlay with transparency, for -layers Composite
  shapes.png, zone.png      copies of the 1.2 test pictures

References (tests/reference/analysis):
  analysis.json   per compare pair and metric, ImageMagick's per-channel and overall values (raw and normalized); per image,
                  identify -verbose channel statistics, %k, opaque and type; per image, imagehash phash and dhash as hex;
                  the PSNR of ImageMagick's -colors 16 and 64 with each -dither against the photo; -kmeans 5 colors and counts
  <a>~<b>.mask.rgba.gz   ImageMagick compare's difference image for the pair with opaque #F1001E and white, raw RGBA 8-bit
  <a>~<b>.diff.rgba.gz   the same with ImageMagick's default colors (80% opaque), so the first image shows through
  <gif>.<method>.rgba.gz  the frames after -layers RemoveDups, RemoveZero or Composite (over.gif at +5+3), raw RGBA 8-bit;
                          their delays in ms are in analysis.json under "frames"

Usage, from the repo root with Docker running:
  python tools/make-analysis-references.py
The script runs alpine:3.20, installs ImageMagick (with JPEG), Pillow, NumPy and SciPy with apk and imagehash (with PyWavelets) with pip.
"""
import gzip
import json
import os
import random
import re
import shutil
import struct
import subprocess
import sys
import zlib

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pip py3-pillow py3-numpy py3-scipy imagemagick imagemagick-jpeg"
IMAGES = "tests/images/analysis"
OPS = "tests/images/ops"
TARGET = "tests/reference/analysis"

PAIRS = [
    ("tiny-a.png", "tiny-b.png"),
    ("duck.png", "duck-q75.png"),
    ("egret.png", "egret-q75.png"),
    ("grad16-a.png", "grad16-b.png"),
    ("alpha-a.png", "alpha-b.png"),
]
METRICS = ["MAE", "MSE", "RMSE", "PSNR", "PAE", "AE", "SSIM", "DSSIM"]
FUZZ = ["0", "5%"]
STATS = ["duck.png", "egret.png", "tiny-a.png", "grad16-a.png", "alpha-a.png", "gray8.png", "almost-gray.png", "mosaic.png"]
HASHES = ["duck.png", "duck-q75.png", "duck-half.png", "duck-bright.png", "egret.png", "egret-q75.png", "shapes.png", "zone.png",
          "mosaic.png"]


def png(path, width, height, data, color_type, depth=8):
    """Writes a PNG with no filtering: color type 0 (gray), 2 (RGB) or 6 (RGBA)."""
    channels = {0: 1, 2: 3, 6: 4}[color_type]
    row = width * channels * depth // 8
    raw = b"".join(b"\x00" + data[y * row:(y + 1) * row] for y in range(height))

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n")
        f.write(chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, depth, color_type, 0, 0, 0)))
        f.write(chunk(b"IDAT", zlib.compress(raw, 9)))
        f.write(chunk(b"IEND", b""))


def generate():
    rng = random.Random(255)
    tiny_a = [10, 20, 30, 200, 100, 50, 0, 255, 128, 64, 32, 16, 1, 2, 3, 250, 240, 230, 90, 91, 92, 7, 77, 177,
              33, 66, 99, 44, 88, 132, 55, 110, 165, 66, 132, 198, 254, 0, 1, 12, 34, 56, 78, 90, 12, 210, 211, 212]
    tiny_b = list(tiny_a)
    for i, d in ((0, 5), (4, -3), (7, -255), (9, 10), (13, 1), (20, -20), (30, 2), (45, 4)):
        tiny_b[i] = max(0, min(255, tiny_b[i] + d))
    png(f"{IMAGES}/tiny-a.png", 4, 4, bytes(tiny_a), 2)
    png(f"{IMAGES}/tiny-b.png", 4, 4, bytes(tiny_b), 2)

    w, h = 96, 64
    a = bytearray()
    b = bytearray()
    for y in range(h):
        for x in range(w):
            v = (x * 65535 // (w - 1), y * 65535 // (h - 1), ((x + y) * 65535 // (w + h - 2)))
            n = tuple(max(0, min(65535, c + rng.randint(-300, 300))) for c in v)
            a += struct.pack(">HHH", *v)
            b += struct.pack(">HHH", *n)
    png(f"{IMAGES}/grad16-a.png", w, h, bytes(a), 2, 16)
    png(f"{IMAGES}/grad16-b.png", w, h, bytes(b), 2, 16)

    w, h = 64, 48
    a = bytearray()
    b = bytearray()
    for y in range(h):
        for x in range(w):
            alpha = 0 if x < 8 else min(255, (x - 8) * 255 // 40)
            p = [(x * 4) % 256, (y * 5) % 256, (x * y) % 256, alpha]
            q = list(p)
            if alpha == 0:
                q[0:3] = [rng.randrange(256) for _ in range(3)]
            elif rng.random() < 0.3:
                q = [max(0, min(255, c + rng.randint(-6, 6))) for c in q]
            a += bytes(p)
            b += bytes(q)
    png(f"{IMAGES}/alpha-a.png", w, h, bytes(a), 6)
    png(f"{IMAGES}/alpha-b.png", w, h, bytes(b), 6)

    w, h = 96, 64
    gray = bytes(((x * 3 + y * 2) ^ (x * y)) & 255 for y in range(h) for x in range(w))
    png(f"{IMAGES}/gray8.png", w, h, gray, 0)
    almost = bytearray()
    for v in gray:
        almost += bytes((v, min(255, v + rng.randint(0, 3)), max(0, v - rng.randint(0, 3))))
    png(f"{IMAGES}/almost-gray.png", w, h, bytes(almost), 2)

    colors = [(rng.randrange(256), rng.randrange(256), rng.randrange(256)) for _ in range(37)]
    mosaic = bytearray()
    for y in range(64):
        for x in range(64):
            mosaic += bytes(colors[((y // 8) * 8 + x // 8) % 37])
    png(f"{IMAGES}/mosaic.png", 64, 64, bytes(mosaic), 2)

    for name in ("duck.png", "egret.png", "shapes.png", "zone.png"):
        shutil.copyfile(f"{OPS}/{name}", f"{IMAGES}/{name}")
    magick(f"{IMAGES}/duck.png", "-quality", "75", "/tmp/duck.jpg")
    magick("/tmp/duck.jpg", "-type", "TrueColor", f"PNG24:{IMAGES}/duck-q75.png")
    magick(f"{IMAGES}/egret.png", "-quality", "75", "/tmp/egret.jpg")
    magick("/tmp/egret.jpg", "-type", "TrueColor", f"PNG24:{IMAGES}/egret-q75.png")
    magick(f"{IMAGES}/duck.png", "-alpha", "off", "-resize", "50%", "-resize", "640x480!", f"PNG24:{IMAGES}/duck-half.png")
    magick(f"{IMAGES}/duck.png", "-alpha", "off", "-evaluate", "multiply", "1.03", f"PNG24:{IMAGES}/duck-bright.png")


def magick(*args):
    # No date or time chunks, so regenerating gives the same files.
    return subprocess.run(["magick", *args[:-1], "-define", "png:exclude-chunks=date,time", args[-1]], check=True, capture_output=True, text=True)


def run(*args):
    result = subprocess.run(["magick", *args], capture_output=True, text=True)
    return result.stdout + result.stderr


NUMBER = r"([-+0-9.eEinfa]+)"


def parse_compare(text):
    """Parses compare -verbose output: per channel 'raw (normalized)' and the 'all' line."""
    out = {}
    for line in text.splitlines():
        m = re.match(r"\s*(red|green|blue|alpha|gray|all):\s*" + NUMBER + r"(?:\s*\(" + NUMBER + r"\))?", line)
        if m:
            out[m.group(1)] = {"raw": float(m.group(2)), "normalized": float(m.group(3)) if m.group(3) else None}
    return out


def parse_verbose(text):
    stats = {}
    channel = None
    section = None
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("Channel statistics:"):
            section = "stats"
            continue
        if s.startswith("Image statistics:") or s.startswith("Channel depth:") or s.startswith("Rendering intent"):
            section = "overall" if s.startswith("Image statistics:") else None
            continue
        if section is None:
            m = re.match(r"Type: (\S+)", s)
            if m:
                stats["type"] = m.group(1)
            continue
        m = re.match(r"(Red|Green|Blue|Alpha|Gray|Overall):$", s)
        if m:
            channel = m.group(1).lower()
            stats.setdefault("channels", {})[channel] = {}
            continue
        m = re.match(r"(min|max|mean|median|standard deviation|kurtosis|skewness|entropy):\s*" + NUMBER + r"(?:\s*\(" + NUMBER + r"\))?", s)
        if m and channel:
            stats["channels"][channel][m.group(1).replace(" ", "_")] = {
                "raw": float(m.group(2)), "normalized": float(m.group(3)) if m.group(3) else None}
    return stats


def inside():
    os.makedirs(IMAGES, exist_ok=True)
    os.makedirs(TARGET, exist_ok=True)
    generate()
    out = {"version": run("-version").splitlines()[0], "compare": {}, "statistics": {}, "hashes": {}}

    for a, b in PAIRS:
        pa, pb = f"{IMAGES}/{a}", f"{IMAGES}/{b}"
        entry = {}
        for metric in METRICS:
            for fuzz in FUZZ if metric == "AE" else ["0"]:
                key = metric if fuzz == "0" else f"{metric}@{fuzz}"
                entry[key] = parse_compare(run("compare", "-precision", "17", "-verbose", "-fuzz", fuzz, "-metric", metric, pa, pb, "null:"))
        out["compare"][f"{a}~{b}"] = entry
        # The difference image goes to a 16-bit PNG first: -depth 8 on the compare line would quantize the inputs too.
        for suffix, colors in (("mask", ["-highlight-color", "#F1001E", "-lowlight-color", "white"]), ("diff", [])):
            run("compare", *colors, pa, pb, "PNG64:/tmp/diff.png")
            magick("/tmp/diff.png", "-depth", "8", "rgba:/tmp/diff.rgba")
            with open("/tmp/diff.rgba", "rb") as raw, open(f"{TARGET}/{a}~{b}.{suffix}.rgba.gz", "wb") as f, \
                    gzip.GzipFile(fileobj=f, mode="wb", mtime=0) as z:
                z.write(raw.read())

    for name in STATS:
        path = f"{IMAGES}/{name}"
        stats = parse_verbose(run("-precision", "17", path, "-verbose", "info:"))
        stats["colors"] = int(run(path, "-format", "%k", "info:").strip())
        stats["opaque"] = run(path, "-format", "%[opaque]", "info:").strip()
        out["statistics"][name] = stats

    # Stage B: ImageMagick's own quantize with Riemersma and Floyd-Steinberg (its PSNR against the original is the gate), and
    # -kmeans colors with their pixel counts.
    out["quantize"] = {}
    out["kmeans"] = {}
    for name in ("duck.png", "egret.png"):
        path = f"{IMAGES}/{name}"
        for dither in ("Riemersma", "FloydSteinberg", "None"):
            for colors in (16, 64):
                magick(path, "-alpha", "off", "-dither", dither, "-colors", str(colors), "PNG24:/tmp/q.png")
                psnr = parse_compare(run("compare", "-precision", "17", "-metric", "PSNR", "-verbose", path, "/tmp/q.png", "null:"))
                out["quantize"][f"{name}:{dither}:{colors}"] = psnr["all"]["raw"]
        text = run(path, "-alpha", "off", "-kmeans", "5", "-format", "%c", "histogram:info:")
        out["kmeans"][name] = [{"count": int(m.group(1)), "color": m.group(2)}
                               for m in re.finditer(r"^\s*(\d+):\s*\([^)]*\)\s*(#[0-9A-Fa-f]{6})", text, re.M)]

    frames(out)

    import imagehash
    from PIL import Image
    out["imagehash"] = imagehash.__version__
    for name in HASHES:
        with Image.open(f"{IMAGES}/{name}") as im:
            im = im.convert("RGBA")
            flat = Image.new("RGBA", im.size, (255, 255, 255, 255))
            flat.alpha_composite(im)
            flat = flat.convert("RGB")
            out["hashes"][name] = {"phash": str(imagehash.phash(flat)), "dhash": str(imagehash.dhash(flat))}

    with open(f"{TARGET}/analysis.json", "w") as f:
        json.dump(out, f, indent=1, sort_keys=True)
    print(f"{len(out['compare'])} pairs, {len(out['statistics'])} statistics, {len(out['hashes'])} hashes")


def animation(name, frames, delays, size=(48, 32)):
    """Writes an unoptimized GIF of flat frames with the given delays in hundredths, through PNGs and ImageMagick."""
    args = []
    for i, (color, box) in enumerate(frames):
        w, h = size
        data = bytearray()
        for y in range(h):
            for x in range(w):
                inside = box is not None and box[0] <= x < box[2] and box[1] <= y < box[3]
                data += bytes((255 - color[0], 255 - color[1], 255 - color[2], 255) if inside else (*color, 255 if len(color) == 3 else color[3]))
        png(f"/tmp/f{i}.png", w, h, bytes(data), 6)
        args += ["-delay", str(delays[i]), f"/tmp/f{i}.png"]
    magick(*args, f"{IMAGES}/{name}")


def frames(out):
    """Stage C: -layers RemoveDups, RemoveZero and Composite (by index) on synthetic GIFs: delays in ms and every frame as RGBA."""
    red, blue, green = (200, 40, 40), (40, 60, 200), (40, 170, 70)
    animation("dups.gif", [(red, None), (red, None), (blue, None), (blue, (4, 4, 20, 20)), (blue, (4, 4, 20, 20)),
                           (blue, (4, 4, 20, 20)), (green, None), (red, None)], [10, 20, 10, 30, 0, 20, 10, 10])
    animation("zero.gif", [(red, None), (blue, None), (green, None), (red, (0, 0, 10, 10)), (blue, (5, 5, 30, 30)), (green, (1, 1, 2, 2))],
              [0, 10, 0, 0, 20, 0])
    animation("allzero.gif", [(red, None), (blue, None), (green, None)], [0, 0, 0])
    animation("base.gif", [(red, (0, 0, 24, 16)), (blue, (24, 0, 48, 16)), (green, (0, 16, 24, 32)), (red, (24, 16, 48, 32))],
              [10, 20, 30, 40])
    animation("over.gif", [((255, 255, 0, 0), (2, 2, 10, 10)), ((255, 255, 0, 0), (6, 2, 14, 10)), ((255, 255, 0, 0), (10, 2, 18, 10)),
                           ((255, 255, 0, 0), (2, 6, 10, 14))], [5, 5, 5, 5], size=(20, 16))
    out["frames"] = {}
    cases = {
        "dups.gif:RemoveDups": [f"{IMAGES}/dups.gif", "-layers", "RemoveDups"],
        "zero.gif:RemoveZero": [f"{IMAGES}/zero.gif", "-layers", "RemoveZero"],
        "allzero.gif:RemoveZero": [f"{IMAGES}/allzero.gif", "-layers", "RemoveZero"],
        "base.gif:Composite": [f"{IMAGES}/base.gif", "-coalesce", "null:", "(", f"{IMAGES}/over.gif", "-coalesce", ")",
                               "-geometry", "+5+3", "-layers", "Composite"],
    }
    for key, args in cases.items():
        # ImageMagick warns on stderr when every delay is zero, and then removes nothing.
        delays = magick(*args, "-format", "%T,", "info:").stdout.strip().rstrip(",")
        magick(*args, "-depth", "8", "rgba:/tmp/frames.rgba")
        with open("/tmp/frames.rgba", "rb") as raw, open(f"{TARGET}/{key.replace(':', '.')}.rgba.gz", "wb") as f,                 gzip.GzipFile(fileobj=f, mode="wb", mtime=0) as z:
            z.write(raw.read())
        out["frames"][key] = [int(d) * 10 for d in delays.split(",")]


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    repo = os.getcwd()
    # One ImageMagick thread, so its sums add up in the same order and the file is the same on every run.
    subprocess.run(["docker", "run", "--rm", "-e", "MAGICK_THREAD_LIMIT=1", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache {PACKAGES} > /dev/null && pip install -q --break-system-packages imagehash==4.3.1 "
                    f"> /dev/null && python3 tools/make-analysis-references.py --inside"], check=True)


if __name__ == "__main__":
    main()
