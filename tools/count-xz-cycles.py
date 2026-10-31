"""Counts the user-mode cycles and instructions Egami's xz decoder and liblzma's xz spend per decode of each file, from the
CPU's hardware counters (Linux perf in Docker, as tools/video/count-instructions.py does), so a busy machine barely moves the
comparison that wall-clock timing cannot make.

  - Egami: the benchmarks' --decode-xz mode run with A and B decodes; the difference is B - A decodes, so start-up and JIT drop
    out. Dynamic PGO is off (DOTNET_TieredPGO=0), as in count-instructions.py, so counts repeat from process to process.
  - liblzma: `xz -dc` run once and run on the file concatenated with itself four times (stream padding allows that); the
    difference is three decodes.

Usage, from the repo root with Docker running and the benchmarks built in Release:
  python tools/count-xz-cycles.py .cache/xz [name part...]
"""
import json
import os
import re
import subprocess
import sys

IMAGE = "egami-perf-counters:1"
DOCKERFILE = "FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine\nRUN apk add --no-cache perf ffmpeg\n"
BENCH = "benchmarks/Egami.Benchmarks/bin/Release/net10.0"
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")


def main():
    folder = os.path.abspath(sys.argv[1])
    only = sys.argv[2:]
    manifest = json.load(open(os.path.join(folder, "manifest.json")))["files"]
    names = [n for n in sorted(manifest) if not only or any(o in n for o in only)]
    if not subprocess.run(["docker", "images", "-q", IMAGE], capture_output=True, text=True, env=ENV).stdout.strip():
        subprocess.run(["docker", "build", "-t", IMAGE, "-"], input=DOCKERFILE, text=True, check=True, env=ENV)
    lines = ["set -e", "apk add --no-cache xz > /dev/null",
             "count() { perf stat -x, -e cycles:u,instructions:u \"$@\" 2>&1 >/dev/null | awk -F, '/cycles|instructions/ {print $3, $1}'; }"]
    for n in names:
        length = manifest[n]["length"]
        lines += [f"cat /z/{n}.xz /z/{n}.xz /z/{n}.xz /z/{n}.xz > /tmp/four.xz", f"echo '== {n}'",
                  "echo xz1", f"count xz -dc /z/{n}.xz", "echo xz4", "count xz -dc /tmp/four.xz",
                  "echo eg2", f"DOTNET_TieredPGO=0 count dotnet /b/Egami.Benchmarks.dll --decode-xz /z/{n}.xz {length} 2",
                  "echo eg12", f"DOTNET_TieredPGO=0 count dotnet /b/Egami.Benchmarks.dll --decode-xz /z/{n}.xz {length} 12"]
    out = subprocess.run(["docker", "run", "--rm", "--privileged", "-v", f"{folder}:/z:ro", "-v", f"{os.path.abspath(BENCH)}:/b:ro",
                          IMAGE, "sh", "-c", "\n".join(lines)], capture_output=True, text=True, env=ENV)
    if out.returncode != 0:
        print(out.stdout, out.stderr)
        sys.exit(1)
    results = {}
    current = None
    tag = None
    for line in out.stdout.splitlines():
        if line.startswith("== "):
            current = line[3:]
            results[current] = {}
        elif line in ("xz1", "xz4", "eg2", "eg12"):
            tag = line
        else:
            m = re.match(r"(\S+) (\d+)", line)
            if m and current:
                results[current][(tag, m.group(1).split(":")[0])] = int(m.group(2))
    ratios = []
    for n, r in results.items():
        xz_cycles = (r[("xz4", "cycles")] - r[("xz1", "cycles")]) / 3
        eg_cycles = (r[("eg12", "cycles")] - r[("eg2", "cycles")]) / 10
        xz_ins = (r[("xz4", "instructions")] - r[("xz1", "instructions")]) / 3
        eg_ins = (r[("eg12", "instructions")] - r[("eg2", "instructions")]) / 10
        ratios.append(xz_cycles / eg_cycles)
        print(f"{n:<20} Egami {eg_cycles / 1e6:8.1f} M cycles {eg_ins / 1e6:8.1f} M instr   xz {xz_cycles / 1e6:8.1f} M cycles "
              f"{xz_ins / 1e6:8.1f} M instr   speed {xz_cycles / eg_cycles:.2f}x")
    if ratios:
        mean = 1.0
        for x in ratios:
            mean *= x
        print(f"{len(ratios)} files: geometric mean {mean ** (1 / len(ratios)):.2f}x, worst {min(ratios):.2f}x of xz, in cycles")


if __name__ == "__main__":
    main()
