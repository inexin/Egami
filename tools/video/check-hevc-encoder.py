"""Checks that FFmpeg decodes Egami.Video's HEVC encoder output exactly as Egami does.

Runs the EncoderInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which writes Annex B streams from the HEVC encoder to
.cache/interop/hevcvideo (every effort; Main and Main 10, from 8-bit and 10-bit frames; no adaptive quantization; the keyframe
interval; constant bitrate and the extreme QPs; sizes that are not whole coding blocks down to 2x2, several CTB rows, and a wide
frame), each <name>.h265 next to <name>.yuv, the shown frames Egami's decoder makes of it as planar 4:2:0 (16-bit little-endian
for Main 10), and <name>.txt, its width, height, frame count and bit depth. Each stream is also encoded at one thread and must
give the same bytes. Then decodes each stream with FFmpeg, run only as a black-box decoder, and requires the same bytes: the
ffmpeg on PATH with --local, else in Docker (egami-h264encref:7.2 from tools/video/h264encref/Dockerfile, FFmpeg 6.1).

Usage, from the repo root:
  python tools/video/check-hevc-encoder.py [--no-test] [--local] [--only name,name]
"""
import os
import subprocess
import sys

IMAGE = "egami-h264encref:7.2"
OUT = ".cache/interop/hevcvideo"


def check(scratch):
    failures, checked = [], 0
    out = os.path.join(scratch, "hevc-out.yuv")
    for file in sorted(f for f in os.listdir(OUT) if f.endswith(".h265")):
        name = file[:-5]
        base = os.path.abspath(os.path.join(OUT, name))
        with open(base + ".yuv", "rb") as f:
            expected = f.read()
        with open(base + ".txt") as f:
            depth = int(f.read().split()[3])
        if os.path.exists(out):
            os.remove(out)
        result = subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", base + ".h265", "-f", "rawvideo", "-pix_fmt",
                                 "yuv420p" if depth == 8 else "yuv420p10le", out], capture_output=True, text=True)
        if result.returncode != 0 or not os.path.exists(out):
            failures.append(f"{name}: ffmpeg cannot decode it: {(result.stderr or result.stdout).strip()[-400:]}")
        else:
            with open(out, "rb") as f:
                got = f.read()
            if got != expected:
                first = next((i for i in range(min(len(got), len(expected))) if got[i] != expected[i]), min(len(got), len(expected)))
                failures.append(f"{name}: ffmpeg gives {len(got)} bytes, Egami {len(expected)}; first difference at byte {first}")
        checked += 1
    for failure in failures:
        print("FAIL", failure)
    print(f"Checked {checked} streams with FFmpeg; {len(failures)} failed.")
    return 1 if failures or checked == 0 else 0


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        return check("/tmp")
    if "--no-test" not in sys.argv:
        env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(".cache/interop"))
        if "--only" in sys.argv:
            env["EGAMI_INTEROP_ONLY"] = sys.argv[sys.argv.index("--only") + 1]
        subprocess.run(["dotnet", "run", "--project", "tests/Egami.Video.Hevc.Tests", "-c", "Release", "--", "-class",
                        "Egami.Video.Hevc.Tests.EncoderInteropFiles"], env=env, check=True)
    if "--local" in sys.argv:
        return check(os.path.abspath(".cache/interop"))
    root = os.path.abspath(".")
    return subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/work", "-w", "/work", IMAGE, "python3",
                           "tools/video/check-hevc-encoder.py", "--inside"], env=dict(os.environ, MSYS_NO_PATHCONV="1")).returncode


if __name__ == "__main__":
    sys.exit(main())
