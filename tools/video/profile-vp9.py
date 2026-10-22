"""The V3 VP9 decode profile: where one thread's time goes in Egami.Video and in libvpx, stage by stage, from the same sampler.

Both run under Linux perf (cpu-clock, timer interrupts, so no safepoint bias) in Docker on the same machine:
  - Egami.Video: the benchmark project's Release build (--time-vp9), run by .NET in mcr.microsoft.com/dotnet/runtime:10.0-alpine
    with perf added (image egami-dotnet-perf:2.4.5, built here if missing). JIT code is resolved through the runtime's perf map,
    with W^X off so it sits in anonymous memory where perf looks for it; recording starts after a delay, so the samples are of
    Tier-1 code.
  - libvpx: vpxdec-fast (libvpx 1.14.1's SIMD build; vpxdec-fast-hbd for clips named 10bit or 12bit) in egami-vp9ref:v3-perf, the reference image with perf added.

Each function's self time goes to a stage, scaled by the decode time per packet measured under perf (best run). Take the shares
from here and the speed ratios from the benchmark's --time-vp9 against timing.json. With --blocks, the hottest JIT instruction
groups of a method are printed (DOTNET_JitDisasm from the same run).

Usage, from the repo root with Docker running and the benchmarks built in Release:
  python tools/video/profile-vp9.py [clip.ivf ...]            (default: the large 720p and 1080p clips under .cache/video/vp9/large)
  python tools/video/profile-vp9.py --blocks <Class::Method> <clip.ivf>
"""
import bisect
import collections
import os
import re
import subprocess
import sys

OUT = ".cache/video/vp9/perf"
BENCH = "benchmarks/Egami.Video.Benchmarks/bin/Release/net10.0"
DOTNET_IMAGE = "egami-dotnet-perf:2.4.5"
VPX_IMAGE = "egami-vp9ref:v3-perf"
CLIPS = [".cache/video/vp9/large/720p.ivf", ".cache/video/vp9/large/1080p.ivf"]
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")
STAGE_NAMES = ["Tokens", "Modes", "Inter", "Reconstruct", "Intra", "Filter", "Borders", "Glue", "Other"]

EGAMI_STAGES = [("Tokens", r"DecodeCoefs|DecodeBlockTokens|ReadCoeff|BoolDecoder"),
                ("Modes", r"ModeInfo|ReadMv|FindMvRefs|ReadRef|ReadInter|ReadIntra|ModeContext|AssignMv|Partition|Segment"),
                ("Inter", r"Convolve|BuildInterPredictor|Memmove"),
                ("Reconstruct", r"Transform|Idct|Iadst|Iwht|InverseAdd|InverseTransform"),
                ("Intra", r"Vp9Intra|PredictAndReconstructIntra"),
                ("Filter", r"LoopFilter|Edge\d|Filter\d|Selectively"),
                ("Borders", r"Extend|Border"),
                ("Glue", r"DecodeFrame|DecodeTiles|DecodeBlock|Vp9StreamDecoder|Clear|ZeroMemory")]
LIBVPX_STAGES = [("Tokens", r"decode_coefs|detokenize|vpx_reader|read_coeff|vpx_read"),
                 ("Modes", r"read_.*mode|read_mv|mv_ref|dec_find|read_ref|read_inter|read_intra|decode_partition|read_partition|segment"),
                 ("Inter", r"convolve|dec_build_inter|build_mc_border|highbd_inter|scaled_"),
                 ("Reconstruct", r"idct|iadst|iwht|inverse_transform"),
                 ("Intra", r"predictor|intra_pred|build_intra"),
                 ("Filter", r"lpf_|loop_filter|filter_block_plane|lf_"),
                 ("Borders", r"extend"),
                 ("Glue", r"decode_tiles|decode_block|vp9_receive|vpx_codec|memcpy|memset|swap")]

EGAMI_SCRIPT = """
cd /tmp
export DOTNET_PerfMapEnabled=3 DOTNET_EnableWriteXorExecute=0 DOTNET_JitDisasm="$3" DOTNET_JitStdOutFile=/work/{out}/$1.disasm
perf record -q -e cpu-clock -F 10000 -D $2 -o p.data -- dotnet /bench/Egami.Video.Benchmarks.dll --time-vp9 --one-thread /work/$4 /work/$4 > /work/{out}/$1.out 2>&1
perf report -q -i p.data --stdio --sort symbol --percent-limit 0.1 > /work/{out}/$1.report 2>/dev/null
perf script -i p.data -F ip > /work/{out}/$1.ips 2>/dev/null
cp /tmp/perf-*.map /work/{out}/$1.map
""".format(out=OUT)

LIBVPX_SCRIPT = """
perf record -q -e cpu-clock -F 10000 -o /tmp/p.data -- sh -c "for i in 1 2 3 4 5; do vpxdec-fast$3 --noblit --summary -t 1 /work/$2; done" > /work/{out}/$1.out 2>&1
perf report -q -i /tmp/p.data --stdio --sort symbol --percent-limit 0.1 > /work/{out}/$1.report 2>/dev/null
""".format(out=OUT)


def docker(image, script, *args, bench=None):
    mounts = ["-v", f"{os.getcwd()}:/work"] + (["-v", f"{os.path.abspath(bench)}:/bench"] if bench else [])
    subprocess.run(["docker", "run", "--rm", "--privileged", *mounts, image, "sh", "-c", script, "sh", *args], env=ENV, check=True)


def ensure_images():
    have = subprocess.run(["docker", "images", "--format", "{{.Repository}}:{{.Tag}}"], capture_output=True, text=True, env=ENV).stdout.split()
    if DOTNET_IMAGE not in have:
        subprocess.run(["docker", "build", "-t", DOTNET_IMAGE, "-"], input="FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine\nRUN apk add --no-cache perf\n",
                       text=True, env=ENV, check=True)
    if VPX_IMAGE not in have:
        subprocess.run(["docker", "build", "-t", VPX_IMAGE, "-"], input="FROM egami-vp9ref:v3\nRUN apk add --no-cache perf\n", text=True, env=ENV, check=True)


def name(clip):
    return clip.replace(".cache/video/vp9/", "").replace(".ivf", "").replace("/", "-")


def profile(clip, methods="*Vp9*", libvpx=True):
    n = name(clip)
    # Two passes of the clip, five runs each; the first seconds (start-up and tiering) are left out.
    docker(DOTNET_IMAGE, EGAMI_SCRIPT, "egami-" + n, "5000", methods, clip, bench=BENCH)
    egami = min(float(m.replace(",", ".")) for m in re.findall(r": ([\d.,]+) ms a packet", read(f"egami-{n}.out")))
    if not libvpx:
        return egami, None
    # The high bit depth build decodes 10 and 12 bit; the 8-bit one is faster at 8 bit.
    docker(VPX_IMAGE, LIBVPX_SCRIPT, "libvpx-" + n, clip, "-hbd" if "bit" in n else "")
    vpx = re.findall(r"(\d+) decoded frames/\d+ showed frames in (\d+) us", read(f"libvpx-{n}.out"))
    return egami, min(int(us) / 1000 / int(frames) for frames, us in vpx)


def read(file):
    with open(os.path.join(OUT, file), encoding="utf-8", errors="replace") as f:
        return f.read()


def rows(file):
    for line in read(file).splitlines():
        m = re.match(r"\s*([\d.]+)%\s+(?:\[JIT\] tid \d+\s+)?\[[.k]\]\s+(.*)", line)
        if m:
            yield float(m.group(1)), m.group(2).strip()


def stages(file, table):
    grouped = collections.Counter()
    for pct, symbol in rows(file):
        key = symbol.split("(")[0]
        grouped[next((stage for stage, pattern in table if re.search(pattern, key)), "Other")] += pct
    return grouped


def report(clip, egami, libvpx):
    n = name(clip)
    ge, gv = stages(f"egami-{n}.report", EGAMI_STAGES), stages(f"libvpx-{n}.report", LIBVPX_STAGES)
    se, sv = sum(ge.values()), sum(gv.values())
    print(f"\n{n}: Egami.Video {egami:.2f} ms, libvpx {libvpx:.2f} ms a packet under perf")
    print(f"  {'stage':12} {'Egami':>7} {'ms':>6}   {'libvpx':>7} {'ms':>6}   {'gap ms':>7}")
    for stage in STAGE_NAMES:
        pe, pv = ge[stage] / se, gv[stage] / sv
        print(f"  {stage:12} {pe:7.1%} {pe * egami:6.2f}   {pv:7.1%} {pv * libvpx:6.2f}   {pe * egami - pv * libvpx:+7.2f}")
    print("  Egami.Video's top functions:", "; ".join(f"{re.sub(r'.*?([A-Za-z0-9+`]+::[A-Za-z0-9_]+).*', r'\1', s)} {p:.1f}%" for p, s in list(rows(f"egami-{n}.report"))[:10]))
    print("  libvpx's top functions:", "; ".join(f"{s.split()[0]} {p:.1f}%" for p, s in list(rows(f"libvpx-{n}.report"))[:10]))


def blocks(method, clip, top=6, lines=16):
    """Prints the hottest instruction groups of one JIT method from the last profile of the clip."""
    n = "egami-" + name(clip)
    bare = method.split("::")[-1]
    spans = []
    for line in read(f"{n}.map").splitlines():
        start, size, symbol = line.split(" ", 2)
        if "::" + bare + "(" in symbol and "[Optimized" in symbol:
            spans.append((int(start, 16), int(size, 16)))
    offsets = collections.Counter()
    total = 0
    for line in read(f"{n}.ips").splitlines():
        try:
            ip = int(line.split()[0], 16)
        except (ValueError, IndexError):
            continue
        total += 1
        for start, size in spans:
            if start <= ip < start + size:
                offsets[(start, ip - start)] += 1
    if not offsets:
        print("no samples in", method)
        return
    main = collections.Counter(s for s, _ in offsets.elements()).most_common(1)[0][0]
    own = {o: c for (s, o), c in offsets.items() if s == main}
    listing = [l for l in re.split(r"(?m)^; Assembly listing for method ", read(f"{n}.disasm"))[1:]
               if ":" + bare + "(" in l.split("\n", 1)[0] and re.search(r"\((Tier1|Tier0-FullOpts|FullOpts)\)", l.split("\n", 1)[0])][-1]
    groups = [(int(m.group(2), 16), m.group(1)) for m in re.finditer(r"(?m)^(G_M\d+_IG\d+):\s+;;.*?offset=0x([0-9A-F]+)", listing)]
    body, current = collections.defaultdict(list), None
    for line in listing.splitlines():
        m = re.match(r"^(G_M\d+_IG\d+):", line)
        if m:
            current = m.group(1)
        elif current and line.strip() and not line.strip().startswith(";"):
            body[current].append(line.strip())
    starts = [o for o, _ in groups]
    hot = collections.Counter()
    for offset, count in own.items():
        hot[groups[max(0, bisect.bisect_right(starts, offset) - 1)][1]] += count
    print(f"{method}: {sum(own.values()) / total:.1%} of the samples")
    for group, count in hot.most_common(top):
        print(f"--- {group}: {count / sum(own.values()):.1%} of the method")
        for instruction in body[group][:lines]:
            print("   ", instruction)


def main():
    os.makedirs(OUT, exist_ok=True)
    ensure_images()
    if sys.argv[1:2] == ["--blocks"]:
        method, clip = sys.argv[2], sys.argv[3]
        profile(clip, method.split("::")[-1], libvpx=False)
        blocks(method, clip)
        return
    for clip in sys.argv[1:] or CLIPS:
        report(clip, *profile(clip))


if __name__ == "__main__":
    main()
