"""Checks Egami.Video's smart cuts (V5.6) with decoders that are not Egami's.

Runs the SmartCutInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which cuts Egami's own VP8, VP9, AV1 and Motion JPEG
encodes (hidden frames, Opus or PCM audio) and libvpx and libaom clips from tests/video/corpus on a frame between keyframes,
into every container that holds the codec (WebM, Matroska, MP4, IVF, AVI), and writes each IVF cut's frames as Egami decodes
them to <name>.yuv, with manifest.json giving each file's frame count. Then, in Docker, as black boxes:

- vpxdec (libvpx, egami-vp8ref:2.3.5 for VP8 and egami-vp9encref:v5.3's high bit depth build for VP9) and dav1d
  (egami-avifref:2.9.2) decode each IVF cut, which must give the same bytes as Egami;
- FFmpeg (egami-video-tools) decodes every cut with -xerror and must print no error, and ffprobe must count the frames the
  manifest gives.

Usage, from the repo root with Docker running (the images are built by tools/video/vp8ref... see their Dockerfiles):
  python tools/video/check-smart-cut.py [--no-test]
"""
import json
import os
import subprocess
import sys

OUT = ".cache/interop/smartcut"
VPX8 = "egami-vp8ref:2.3.5"
VPX9 = "egami-vp9encref:v5.3"
DAV1D = "egami-avifref:2.9.2"
FFMPEG = "egami-video-tools:latest"


def docker(image, args):
    root = os.path.abspath(".")
    return subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/work", "-w", "/work", "--entrypoint", args[0], image, *args[1:]],
                          capture_output=True)


def compare(name, decoded_path, failures):
    with open(os.path.join(OUT, name + ".yuv"), "rb") as f:
        ours = f.read()
    with open(decoded_path, "rb") as f:
        theirs = f.read()
    if ours != theirs:
        first = next((i for i in range(min(len(ours), len(theirs))) if ours[i] != theirs[i]), min(len(ours), len(theirs)))
        failures.append(f"{name}: {len(theirs)} bytes from the reference decoder, {len(ours)} from Egami, first difference at byte {first}")
        return False
    return True


def main():
    if "--no-test" not in sys.argv:
        env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(".cache/interop"))
        subprocess.run(["dotnet", "test", "tests/Egami.Video.Tests", "--filter", "FullyQualifiedName~SmartCutInteropFiles"], env=env, check=True)
    with open(os.path.join(OUT, "manifest.json"), encoding="utf-8") as f:
        manifest = json.load(f)

    failures, checked = [], 0
    for file, entry in sorted(manifest.items()):
        name, container = file.rsplit(".", 1)
        path = f"{OUT}/{file}"
        if container == "ivf" and entry["codec"] in ("vp8", "vp9", "av1"):
            decoded = f"{OUT}/{name}.ref.yuv"
            if entry["codec"] == "av1":
                result = docker(DAV1D, ["dav1d", "-q", "-i", path, "-o", decoded])
                tool = "dav1d"
            elif entry["codec"] == "vp9":
                result = docker(VPX9, ["vpxdec-hbd", "--rawvideo", "--output-bit-depth=0", "-o", decoded, path])
                if result.returncode != 0:
                    result = docker(VPX9, ["vpxdec-hbd", "--rawvideo", "-o", decoded, path])
                tool = "vpxdec"
            else:
                result = docker(VPX8, ["vpxdec", "--i420", "-o", decoded, path])
                tool = "vpxdec"
            checked += 1
            if result.returncode != 0:
                failures.append(f"{file}: {tool} failed: {result.stderr.decode(errors='replace').strip()}")
            elif compare(name, decoded, failures):
                print(f"{file}: {tool} gives Egami's {entry['frames']} frames ({entry['reencoded']} encoded again)")

        result = docker(FFMPEG, ["ffmpeg", "-v", "error", "-xerror", "-i", path, "-f", "null", "-"])
        errors = result.stderr.decode(errors="replace").strip()
        checked += 1
        if result.returncode != 0 or errors:
            failures.append(f"{file}: FFmpeg: {errors or 'exit code ' + str(result.returncode)}")
            continue
        probe = docker(FFMPEG, ["ffprobe", "-v", "error", "-count_frames", "-select_streams", "v:0", "-show_entries", "stream=nb_read_frames",
                                "-of", "csv=p=0", path])
        frames = probe.stdout.decode().strip().split(",")[0]
        if frames != str(entry["frames"]):
            failures.append(f"{file}: ffprobe counts {frames} frames, the cut has {entry['frames']}")
        else:
            print(f"{file}: FFmpeg decodes {frames} frames without errors")

    for failure in failures:
        print("FAIL", failure)
    print(f"{checked} checks, {len(failures)} failed")
    return 1 if failures or checked == 0 else 0


if __name__ == "__main__":
    sys.exit(main())
