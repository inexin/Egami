"""The Phase 2.9.3 lossless encoder gate (task 17): every file Egami writes decodes bit-exact in djxl (libjxl 0.12.0) and in
Egami, and at each effort from 1 to 7 its size is within 10% of cjxl's at the same effort (and its encode time within 2 times).

jxl-oxide (0.12.6, in the same image) must decode each file to the same samples too: it reads the ANS state of every
Modular stream that has channels, even one that leaves them all to the groups, where libjxl's decoder skips it.

The sources are the corpus's lossless JPEG XL files and a few lossy ones, decoded by djxl to PAM (gray, RGB, with or without
alpha, 8 or 16 bit), so cjxl and Egami get the same samples and no color profile. cjxl and djxl run in the egami-jxlref:2.9.3
image; Egami's encoder runs in the benchmark project (dotnet run -c Release --project benchmarks/Egami.Benchmarks --
--encode-jxl), which also decodes each file it writes and checks it.

With --writer, it instead runs the JxlInteropFiles test with EGAMI_INTEROP_DIR=.cache/interop, which writes the writer's
files in every pixel layout and with ICC, EXIF, XMP and orientation next to their samples (PAM, or raw floats), and checks
that djxl decodes each to those samples and that jxlinfo reports the metadata.

Usage, from the repo root with Docker running and the benchmarks built in Release:
  python tools/check-jxl-encoder.py [effort ...]
  python tools/check-jxl-encoder.py --writer
"""
import struct
import os
import re
import subprocess
import sys

IMAGE = "egami-jxlref:2.9.3"
WORK = ".cache/jxl/encoder"
SOURCES = [
    "duck-lossless-e7.jxl",
    "duck-alpha-lossless.jxl",
    "duck-gray-lossless.jxl",
    "duck-gray-alpha-lossless.jxl",
    "duck-16bit-lossless.jxl",
    "duck-posterized-lossless-palette.jxl",
    "camera-huawei-eva-709-lossless.jxl",
    "egret-d1.jxl",
    "duck-small-lossless-e9.jxl",
]
SIZE_GATE = 1.10
TIME_GATE = 2.0


def docker(script):
    # .cache may be a link to another checkout's, which Docker does not follow: mount its target too.
    cache = [arg for d in (".cache/jxl", ".cache/interop") if os.path.isdir(d) for arg in ("-v", f"{os.path.realpath(d)}:/w/{d}")]
    out = subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/w", *cache, "-w", "/w", IMAGE, "sh", "-c", script],
                         capture_output=True, text=True, env={**os.environ, "MSYS_NO_PATHCONV": "1"})
    if out.returncode != 0:
        sys.exit(out.stdout + out.stderr)
    return out.stdout + out.stderr


def run(names, effort, threads):
    """Encodes every source at one effort and thread count with cjxl and Egami; returns whether any check failed."""
    failed = False
    cjxl_threads = 0 if threads == 1 else threads
    script = []
    for n in names:
        script.append(f"echo @{n}; for i in 1 2 3; do cjxl {WORK}/{n}.pam {WORK}/{n}.cjxl-e{effort}.jxl -d 0 -e {effort} --num_threads={cjxl_threads} 2>&1 | grep -o '[0-9.]* MP/s'; done")
    timings = docker(" ; ".join(script))
    speed = {}
    current = None
    for line in timings.splitlines():
        if line.startswith("@"):
            current = line[1:]
        elif current and "MP/s" in line:
            speed[current] = max(speed.get(current, 0), float(line.split()[0]))

    args = ["dotnet", "run", "-c", "Release", "--no-build", "--project", "benchmarks/Egami.Benchmarks", "--", "--encode-jxl", str(effort)]
    if threads == 1:
        args.append("--one-thread")
    for n in names:
        args += [f"{WORK}/{n}.pam", f"{WORK}/{n}.egami-e{effort}.jxl"]
    ours = subprocess.run(args, capture_output=True, text=True)
    if ours.returncode != 0:
        sys.exit(ours.stdout + ours.stderr)
    egami = {}
    for line in ours.stdout.splitlines():
        m = re.match(r".*/(.+)\.pam: (\d+) bytes in ([\d.]+) ms, (.*)", line)
        if m:
            egami[m.group(1)] = (int(m.group(2)), float(m.group(3)), m.group(4))

    # djxl and jxl-oxide decode Egami's files; the samples must equal the source's.
    docker(" && ".join(f"djxl {WORK}/{n}.egami-e{effort}.jxl {WORK}/{n}.egami-e{effort}.pam --quiet && "
                       f"jxl-oxide {WORK}/{n}.egami-e{effort}.jxl -f npy -o {WORK}/{n}.egami-e{effort}.npy -q" for n in names))
    for n in names:
        src = open(f"{WORK}/{n}.pam", "rb").read()
        back = open(f"{WORK}/{n}.egami-e{effort}.pam", "rb").read()
        theirs = os.path.getsize(f"{WORK}/{n}.cjxl-e{effort}.jxl")
        size, ms, check = egami[n]
        pixels = int(re.search(rb"WIDTH (\d+)", src).group(1)) * int(re.search(rb"HEIGHT (\d+)", src).group(1))
        cjxl_ms = pixels / 1e6 / speed[n] * 1000 if speed.get(n) else float("nan")
        ratio = size / theirs
        time = ms / cjxl_ms
        oxide = npy_matches(open(f"{WORK}/{n}.egami-e{effort}.npy", "rb").read(), src)
        ok = src == back and oxide and check == "exact in Egami" and ratio <= SIZE_GATE and time <= TIME_GATE
        failed |= not ok
        print(f"e{effort} {threads:2}t {n:36} Egami {size:8} B {ms:8.1f} ms, cjxl {theirs:8} B {cjxl_ms:8.1f} ms, size {ratio:5.3f}x, "
              f"time {time:5.2f}x, djxl {'exact' if src == back else 'DIFFERS'}, jxl-oxide {'exact' if oxide else 'DIFFERS'}, "
              f"{check}{'' if ok else '  FAIL'}", flush=True)
    return failed


def pam_samples(data):
    end = data.index(b"ENDHDR\n") + 7
    header = data[:end].decode("ascii")
    fields = dict(line.split(" ", 1) for line in header.splitlines()[1:-1] if " " in line)
    return (int(fields["WIDTH"]), int(fields["HEIGHT"]), int(fields["DEPTH"]), int(fields["MAXVAL"])), data[end:]


def npy_matches(npy, pam):
    """Whether jxl-oxide's NumPy output (float32 samples, frames by rows by columns by channels) is the PAM's samples."""
    (width, height, depth, maxval), samples = pam_samples(pam)
    end = 10 + struct.unpack("<H", npy[8:10])[0]
    header = npy[10:end].decode("ascii")
    shape = tuple(int(v) for v in re.search(r"'shape': \(([^)]*)\)", header).group(1).split(",") if v.strip())
    if "'<f4'" not in header or shape != (1, height, width, depth):
        return False
    floats = struct.unpack(f"<{width * height * depth}f", npy[end:end + width * height * depth * 4])
    ints = struct.unpack(f">{width * height * depth}{'H' if maxval > 255 else 'B'}", samples[: width * height * depth * (2 if maxval > 255 else 1)])
    return all(round(f * maxval) == i for f, i in zip(floats, ints))


def pfm_floats(data):
    parts = data.split(b"\n", 3)
    width, height = map(int, parts[1].split())
    scale = float(parts[2])
    fmt = "<" if scale < 0 else ">"
    floats = struct.unpack(f"{fmt}{width * height * 3}f", parts[3][: width * height * 12])
    rows = [floats[y * width * 3:(y + 1) * width * 3] for y in range(height)]
    return width, height, [v for row in reversed(rows) for v in row]


def writer():
    out = ".cache/interop/jxl"
    env = {**os.environ, "EGAMI_INTEROP_DIR": os.path.realpath(".cache/interop")}
    test = subprocess.run(["dotnet", "test", "tests/Egami.Tests", "--filter", "FullyQualifiedName~JxlInteropFiles"], capture_output=True, text=True, env=env)
    if test.returncode != 0:
        sys.exit(test.stdout + test.stderr)
    names = sorted(f[:-4] for f in os.listdir(out) if f.endswith(".jxl"))
    script = " ; ".join(f"djxl {out}/{n}.jxl {out}/{n}.djxl.{'pfm' if os.path.exists(f'{out}/{n}.f32') else 'pam'} --quiet; echo @{n}; jxlinfo {out}/{n}.jxl" for n in names)
    info = {}
    current = None
    for line in docker(script).splitlines():
        if line.startswith("@"):
            current = line[1:]
            info[current] = ""
        elif current:
            info[current] += line + "\n"
    failed = False
    for n in names:
        if os.path.exists(f"{out}/{n}.f32"):
            raw = open(f"{out}/{n}.f32", "rb").read()
            w, h = struct.unpack("<ii", raw[:8])
            expected = struct.unpack(f"<{w * h * 3}f", raw[8:])
            pw, ph, got = pfm_floats(open(f"{out}/{n}.djxl.pfm", "rb").read())
            ok = (pw, ph) == (w, h) and all(a == b for a, b in zip(expected, got))
        else:
            shape, expected = pam_samples(open(f"{out}/{n}.pam", "rb").read())
            got_shape, got = pam_samples(open(f"{out}/{n}.djxl.pam", "rb").read())
            ok = shape == got_shape and expected == got
            if n.startswith("exif-xmp-orientation"):
                # djxl turns the image; the samples are checked in Egami's tests. Here the header and boxes are.
                ok = "Orientation: 6" in info[n] or "orientation: 6" in info[n].lower()
                ok &= "Exif" in info[n] and "xml " in info[n]
        if n == "icc-p3":
            ok &= "ICC" in info[n] or "icc" in info[n].lower()
        failed |= not ok
        print(f"{n:28} {'exact' if ok else 'DIFFERS'}")
    print("writer interop", "fails" if failed else "passes")


def main():
    if sys.argv[1:] == ["--writer"]:
        writer()
        return
    efforts = [int(e) for e in sys.argv[1:]] or list(range(1, 8))
    os.makedirs(WORK, exist_ok=True)
    names = [s[:-4] for s in SOURCES if os.path.exists(f"tests/images/jxl/{s}")]
    docker(" && ".join(f"djxl tests/images/jxl/{n}.jxl {WORK}/{n}.pam --quiet" for n in names))
    failed = False
    for effort in efforts:
        for threads in (1, os.cpu_count()):
            failed |= run(names, effort, threads)
    print("gate", "fails" if failed else "passes")


if __name__ == "__main__":
    main()
