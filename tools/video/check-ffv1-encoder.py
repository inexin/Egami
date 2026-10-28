"""Checks that FFmpeg decodes the FFV1 encoder's output exactly as the source frames (V6 task 14).

Runs the Ffv1EncoderInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which re-encodes every clip of the FFV1 corpus with
Egami's encoder in several settings (custom and default state tables, Golomb-Rice, both context models, 1 to 24 slices, slice
CRCs on and off, non-keyframes, AVI) into .cache/interop/ffv1/<clip>.<variant>.mkv or .avi, each next to a .json naming its clip. Then decodes
each file with FFmpeg and requires every frame's hash to equal the clip's reference (tests/video/corpus/ffv1/<clip>.frames.json),
hashed as tools/video/make-ffv1-references.py hashes FFmpeg's decode of the original.

Usage, from the repo root:
  python tools/video/check-ffv1-encoder.py [--no-test] [--local]   (--local: the ffmpeg on the PATH instead of Docker)
"""
import hashlib
import importlib.util
import json
import os
import subprocess
import sys

OUT = ".cache/interop/ffv1"
CORPUS = "tests/video/corpus/ffv1"
IMAGE = "egami-video-tools"

spec = importlib.util.spec_from_file_location("refs", os.path.join(os.path.dirname(__file__), "make-ffv1-references.py"))
refs = importlib.util.module_from_spec(spec)
spec.loader.exec_module(refs)


def decode(file, local):
    folder, name = os.path.split(file)
    probe_cmd = f"ffprobe -v error -select_streams v:0 -show_entries stream=width,height,pix_fmt -of json {name}"
    raw_cmd = f"ffmpeg -v error -nostdin -i {name} -fps_mode passthrough -f rawvideo -"
    if local:
        run = lambda c, b=False: subprocess.run(["sh", "-c", f"cd {folder} && {c}"], check=True, capture_output=True, text=not b).stdout
    else:
        run = lambda c, b=False: subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(folder)}:/work", IMAGE, "sh", "-c", c],
                                                check=True, capture_output=True, text=not b).stdout
    stream = json.loads(run(probe_cmd))["streams"][0]
    w, h, fmt = stream["width"], stream["height"], stream["pix_fmt"]
    raw = run(raw_cmd, True)
    if fmt in refs.PACKED:
        size = w * h * refs.PACKED[fmt][0] * refs.PACKED[fmt][1]
    else:
        size = sum(pw * ph for pw, ph in refs.plane_sizes(fmt, w, h)) * (1 if refs.depth_of(fmt) == 8 else 2)
    return [hashlib.md5(b"".join(refs.planes_of(raw[at:at + size], fmt, w, h))).hexdigest() for at in range(0, len(raw), size)]


def main():
    local = "--local" in sys.argv
    if "--no-test" not in sys.argv:
        env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(".cache/interop"))
        subprocess.run(["dotnet", "test", "tests/Egami.Video.Tests/Egami.Video.Tests.csproj", "-c", "Release", "--filter",
                        "FullyQualifiedName~Ffv1EncoderInteropFiles"], check=True, env=env)
    failures, checked = [], 0
    for file in sorted(f for f in os.listdir(OUT) if f.endswith(".mkv") or f.endswith(".avi")):
        with open(os.path.join(OUT, file[:-4] + ".json"), encoding="utf-8") as f:
            expected = json.load(f)["md5"]
        try:
            ours = decode(os.path.join(OUT, file), local)
        except subprocess.CalledProcessError as e:
            failures.append(f"{file}: FFmpeg failed: {e.stderr}")
            continue
        checked += 1
        if ours != expected:
            first = next((i for i, (a, b) in enumerate(zip(ours, expected)) if a != b), min(len(ours), len(expected)))
            failures.append(f"{file}: {len(ours)} frames, {len(expected)} expected, first difference at frame {first}")
    print(f"{checked} files decoded by FFmpeg, {len(failures)} differ")
    for line in failures:
        print("  " + line)
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
