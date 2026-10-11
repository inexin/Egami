"""Checks that libwebp decodes Egami's WebP files to the pixels they must have.

Runs the WebpInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which writes Egami's WebP files to .cache/interop/webp, each
<name>.webp next to <name>.rgba, the RGBA it must decode to; then, in Docker (alpine:3.20 with libwebp 1.3.2), decodes each file
with tools/webpref/webpref.c and compares:
  - lossless files (VP8L, or the files whose name has no ".lossy"): identical pixels;
  - lossy files: <name>.rgba is Egami's own decode, and its Y, U and V planes (<name>.yuv, written by the test) must equal
    WebPDecodeYUV's, and its RGB every channel within 1 level of libwebp's RGBA, alpha identical.
Animated files (<name>.frames.rgba) are compared frame by frame with WebPAnimDecoder's canvases, under the same rules per frame.
Every file is also run through webpinfo, which checks the container.

Usage, from the repo root with Docker running:
  python tools/check-webp-interop.py
"""
import os
import subprocess
import sys

IMAGE = "alpine:3.20"
PACKAGES = "python3 gcc musl-dev libwebp-dev libwebp-tools"
OUT = ".cache/interop/webp"
TOOL = "/tmp/webpref"


def compare(name, ours, theirs, lossy):
    if len(ours) != len(theirs):
        return f"{name}: {len(ours)} bytes against libwebp's {len(theirs)}"
    if not lossy:
        if ours != theirs:
            first = next(i for i in range(len(ours)) if ours[i] != theirs[i])
            return f"{name}: differs from libwebp at byte {first} (pixel {first // 4})"
        return None
    worst = 0
    for i in range(len(ours)):
        d = abs(ours[i] - theirs[i])
        if i % 4 == 3 and d:
            return f"{name}: alpha differs at pixel {i // 4}"
        worst = max(worst, d)
    if worst > 1:
        return f"{name}: a channel is {worst} levels from libwebp"
    return None


def inside():
    subprocess.run(["gcc", "-O2", "-Wall", "-o", TOOL, "tools/webpref/webpref.c", "-lwebp", "-lwebpdemux"], check=True)
    failures, checked = [], 0
    for file in sorted(f for f in os.listdir(OUT) if f.endswith(".webp")):
        path = os.path.join(OUT, file)
        base = path[:-5]
        lossy = ".lossy" in file
        info = subprocess.run(["webpinfo", "-quiet", path], capture_output=True, text=True)
        if info.returncode != 0:
            failures.append(f"{file}: webpinfo rejects it: {info.stdout.strip()} {info.stderr.strip()}")
            continue
        if os.path.exists(base + ".frames.rgba"):
            result = subprocess.run([TOOL, "anim", path, "/tmp/out.rgba"], capture_output=True, text=True)
            expected = open(base + ".frames.rgba", "rb").read()
        else:
            result = subprocess.run([TOOL, "decode", path, "/tmp/out.rgba"], capture_output=True, text=True)
            expected = open(base + ".rgba", "rb").read()
        if result.returncode != 0:
            failures.append(f"{file}: libwebp cannot decode it: {result.stderr.strip()}")
            continue
        problem = compare(file, expected, open("/tmp/out.rgba", "rb").read(), lossy)
        if problem is None and lossy and os.path.exists(base + ".yuv"):
            result = subprocess.run([TOOL, "yuv", path, "/tmp/out.yuv"], capture_output=True, text=True)
            if result.returncode != 0:
                problem = f"{file}: WebPDecodeYUV fails: {result.stderr.strip()}"
            elif open(base + ".yuv", "rb").read() != open("/tmp/out.yuv", "rb").read():
                problem = f"{file}: the planes differ from WebPDecodeYUV's"
        if problem:
            failures.append(problem)
        checked += 1
    for failure in failures:
        print("FAIL", failure)
    print(f"Checked {checked} files; {len(failures)} failed.")
    sys.exit(1 if failures else 0)


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    env = dict(os.environ, EGAMI_INTEROP_DIR=os.path.abspath(".cache/interop"))
    subprocess.run(["dotnet", "build", "tests/Egami.Tests", "-c", "Release", "-v", "q", "-nologo"], check=True)
    exe = os.path.abspath(os.path.join("tests", "Egami.Tests", "bin", "Release", "net10.0", "Egami.Tests"))
    subprocess.run([exe, "-class", "Egami.Tests.Formats.Webp.WebpInteropFiles"], check=True, env=env)
    repo = os.getcwd()
    subprocess.run(["docker", "run", "--rm", "-v", f"{repo}:/work", "-w", "/work", IMAGE, "sh", "-c",
                    f"apk add --no-cache {PACKAGES} > /dev/null && python3 tools/check-webp-interop.py --inside"], check=True)


if __name__ == "__main__":
    main()
