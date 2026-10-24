"""Runs the AFL++ fuzz targets in Docker, one container per target, on any machine with Docker.

Each container copies the sources it needs from the repository (mounted read-only), publishes tests/Egami.Video.Fuzz,
instruments Egami.Video.dll with SharpFuzz and runs afl-fuzz on the target's seeds for the given time. Findings go to
.cache/video/afl/<target>: crashes in <target>/default/crashes, hangs in <target>/default/hangs. The image is Egami's.

Usage, from the repo root:
  docker build -t egami-fuzz -f tests/Egami.Fuzz/Dockerfile tests/Egami.Fuzz
  python tools/video/fuzz-afl.py 10m ivf webm vp8         run time (10m, 2h) then targets (default: all)
  docker logs -f egami-video-afl-avi              to follow one
  dotnet run --project tests/Egami.Video.Fuzz -c Release -- replay avi .cache/video/afl/avi/default/crashes
"""
import os
import subprocess
import sys

SEEDS = {"avi": "tests/video/fuzz-seeds/avi/*", "mkv": "tests/video/fuzz-seeds/mkv/*", "y4m": "tests/video/fuzz-seeds/y4m/*", "mjpeg": "tests/video/fuzz-seeds/mjpeg/*",
         "ivf": "tests/video/fuzz-seeds/ivf/*", "webm": "tests/video/fuzz-seeds/webm/*", "vp8": "tests/video/fuzz-seeds/vp8/*",
         "vp9": "tests/video/fuzz-seeds/vp9/*", "vp9raw": "tests/video/fuzz-seeds/vp9raw/*",
         "h264": "tests/video/fuzz-seeds/h264/*",
         "av1": "tests/video/fuzz-seeds/av1/*", "av1raw": "tests/video/fuzz-seeds/av1raw/*", "obu": "tests/video/fuzz-seeds/obu/*",
         "mp4": "tests/video/fuzz-seeds/mp4/*", "mp4mux": "tests/video/fuzz-seeds/mp4/*",
         "wav": "tests/video/fuzz-seeds/wav/*", "flac": "tests/video/fuzz-seeds/flac/*",
         "ogg": "tests/video/fuzz-seeds/ogg/*", "aiff": "tests/video/fuzz-seeds/aiff/*", "caf": "tests/video/fuzz-seeds/caf/*",
         "ac3": "tests/video/fuzz-seeds/ac3/*", "ac3raw": "tests/video/fuzz-seeds/ac3raw/*", "alac": "tests/video/fuzz-seeds/alac/*",
         "opusraw": "tests/video/fuzz-seeds/opusraw/*",
         "vorbisraw": "tests/video/fuzz-seeds/vorbisraw/*", "vp8enc": "tests/video/fuzz-seeds/vp8enc/*", "mp3": "tests/video/fuzz-seeds/mp3/*",
         "mp3raw": "tests/video/fuzz-seeds/mp3raw/*", "opusenc": "tests/video/fuzz-seeds/opusenc/*",
         "subs": "tests/video/fuzz-seeds/subs/*", "subtrack": "tests/video/fuzz-seeds/subtrack/*",
         "vp9enc": "tests/video/fuzz-seeds/vp9enc/*", "av1enc": "tests/video/fuzz-seeds/av1enc/*", "trim": "tests/video/fuzz-seeds/trim/*"}

# The VP8 decoder itself is in Egami; its own fuzzing (Egami 2.3.5) covers it, and the vp8 target instruments it too.
INSTRUMENT = {"h264": "sharpfuzz /out/Egami.Video.H264.dll", "vp8": "sharpfuzz /out/Egami.dll", "av1": "sharpfuzz /out/Egami.dll",
              "av1raw": "sharpfuzz /out/Egami.dll", "av1enc": "sharpfuzz /out/Egami.dll",
              "trim": "sharpfuzz /out/Egami.dll"}

SCRIPT = """
set -e
mkdir -p /build/tests
cp -r /src/src /build/
cp -r /src/tests/Egami.Video.Fuzz /build/tests/
cp /src/Directory.Build.props /src/Directory.Packages.props /src/global.json /build/
cp /src/Directory.Build.targets /build/ 2>/dev/null || true
cp /src/.editorconfig /build/ 2>/dev/null || true
find /build -type d \\( -name bin -o -name obj \\) -prune -exec rm -rf {{}} +
dotnet publish /build/tests/Egami.Video.Fuzz -c Release -o /out -p:TreatWarningsAsErrors=false > /tmp/publish.log
sharpfuzz /out/Egami.Video.dll
{instrument}
mkdir -p /seeds && cp {seeds} /seeds/
afl-fuzz -V {seconds} -i /seeds -o /findings/{target} -t 5000 -m none -- /out/Egami.Video.Fuzz {target}
"""


def seconds(text):
    units = {"s": 1, "m": 60, "h": 3600}
    return int(float(text[:-1]) * units[text[-1]]) if text[-1] in units else int(float(text) * 3600)


def main():
    duration = seconds(sys.argv[1]) if len(sys.argv) > 1 else 600
    targets = sys.argv[2:] or list(SEEDS)
    repo = os.getcwd()
    findings = os.path.join(repo, ".cache", "video", "afl")
    os.makedirs(findings, exist_ok=True)
    for target in targets:
        name = f"egami-video-afl-{target}"
        subprocess.run(["docker", "rm", "-f", name], capture_output=True)
        script = SCRIPT.format(seeds=" ".join("/src/" + p for p in SEEDS[target].split()), seconds=duration, target=target,
                               instrument=INSTRUMENT.get(target, ""))
        subprocess.run(["docker", "run", "-d", "--name", name, "-e", "AFL_AUTORESUME=1", "-v", f"{repo}:/src:ro",
                        "-v", f"{findings}:/findings", "egami-fuzz", "bash", "-c", script], check=True)
        print(f"Started {name} for {duration} s.")


if __name__ == "__main__":
    main()
