"""Times Egami's hot paths at four instruction-set settings, for roadmap 2.4.13's SIMD work (ARM64 and AVX-512).

Every scenario runs on one thread under each setting, in a fresh process with that setting's environment:
  default     what the CPU has
  no-avx512   DOTNET_EnableAVX512F=0 (and DOTNET_EnableAVX512=0): AVX2 is the widest
  no-avx2     DOTNET_EnableAVX2=0: Vector256 is not accelerated, so this is the 128-bit path ARM64 takes (the ARM64 stand-in)
  scalar      DOTNET_EnableHWIntrinsic=0: no SIMD at all
On arm64 the two x86 settings change nothing, so only default and scalar run there unless --settings names them.

The scenarios are the Egami.Benchmarks --time-image ones (the 4000 x 3000 benchmark photo; see benchmarks/Egami.Benchmarks/
ImageTiming.cs) other than the jpeg-stage-* stages, and --time-jxl decodes of a small corpus that the benchmarks write with
Egami's own encoders into .cache/simd-corpus (the photo at 1920 x 1080 as JPEG, lossy and lossless WebP and JPEG XL, PNG, AVIF
and HEIC). The jpeg-decode-* scenarios need the cjpeg files of tools/make-jpeg-bench-files.py in .cache/bench-jpeg and are left
out when those are missing. Each time is the median of --runs runs after --warmup untimed runs, in milliseconds; the runtime's
tiered JIT stays on, as in an app, so the corpus decodes (small files, called often) take --decode-warmup runs, 30 by
default, before the JIT has optimized every method they call. Keep the machine quiet while it runs: the scalar setting alone takes several minutes.

The script prints a table and writes JSON (--json, default .cache/time-simd.json) with the machine, what the runtime reported
for each setting (--simd-info) and every time. It works on Windows, Linux and macOS, x64 and arm64. Run from the repo root:
  python tools/time-simd.py [--runs=5] [--warmup=3] [--decode-warmup=30] [--settings=default,no-avx2] [--only=name,...]
                            [--skip=avif,heic] [--json=path] [--no-build] [--fresh-corpus]
--only keeps the scenarios whose name starts with one of the given parts (corpus decodes are named decode-<file>).
.github/workflows/benchmarks-arm64.yml runs it on GitHub's ARM64 Linux runner when started by hand.
"""
import json
import os
import platform
import re
import subprocess
import sys

PROJECT = "benchmarks/Egami.Benchmarks"
DLL = os.path.join(PROJECT, "bin", "Release", "net10.0", "Egami.Benchmarks.dll")
CORPUS = ".cache/simd-corpus"
BENCH_JPEG = ".cache/bench-jpeg/photo-420.jpg"
SETTINGS = {
    "default": {},
    "no-avx512": {"DOTNET_EnableAVX512F": "0", "DOTNET_EnableAVX512": "0"},
    "no-avx2": {"DOTNET_EnableAVX2": "0"},
    "scalar": {"DOTNET_EnableHWIntrinsic": "0"},
}
CORPUS_FILES = {
    "photo-420.jpg": "jpeg", "photo-444.jpg": "jpeg", "photo-progressive.jpg": "jpeg", "photo-lossy.webp": "webp",
    "photo-lossless.webp": "webp", "photo-lossy.jxl": "jxl", "photo-lossless.jxl": "jxl", "photo.png": "png",
    "photo.avif": "avif", "photo.heic": "heic",
}
IMAGE_LINE = re.compile(r"^(\S+)\s+1 thread\s+([\d.]+) ms\s+(\d+) bytes")
DECODE_LINE = re.compile(r"^(.*): ([\d.]+) ms an image on 1 thread")


def option(args, name, default):
    for arg in args:
        if arg.startswith(f"--{name}="):
            return arg.split("=", 1)[1]
    return default


def run(args, settings=None):
    # Invariant culture, so the numbers print with a decimal point everywhere.
    env = dict(os.environ, DOTNET_SYSTEM_GLOBALIZATION_INVARIANT="1", **(settings or {}))
    out = subprocess.run(["dotnet", DLL, *args], capture_output=True, text=True, env=env)
    if out.returncode != 0:
        sys.exit(f"Egami.Benchmarks {' '.join(args)} failed:\n{out.stdout}\n{out.stderr}")
    return out.stdout.splitlines()


def machine():
    info = {"system": platform.system(), "machine": platform.machine(), "processor": platform.processor(), "cpus": os.cpu_count()}
    try:
        with open("/proc/cpuinfo", encoding="utf-8") as cpuinfo:
            for line in cpuinfo:
                if line.lower().startswith(("model name", "cpu part", "hardware")):
                    info["model"] = line.split(":", 1)[1].strip()
                    break
    except OSError:
        pass
    if platform.system() == "Darwin":
        out = subprocess.run(["sysctl", "-n", "machdep.cpu.brand_string"], capture_output=True, text=True)
        info["model"] = out.stdout.strip()
    elif platform.system() == "Windows":
        out = subprocess.run(["powershell", "-NoProfile", "-Command", "(Get-CimInstance Win32_Processor).Name"], capture_output=True, text=True)
        info["model"] = out.stdout.strip()
    return info


def main():
    args = sys.argv[1:]
    runs = int(option(args, "runs", "5"))
    warmup = int(option(args, "warmup", "3"))
    decode_warmup = int(option(args, "decode-warmup", "30"))
    arm = platform.machine().lower() in ("arm64", "aarch64")
    names = option(args, "settings", "default,scalar" if arm else ",".join(SETTINGS)).split(",")
    unknown = [n for n in names if n not in SETTINGS]
    if unknown:
        sys.exit(f"unknown settings {unknown}; known: {list(SETTINGS)}")
    only = [p for p in option(args, "only", "").split(",") if p]
    skip = {s for s in option(args, "skip", "").split(",") if s}
    output = option(args, "json", ".cache/time-simd.json")

    if "--no-build" not in args:
        subprocess.run(["dotnet", "build", "-c", "Release", PROJECT, "-v", "q", "-nologo"], check=True)

    files = [f for f, kind in CORPUS_FILES.items() if kind not in skip]
    if "--fresh-corpus" in args or any(not os.path.exists(os.path.join(CORPUS, f)) for f in files):
        print(f"Writing the corpus to {CORPUS}")
        skipped = [kind for kind in set(CORPUS_FILES.values()) if kind in skip]
        for line in run(["--write-simd-corpus", CORPUS, *([f"--skip={','.join(skipped)}"] if skipped else [])]):
            print("  " + line)

    scenarios = [s for s in run(["--time-image", "--list"]) if s and not s.startswith("jpeg-stage-")]
    if not os.path.exists(BENCH_JPEG):
        print(f"{BENCH_JPEG} is missing (tools/make-jpeg-bench-files.py makes it): leaving out the jpeg-decode-* scenarios")
        scenarios = [s for s in scenarios if not s.startswith("jpeg-decode-")]
    decodes = [f for f in files if not only or any(f"decode-{f}".startswith(p) for p in only)]
    if only:
        scenarios = [s for s in scenarios if any(s.startswith(p) for p in only)]

    results = {}
    runtime = {}
    timing = [f"--runs={runs}", "--median", "--one-thread"]
    for setting in names:
        env = SETTINGS[setting]
        runtime[setting] = dict(line.split("=", 1) for line in run(["--simd-info"], env) if "=" in line)
        print(f"{setting}: {', '.join(k for k, v in runtime[setting].items() if v == 'True') or 'no SIMD'}", flush=True)
        if scenarios:
            for line in run(["--time-image", *timing, f"--warmup={warmup}", *scenarios], env):
                if match := IMAGE_LINE.match(line):
                    results.setdefault(match[1], {})[setting] = float(match[2])
        if decodes:
            for line in run(["--time-jxl", *timing, f"--warmup={decode_warmup}", *(os.path.join(CORPUS, f) for f in decodes)], env):
                if match := DECODE_LINE.match(line):
                    results.setdefault(f"decode-{os.path.basename(match[1])}", {})[setting] = float(match[2])

    width = max([len(n) for n in results] + [8])
    header = f"{'scenario':<{width}} " + " ".join(f"{s:>10}" for s in names)
    ratio = "default" in names and "scalar" in names
    print()
    print(header + ("  scalar/default" if ratio else ""))
    for name, times in results.items():
        row = f"{name:<{width}} " + " ".join(f"{times[s]:>10.2f}" if s in times else f"{'-':>10}" for s in names)
        if ratio and times.get("default"):
            row += f"  {times['scalar'] / times['default']:>13.2f}x"
        print(row)
    print("(milliseconds on one thread, median of", runs, "runs)")

    os.makedirs(os.path.dirname(output) or ".", exist_ok=True)
    head = subprocess.run(["git", "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    with open(output, "w", encoding="utf-8") as stream:
        json.dump({"commit": head, "machine": machine(), "runs": runs, "warmup": warmup, "decode_warmup": decode_warmup, "threads": 1,
                   "settings": {s: SETTINGS[s] for s in names}, "runtime": runtime, "milliseconds": results}, stream, indent=2)
    print(f"Wrote {output}")


if __name__ == "__main__":
    main()
