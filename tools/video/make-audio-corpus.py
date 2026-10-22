"""Makes the V5.0 audio test corpus: short WAV, Wave64, RF64 and FLAC clips (and FLAC in Matroska and MP4) written by FFmpeg and
the flac tool in Docker, with the decoded samples' MD5 from libFLAC's flac -d (FLAC) or FFmpeg (PCM) as the expected result.
The tools run only as black boxes; their source is never read.

Usage, from the repo root:
  docker build -t egami-audioref:v5.0 tools/video/audioref
  python tools/video/make-audio-corpus.py              clips into tests/video/corpus/audio (checked in) with manifest.json
  python tools/video/make-audio-corpus.py --ietf       also fetch the IETF CELLAR flac-test-files into .cache/audio/flac-test-files
                                                       and write their libFLAC references (not checked in: terms unrecorded)
  python tools/video/make-audio-corpus.py --check DIR  run flac -t on every .flac file in DIR (Egami.Video's own output)
  python tools/video/make-audio-corpus.py --gates      after the EGAMI_AUDIO_GATES=1 tests: flac -t on Egami.Video's encodings
                                                       of the IETF files, their size against flac -0 to -8, and flac -d's time

manifest.json maps each clip to {samples, rate, channels, bits, md5}: the MD5 of every sample interleaved, little-endian, signed,
in whole bytes of the stream's depth (24-bit in 3 bytes), as FLAC's STREAMINFO defines it; float clips use 32-bit floats.
"""
import hashlib
import json
import os
import subprocess
import sys

IMAGE = "egami-audioref:v5.0"
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "tests", "video", "corpus", "audio")
CACHE = os.path.join(ROOT, ".cache", "audio")

# Half a second of a chord, so prediction and stereo decorrelation have something to do.
def source(rate, channels, seconds=0.5):
    tones = [220, 277, 330, 440, 554, 659, 880, 1100]
    inputs = "".join(f"-f lavfi -i sine=frequency={tones[c]}:sample_rate={rate}:duration={seconds} " for c in range(channels))
    if channels == 1:
        return inputs + "-filter_complex \"[0]volume=0.6[out]\" -map \"[out]\""
    pads = "".join(f"[{c}]" for c in range(channels))
    return inputs + f"-filter_complex \"{pads}amerge=inputs={channels},volume=0.6[out]\" -map \"[out]\""


# name: (shell command in /work with {src} and {out}, rate, channels, bits, float)
CLIPS = {
    "wav-s16-stereo.wav": ("ffmpeg {src} -c:a pcm_s16le {out}", 44100, 2, 16, False),
    "wav-u8-mono.wav": ("ffmpeg {src} -c:a pcm_u8 {out}", 8000, 1, 8, False),
    "wav-s24-51.wav": ("ffmpeg {src} -c:a pcm_s24le -channel_layout 5.1 {out}", 16000, 6, 24, False),
    "wav-s32-stereo.wav": ("ffmpeg {src} -c:a pcm_s32le {out}", 22050, 2, 32, False),
    "wav-f32-stereo.wav": ("ffmpeg {src} -c:a pcm_f32le {out}", 22050, 2, 32, True),
    "wav-tags.wav": ("ffmpeg {src} -c:a pcm_s16le -metadata title='Corpus tone' -metadata artist=Egami -metadata comment='Test clip' {out}", 22050, 1, 16, False),
    "w64-s16-stereo.w64": ("ffmpeg {src} -c:a pcm_s16le -f w64 {out}", 44100, 2, 16, False),
    "rf64-s16-stereo.wav": ("ffmpeg {src} -c:a pcm_s16le -rf64 always {out}", 44100, 2, 16, False),
    "flac-l0-stereo.flac": ("ffmpeg {src} -c:a pcm_s16le /tmp/in.wav && flac -s -0 -o {out} /tmp/in.wav", 44100, 2, 16, False),
    "flac-l5-stereo.flac": ("ffmpeg {src} -c:a pcm_s16le /tmp/in.wav && flac -s -5 -o {out} /tmp/in.wav", 44100, 2, 16, False),
    "flac-l8-stereo.flac": ("ffmpeg {src} -c:a pcm_s16le /tmp/in.wav && flac -s -8 -o {out} /tmp/in.wav", 44100, 2, 16, False),
    "flac-24-51.flac": ("ffmpeg {src} -c:a pcm_s24le /tmp/in.wav && flac -s -5 -o {out} /tmp/in.wav", 16000, 6, 24, False),
    "flac-8-mono.flac": ("ffmpeg {src} -c:a pcm_u8 /tmp/in.wav && flac -s -5 -o {out} /tmp/in.wav", 8000, 1, 8, False),
    "flac-32-stereo.flac": ("ffmpeg {src} -c:a pcm_s32le /tmp/in.wav && flac -s -5 -o {out} /tmp/in.wav", 22050, 2, 32, False),
    "flac-odd-block.flac": ("ffmpeg {src} -c:a pcm_s16le /tmp/in.wav && flac -s -5 -b 1000 -o {out} /tmp/in.wav", 44100, 2, 16, False),
    "flac-tags-picture.flac": ("ffmpeg {src} -c:a pcm_s16le /tmp/in.wav && ffmpeg -v error -f lavfi -i color=red:size=16x16 -frames:v 1 /tmp/cover.png"
                               " && flac -s -5 -T TITLE='Corpus tone' -T ARTIST=Egami --picture=/tmp/cover.png -o {out} /tmp/in.wav", 44100, 2, 16, False),
    "mka-flac-stereo.mka": ("ffmpeg {src} -sample_fmt s16 -c:a flac {out}", 44100, 2, 16, False),
    "mp4-flac-stereo.mp4": ("ffmpeg {src} -sample_fmt s16 -c:a flac -strict -2 {out}", 44100, 2, 16, False),
}


def docker(command, workdir):
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(workdir)}:/work", IMAGE, "sh", "-c", command], check=True)


def reference(name, rate, channels, bits, is_float, workdir):
    """MD5 and sample count of the decoded samples, decoded by flac -d for FLAC files and FFmpeg otherwise."""
    if name.endswith(".flac"):
        decode = f"flac -s -d -f --force-raw-format --endian=little --sign=signed -o /tmp/out.raw /work/{name} && cat /tmp/out.raw"
    else:
        codec = "pcm_f32le" if is_float else {8: "pcm_s8", 16: "pcm_s16le", 24: "pcm_s24le", 32: "pcm_s32le"}[bits]
        fmt = "f32le" if is_float else {8: "s8", 16: "s16le", 24: "s24le", 32: "s32le"}[bits]
        decode = f"ffmpeg -v error -i /work/{name} -c:a {codec} -f {fmt} -"
    raw = subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(workdir)}:/work", IMAGE, "sh", "-c", decode],
                         check=True, capture_output=True).stdout
    width = 4 if is_float else (bits + 7) // 8
    return {"samples": len(raw) // (width * channels), "rate": rate, "channels": channels, "bits": bits, "float": is_float,
            "md5": hashlib.md5(raw).hexdigest()}


def make_corpus():
    os.makedirs(OUT, exist_ok=True)
    manifest = {}
    for name, (command, rate, channels, bits, is_float) in CLIPS.items():
        path = os.path.join(OUT, name)
        if os.path.exists(path):
            os.remove(path)
        docker(command.format(src="-y -v error " + source(rate, channels), out=f"/work/{name}"), OUT)
        manifest[name] = reference(name, rate, channels, bits, is_float, OUT)
        print(name, manifest[name]["samples"], manifest[name]["md5"])
    with open(os.path.join(OUT, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


def fetch_ietf():
    target = os.path.join(CACHE, "flac-test-files")
    if not os.path.isdir(target):
        subprocess.run(["git", "clone", "--depth", "1", "https://github.com/ietf-wg-cellar/flac-test-files", target], check=True)
    manifest = {}
    for folder in ["subset", "uncommon"]:
        for name in sorted(os.listdir(os.path.join(target, folder))):
            if not name.endswith(".flac"):
                continue
            rel = f"{folder}/{name}"
            command = (f"flac -s -d -f --force-raw-format --endian=little --sign=signed -o /tmp/out.raw '/work/{rel}' 2>/dev/null"
                       " && md5sum /tmp/out.raw | cut -d' ' -f1 && stat -c %s /tmp/out.raw"
                       f" && metaflac --show-sample-rate --show-channels --show-bps '/work/{rel}'")
            result = subprocess.run(["docker", "run", "--rm", "-v", f"{target}:/work", IMAGE, "sh", "-c", command],
                                    capture_output=True, text=True)
            lines = result.stdout.split()
            if result.returncode != 0 or len(lines) < 5:
                manifest[rel] = {"decodes": False}
                print(rel, "flac cannot decode it")
                continue
            md5, size, rate, channels, bits = lines[0], int(lines[1]), int(lines[2]), int(lines[3]), int(lines[4])
            manifest[rel] = {"decodes": True, "md5": md5, "samples": size // (channels * ((bits + 7) // 8)), "rate": rate,
                             "channels": channels, "bits": bits}
            print(rel, md5)
    with open(os.path.join(target, "reference.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=2)


def check(folder):
    folder = os.path.abspath(folder)
    files = [n for n in sorted(os.listdir(folder)) if n.endswith(".flac")]
    failed = 0
    for name in files:
        result = subprocess.run(["docker", "run", "--rm", "-v", f"{folder}:/work", IMAGE, "flac", "-s", "-t", f"/work/{name}"],
                                capture_output=True, text=True)
        ok = result.returncode == 0
        failed += not ok
        print(("ok  " if ok else "FAIL"), name, result.stderr.strip())
    print(f"{len(files) - failed} of {len(files)} pass flac -t")
    return failed


def gates():
    """Compares .cache/audio/gates/L<n> (Egami.Video's FLAC) with libFLAC at the same level; prints sizes and flac -d's time."""
    gates_dir = os.path.join(CACHE, "gates")
    sources = os.path.join(CACHE, "flac-test-files", "subset")
    names = sorted(os.listdir(os.path.join(gates_dir, "L0")))
    script = "mkdir -p /tmp/wav && for f in " + " ".join(f"'/src/{n}'" for n in names) + "; do b=$(basename \"$f\" .flac);"              " flac -s -d -f --keep-foreign-metadata-if-present -o \"/tmp/wav/$b.wav\" \"$f\" 2>/dev/null || flac -s -d -f -o \"/tmp/wav/$b.wav\" \"$f\"; done;"              " start=$(date +%s.%N); for f in " + " ".join(f"'/src/{n}'" for n in names) + "; do for r in 1 2 3; do flac -s -d -f -o /tmp/x.wav \"$f\"; done; done;"              " end=$(date +%s.%N); echo decode $(echo \"($end - $start) / 3\" | bc -l 2>/dev/null || python3 -c \"print(($end-$start)/3)\");"              " for l in 0 1 2 3 4 5 6 7 8; do total=0; for w in /tmp/wav/*.wav; do flac -s -f -$l -o /tmp/l.flac \"$w\"; total=$((total + $(stat -c %s /tmp/l.flac))); done; echo size $l $total; done;"              " for l in 0 1 2 3 4 5 6 7 8; do flac -s -t /gates/L$l/*.flac && echo test $l ok || echo test $l FAIL; done"
    result = subprocess.run(["docker", "run", "--rm", "-v", f"{sources}:/src:ro", "-v", f"{gates_dir}:/gates:ro", IMAGE, "sh", "-c", script],
                            capture_output=True, text=True)
    print(result.stderr[-2000:])
    libflac = {}
    for line in result.stdout.splitlines():
        parts = line.split()
        if parts[0] == "size":
            libflac[int(parts[1])] = int(parts[2])
        else:
            print(line)
    for level in range(9):
        ours = sum(os.path.getsize(os.path.join(gates_dir, f"L{level}", n)) for n in names)
        print(f"level {level}: Egami.Video {ours} bytes, libFLAC {libflac.get(level)} bytes, {100 * (ours / libflac[level] - 1):+.2f} %")


if __name__ == "__main__":
    if "--gates" in sys.argv:
        gates()
        sys.exit(0)
    if "--check" in sys.argv:
        sys.exit(1 if check(sys.argv[sys.argv.index("--check") + 1]) else 0)
    if "--ietf" in sys.argv:
        fetch_ietf()
    else:
        make_corpus()
