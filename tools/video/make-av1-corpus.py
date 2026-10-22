"""Makes the AV1 container corpus for V4.2 from our own AV1 clips (tests/video/corpus/av1/*.ivf, encoded by libaom's aomenc in
tools/avifref/av1corpus.py), with tools run in Docker as black boxes: FFmpeg, ffprobe and mkvmerge (egami-video-tools, see
tools/video/docker) and libaom 3.15.2's aomdec and dav1d 1.5.4 (egami-avifref:2.9.2, see tools/avifref).

Outputs, committed, next to each clip in tests/video/corpus/av1:
  <clip>.frames.json        libaom's MD5 of every output frame, copied from .cache/avif/reference/av1/<clip>.ivf.json (made by
                            tools/make-av1-references.py): {"frames": [[width, height, bit depth, monochrome, md5, md5 without
                            film grain], ...]}, the MD5 over the planes Y, U and V cropped to the frame as 16-bit little-endian samples.
  <clip>.webm, <clip>.mkv   the IVF clip's packets remuxed by FFmpeg (-c copy) into WebM, and for some clips into Matroska, with
  <file>.json               ffprobe's streams and packets (as tools/video/make-corpus.py writes them).
  <clip>.obu                for some clips, the packets written by FFmpeg's obu muxer (low-overhead OBUs, a temporal delimiter
                            starting each temporal unit), with ffprobe's view of it.
  <clip>.annexb.obu         the same clips in the length-delimited format of the AV1 specification's Annex B, written by this script
                            from the IVF frames (each temporal unit's frames split as FFmpeg's av1_frame_split does, OBUs without
                            their size fields), with ffprobe's view of it (-f av1).
Both raw formats are then decoded by aomdec (--annexb for Annex B) and dav1d (--demuxer section5 or annexb), and every frame must
hash as libaom's reference does.
  hdr10.ivf                 a 10-bit BT.2020 PQ clip written by FFmpeg's libsvtav1 (SVT-AV1 1.4.1) with mastering display and
                            content light level metadata OBUs; its .frames.json comes from aomdec's frames.
  hdr10plus.ivf             the same units with an ITU-T T.35 HDR10+ metadata OBU (SMPTE ST 2094-40, the payload
                            make-vp9-corpus.py makes, changing per frame) inserted after each temporal delimiter.
  <clip>.side_data.json     ffprobe's side data of each frame of those two (FFmpeg's libdav1d decoder): the HDR values to match.

Usage, from the repo root with Docker running, after python tools/make-av1-references.py:
  python tools/video/make-av1-corpus.py
"""
import hashlib
import json
import os
import shutil
import subprocess
import sys

TOOLS = "egami-video-tools"
DECODERS = "egami-avifref:2.9.2"
CLIPS = "tests/video/corpus/av1"
REFERENCES = ".cache/avif/reference/av1"

# Clips also remuxed into Matroska (every clip goes into WebM).
MKV = {"tools-8bit", "tools-10bit", "tools-12bit", "monochrome", "forward-keyframes", "resize", "yuv444"}

# Clips this script encodes itself, whose frame references come from aomdec rather than tools/make-av1-references.py.
HDR = ("hdr10", "hdr10plus")
MASTERING = "G(0.265,0.690)B(0.150,0.060)R(0.680,0.320)WP(0.3127,0.3290)L(1000,0.0001)"

# Clips also written as raw OBU and Annex B streams.
RAW = {"tools-8bit", "tools-10bit", "monochrome", "forward-keyframes"}

PACKET_FIELDS = ["stream_index", "pts", "dts", "duration", "size", "pos", "flags"]
STREAM_FIELDS = ["index", "codec_type", "codec_name", "codec_tag_string", "profile", "width", "height", "pix_fmt", "color_range",
                 "color_space", "color_transfer", "color_primaries", "chroma_location", "r_frame_rate", "avg_frame_rate",
                 "time_base", "nb_frames", "duration_ts", "side_data_list"]


def run(cmd):
    return subprocess.run(cmd, shell=True, check=True, capture_output=True)


def probe(path, extra=""):
    data = json.loads(run(f"ffprobe -v error {extra} -show_streams -show_format -show_packets -of json {path}").stdout)
    trimmed = {
        "format": {k: data["format"].get(k) for k in ["format_name", "duration", "nb_streams"]},
        "streams": [{k: s[k] for k in STREAM_FIELDS if k in s} for s in data["streams"]],
        "packets": [{k: p[k] for k in PACKET_FIELDS if k in p} for p in data.get("packets", [])],
    }
    with open(path + ".json", "w", encoding="utf-8", newline="\n") as f:
        f.write('{"format":' + json.dumps(trimmed["format"]) + ',\n"streams":[\n')
        f.write(",\n".join(json.dumps(s) for s in trimmed["streams"]) + '],\n"packets":[\n')
        f.write(",\n".join(json.dumps(p) for p in trimmed["packets"]) + "]}\n")


def ivf_frames(path):
    data = open(path, "rb").read()
    at = int.from_bytes(data[6:8], "little")
    while at + 12 <= len(data):
        size = int.from_bytes(data[at:at + 4], "little")
        yield data[at + 12:at + 12 + size]
        at += 12 + size


def leb128(value):
    out = bytearray()
    while True:
        b = value & 0x7F
        value >>= 7
        out.append(b | (0x80 if value else 0))
        if not value:
            return bytes(out)


def read_leb128(data, at):
    value = 0
    for i in range(8):
        value |= (data[at + i] & 0x7F) << (7 * i)
        if not data[at + i] & 0x80:
            return value, at + i + 1
    raise ValueError("leb128 over 8 bytes")


def obus(unit):
    """(type, header bytes, payload) of each OBU of a low-overhead temporal unit."""
    at = 0
    while at < len(unit):
        header = unit[at]
        length = 2 if header & 4 else 1
        head = bytes(unit[at:at + length])
        at += length
        size, at = read_leb128(unit, at) if header & 2 else (len(unit) - at, at)
        yield (header >> 3) & 15, head, unit[at:at + size]
        at += size


def annexb(ivf, out):
    """Annex B from the IVF frames: frame units as FFmpeg's av1_frame_split splits a temporal unit (non-frame OBUs go with the frame
    after them, tile groups with their frame), OBUs without their size field."""
    with open(out, "wb") as f:
        for unit in ivf_frames(ivf):
            frames, current, pending = [], [], []
            for kind, head, payload in obus(unit):
                obu = bytes([head[0] & ~2]) + head[1:] + payload
                if kind in (3, 6):
                    if current:
                        frames.append(current)
                    current = pending + [obu]
                    pending = []
                elif kind in (4, 7, 8):
                    current += pending + [obu]
                    pending = []
                else:
                    pending.append(obu)
            frames.append(current + pending)
            if not frames[0] or (frames[0][0][0] >> 3) & 15 != 2:
                frames[0].insert(0, bytes([2 << 3]))
            units = [b"".join(leb128(len(o)) + o for o in frame) for frame in frames]
            body = b"".join(leb128(len(u)) + u for u in units)
            f.write(leb128(len(body)) + body)


def write_ivf(source, out, units):
    data = open(source, "rb").read()
    header = bytearray(data[:int.from_bytes(data[6:8], "little")])
    header[24:28] = len(units).to_bytes(4, "little")
    at = len(header)
    with open(out, "wb") as f:
        f.write(header)
        for unit in units:
            f.write(len(unit).to_bytes(4, "little") + data[at + 4:at + 12] + unit)
            at += 12 + int.from_bytes(data[at:at + 4], "little")


def make_hdr():
    run(f"ffmpeg -v error -y -f lavfi -i testsrc2=size=128x96:rate=30 -frames:v 10 -pix_fmt yuv420p10le -color_primaries bt2020 "
        f"-color_trc smpte2084 -colorspace bt2020nc -color_range tv -c:v libsvtav1 -preset 10 -g 5 "
        f"-svtav1-params \"mastering-display={MASTERING}:content-light=1000,400\" -f ivf {CLIPS}/hdr10.ivf")
    import importlib.util
    spec = importlib.util.spec_from_file_location("vp9corpus", "tools/video/make-vp9-corpus.py")
    vp9 = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(vp9)
    units = []
    for n, unit in enumerate(ivf_frames(f"{CLIPS}/hdr10.ivf")):
        payload = leb128(4) + vp9.hdr10plus_payload(n) + b"\x80"  # metadata_type ITUT_T35, the payload, trailing bits
        assert unit[:2] == b"\x12\x00"
        units.append(unit[:2] + bytes([5 << 3 | 2]) + leb128(len(payload)) + payload + unit[2:])
    write_ivf(f"{CLIPS}/hdr10.ivf", f"{CLIPS}/hdr10plus.ivf", units)
    for name in HDR:
        frames = json.loads(run(f"ffprobe -v error -select_streams v -show_frames -of json {CLIPS}/{name}.ivf").stdout)["frames"]
        with open(f"{CLIPS}/{name}.side_data.json", "w", encoding="utf-8", newline="\n") as f:
            f.write('{"frames": [\n' + ",\n".join(json.dumps(fr.get("side_data_list", [])) for fr in frames) + "]}\n")


def clips():
    return sorted(f[:-4] for f in os.listdir(CLIPS) if f.endswith(".ivf"))


def inside():
    make_hdr()
    for name in clips():
        run(f"ffmpeg -v error -y -i {CLIPS}/{name}.ivf -c copy -fflags +bitexact {CLIPS}/{name}.webm")
        probe(f"{CLIPS}/{name}.webm")
        if name in MKV:
            run(f"ffmpeg -v error -y -i {CLIPS}/{name}.ivf -c copy -fflags +bitexact {CLIPS}/{name}.mkv")
            probe(f"{CLIPS}/{name}.mkv")
        if name in RAW:
            run(f"ffmpeg -v error -y -i {CLIPS}/{name}.ivf -c copy -f obu {CLIPS}/{name}.obu")
            probe(f"{CLIPS}/{name}.obu", "-f obu")
            annexb(f"{CLIPS}/{name}.ivf", f"{CLIPS}/{name}.annexb.obu")
            probe(f"{CLIPS}/{name}.annexb.obu", "-f av1")
        print(name)


def frame_md5s(raw, frames):
    """Our reference MD5s of a decoder's raw output: each frame's Y, U and V as 16-bit little-endian samples. Both decoders write
    a monochrome frame's Y plane only, and 8-bit frames as bytes."""
    out, at = [], 0
    for w, h, bd, mono, _, _ in frames:
        bps = 1 if bd == 8 else 2
        cw, ch = (w + 1) >> 1, (h + 1) >> 1
        y = raw[at:at + w * h * bps]
        at += w * h * bps
        planes = [y]
        if not mono:
            planes += [raw[at:at + 2 * cw * ch * bps]]
            at += 2 * cw * ch * bps
        data = b"".join(planes)
        if bps == 1:
            data = bytes(b for v in data for b in (v, 0))
        out.append(hashlib.md5(data).hexdigest())
    return out, at == len(raw)


def hdr_references():
    """aomdec's frames of the clips this script encodes: 128x96 10-bit 4:2:0, no film grain."""
    for name in HDR:
        run(f"aomdec --rawvideo -o /tmp/out.yuv {CLIPS}/{name}.ivf")
        raw = open("/tmp/out.yuv", "rb").read()
        size = (128 * 96 + 2 * 64 * 48) * 2
        rows = [[128, 96, 10, 0, hashlib.md5(raw[i:i + size]).hexdigest(), hashlib.md5(raw[i:i + size]).hexdigest()] for i in range(0, len(raw), size)]
        with open(f"{CLIPS}/{name}.frames.json", "w", encoding="utf-8", newline="\n") as f:
            f.write('{"frames": [\n' + ",\n".join(json.dumps(r) for r in rows) + "]}\n")
        print(f"{name}: {len(rows)} frames from aomdec")


def check_decoders():
    hdr_references()
    for name in sorted(RAW):
        frames = json.load(open(f"{CLIPS}/{name}.frames.json"))["frames"]
        for cmd in (f"aomdec --rawvideo -o /tmp/out.yuv {CLIPS}/{name}.obu",
                    f"aomdec --annexb --rawvideo -o /tmp/out.yuv {CLIPS}/{name}.annexb.obu",
                    f"dav1d -q --demuxer section5 -i {CLIPS}/{name}.obu -o /tmp/out.yuv",
                    f"dav1d -q --demuxer annexb -i {CLIPS}/{name}.annexb.obu -o /tmp/out.yuv"):
            run(cmd)
            got, whole = frame_md5s(open("/tmp/out.yuv", "rb").read(), frames)
            if got != [f[4] for f in frames] or not whole:
                sys.exit(f"{name}: {cmd} does not give libaom's reference frames")
            print(f"{name}: {cmd} gives the reference frames")


def copy_references():
    for name in clips():
        if name in HDR:
            continue
        source = os.path.join(REFERENCES, name + ".ivf.json")
        if not os.path.isfile(source):
            sys.exit(f"{source} is missing: run python tools/make-av1-references.py first.")
        with open(source, encoding="utf-8") as f:
            rows = json.load(f)["frames"]
        with open(os.path.join(CLIPS, name + ".frames.json"), "w", encoding="utf-8", newline="\n") as f:
            f.write('{"frames": [\n' + ",\n".join(json.dumps(r) for r in rows) + "]}\n")


def main():
    if len(sys.argv) > 1 and sys.argv[1] == "--inside":
        inside()
        return

    if len(sys.argv) > 1 and sys.argv[1] == "--decoders":
        check_decoders()
        return

    copy_references()
    env = dict(os.environ, MSYS_NO_PATHCONV="1")
    for image, step in ((TOOLS, "--inside"), (DECODERS, "--decoders")):
        subprocess.run(["docker", "run", "--rm", "-v", f"{os.getcwd()}:/work", "-w", "/work", image, "python3", "tools/video/make-av1-corpus.py",
                        step], check=True, env=env)


if __name__ == "__main__":
    main()
