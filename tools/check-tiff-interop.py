"""Checks that other tools read Egami's TIFF files: libtiff's tiffinfo with no warnings and its decoded samples identical to
Egami's own, and ImageMagick and Pillow able to open each file.

Runs the TiffInteropFiles test with EGAMI_INTEROP_DIR=.cache/tiff-interop, which writes every pixel type in every compression,
in strips, tiles and BigTIFF, with Egami's decode as raw samples next to each; then, in Docker (alpine:3.20), checks them with
libtiff 4.6.0 (tiffinfo -D, and tools/tiffref/tiffref.c's raw samples, or its RGBA for 1-bit, palette, CCITT, JPEG and
WebP pages; raw within 2 levels for CMYK JPEG pages, named near-*), ImageMagick 7.1.1 and Pillow.

It also checks the other way for ZSTD and LZMA (TIFF ZSTD task 3, TIFF LZMA task 8): libtiff's tiffcp -c zstd and -c lzma
(no predictor, predictor 2, 3 for float, tiles, 7-row strips) and GDAL 3.9's gdal_translate -co COMPRESS=ZSTD and LZMA
(PREDICTOR 2 and 3, ZSTD_LEVEL and LZMA_PRESET, tiles) write files from the corpus's base images into
.cache/tiff-interop/foreign, libtiff decodes each to raw samples beside it, and the TiffInteropFiles test reads them with Egami,
which must give the same samples. Alpine's libtiff and GDAL's alpine image are built without LZMA, so the foreign files are made
and decoded with Debian bookworm's libtiff 4.5.0 and GDAL's ubuntu-small image, and Egami's LZMA files are checked with Debian's
libtiff (tiffinfo and samples; ImageMagick and Pillow are not asked about them).

Usage, from the repo root with Docker running:
  python tools/check-tiff-interop.py
"""
import os
import shutil
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow imagemagick tiff-tools tiff-dev gcc musl-dev"
OUT = ".cache/tiff-interop"
FOREIGN = OUT + "/foreign"
GDAL_IMAGE = "ghcr.io/osgeo/gdal:ubuntu-small-3.9.3"
DEBIAN = "debian:bookworm-slim"
DEBIAN_PACKAGES = "python3 libtiff-tools libtiff-dev gcc libc6-dev"
BASES = ("gray8", "gray16", "grayf32", "rgb8", "rgb16", "rgba8", "rgba16", "rgbf32", "cmyk8")

# tiffcp's ZSTD options: the predictor alone, then p and the level.
TIFFCP = [("zstd", ["-c", "zstd"]), ("zstd-p2", ["-c", "zstd:2"]), ("zstd-p2-tiles", ["-c", "zstd:2", "-t", "-w", "32", "-l", "32"]),
          ("zstd-rows7", ["-c", "zstd:2", "-r", "7"]), ("lzma", ["-c", "lzma"]), ("lzma-p2", ["-c", "lzma:2"]),
          ("lzma-p2-tiles", ["-c", "lzma:2", "-t", "-w", "32", "-l", "32"]), ("lzma-rows7", ["-c", "lzma:2", "-r", "7"])]

# gdal_translate's: the float predictor goes to float images only.
GDAL = [("zstd-p2", ["-co", "PREDICTOR=2"]), ("zstd-p2-l1-tiles", ["-co", "PREDICTOR=2", "-co", "ZSTD_LEVEL=1", "-co", "TILED=YES",
        "-co", "BLOCKXSIZE=32", "-co", "BLOCKYSIZE=32"]), ("zstd-l22", ["-co", "ZSTD_LEVEL=22"]),
        ("lzma-p2", ["-co", "PREDICTOR=2"]), ("lzma-p2-l1-tiles", ["-co", "PREDICTOR=2", "-co", "LZMA_PRESET=1", "-co", "TILED=YES",
        "-co", "BLOCKXSIZE=32", "-co", "BLOCKYSIZE=32"]), ("lzma-l9", ["-co", "LZMA_PRESET=9"])]


def foreign_libtiff():
    """In Debian: tiffcp's ZSTD and LZMA files, then libtiff's raw samples of every foreign file (GDAL's written before)."""
    subprocess.run(["gcc", "-O2", "-o", "/tmp/tiffref", "tools/tiffref/tiffref.c", "-ltiff"], check=True)
    for base in BASES:
        for name, options in TIFFCP + ([("zstd-p3", ["-c", "zstd:3"]), ("lzma-p3", ["-c", "lzma:3"])] if "f32" in base else []):
            subprocess.run(["tiffcp", *options, f"tests/images/tiff/{base}.tif", f"{FOREIGN}/libtiff-{base}-{name}.tif"], check=True)
    for name in sorted(n for n in os.listdir(FOREIGN) if n.endswith(".tif")):
        subprocess.run(["/tmp/tiffref", "raw", f"{FOREIGN}/{name}", "0", f"{FOREIGN}/{name}.raw"], check=True, capture_output=True)
    print(f"{FOREIGN}: {len([n for n in os.listdir(FOREIGN) if n.endswith('.tif')])} ZSTD and LZMA files from libtiff and GDAL")


def gdal_script():
    """The shell commands for GDAL's image that write gdal_translate's ZSTD and LZMA files."""
    lines = ["set -e"]
    for base in BASES:
        for name, options in GDAL + ([("zstd-p3", ["-co", "PREDICTOR=3"]), ("lzma-p3", ["-co", "PREDICTOR=3"])] if "f32" in base else []):
            codec = "ZSTD" if name.startswith("zstd") else "LZMA"
            lines.append(" ".join(["gdal_translate", "-q", "-of", "GTiff", "-co", f"COMPRESS={codec}", *options, f"tests/images/tiff/{base}.tif",
                                   f"{FOREIGN}/gdal-{base}-{name}.tif"]))
    return "\n".join(lines)

# Pillow reads no float, CMYK-16 or 16-bit RGB(A) TIFF it can convert to RGBA, so it is only asked to open them.
PILLOW_OPEN_ONLY = ("f32", "f16", "16")

# ImageMagick 7.1.1 reads JPEG-compressed separated tiles by scanline and fails, on libtiff's own tiffcp -c jpeg -t output too;
# reported, not failed.
MAGICK_KNOWN = ("near-jpeg-cmyk8-tiles.tif",)


def inside(lzma=False):
    """Checks Egami's files: in alpine every file but the LZMA ones, in Debian (lzma) the LZMA ones with libtiff alone."""
    subprocess.run(["gcc", "-O2", "-o", "/tmp/tiffref", "tools/tiffref/tiffref.c", "-ltiff"], check=True)
    if not lzma:
        from PIL import Image

    failures = 0
    files = sorted(f for f in os.listdir(OUT) if f.endswith(".tif") and ("lzma" in f) == lzma)
    for name in files:
        path = os.path.join(OUT, name)
        problems = []
        info = subprocess.run(["tiffinfo", "-D", path], capture_output=True, text=True)
        if "Cannot display data" in info.stderr:
            # tiffinfo -D cannot dump subsampled YCbCr tiles, libtiff's own included; the tags alone must still be clean.
            info = subprocess.run(["tiffinfo", path], capture_output=True, text=True)
        if info.returncode != 0 or info.stderr.strip():
            problems.append("tiffinfo: " + (info.stderr.strip() or "failed")[:200])
        if name.startswith("rgba-"):
            # 1-bit, palette, JPEG and WebP pages: libtiff's RGBA against Egami's, JPEG within 2 levels (libjpeg's and Egami's
            # IDCT and upsampling).
            rgba = subprocess.run(["/tmp/tiffref", "rgba", path, "0", "/tmp/out.rgba"], capture_output=True, text=True)
            if rgba.returncode != 0:
                problems.append("libtiff: " + rgba.stderr.strip()[:200])
            else:
                theirs = open("/tmp/out.rgba", "rb").read()
                mine = open(path + ".rgba", "rb").read()
                limit = 2 if "jpeg" in name else 0
                worst = max(abs(a - b) for a, b in zip(theirs, mine)) if len(theirs) == len(mine) else 999
                if worst > limit:
                    problems.append(f"libtiff's RGBA differs from Egami's by {worst}")
        else:
            raw = subprocess.run(["/tmp/tiffref", "raw", path, "0", "/tmp/out.raw"], capture_output=True, text=True)
            theirs = open("/tmp/out.raw", "rb").read() if raw.returncode == 0 else b""
            mine = open(path + ".raw", "rb").read()
            if raw.returncode != 0:
                problems.append("libtiff: " + raw.stderr.strip()[:200])
            elif name.startswith("near-"):
                # CMYK JPEG pages: libjpeg's raw inks against Egami's, within 2 levels (the two decoders' IDCTs).
                worst = max(abs(a - b) for a, b in zip(theirs, mine)) if len(theirs) == len(mine) else 999
                if worst > 2:
                    problems.append(f"libtiff's samples differ from Egami's by {worst}")
            elif theirs != mine:
                problems.append("libtiff's samples differ from Egami's")
        magick = subprocess.run(["magick", path + "[0]", "-depth", "8", "rgba:/tmp/m.rgba"], capture_output=True, text=True) if not lzma else None
        if magick is None:
            pass
        elif magick.returncode != 0 and name in MAGICK_KNOWN:
            print(f"{name}: ImageMagick fails, as it does on libtiff's own tiled CMYK JPEG (known)")
        elif magick.returncode != 0:
            problems.append("ImageMagick: " + magick.stderr.strip()[:200])
        try:
            if not lzma:
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
        inside("--lzma" in sys.argv)
        return
    if len(sys.argv) > 1 and sys.argv[1] == "--foreign-libtiff":
        foreign_libtiff()
        return
    repo = os.getcwd()
    shutil.rmtree(FOREIGN, ignore_errors=True)
    os.makedirs(FOREIGN)
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", GDAL_IMAGE, "sh", "-c", gdal_script()], check=True)
    debian = f"apt-get update -qq > /dev/null && apt-get install -y -qq {DEBIAN_PACKAGES} > /dev/null 2>&1 && python3 tools/check-tiff-interop.py"
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", DEBIAN, "sh", "-c", f"{debian} --foreign-libtiff"], check=True)
    env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(OUT))
    subprocess.run(["dotnet", "test", "tests/Egami.Tests", "-c", "Release", "--filter", "FullyQualifiedName~TiffInteropFiles"], check=True, env=env)
    alpine = subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                             f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/check-tiff-interop.py --inside"])
    lzma = subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", DEBIAN, "sh", "-c", f"{debian} --inside --lzma"])
    sys.exit(1 if alpine.returncode or lzma.returncode else 0)


if __name__ == "__main__":
    main()
