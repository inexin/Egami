"""Makes the Vorbis test corpus: short Ogg Vorbis clips written by oggenc (vorbis-tools) and FFmpeg's libvorbis encoder in
Docker from synthetic lavfi sources, with libvorbisfile's decoded float output as the expected result. The tools run only as
black boxes; their source is never read.

Usage, from the repo root:
  python tools/video/make-vorbis-corpus.py           clips into tests/video/corpus/audio/vorbis (checked in) with manifest.json
  python tools/video/make-vorbis-corpus.py --speed   a 60 s stereo clip in .cache/vorbis/speed.ogg and the time libvorbisfile
                                                      takes to decode it (best of 5); the test VorbisTests.Speed times Egami.Video
                                                      on the same file when EGAMI_VORBIS_SPEED=1

The Docker image egami-vorbisref:v5.5 holds FFmpeg, vorbis-tools and /usr/local/bin/vorbisref (in.ogg out.f32), which writes
ov_read_float's output, channels interleaved in Vorbis order, as little-endian 32-bit floats and prints "channels rate samples".

Each clip <name>.ogg has <name>.f32.gz beside it: that output, gzip'd. manifest.json maps each clip to {channels, rate, samples}.
q6-stereo-48k is also copied by FFmpeg, without re-encoding, into .mka, .webm and .mp4 (FFmpeg's own Vorbis-in-MP4 mapping).
No encoder at hand writes floor 0 (libvorbis 1.x encodes floor 1 only), so floor 0 has no clip here.
"""
import json
import os
import subprocess
import sys

IMAGE = "egami-vorbisref:v5.5"
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "tests", "video", "corpus", "audio", "vorbis")
CACHE = os.path.join(ROOT, ".cache", "vorbis")

# Per-channel aevalsrc expressions: tones, a little noise, and bursts that make the encoder switch to short blocks.
TONE = "0.4*sin(2*PI*{f}*t)+0.03*(random({c})-0.5)"
BURST = "0.5*sin(2*PI*{f}*t)*lt(mod(t\\,0.2)\\,0.02)+0.2*sin(2*PI*{g}*t)+0.3*(random({c})-0.5)*lt(mod(t+0.1\\,0.2)\\,0.01)"
NOISE = "0.5*(random({c})-0.5)"
# Left plays, right is silent for the first half, so floors go unused on one side of a coupled pair.
SILENT = "0.4*sin(2*PI*{f}*t)*gte(t\\,0.2*{c})"


def source(rate, channels, kind, seconds):
    tones = [220, 277, 330, 440, 554, 659, 880, 1100]
    exprs = "|".join(kind.format(f=tones[c], g=tones[c] * 3, c=c) for c in range(channels))
    return f"-f lavfi -i \"aevalsrc='{exprs}':s={rate}:d={seconds}\""


def oggenc(args):
    return "ffmpeg -y -v error {src} -c:a pcm_s16le /tmp/in.wav && oggenc -Q " + args + " -o {out} /tmp/in.wav"


# name: (command with {src} and {out}, rate, channels, source kind, seconds)
CLIPS = {
    "q-1-mono-8k.ogg": (oggenc("-q -1"), 8000, 1, TONE, 0.5),
    "q0-stereo-22k.ogg": (oggenc("-q 0 -c TITLE='Corpus tone' -c ARTIST=Egami"), 22050, 2, TONE, 0.2),
    "q3-stereo-44k-bursts.ogg": (oggenc("-q 3"), 44100, 2, BURST, 0.12),
    "q6-stereo-48k.ogg": (oggenc("-q 6"), 48000, 2, BURST, 0.1),
    "q10-stereo-44k-noise.ogg": (oggenc("-q 10"), 44100, 2, NOISE, 0.08),
    "q3-51-16k.ogg": (oggenc("-q 3"), 16000, 6, BURST, 0.1),
    "q5-3ch-11k.ogg": (oggenc("-q 5"), 11025, 3, TONE, 0.25),
    "q4-mono-96k.ogg": (oggenc("-q 4"), 96000, 1, BURST, 0.1),
    "q2-stereo-32k-silent.ogg": (oggenc("-q 2"), 32000, 2, SILENT, 0.15),
    "managed-stereo-44k.ogg": (oggenc("--managed -b 64 -m 48 -M 72"), 44100, 2, BURST, 0.12),
    "ffmpeg-stereo-44k.ogg": ("ffmpeg -y -v error {src} -c:a libvorbis -q:a 4 {out}", 44100, 2, BURST, 0.1),
}


def docker(command, workdir):
    result = subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(workdir)}:/work", IMAGE, "sh", "-c", command],
                            check=True, capture_output=True, text=True)
    return result.stdout + result.stderr


def make_corpus():
    os.makedirs(OUT, exist_ok=True)
    for name in os.listdir(OUT):
        os.remove(os.path.join(OUT, name))
    manifest = {}
    for name, (command, rate, channels, kind, seconds) in CLIPS.items():
        stem = name[:-4]
        out = docker(command.format(src=source(rate, channels, kind, seconds), out=f"/work/{name}")
                     + f" && vorbisref /work/{name} /tmp/out.f32 && gzip -9 -n -c /tmp/out.f32 > /work/{stem}.f32.gz", OUT)
        ch, r, samples = (int(v) for v in out.split()[-3:])
        manifest[name] = {"channels": ch, "rate": r, "samples": samples}
        size = os.path.getsize(os.path.join(OUT, name)) + os.path.getsize(os.path.join(OUT, stem + ".f32.gz"))
        print(name, ch, r, samples, f"{size} bytes")
    docker(" && ".join(f"ffmpeg -y -v error -i /work/q6-stereo-48k.ogg -c copy {extra}/work/q6-stereo-48k.{ext}"
                       for ext, extra in (("mka", ""), ("webm", ""), ("mp4", "-strict -2 "))), OUT)
    with open(os.path.join(OUT, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


def speed():
    os.makedirs(CACHE, exist_ok=True)
    docker(oggenc("-q 3").format(src=source(44100, 2, BURST, 60), out="/work/speed.ogg"), CACHE)
    script = ("best=999; for i in 1 2 3 4 5; do s=$(date +%s.%N); vorbisref /work/speed.ogg /tmp/o.f32 2>/dev/null; e=$(date +%s.%N);"
              " best=$(echo \"$e $s $best\" | awk '{d=$1-$2; print (d<$3)?d:$3}'); done; echo $best")
    seconds = float(docker(script, CACHE).split()[-1])
    print(f"libvorbisfile (vorbisref): {seconds * 1000:.1f} ms for 60 s of stereo 44.1 kHz, {60 / seconds:.0f}x real time")


if __name__ == "__main__":
    if "--speed" in sys.argv:
        speed()
    else:
        make_corpus()
