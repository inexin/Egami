"""Runs the AFL++ fuzz targets in Docker, one container per target, on any machine with Docker.

Each container copies the sources it needs from the repository (mounted read-only), publishes tests/Egami.Fuzz, instruments
Egami.dll with SharpFuzz and runs afl-fuzz on the target's seeds for the given time. Findings go to .cache/afl/<target>;
crashes are in <target>/<instance>/crashes and hangs in <target>/<instance>/hangs, the instance being "default" for one
instance, else "main" and "secondary1" and so on.

Usage, from the repo root:
  docker build -t egami-fuzz -f tests/Egami.Fuzz/Dockerfile tests/Egami.Fuzz
  python tools/fuzz-afl.py 3 jpeg:4 exif      hours, then targets (default: png jpeg exif); ":4" runs four instances
                                              (one main, three secondaries) sharing the target's findings
  docker logs -f egami-afl-jpeg               to follow one
"""
import os
import subprocess
import sys

SEEDS = {"png": "tests/images/pngsuite/*.png tests/images/fuzz/png-seeds/*.png", "jpeg": "tests/images/jpeg/*.jpg", "jpeg-depth": "tests/images/jpeg/depth/*.jpg", "jpeg-write": "tests/images/fuzz/jpeg-write-seeds/*", "jpeg-transform": "tests/images/jpeg/transform/*.jpg tests/images/jpeg/exif-orientation-6.jpg", "exif": "tests/images/fuzz/exif-seeds/*.tiff", "icc": "tests/images/icc/*/*.icc",
         "iptc": "tests/images/fuzz/iptc-seeds/*", "psd": "tests/images/fuzz/psd-seeds/*", "xmp": "tests/images/fuzz/xmp-seeds/*",
         "rawprofile": "tests/images/fuzz/rawprofile-seeds/*",
         "gif": "tests/images/gif/*.gif", "apng": "tests/images/apng/*.png", "lzw": "tests/images/fuzz/lzw-seeds/*.bin",
         "bmp": "tests/images/bmp/*", "tga": "tests/images/tga/*", "qoi": "tests/images/qoi/*", "pnm": "tests/images/pnm/*", "pfm": "tests/images/pfm/*.pfm",
         "detect": "tests/images/fuzz/detect-seeds/*",
         "stream": "tests/images/fuzz/stream-seeds/*",
         "tiff": "tests/images/tiff/*.tif", "tiffcodec": "tests/images/fuzz/tiffcodec-seeds/*",
         "webp": "tests/images/webp/*.webp", "webpcodec": "tests/images/fuzz/webpcodec-seeds/*", "vp8video": "tests/images/fuzz/vp8video-seeds/*",
         "jbig2": "tests/images/jbig2/*", "jbig2codec": "tests/images/fuzz/jbig2codec-seeds/*",
         "cube": "tests/images/fuzz/cube-seeds/*",
         "jpeg2000": "tests/images/fuzz/jpeg2000-seeds/*", "jpeg2000codec": "tests/images/fuzz/jpeg2000codec-seeds/*",
         "font": "tests/images/fuzz/font-seeds/*", "cff": "tests/images/fuzz/cff-seeds/*", "subset": "tests/images/fuzz/font-seeds/*",
         "svgpath": "tests/images/fuzz/svgpath-seeds/*",
         "woff2": "tests/images/fuzz/woff2-seeds/*", "gvar": "tests/images/fuzz/gvar-seeds/*", "cff2": "tests/images/fuzz/cff2-seeds/*",
         "colr": "tests/images/fuzz/colr-seeds/*", "instance": "tests/images/fuzz/instance-seeds/*",
         "ico": "tests/images/ico/bmp[0-9]*.ico tests/images/ico/bmp-[13]*.ico tests/images/ico/cursor.cur tests/images/ico/dir-mismatch.ico tests/images/ico/magick-16colors.ico tests/images/ico/bad/*", "icoentry": "tests/images/fuzz/icoentry-seeds/*",
         "psdfile": "tests/images/psd/*[0-9].psd tests/images/psd/*-r*.psd tests/images/psd/layers.psd tests/images/psd/metadata.psd tests/images/psd/*.psb tests/images/psd/bad/*",
         "psddata": "tests/images/fuzz/psddata-seeds/*",
         "egdr": "tests/images/fuzz/egdr-seeds/*",
         "shaping": "tests/images/fuzz/shaping-seeds/*", "bidi": "tests/images/fuzz/bidi-seeds/*",
         "radiance": "tests/images/radiance/*.hdr", "exr": "tests/images/exr/*.exr",
         "gainmap": "tests/images/gainmap/*.jpg tests/images/mpf/stereo*.jpg tests/images/mpf/preview.jpg",
         "css": "tests/images/fuzz/css-seeds/*", "paragraph": "tests/images/fuzz/paragraph-seeds/*",
         "svg": "tests/images/fuzz/svg-seeds/*"}

SCRIPT = """
set -e
mkdir -p /build/tests
cp -r /src/src /build/
cp -r /src/tests/Egami.Fuzz /build/tests/
mkdir -p /build/tests/fonts/derived && cp /src/tests/fonts/derived/NotoSansCJK-layout-subset.otf /build/tests/fonts/derived/
cp /src/Directory.Build.props /src/Directory.Packages.props /src/global.json /build/
cp /src/.editorconfig /build/ 2>/dev/null || true
find /build -type d \\( -name bin -o -name obj \\) -prune -exec rm -rf {{}} +
dotnet publish /build/tests/Egami.Fuzz -c Release -o /out -p:TreatWarningsAsErrors=false > /tmp/publish.log
sharpfuzz /out/Egami.dll
[ -f /out/Egami.Fonts.dll ] && sharpfuzz /out/Egami.Fonts.dll
if [ -f /out/Egami.Drawing.dll ]; then sharpfuzz /out/Egami.Drawing.dll; fi
if [ -f /out/Egami.Css.dll ]; then sharpfuzz /out/Egami.Css.dll; fi
mkdir -p /seeds && cp {seeds} /seeds/
afl-fuzz -V {seconds} -i /seeds -o /findings/{target} {role} -t 5000 -m none -- /out/Egami.Fuzz {target}
"""


def main():
    hours = float(sys.argv[1]) if len(sys.argv) > 1 else 3
    targets = sys.argv[2:] or list(SEEDS)
    repo = os.getcwd()
    findings = os.path.join(repo, ".cache", "afl")
    os.makedirs(findings, exist_ok=True)
    for spec in targets:
        target, _, count = spec.partition(":")
        instances = int(count or 1)
        for i in range(instances):
            name = f"egami-afl-{target}" + (f"-{i}" if instances > 1 else "")
            role = "" if instances == 1 else "-M main" if i == 0 else f"-S secondary{i}"
            subprocess.run(["docker", "rm", "-f", name], capture_output=True)
            script = SCRIPT.format(seeds=" ".join("/src/" + pattern for pattern in SEEDS[target].split()), seconds=int(hours * 3600), target=target, role=role)
            # AFL_AUTORESUME carries on from an earlier run's queue in the same findings directory.
            subprocess.run(["docker", "run", "-d", "--name", name, "-e", "AFL_AUTORESUME=1", "-v", f"{repo}:/src:ro", "-v", f"{findings}:/findings",
                            "egami-fuzz", "bash", "-c", script], check=True)
            print(f"Started {name} for {hours} h.")


if __name__ == "__main__":
    main()
