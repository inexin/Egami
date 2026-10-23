"""Makes the ALAC gate references in tests/video/corpus/audio/alac: integer test signals encoded by Apple's reference
encoder (tools/video/alacref), which Egami.Video's encoder must match packet for packet and its decoder must decode to the
signal exactly. The signals come from a small integer generator that AlacReferenceTests repeats, so only the encoded
streams are checked in.

  python tools/video/make-alac-references.py                        alacref and FFmpeg in Docker (tools/video/alacref)
  python tools/video/make-alac-references.py --alacref PATH         a local alacref (tools/video/alacref/build.sh)

Each .alac file is the 24-byte ALACSpecificConfig, then every packet as a 4-byte little-endian length and its bytes. The
script also checks that Apple's decoder turns each stream back into its signal.
"""
import json
import os
import subprocess
import sys
import tempfile

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
OUT = os.path.join(ROOT, "tests", "video", "corpus", "audio", "alac")
IMAGE = "egami-alacref"
SAMPLES = 9000  # two full 4096-sample frames and a partial one

# name: (bits, channels, rate, fast, kind, frame size)
CLIPS = {
    "s16-mono-tone": (16, 1, 44100, False, "tone", 4096),
    "s16-stereo-tone": (16, 2, 44100, False, "tone", 4096),
    "s16-stereo-tone-fast": (16, 2, 44100, True, "tone", 4096),
    "s16-stereo-noise": (16, 2, 44100, False, "noise", 4096),
    "s16-stereo-noise-fast": (16, 2, 44100, True, "noise", 4096),
    "s16-stereo-silence": (16, 2, 48000, False, "burst", 4096),
    "s16-stereo-frame1024": (16, 2, 22050, False, "tone", 1024),
    "s20-stereo-tone": (20, 2, 48000, False, "tone", 4096),
    "s20-stereo-tone-fast": (20, 2, 48000, True, "tone", 4096),
    "s20-mono-noise": (20, 1, 48000, False, "noise", 4096),
    "s24-mono-tone": (24, 1, 96000, False, "tone", 4096),
    "s24-stereo-tone": (24, 2, 96000, False, "tone", 4096),
    "s24-stereo-tone-fast": (24, 2, 96000, True, "tone", 4096),
    "s24-stereo-noise": (24, 2, 48000, False, "noise", 4096),
    "s32-mono-noise": (32, 1, 48000, False, "noise", 4096),
    "s32-stereo-tone": (32, 2, 192000, False, "tone", 4096),
    "s32-stereo-tone-fast": (32, 2, 192000, True, "tone", 4096),
    "s16-3ch-tone": (16, 3, 44100, False, "tone", 4096),
    "s24-4ch-tone": (24, 4, 48000, False, "tone", 4096),
    "s16-5ch-tone": (16, 5, 44100, False, "tone", 4096),
    "s24-6ch-tone": (24, 6, 48000, False, "tone", 4096),
    "s16-6ch-tone-fast": (16, 6, 48000, True, "tone", 4096),
    "s16-7ch-tone": (16, 7, 48000, False, "tone", 4096),
    "s20-8ch-tone": (20, 8, 48000, False, "tone", 4096),
}


def signal(kind, n, channels, bits, seed):
    """Interleaved samples, right-aligned: triangle tones with a little noise, full-scale noise, or silence with a burst."""
    state = seed
    full = 1 << (bits - 1)
    out = []
    for i in range(n):
        for c in range(channels):
            state = (state * 1664525 + 1013904223) & 0xFFFFFFFF
            noise = (state >> 16) - 32768
            if kind == "noise":
                v = state >> (32 - bits)
                v = v - (1 << bits) if v >= full else v
            else:
                period = 97 + 31 * c + (i // 1500) * 7
                t = i % period
                v = (abs(2 * t - period) * full) // period - full // 2 + ((noise * (full >> 10)) >> 15)
                if kind == "burst" and not 3000 <= i < 3500:
                    v = 0
            out.append(v)
    return out


def raw(samples, bits):
    width = 2 if bits == 16 else 4 if bits == 32 else 3
    data = bytearray()
    for v in samples:
        if bits == 20:
            v <<= 4
        data += (v & ((1 << (8 * width)) - 1)).to_bytes(width, "little")
    return bytes(data)


def main():
    local = sys.argv[sys.argv.index("--alacref") + 1] if "--alacref" in sys.argv else None
    if not local:
        subprocess.run(["docker", "build", "-q", "-t", IMAGE, os.path.join(ROOT, "tools", "video", "alacref")], check=True)
    os.makedirs(OUT, exist_ok=True)
    manifest = {}
    with tempfile.TemporaryDirectory() as work:
        for index, (name, (bits, channels, rate, fast, kind, frame)) in enumerate(CLIPS.items()):
            seed = 1000 + index
            pcm = raw(signal(kind, SAMPLES, channels, bits, seed), bits)
            with open(os.path.join(work, "in.raw"), "wb") as f:
                f.write(pcm)
            target = os.path.join(OUT, name + ".alac")
            encode = ["encode", "in.raw", "out.alac", str(rate), str(channels), str(bits), "1" if fast else "0", str(frame)]
            decode = ["decode", "out.alac", "back.raw"]
            for args in (encode, decode):
                if local:
                    subprocess.run([os.path.abspath(local)] + args, cwd=work, check=True)
                else:
                    subprocess.run(["docker", "run", "--rm", "-v", f"{work}:/work", IMAGE, "alacref"] + args, check=True)
            with open(os.path.join(work, "back.raw"), "rb") as f:
                if f.read() != pcm:
                    sys.exit(f"{name}: Apple's decoder does not give the signal back")
            with open(os.path.join(work, "out.alac"), "rb") as f, open(target, "wb") as g:
                g.write(f.read())
            manifest[name] = {"bits": bits, "channels": channels, "rate": rate, "fast": fast, "kind": kind, "frameSize": frame,
                              "samples": SAMPLES, "seed": seed}
            print(name, os.path.getsize(target))
    with open(os.path.join(OUT, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


if __name__ == "__main__":
    main()
