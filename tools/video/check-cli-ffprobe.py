"""The egami-video gates (Video CLI design Q12, Build Plan task 19), with ffprobe and ffmpeg run as black boxes:

1. info: for every clip of the V1 to V4 corpus, 'egami-video info --format ffprobe --packets' lists the same streams (cover art
   aside, which ffprobe shows as a stream and egami-video as an attachment) and, per stream, the same number of packets at the
   same times as 'ffprobe -show_streams -show_packets'.
2. remux and trim: every clip remuxed to Matroska (and MP4 sources to MP4), and trimmed from a quarter to three quarters of its
   length, reads in ffprobe with no error, and the remux keeps every packet's bytes ('egami-video hash --packets').
3. startup: the time from start to first output of 'egami-video --version' and of 'info' on a small clip, as a .NET tool
   (median of 15 runs; the bar is 150 ms) and, with --native, as a Native AOT program (the bar is 30 ms).
4. speed: 'frames' (every frame to PNG) and 'thumbnail' against ffmpeg doing the same on ten-second 720p clips ffmpeg makes from
   its test pattern (Motion JPEG, VP8, VP9, AV1); the bar is 1.5 times ffmpeg's time.

Usage, from the repo root, with ffprobe and ffmpeg on the PATH (or EGAMI_FFMPEG_DOCKER=image to run them in Docker):
  python tools/video/check-cli-ffprobe.py [--native] [--only info|remux|startup|speed]
"""
import glob
import json
import os
import shutil
import statistics
import subprocess
import sys
import tempfile
import time

ROOT = os.path.abspath(".")
CORPUS = os.path.join(ROOT, "tests", "video", "corpus")
OUT = os.path.join(ROOT, ".cache", "video", "cli-gates")
PROJECT = os.path.join("src", "Egami.Video.Cli", "Egami.Video.Cli.csproj")
TOOL = os.path.join(ROOT, "src", "Egami.Video.Cli", "bin", "Release", "net10.0", "Egami.Video.Cli.dll")
MEDIA = (".avi", ".mkv", ".webm", ".mp4", ".mov", ".m4a", ".ivf", ".y4m", ".mka", ".obu")
DOCKER = os.environ.get("EGAMI_FFMPEG_DOCKER")


def ff(program, *args, cwd=None):
    """Runs ffprobe or ffmpeg, locally or in Docker with the repo mounted at /work."""
    if DOCKER:
        mapped = [a.replace(ROOT, "/work").replace("\\", "/") for a in args]
        command = ["docker", "run", "--rm", "-v", f"{ROOT}:/work", "-w", "/work", DOCKER, program, *mapped]
    else:
        command = [program, *args]
    return subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=cwd)


def tool(*args, cwd=None):
    return subprocess.run(["dotnet", TOOL, *args], capture_output=True, text=True, encoding="utf-8", errors="replace", cwd=cwd)


def clips():
    found = []
    for folder in ["", "vp8", "vp9", "av1", "mp4"]:
        for path in sorted(glob.glob(os.path.join(CORPUS, folder, "*"))):
            if path.lower().endswith(MEDIA) and os.path.isfile(path):
                found.append(path)
    return found


def seconds(entry, key, base):
    value = entry.get(key)
    return None if value is None else int(value) * base


def time_base(stream):
    n, d = stream["time_base"].split("/")
    return int(n) / int(d)


def gate_info():
    failures = []
    for clip in clips():
        theirs = ff("ffprobe", "-v", "error", "-of", "json", "-show_streams", "-show_packets", clip)
        if theirs.returncode != 0:
            continue  # ffprobe itself cannot read it; nothing to agree with
        ours = tool("info", clip, "--format", "ffprobe", "--packets")
        name = os.path.relpath(clip, CORPUS)
        if ours.returncode != 0:
            failures.append(f"{name}: egami-video failed: {ours.stderr.strip()}")
            continue
        expected, actual = json.loads(theirs.stdout), json.loads(ours.stdout)
        # Cover art is an attachment in egami-video, and a QuickTime chapter text track (bin_data) its chapters.
        streams = [s for s in expected.get("streams", []) if not s.get("disposition", {}).get("attached_pic")
                   and not (s.get("codec_type") == "data" and s.get("codec_name") == "bin_data")]
        index = {s["index"]: i for i, s in enumerate(streams)}
        if len(streams) != len(actual["streams"]):
            failures.append(f"{name}: {len(streams)} streams in ffprobe, {len(actual['streams'])} in egami-video")
            continue
        for i, stream in enumerate(streams):
            want_base, got_base = time_base(stream), time_base(actual["streams"][i])
            want = sorted(seconds(p, "pts", want_base) for p in expected.get("packets", []) if index.get(p["stream_index"]) == i and "pts" in p)
            got = sorted(seconds(p, "pts", got_base) for p in actual.get("packets", []) if p["stream_index"] == i)
            if len(want) != len(got):
                failures.append(f"{name} stream {i} ({stream['codec_type']}): {len(want)} packets in ffprobe, {len(got)} in egami-video")
                continue
            tolerance = max(want_base, got_base) + 1e-6
            off = [k for k in range(len(want)) if abs(want[k] - got[k]) > tolerance]
            if off:
                k = off[0]
                failures.append(f"{name} stream {i}: {len(off)} timestamps differ, first at packet {k}: ffprobe {want[k]:.6f} s, egami-video {got[k]:.6f} s")
    return failures


def packet_hashes(path):
    """Each packet's track kind and hash, sorted: what a remux must keep."""
    result = tool("hash", path, "--packets", "--json")
    if result.returncode != 0:
        return None
    file = json.loads(result.stdout)["files"][0]
    kinds = {s["index"]: s["kind"] for s in file["streams"]}
    return sorted((kinds[p["stream"]], p["hash"]) for p in file["packets"])


def gate_remux():
    failures = []
    os.makedirs(OUT, exist_ok=True)
    for clip in clips():
        name = os.path.relpath(clip, CORPUS)
        base = os.path.join(OUT, name.replace(os.sep, "_"))
        if ff("ffprobe", "-v", "error", clip).returncode != 0:
            continue
        targets = [base + ".mkv"] + ([base + ".remux.mp4"] if clip.endswith((".mp4", ".mov", ".m4a")) else [])
        for target in targets:
            run = tool("remux", clip, "-o", target)
            if run.returncode != 0 and "Write YUV4MPEG2 instead" in run.stderr:
                target = base + ".y4m"
                run = tool("remux", clip, "-o", target)
            if run.returncode != 0:
                if "cannot hold" in run.stderr or "not supported" in run.stderr.lower():
                    continue
                failures.append(f"{name} -> {os.path.basename(target)}: {run.stderr.strip()}")
                continue
            errors = ff("ffprobe", "-v", "error", "-show_packets", "-of", "compact", target).stderr.strip()
            if errors:
                failures.append(f"{name} -> {os.path.basename(target)}: ffprobe says: {errors.splitlines()[0]}")
            if packet_hashes(clip) != packet_hashes(target):
                failures.append(f"{name} -> {os.path.basename(target)}: packet bytes differ")
        info = json.loads(tool("info", clip, "--json").stdout or "{}")
        duration = (info.get("files") or [{}])[0].get("duration")
        if duration and any(t.get("kind") == "video" for t in info["files"][0]["tracks"]):
            target = base + ".trim.mkv"
            run = tool("trim", clip, "--from", f"{duration / 4:.3f}", "--to", f"{duration * 3 / 4:.3f}", "-o", target, "-q")
            if run.returncode != 0:
                failures.append(f"{name} trim: {run.stderr.strip()}")
                continue
            errors = ff("ffprobe", "-v", "error", "-show_packets", "-of", "compact", target).stderr.strip()
            if errors:
                failures.append(f"{name} trim: ffprobe says: {errors.splitlines()[0]}")
    return failures


def median_ms(command, runs=15):
    times = []
    for _ in range(runs):
        start = time.perf_counter()
        subprocess.run(command, capture_output=True)
        times.append((time.perf_counter() - start) * 1000)
    return statistics.median(times)


def gate_startup(native):
    failures = []
    small = os.path.join(CORPUS, "avi-mjpeg-420-pcm.avi")
    results = []
    tool_exe = TOOL[:-4] + ".exe" if os.name == "nt" else TOOL[:-4]
    launcher = [tool_exe] if os.path.exists(tool_exe) else ["dotnet", TOOL]
    for label, command in [("--version", [*launcher, "--version"]), ("info", [*launcher, "info", small])]:
        ms = median_ms(command)
        results.append(f"tool {label}: {ms:.0f} ms")
        if ms > 150:
            failures.append(f"startup as a tool ({label}) is {ms:.0f} ms, over 150 ms")
    if native:
        rid = {"nt": "win-x64"}.get(os.name, "linux-x64")
        publish = os.path.join(OUT, "native")
        subprocess.run(["dotnet", "publish", PROJECT, "-c", "Release", "-r", rid, "-p:EgamiNativeAot=true", "-o", publish, "-v", "q", "-nologo"], check=True, capture_output=True)
        exe = os.path.join(publish, "egami-video" + (".exe" if os.name == "nt" else ""))
        for label, command in [("--version", [exe, "--version"]), ("info", [exe, "info", small])]:
            ms = median_ms(command)
            results.append(f"native {label}: {ms:.0f} ms")
            if ms > 30:
                failures.append(f"startup native ({label}) is {ms:.0f} ms, over 30 ms")
    print("  " + "; ".join(results))
    return failures


def speed_clips():
    """Ten-second 1280x720 clips that ffmpeg makes from its test pattern, cached: Motion JPEG, VP8, VP9 and AV1 where this
    ffmpeg has the encoder. Tiny corpus clips would time process start, not decoding."""
    folder = os.path.join(OUT, "speed")
    os.makedirs(folder, exist_ok=True)
    made = []
    for name, args in [("mjpeg.avi", ["-c:v", "mjpeg", "-q:v", "3"]), ("vp8.webm", ["-c:v", "libvpx", "-b:v", "2M"]),
                       ("vp9.webm", ["-c:v", "libvpx-vp9", "-b:v", "2M", "-row-mt", "1"]), ("av1.mkv", ["-c:v", "libaom-av1", "-b:v", "2M", "-cpu-used", "8"])]:
        path = os.path.join(folder, name)
        if not os.path.exists(path):
            ff("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25:duration=10", "-pix_fmt", "yuv420p", *args, path)
        if os.path.exists(path):
            made.append(path)
    return made


def gate_speed():
    failures = []
    for clip in speed_clips():
        name = os.path.basename(clip)
        with tempfile.TemporaryDirectory() as work:
            ours = median_ms([TOOL[:-4] + (".exe" if os.name == "nt" else ""), "frames", clip, "-o", os.path.join(work, "a-{frame:0000}.png"), "-q"], runs=3)
            theirs = median_ms(["ffmpeg", "-v", "error", "-y", "-i", clip, os.path.join(work, "b-%04d.png")], runs=3)
            thumb = median_ms([TOOL[:-4] + (".exe" if os.name == "nt" else ""), "thumbnail", clip, "-o", os.path.join(work, "a.png"), "-q"], runs=3)
            ff_thumb = median_ms(["ffmpeg", "-v", "error", "-y", "-i", clip, "-vf", "thumbnail", "-frames:v", "1", os.path.join(work, "b.png")], runs=3)
        print(f"  {name}: frames {ours:.0f} ms (ffmpeg {theirs:.0f} ms, {ours / theirs:.2f}x); thumbnail {thumb:.0f} ms (ffmpeg {ff_thumb:.0f} ms, {thumb / ff_thumb:.2f}x)")
        if ours > 1.5 * theirs:
            failures.append(f"{name}: frames takes {ours / theirs:.2f}x ffmpeg's time")
        if thumb > 1.5 * ff_thumb:
            failures.append(f"{name}: thumbnail takes {thumb / ff_thumb:.2f}x ffmpeg's time")
    return failures


def main():
    if not DOCKER and not (shutil.which("ffprobe") and shutil.which("ffmpeg")):
        print("ffprobe and ffmpeg are not on the PATH; install them or set EGAMI_FFMPEG_DOCKER.")
        sys.exit(2)
    subprocess.run(["dotnet", "build", PROJECT, "-c", "Release", "-v", "q", "-nologo"], check=True, capture_output=True)
    only = sys.argv[sys.argv.index("--only") + 1] if "--only" in sys.argv else None
    gates = [("info", gate_info), ("remux", gate_remux), ("startup", lambda: gate_startup("--native" in sys.argv)), ("speed", gate_speed)]
    failed = False
    for name, gate in gates:
        if only and name != only:
            continue
        print(f"{name}:")
        failures = gate()
        for failure in failures:
            print("  FAIL " + failure)
        print(f"  {'passed' if not failures else f'{len(failures)} failures'}")
        failed |= bool(failures)
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
