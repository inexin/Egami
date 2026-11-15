"""The H.264 encoder speed profile (H.264 Encoder Speed Build Plan, task 1): where one thread's cycles go, stage by stage; cycles
a frame with tiered PGO on (the runtime users get) and off; and the encoder's work per macroblock from its counters.

  stages    perf record (cpu-clock, frame-pointer call graphs) of the benchmark project's --encode-h264 in Docker
            (egami-dotnet-perf:2.4.5), JIT code resolved through the runtime's perf map (DOTNET_PerfMapEnabled=3, W^X off).
            Recording starts after --delay seconds, so start-up and tiering stay out. Each stage's share is inclusive (a sample
            counts in every stage on its stack) and split by picture type; with a cycles run of the same clip, effort and
            quality, the share times the cycles a frame gives each stage's cycles a frame. The top methods by self time follow.
  cycles    user-mode cycles and instructions a frame from perf stat (egami-perf-counters:2): Egami at --runs 1 and 3, the
            difference over the extra frames, so start-up and the JIT drop out; with PGO on (default runtime) and off
            (DOTNET_TieredPGO=0); Egami without adaptive quantization, tuned for PSNR as x264 is. x264 --preset medium --tune psnr
            (egami-h264encref:7.2, black box) at the CRF that gives effort 4's PSNR-Y at the same quality, at N and 2N frames, for
            the ratio. Each count is the least of --repeats runs (time-h264-encoder.py's counters).
  counts    the encoder's work counters (H264EncodeStats) by picture type, divided by the macroblocks of that type: full RD codings,
            inter candidates and luma codings, intra passes, RDOQ blocks and RDOQ rate counts, block searches, whole-sample
            costs (SAD), subpel costs (MC + SATD), B partition costs, SATD calls and samples. The counters exist only in a build
            with -p:EgamiH264Stats=true, which this makes under .cache/h264prof/stats-build (the normal Release build has none,
            and its output is the same).
  bits      the coded bits by picture kind (I, P, referenced B, unreferenced B) from the same counting build: pictures, bits a
            picture and share of the stream, the CABAC macroblock bits split into modes (skip flag, mb_type, sub_mb_type, intra
            modes, coded_block_pattern, transform flag), mb_qp_delta, ref_idx, mvd and residual by the rate counter's estimate
            of each bin (the rest of the bits, slice headers and the coder's rounding, is "other"), and the share of skipped,
            direct, direct-with-residual, partitioned inter and intra macroblocks. Per clip at each of --qualities, then the
            clips together (the H.264 compression Build Plan's task 3); Egami without adaptive quantization unless --aq.
  baseline  cycles, counts and BD-rate (time-h264-encoder.py bd against x264 medium, PSNR-Y, 60 frames) for --efforts on every
            clip, then the table (report).
  report    the baseline table from the results so far, as Markdown, also written to .cache/h264prof/baseline.md.

Perf counts barely move when the machine is busy; the stage shares are of the encoder process's own samples. Wall times are
not used.

Usage, from the repo root with Docker running and the benchmarks built in Release
(dotnet build -c Release benchmarks/Egami.Video.Benchmarks):
  python tools/video/profile-h264-encoder.py stages [--clips crowd_run] [--effort 4] [--quality 50] [--frames 20] [--no-pgo]
  python tools/video/profile-h264-encoder.py cycles [--efforts 0,4] [--clips a,b] [--quality 50] [--frames 20]
  python tools/video/profile-h264-encoder.py counts [--efforts 0,4] [--clips a,b] [--quality 50] [--frames 20]
  python tools/video/profile-h264-encoder.py bits [--effort 4] [--clips a,b | --wide] [--qualities 50,34] [--frames 60] [--aq]
  python tools/video/profile-h264-encoder.py baseline [--efforts 0-10] [--jobs 4]
  python tools/video/profile-h264-encoder.py report [--efforts 0-10]
Every mode takes --out <folder> (default .cache/h264prof) for its results.
"""
import argparse
import collections
import concurrent.futures
import gzip
import importlib.util
import json
import os
import re
import subprocess

OUT = ".cache/h264prof"
STATS_BUILD = ".cache/h264prof/stats-build"
STATS_BENCH = f"{STATS_BUILD}/bin/Egami.Video.Benchmarks/release/Egami.Video.Benchmarks.dll"
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")
TYPES = ["P", "B", "I"]

_spec = importlib.util.spec_from_file_location("time_h264", os.path.join(os.path.dirname(__file__), "time-h264-encoder.py"))
th = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(th)

# Inclusive stages: a sample counts in a stage when a frame of its stack matches the first pattern and none matches the second.
# Indented rows are parts of the row above. Refine is inlined into Search, so subpel is Search without SearchWhole.
STAGES = [
    ("Pictures: I", r"H264PictureEncoder::EncodeIntra$"),
    ("Pictures: P", r"H264PictureEncoder::EncodePredicted$"),
    ("Pictures: B", r"H264PictureEncoder::EncodeBidirectional$"),
    ("Lookahead and B planning", r"H264VideoEncoder::Send$"),
    ("Rate control and AQ", r"RateController|AdaptQuantizers"),
    ("Deblocking", r"H264Deblock::"),
    ("Reference planes", r"H264RefPlanes::Build"),
    ("Motion search", r"H264MbEncoder::(Search16x16|SearchHalves|Search8x8|SearchBPart|SearchB8x8)$"),
    ("  whole-sample (SearchWhole)", r"H264MbEncoder::SearchWhole$"),
    ("  subpel (Refine)", r"H264MbEncoder::(Refine|Search)$", r"H264MbEncoder::SearchWhole$"),
    ("  B partition costs (PartSatd)", r"H264MbEncoder::PartSatd$"),
    ("Inter RD coding (CodeInter)", r"H264MbEncoder::CodeInter$"),
    ("  luma (CodeInterLuma)", r"H264MbEncoder::CodeInterLuma$"),
    ("Intra RD coding (Encode)", r"H264MbEncoder::Encode$"),
    ("  intra mode choice (TryIntra)", r"H264MbEncoder::TryIntra$"),
    ("Macroblock rate (Rate)", r"H264MbEncoder::Rate$"),
    ("Quantization and RDOQ (QuantizeBlock)", r"H264MbEncoder::QuantizeBlock$"),
    ("  RDOQ rate counts (BlockRate)", r"H264MbEncoder::BlockRate$"),
    ("CABAC block counting (BlockCabac, any caller)", r"H264MbSyntax::BlockCabac$"),
    ("Transforms", r"H264Forward::|H264Transform::"),
    ("Motion compensation (H264RefPlanes)", r"H264RefPlanes::(Luma|Chroma|Sources|Weigh)"),
    ("SATD kernel", r"H264EncodeSimd::Satd$|H264MbEncoder::Satd$"),
    ("SAD kernel", r"H264EncodeSimd::Sad$|H264MbEncoder::Sad$"),
    ("Intra prediction", r"H264IntraPredict::"),
    ("Slice writing (outside the rate counts)", r"H264MbSyntax::Write", r"H264MbEncoder::(Rate|BlockRate)$"),
]

PERF_SCRIPT = """
cd /tmp
export DOTNET_PerfMapEnabled=3 DOTNET_EnableWriteXorExecute=0 {pgo}
perf record -q -e cpu-clock -F 2000 -g -D {delay} -o p.data -- dotnet /work/{bench} --encode-h264 /work/{clip} --frames={frames} \
  --quality={quality} --effort={effort} --threads=1 --runs={runs} > /work/{out}.json 2>&1
perf script -i p.data -F ip,sym 2>/dev/null | gzip > /work/{out}-stacks.txt.gz
"""


def efforts(text):
    values = []
    for part in text.split(","):
        a, _, b = part.partition("-")
        values += range(int(a), int(b or a) + 1)
    return values


def frame_name(line):
    s = re.sub(r"^\s*[0-9a-f]+ ", "", line.rstrip())
    s = re.sub(r"\(.*", "", s)
    s = re.sub(r".*\] ", "", s)
    return s.replace("Egami.Video.Codecs.H264.", "").replace("Egami.Video.Codecs.Encoding.", "").replace("Egami.Video.Codecs.", "")


def read_stacks(path):
    stacks, current = [], []
    with gzip.open(path, "rt", errors="replace") as f:
        for line in f:
            if line.strip():
                current.append(frame_name(line))
            elif current:
                stacks.append(current)
                current = []
    if current:
        stacks.append(current)
    return stacks


def stage_table(stacks, cycles=None):
    """The stage rows: share of all samples, shares within I, P and B pictures, and cycles a frame when known."""
    n = len(stacks)
    types = [re.compile(stage[1]) for stage in STAGES[:3]]
    kinds = [next((i for i, t in enumerate(types) if any(t.search(f) for f in s)), -1) for s in stacks]
    rows = []
    for name, *patterns in STAGES:
        include, exclude = re.compile(patterns[0]), re.compile(patterns[1]) if len(patterns) > 1 else None
        hits = [any(include.search(f) for f in s) and not (exclude and any(exclude.search(f) for f in s)) for s in stacks]
        share = sum(hits) / n
        split = [sum(1 for h, k in zip(hits, kinds) if h and k == t) / n for t in (0, 1, 2)]
        rows.append((name, share, split, share * cycles if cycles else None))
    self_time = collections.Counter(s[0] for s in stacks)
    return rows, [(name, c / n) for name, c in self_time.most_common(30)]


def stages_text(title, rows, top):
    lines = [title, "", "| stage | all | in I | in P | in B | M cycles a frame |", "|---|---:|---:|---:|---:|---:|"]
    for name, share, split, cycles in rows:
        c = f"{cycles / 1e6:.0f}" if cycles else ""
        lines.append(f"| {name} | {share:.1%} | {split[0]:.1%} | {split[1]:.1%} | {split[2]:.1%} | {c} |")
    lines += ["", "| self | method |", "|---:|---|"] + [f"| {share:.2%} | {name} |" for name, share in top]
    return "\n".join(lines)


def run_stages(args):
    os.makedirs(OUT, exist_ok=True)
    for clip in args.clips.split(","):
        tag = f"stages-{clip}-e{args.effort}-q{args.quality}" + ("-nopgo" if args.no_pgo else "")
        script = PERF_SCRIPT.format(pgo="DOTNET_TieredPGO=0" if args.no_pgo else "", delay=int(args.delay * 1000), bench=th.BENCH,
                                    clip=th.clip_path(clip, args.size), frames=args.frames, quality=args.quality, effort=args.effort,
                                    runs=args.runs, out=f"{OUT}/{tag}")
        th.checked(th.docker(th.DOTNET_IMAGE, script, privileged=True))
        stacks = read_stacks(f"{OUT}/{tag}-stacks.txt.gz")
        cycles = load(f"cycles-{clip}-e{args.effort}-q{args.quality}")
        per_frame = cycles and cycles["nopgo" if args.no_pgo else "pgo"]["cycles"]
        rows, top = stage_table(stacks, per_frame)
        title = (f"{clip}, {args.size}p, effort {args.effort}, quality {args.quality}, PGO {'off' if args.no_pgo else 'on'}:"
                 f" {len(stacks)} samples" + (f", {per_frame / 1e6:.0f} M cycles a frame" if per_frame else ""))
        text = stages_text(title, rows, top)
        with open(f"{OUT}/{tag}.md", "w") as f:
            f.write(text + "\n")
        print("\n" + text)


def load(name):
    path = f"{OUT}/{name}.json"
    if not os.path.exists(path):
        return None
    with open(path) as f:
        return json.load(f)


def save(name, data):
    with open(f"{OUT}/{name}.json", "w") as f:
        json.dump(data, f, indent=1)


def egami_cycles(clip, effort, quality, frames, size, pgo):
    src = th.clip_path(clip, size)
    base = f"dotnet {th.BENCH} --encode-h264 {src} --frames={frames} --quality={quality} --effort={effort} --threads=1 --no-aq"
    env = "" if pgo else "export DOTNET_TieredPGO=0; "
    a, b = (th.counters(th.PERF_IMAGE, f"{base} --runs={r}", env) for r in (1, 3))
    return {k: (b[k] - a[k]) / (2 * frames) for k in a}


def cycles_job(clip, effort, args):
    name = f"cycles-{clip}-e{effort}-q{args.quality}"
    if args.reuse and load(name):
        return clip, effort, load(name)
    row = {"frames": args.frames}
    for pgo in (True, False):
        row["pgo" if pgo else "nopgo"] = egami_cycles(clip, effort, args.quality, args.frames, args.size, pgo)
    save(name, row)
    return clip, effort, row


def x264_job(clip, args):
    """x264 medium's cycles a frame at the CRF that gives the PSNR-Y Egami's effort 4 gives at the quality."""
    name = f"x264-{clip}-q{args.quality}"
    if args.reuse and load(name):
        return clip, load(name)
    src = th.clip_path(clip, args.size)
    stream, _, _ = th.egami(src, args.frames, args.quality, 4, 1, f"x264match-{clip}")
    crf, psnr = th.matched_crf(src, args.frames, "medium", th.psnr_y(src, stream, args.frames))
    out = f"{OUT}/runs/x264-cycles-{clip}.h264"
    a, b = (th.counters(th.REF_IMAGE, th.x264_command(src, n, "medium", f"--crf {crf:.2f}", 1, out))
            for n in (args.frames, 2 * args.frames))
    row = {"crf": crf, "psnr": psnr, "medium": {k: (b[k] - a[k]) / args.frames for k in a}}
    save(name, row)
    return clip, row


def run_cycles(args):
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    th.ensure_perf_image()
    clips = args.clips.split(",")
    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        x264s = dict(pool.map(lambda c: x264_job(c, args), clips))
        for clip, effort, row in pool.map(lambda j: cycles_job(*j, args), [(c, e) for e in efforts(args.efforts) for c in clips]):
            medium = x264s[clip]["medium"]["cycles"]
            print(f"  {clip} effort {effort}: PGO on {row['pgo']['cycles'] / 1e6:.0f} M cycles a frame "
                  f"({row['pgo']['cycles'] / medium:.1f}x x264 medium), PGO off {row['nopgo']['cycles'] / 1e6:.0f} M "
                  f"({row['nopgo']['cycles'] / medium:.1f}x)", flush=True)


def build_stats():
    subprocess.run(["dotnet", "build", "-c", "Release", "-p:EgamiH264Stats=true", "--artifacts-path", STATS_BUILD, "-v", "q", "-nologo",
                    "benchmarks/Egami.Video.Benchmarks"], check=True, env=ENV)


def counts_job(clip, effort, args):
    name = f"counts-{clip}-e{effort}-q{args.quality}"
    if args.reuse and load(name):
        return clip, effort, load(name)
    src = th.clip_path(clip, args.size)
    cmd = f"dotnet {STATS_BENCH} --encode-h264 {src} --frames={args.frames} --quality={args.quality} --effort={effort} --threads=1"
    data = json.loads(th.checked(th.docker(th.DOTNET_IMAGE, cmd)).stdout.strip().splitlines()[-1])
    if "stats" not in data:
        raise RuntimeError(f"{STATS_BENCH} does not count: rebuild it with -p:EgamiH264Stats=true")
    row = {"frames": args.frames, "bytes": data["bytes"], "stats": data["stats"]}
    save(name, row)
    return clip, effort, row


def per_mb(stats, types=TYPES):
    mbs = sum(stats[t]["Macroblocks"] for t in types)
    return {k: sum(stats[t][k] for t in types) / max(mbs, 1) for k in stats[types[0]]}


def print_counts(clip, effort, row):
    stats = row["stats"]
    print(f"\n{clip}, effort {effort}: work per macroblock (macroblocks P/B/I = "
          + "/".join(str(stats[t]["Macroblocks"]) for t in TYPES) + ")")
    print("| per macroblock | P | B | I | all |\n|---|---:|---:|---:|---:|")
    columns = [per_mb(stats, [t]) for t in TYPES] + [per_mb(stats)]
    for key in stats["P"]:
        if key != "Macroblocks":
            print(f"| {key} | " + " | ".join(f"{c[key]:.1f}" for c in columns) + " |")


def run_counts(args):
    os.makedirs(OUT, exist_ok=True)
    if not args.reuse or not os.path.exists(STATS_BENCH):
        build_stats()
    clips = args.clips.split(",")
    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        for clip, effort, row in pool.map(lambda j: counts_job(*j, args), [(c, e) for e in efforts(args.efforts) for c in clips]):
            print_counts(clip, effort, row)


PARTS = ["Modes", "QpDelta", "Refs", "Vectors", "Residual"]
SHARES = ["Skip", "Direct", "DirectResidual", "Inter", "Intra"]
KINDS = ["I", "P", "Bref", "B"]


def bits_job(clip, quality, args):
    name = f"bits-{clip}-e{args.effort}-q{quality}-f{args.frames}{'-aq' if args.aq else ''}"
    if args.reuse and load(name):
        return clip, quality, load(name)
    src = th.clip_path(clip, args.size)
    cmd = (f"dotnet {STATS_BENCH} --encode-h264 {src} --frames={args.frames} --quality={quality} --effort={args.effort} --threads=1"
           + ("" if args.aq else " --no-aq"))
    data = json.loads(th.checked(th.docker(th.DOTNET_IMAGE, cmd)).stdout.strip().splitlines()[-1])
    if "pictures" not in data:
        raise RuntimeError(f"{STATS_BENCH} does not split bits: rebuild it with -p:EgamiH264Stats=true")
    row = {"frames": args.frames, "bytes": data["bytes"], "pictures": data["pictures"]}
    save(name, row)
    return clip, quality, row


def bits_table(title, pictures):
    """A Markdown table of picture kinds: count, kbit a picture, share of the bits, the parts' shares and the macroblock shares."""
    total = sum(pictures[k]["Bits"] for k in KINDS) or 1
    lines = [title, "", "| kind | pictures | kbit a picture | share of bits | " + " | ".join(PARTS) + " | other | "
             + " | ".join(SHARES) + " |", "|---|---:|---:|---:|" + "---:|" * (len(PARTS) + 1 + len(SHARES))]
    for kind in KINDS:
        p = pictures[kind]
        if p["Pictures"] == 0:
            continue
        parts = [p[f"{n}Bits"] / 32768 for n in PARTS]
        mbs = sum(p[n] for n in SHARES) or 1
        cells = [str(p["Pictures"]), f"{p['Bits'] / p['Pictures'] / 1000:.1f}", f"{p['Bits'] / total * 100:.1f} %"]
        cells += [f"{v / p['Bits'] * 100:.1f} %" for v in parts] + [f"{(p['Bits'] - sum(parts)) / p['Bits'] * 100:.1f} %"]
        cells += [f"{p[n] / mbs * 100:.1f} %" for n in SHARES]
        lines.append(f"| {kind} | " + " | ".join(cells) + " |")
    return "\n".join(lines)


def run_bits(args):
    os.makedirs(OUT, exist_ok=True)
    if not args.reuse or not os.path.exists(STATS_BENCH):
        build_stats()
    clips = args.clips.split(",")
    qualities = [int(q) for q in args.qualities.split(",")]
    with concurrent.futures.ThreadPoolExecutor(args.jobs) as pool:
        rows = list(pool.map(lambda j: bits_job(*j, args), [(c, q) for q in qualities for c in clips]))
    aq = "on" if args.aq else "off"
    out = []
    for quality in qualities:
        together = {k: collections.Counter() for k in KINDS}
        for clip, q, row in rows:
            if q != quality:
                continue
            out.append(bits_table(f"\n{clip}, effort {args.effort}, quality {quality}, {row['frames']} frames, adaptive quantization {aq}, "
                                  f"{row['bytes'] * 8 / 1000:.0f} kbit:", row["pictures"]))
            for k in KINDS:
                together[k].update(row["pictures"][k])
        out.append(bits_table(f"\nThe {len(clips)} clips together, effort {args.effort}, quality {quality}, adaptive quantization {aq}:",
                              {k: {c: together[k][c] for c in ["Pictures", "Bits"] + [f"{n}Bits" for n in PARTS] + SHARES} for k in KINDS}))
    text = "\n".join(out)
    with open(f"{OUT}/bits-e{args.effort}{'-aq' if args.aq else ''}.md", "w") as f:
        f.write(text + "\n")
    print(text)


def run_bd(args):
    for effort in efforts(args.efforts):
        subprocess.run(["python", "tools/video/time-h264-encoder.py", "bd", "--size", args.size, "--effort", str(effort),
                        "--presets", "medium", "--clips", args.clips, "--jobs", str(args.jobs), "--out", f"{OUT}/bd", "--reuse"],
                       check=True, env=ENV)


def run_baseline(args):
    args.reuse = True
    run_cycles(args)
    run_counts(args)
    run_bd(args)
    report(args)


COUNT_COLUMNS = [("RD codings", "RdCodings"), ("RDOQ rate counts", "RdoqRateCounts"), ("block searches", "BlockSearches"),
                 ("SADs", "IntegerCosts"), ("subpel SATDs", "SubpelCosts")]


def report(args):
    clips = args.clips.split(",")
    lines = [f"Cycles a frame are user-mode perf counts at one thread, {args.size}p, quality {args.quality}, {args.frames} frames, "
             "the mean of the clips; x is against x264 medium at the CRF that gives effort 4's PSNR-Y. BD-rate is PSNR-Y against x264 medium "
             "(60 frames, four qualities): the mean of the clips and the worst. Work counts are per macroblock over all picture types.",
             "",
             "| effort | G cycles PGO on | x264 medium x | G cycles PGO off | BD mean | BD worst | "
             + " | ".join(c for c, _ in COUNT_COLUMNS) + " |",
             "|---:|---:|---:|---:|---:|---:|" + "---:|" * len(COUNT_COLUMNS)]
    detail = ["", "| effort | clip | G cycles PGO on | x | G cycles PGO off | BD | " + " | ".join(c for c, _ in COUNT_COLUMNS) + " |",
              "|---:|---|---:|---:|---:|---:|" + "---:|" * len(COUNT_COLUMNS)]
    for effort in efforts(args.efforts):
        on, off, ratio, bds, counts = [], [], [], [], []
        bd = load(f"bd/bd-{args.size}-e{effort}")
        for clip in clips:
            c, x, k = load(f"cycles-{clip}-e{effort}-q{args.quality}"), load(f"x264-{clip}-q{args.quality}"), load(f"counts-{clip}-e{effort}-q{args.quality}")
            b = th.bd_against(bd["clips"][clip], "medium")[0] if bd and clip in bd["clips"] else None
            cells = [f"{c['pgo']['cycles'] / 1e9:.2f}" if c else "", f"{c['pgo']['cycles'] / x['medium']['cycles']:.1f}" if c and x else "",
                     f"{c['nopgo']['cycles'] / 1e9:.2f}" if c else "", f"{b:+.1f} %" if b is not None else ""]
            m = per_mb(k["stats"]) if k else None
            cells += [f"{m[key]:.0f}" if m else "" for _, key in COUNT_COLUMNS]
            detail.append(f"| {effort} | {clip} | " + " | ".join(cells) + " |")
            if c:
                on.append(c["pgo"]["cycles"])
                off.append(c["nopgo"]["cycles"])
                if x:
                    ratio.append(c["pgo"]["cycles"] / x["medium"]["cycles"])
            if b is not None:
                bds.append(b)
            if m:
                counts.append(m)

        def mean(v, fmt, scale=1.0):
            return fmt.format(sum(v) / len(v) / scale) if v else ""
        cells = [mean(on, "{:.2f}", 1e9), mean(ratio, "{:.1f}"), mean(off, "{:.2f}", 1e9), mean(bds, "{:+.1f} %"),
                 f"{max(bds):+.1f} %" if bds else ""]
        cells += [f"{sum(m[key] for m in counts) / len(counts):.0f}" if counts else "" for _, key in COUNT_COLUMNS]
        lines.append(f"| {effort} | " + " | ".join(cells) + " |")
    text = "\n".join(lines + detail)
    with open(f"{OUT}/baseline.md", "w") as f:
        f.write(text + "\n")
    print(text)


def main():
    global OUT
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=["stages", "cycles", "counts", "bits", "baseline", "report"])
    parser.add_argument("--size", default="720")
    parser.add_argument("--clips", default=",".join(th.CLIPS))
    parser.add_argument("--wide", action="store_true", help="the eight clips of the compression Build Plan (four busy, four calm)")
    parser.add_argument("--qualities", default="50,34", help="bits: the qualities")
    parser.add_argument("--aq", action="store_true", help="bits: Egami with adaptive quantization (its default) instead of without")
    parser.add_argument("--effort", type=int, default=4, help="stages, bits: the effort to profile")
    parser.add_argument("--efforts", default="0-10", help="cycles, counts, baseline, report: efforts, e.g. 0,2,4 or 0-10")
    parser.add_argument("--quality", type=int, default=50)
    parser.add_argument("--frames", type=int, default=None, help="frames to code (20; bits 60)")
    parser.add_argument("--runs", type=int, default=2, help="stages: encodes of the frames in one process")
    parser.add_argument("--delay", type=float, default=8, help="stages: seconds before recording starts (start-up, tiering)")
    parser.add_argument("--no-pgo", action="store_true", help="stages: profile with tiered PGO off")
    parser.add_argument("--jobs", type=int, default=max(1, (os.cpu_count() or 4) // 4))
    parser.add_argument("--reuse", action="store_true", help="keep results already in .cache/h264prof")
    parser.add_argument("--clips-from", default=".cache/video/derf")
    parser.add_argument("--out", default=OUT, help="where runs and results go (clips stay in .cache/h264enc/clips)")
    parser.add_argument("--repeats", type=int, default=3, help="cycles, baseline: runs of each count, the least kept")
    args = parser.parse_args()
    args.frames = args.frames or (60 if args.mode == "bits" else 20)
    if args.wide:
        args.clips = ",".join(th.WIDE_CLIPS)
    OUT = th.OUT = args.out
    th.REPEATS = args.repeats
    os.makedirs(f"{OUT}/runs", exist_ok=True)
    if args.mode != "report":
        th.ensure_clips(args.clips.split(","), args.size, args.clips_from)
    {"stages": run_stages, "cycles": run_cycles, "counts": run_counts, "bits": run_bits, "baseline": run_baseline, "report": report}[args.mode](args)


if __name__ == "__main__":
    main()
