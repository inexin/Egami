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

SEEDS = {"png": "tests/images/pngsuite/*.png", "jpeg": "tests/images/jpeg/*.jpg", "exif": "tests/images/fuzz/exif-seeds/*.tiff", "icc": "tests/images/icc/*/*.icc",
         "gif": "tests/images/gif/*.gif", "apng": "tests/images/apng/*.png", "lzw": "tests/images/fuzz/lzw-seeds/*.bin",
         "bmp": "tests/images/bmp/*", "tga": "tests/images/tga/*", "qoi": "tests/images/qoi/*", "pnm": "tests/images/pnm/*", "pfm": "tests/images/pfm/*.pfm",
         "detect": "tests/images/fuzz/detect-seeds/*"}

SCRIPT = """
set -e
mkdir -p /build/tests
cp -r /src/src /build/
cp -r /src/tests/Egami.Fuzz /build/tests/
cp /src/Directory.Build.props /src/Directory.Packages.props /src/global.json /build/
cp /src/.editorconfig /build/ 2>/dev/null || true
find /build -type d \\( -name bin -o -name obj \\) -prune -exec rm -rf {{}} +
dotnet publish /build/tests/Egami.Fuzz -c Release -o /out -p:TreatWarningsAsErrors=false > /tmp/publish.log
sharpfuzz /out/Egami.dll
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
            script = SCRIPT.format(seeds="/src/" + SEEDS[target], seconds=int(hours * 3600), target=target, role=role)
            subprocess.run(["docker", "run", "-d", "--name", name, "-v", f"{repo}:/src:ro", "-v", f"{findings}:/findings",
                            "egami-fuzz", "bash", "-c", script], check=True)
            print(f"Started {name} for {hours} h.")


if __name__ == "__main__":
    main()
