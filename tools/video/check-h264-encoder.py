"""Checks that FFmpeg and the JM reference decoder decode Egami.Video's H.264 encoder output exactly as Egami does.

Runs the EncoderInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which writes Annex B streams from the H.264 encoder to
.cache/interop/h264video (every effort; Constrained Baseline, Main and High; CABAC and CAVLC; P-only, one B-frame, B pyramids
up to 16 B-frames; 4 and 32 slices; fades with weighted prediction; no adaptive quantization; the keyframe interval; CBR, ABR,
real time and the extreme QPs; sizes that are not whole macroblocks down to 2x2, and a wide frame), each <name>.h264 next to
<name>.yuv, the shown frames Egami's decoder makes of it as planar 4:2:0, and <name>.txt, its width, height and frame count.
Each stream is also encoded at one thread and must give the same bytes. Then, in Docker (egami-h264encref:7.2 from
tools/video/h264encref/Dockerfile: FFmpeg 6.1 and JM 19.0's ldecod, both run only as black-box decoders), decodes each
stream with both and requires the same bytes.

Usage, from the repo root with Docker running:
  docker build -t egami-h264encref:7.2 tools/video/h264encref
  python tools/video/check-h264-encoder.py [--no-test] [--only name,name]
"""
import os
import subprocess
import sys

IMAGE = "egami-h264encref:7.2"
OUT = ".cache/interop/h264video"


def inside():
    failures, checked = [], 0
    for file in sorted(f for f in os.listdir(OUT) if f.endswith(".h264")):
        name = file[:-5]
        base = os.path.abspath(os.path.join(OUT, name))
        with open(base + ".yuv", "rb") as f:
            expected = f.read()
        for decoder, command in (("ffmpeg", ["ffmpeg", "-v", "error", "-y", "-i", base + ".h264", "-f", "rawvideo",
                                             "-pix_fmt", "yuv420p", "/tmp/out.yuv"]),
                                 ("ldecod", ["ldecod", "-i", base + ".h264", "-o", "/tmp/out.yuv"])):
            if os.path.exists("/tmp/out.yuv"):
                os.remove("/tmp/out.yuv")
            result = subprocess.run(command, capture_output=True, text=True, cwd="/tmp")
            if result.returncode != 0 or not os.path.exists("/tmp/out.yuv"):
                failures.append(f"{name}: {decoder} cannot decode it: {(result.stderr or result.stdout).strip()[-400:]}")
                continue
            with open("/tmp/out.yuv", "rb") as f:
                got = f.read()
            if got != expected:
                first = next((i for i in range(min(len(got), len(expected))) if got[i] != expected[i]), min(len(got), len(expected)))
                failures.append(f"{name}: {decoder} gives {len(got)} bytes, Egami {len(expected)}; first difference at byte {first}")
        checked += 1
    for failure in failures:
        print("FAIL", failure)
    print(f"Checked {checked} streams with FFmpeg and JM; {len(failures)} failed.")
    return 1 if failures or checked == 0 else 0


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        return inside()
    if "--no-test" not in sys.argv:
        env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(".cache/interop"))
        if "--only" in sys.argv:
            env["EGAMI_INTEROP_ONLY"] = sys.argv[sys.argv.index("--only") + 1]
        subprocess.run(["dotnet", "run", "--project", "tests/Egami.Video.H264.Tests", "-c", "Release", "--", "-class",
                        "Egami.Video.H264.Tests.EncoderInteropFiles"], env=env, check=True)
    root = os.path.abspath(".")
    return subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/work", "-w", "/work", IMAGE, "python3",
                           "tools/video/check-h264-encoder.py", "--inside"], env=dict(os.environ, MSYS_NO_PATHCONV="1")).returncode


if __name__ == "__main__":
    sys.exit(main())
