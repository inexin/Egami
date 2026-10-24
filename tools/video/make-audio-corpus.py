"""Makes the V5.0 audio test corpus: short WAV, Wave64, RF64 and FLAC clips (and FLAC in Matroska and MP4) written by FFmpeg and
the flac tool in Docker, with the decoded samples' MD5 from libFLAC's flac -d (FLAC) or FFmpeg (PCM) as the expected result.
The tools run only as black boxes; their source is never read.

Usage, from the repo root:
  docker build -t egami-audioref:v5.0 tools/video/audioref
  python tools/video/make-audio-corpus.py              clips into tests/video/corpus/audio (checked in) with manifest.json
  python tools/video/make-audio-corpus.py --ietf       also fetch the IETF CELLAR flac-test-files into .cache/audio/flac-test-files
                                                       and write their libFLAC references (not checked in: terms unrecorded)
  docker build -t egami-opusref:v5.1 tools/video/opusref
  python tools/video/make-audio-corpus.py --opus       V5.1: Ogg and Opus clips into tests/video/corpus/audio/opus (checked in),
                                                       each with opusdec's 16-bit decode, and the RFC 8251 test vectors fetched
                                                       into .cache/audio/opus-vectors
  python tools/video/make-audio-corpus.py --mp3        V5.8: MP3 and MP2 clips into tests/video/corpus/audio/mp3 (checked in), each with
                                                       FFmpeg's gapless float decode (.f32.gz) and ffprobe's packet count in
                                                       manifest.json, and the minimp3 copy of the ISO/IEC 11172-4 compliance
                                                       streams fetched into .cache/audio/mp3-vectors (not checked in: ISO terms)
                                                       Add --local to run FFmpeg from the PATH instead of in Docker.
  python tools/video/make-audio-corpus.py --aiff       V5.9: AIFF and AIFF-C clips, and G.711 in WAV and MOV, into
                                                       tests/video/corpus/audio/aiff (checked in) with FFmpeg's decode MD5 in
                                                       manifest.json. Add --local to run FFmpeg from the PATH instead of in Docker.
  python tools/video/make-audio-corpus.py --alac       V5.9: FFmpeg's ALAC in MP4, QuickTime and Matroska, into
                                                       tests/video/corpus/audio/alac-files (add --local to run FFmpeg here).
  python tools/video/make-audio-corpus.py --ac3        V5.9: raw AC-3 and E-AC-3, and both in MP4 and Matroska, into
                                                       tests/video/corpus/audio/ac3 (add --local to use the local ffmpeg).
  python tools/video/make-audio-corpus.py --ac3-decode V5.9: FFmpeg-encoded AC-3 at 32 to 640 kbit/s in every channel mode FFmpeg's
                                                       encoder has, LFE on and off, coupling on and off, into
                                                       tests/video/corpus/audio/ac3-decode (checked in), each with FFmpeg's float
                                                       decode (.f32.gz) of the stream with its dither flags
                                                       cleared. Add --local to run FFmpeg from the PATH.
  python tools/video/make-audio-corpus.py --caf        V5.9: CAF clips (PCM, G.711, FLAC, ALAC, Opus, MP3, AC-3) into
                                                       tests/video/corpus/audio/caf (checked in) with FFmpeg's decode MD5 for the
                                                       lossless ones and ffprobe's packet count for every clip. Add --local to run
                                                       FFmpeg from the PATH instead of in Docker.
  python tools/video/make-audio-corpus.py --check DIR  run flac -t on every .flac file in DIR (Egami.Video's own output)
  python tools/video/make-audio-corpus.py --gates      after the EGAMI_AUDIO_GATES=1 tests: flac -t on Egami.Video's encodings
                                                       of the IETF files, their size against flac -0 to -8, and flac -d's time

manifest.json maps each clip to {samples, rate, channels, bits, md5}: the MD5 of every sample interleaved, little-endian, signed,
in whole bytes of the stream's depth (24-bit in 3 bytes), as FLAC's STREAMINFO defines it; float clips use 32-bit floats.
"""
import gzip
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


OPUS_IMAGE = "egami-opusref:v5.1"
OPUS_OUT = os.path.join(OUT, "opus")

# name: shell command in /work making {out} from the sources in /tmp (a.wav: stereo 48 kHz, m.wav: mono 16 kHz, s.wav: 5.1)
OPUS_CLIPS = {
    "opus-stereo.opus": "opusenc --quiet --bitrate 96 /tmp/a.wav {out}",
    "opus-mono-speech.opus": "opusenc --quiet --bitrate 10 --framesize 20 /tmp/m.wav {out}",
    "opus-hybrid.opus": "opusenc --quiet --bitrate 24 /tmp/a.wav {out}",
    "opus-51.opus": "opusenc --quiet --bitrate 192 /tmp/s.wav {out}",
    "opus-fec.opus": "opusenc --quiet --bitrate 16 --expect-loss 20 /tmp/m.wav {out}",
    "opus-chained.opus": "opusenc --quiet --bitrate 64 --title One /tmp/a.wav /tmp/1.opus && opusenc --quiet --bitrate 64 --title Two /tmp/a.wav /tmp/2.opus && cat /tmp/1.opus /tmp/2.opus > {out}",
    "webm-opus.webm": "opusenc --quiet --bitrate 64 /tmp/a.wav /tmp/1.opus && ffmpeg -v error -i /tmp/1.opus -c:a copy {out}",
    "mp4-opus.mp4": "opusenc --quiet --bitrate 64 /tmp/a.wav /tmp/1.opus && ffmpeg -v error -i /tmp/1.opus -c:a copy -strict -2 {out}",
    "ogg-flac.oga": "ffmpeg -v error -i /tmp/a.wav -c:a flac -sample_fmt s16 {out}",
    "ogg-vorbis.ogg": "ffmpeg -v error -i /tmp/a.wav -c:a libvorbis -q:a 3 {out}",
}

OPUS_SOURCES = ("ffmpeg -v error -y " + source(48000, 2) + " -c:a pcm_s16le /tmp/a.wav && ffmpeg -v error -y "
                + source(16000, 1) + " -c:a pcm_s16le /tmp/m.wav && ffmpeg -v error -y " + source(48000, 6)
                + " -c:a pcm_s16le -channel_layout 5.1 /tmp/s.wav")


def make_opus():
    os.makedirs(OPUS_OUT, exist_ok=True)
    for name, command in OPUS_CLIPS.items():
        out = f"/work/{name}"
        ref = ""
        if name.endswith(".opus"):
            # opusdec's 16-bit decode without dither, gain and pre-skip applied, end trimmed: what Egami.Video's reader must match.
            ref = f" && opusdec --quiet --no-dither --rate 48000 {out} /work/{name}.wav"
        subprocess.run(["docker", "run", "--rm", "-v", f"{OPUS_OUT}:/work", OPUS_IMAGE, "sh", "-c",
                        OPUS_SOURCES + " && " + command.format(out=out) + ref], check=True)
        print(name, os.path.getsize(os.path.join(OPUS_OUT, name)))
    vectors = os.path.join(CACHE, "opus-vectors")
    if not os.path.isdir(os.path.join(vectors, "opus_newvectors")):
        os.makedirs(vectors, exist_ok=True)
        subprocess.run(["curl", "-sSL", "-o", os.path.join(vectors, "v.tar.gz"),
                        "https://opus-codec.org/static/testvectors/opus_testvectors-rfc8251.tar.gz"], check=True)
        subprocess.run(["tar", "xzf", "v.tar.gz"], cwd=vectors, check=True)


# name: shell command making {out} from the sources in /tmp (a.wav: stereo 44.1 kHz, b.wav: stereo 48 kHz, m.wav: mono 22.05 kHz,
# e.wav: mono 8 kHz, cover.png, chapters.txt). FFmpeg's libmp3lame and mp3 muxer write the Xing/Info and LAME tags, ID3v2.4 by
# default; libtwolame writes Layer II with CRCs.
MP3_OUT = os.path.join(OUT, "mp3")
MP3_CLIPS = {
    "mp3-cbr-stereo.mp3": "ffmpeg -v error -i /tmp/a.wav -c:a libmp3lame -b:a 128k -metadata title='Corpus tone' -metadata artist=Egami {out}",
    "mp3-vbr-joint.mp3": "ffmpeg -v error -i /tmp/a.wav -c:a libmp3lame -q:a 4 {out}",
    "mp3-mpeg2-mono.mp3": "ffmpeg -v error -i /tmp/m.wav -c:a libmp3lame -b:a 32k {out}",
    "mp3-mpeg25-8k.mp3": "ffmpeg -v error -i /tmp/e.wav -c:a libmp3lame -b:a 16k {out}",
    "mp3-id3v23-cover-chapters.mp3": "ffmpeg -v error -i /tmp/a.wav -i /tmp/cover.png -i /tmp/chapters.txt -map 0:a -map 1:v -map_metadata 2"
                                     " -map_chapters 2 -c:a libmp3lame -b:a 96k -c:v copy -disposition:v attached_pic -id3v2_version 3 {out}",
    "mp3-id3v1-noxing.mp3": "ffmpeg -v error -i /tmp/a.wav -c:a libmp3lame -b:a 112k -write_xing 0 -id3v2_version 0 {out}",
    "mp2-stereo.mp2": "ffmpeg -v error -i /tmp/b.wav -c:a mp2 -b:a 192k {out}",
    "mp2-crc-mono.mp2": "ffmpeg -v error -i /tmp/m.wav -c:a libtwolame -b:a 64k -error_protection 1 {out}",
    "mka-mp3.mka": "ffmpeg -v error -i /tmp/a.wav -c:a libmp3lame -b:a 128k -y /tmp/x.mp3 && ffmpeg -v error -i /tmp/x.mp3 -c:a copy {out}",
    "m4a-mp3.m4a": "ffmpeg -v error -i /tmp/a.wav -c:a libmp3lame -b:a 128k -y /tmp/x.mp3 && ffmpeg -v error -i /tmp/x.mp3 -c:a copy -f mp4 {out}",
}

# Clips that get an ID3v1.1 tag appended: title, artist, track number and genre index.
MP3_ID3V1 = {"mp3-id3v1-noxing.mp3": ("Corpus tone", "Egami", 7, 13)}

MP3_SOURCES = ("ffmpeg -v error -y " + source(44100, 2, 0.3) + " -c:a pcm_s16le /tmp/a.wav && ffmpeg -v error -y " + source(48000, 2, 0.3)
               + " -c:a pcm_s16le /tmp/b.wav && ffmpeg -v error -y " + source(22050, 1, 0.3) + " -c:a pcm_s16le /tmp/m.wav && ffmpeg -v error -y "
               + source(8000, 1, 0.3) + " -c:a pcm_s16le /tmp/e.wav && ffmpeg -v error -y -f lavfi -i color=red:size=16x16 -frames:v 1 /tmp/cover.png"
               " && printf ';FFMETADATA1\\ntitle=Corpus tone\\nartist=Egami\\n[CHAPTER]\\nTIMEBASE=1/1000\\nSTART=0\\nEND=150\\ntitle=One\\n"
               "[CHAPTER]\\nTIMEBASE=1/1000\\nSTART=150\\nEND=300\\ntitle=Two\\n' > /tmp/chapters.txt")

# The ISO/IEC 11172-4 compliance streams (and minimp3's own damaged-stream vectors) with 16-bit references, from minimp3 (CC0).
MP3_VECTORS = ["l1-fl1", "l1-fl2", "l1-fl3", "l1-fl4", "l1-fl5", "l1-fl6", "l1-fl7", "l1-fl8", "l2-fl10", "l2-fl11", "l2-fl12",
               "l2-fl13", "l2-fl14", "l2-fl15", "l2-fl16", "l3-compl", "l3-he_32khz", "l3-he_44khz", "l3-he_48khz", "l3-he_free",
               "l3-he_mode", "l3-hecommon", "l3-si", "l3-si_huff", "l3-sin1k0db", "l3-test45", "l3-test46", "M2L3_bitrate_16_all",
               "M2L3_bitrate_22_all", "M2L3_bitrate_24_all", "M2L3_compl24", "M2L3_noise", "l3-nonstandard-compl-sideinfo-bigvalues",
               "l3-nonstandard-compl-sideinfo-blocktype", "l3-nonstandard-compl-sideinfo-size", "l3-nonstandard-id3v1",
               "l3-nonstandard-sideinfo-size"]
MP3_VECTOR_URL = "https://raw.githubusercontent.com/lieff/minimp3/master/vectors/"


def make_mp3(local):
    os.makedirs(MP3_OUT, exist_ok=True)

    def run(command):
        if local:
            subprocess.run(["sh", "-c", command.replace("/work/", MP3_OUT + "/")], check=True)
        else:
            subprocess.run(["docker", "run", "--rm", "-v", f"{MP3_OUT}:/work", IMAGE, "sh", "-c", command], check=True)

    def output(command):
        if local:
            return subprocess.run(["sh", "-c", command.replace("/work/", MP3_OUT + "/")], check=True, capture_output=True).stdout
        return subprocess.run(["docker", "run", "--rm", "-v", f"{MP3_OUT}:/work", IMAGE, "sh", "-c", command], check=True,
                              capture_output=True).stdout

    manifest = {}
    for name, command in MP3_CLIPS.items():
        path = os.path.join(MP3_OUT, name)
        if os.path.exists(path):
            os.remove(path)
        run(MP3_SOURCES + " && " + command.format(out=f"/work/{name}"))
        if name in MP3_ID3V1:
            # FFmpeg 6.1's -write_id3v1 writes nothing here, so the 128-byte ID3v1.1 tag is appended by hand.
            title, artist, track, genre = MP3_ID3V1[name]
            tag = b"TAG" + title.encode("latin-1").ljust(30, b"\0") + artist.encode("latin-1").ljust(30, b"\0") + b"\0" * 30
            tag += b"2026" + b"\0" * 28 + bytes([0, track, genre])
            with open(path, "ab") as f:
                f.write(tag)
        # FFmpeg's float decode with its gapless trimming (the LAME tag's delay and padding, MP4 edit lists).
        raw = output(f"ffmpeg -v error -i /work/{name} -map 0:a -c:a pcm_f32le -f f32le -")
        with gzip.GzipFile(path + ".f32.gz", "wb", 9, mtime=0) as f:
            f.write(raw)
        info = json.loads(output(f"ffprobe -v error -count_packets -select_streams a -show_entries stream=channels,sample_rate,nb_read_packets"
                                 f" -of json /work/{name}"))["streams"][0]
        channels = int(info["channels"])
        manifest[name] = {"channels": channels, "rate": int(info["sample_rate"]), "samples": len(raw) // 4 // channels,
                          "packets": int(info["nb_read_packets"])}
        print(name, os.path.getsize(path), manifest[name])
    with open(os.path.join(MP3_OUT, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")

    vectors = os.path.join(CACHE, "mp3-vectors")
    os.makedirs(vectors, exist_ok=True)
    for name in MP3_VECTORS:
        for ext in (".bit", ".pcm"):
            target = os.path.join(vectors, name + ext)
            if not os.path.exists(target):
                subprocess.run(["curl", "-sSfL", "-o", target, MP3_VECTOR_URL + name + ext], check=ext == ".bit")


# name: (FFmpeg arguments after the source, rate, channels, bits of the reference decode, float, decodes). FFmpeg's aiff muxer writes
# AIFF for big-endian integers and AIFF-C for the others; ima4 is there to check pass-through, so it has no reference.
AIFF_OUT = os.path.join(OUT, "aiff")
AIFF_CLIPS = {
    "aiff-s16-stereo.aiff": ("-c:a pcm_s16be", 44100, 2, 16, False, True),
    "aiff-s8-mono.aiff": ("-c:a pcm_s8", 8000, 1, 8, False, True),
    "aiff-s24-51.aiff": ("-c:a pcm_s24be", 16000, 6, 24, False, True),
    "aiff-s32-stereo.aiff": ("-c:a pcm_s32be", 22050, 2, 32, False, True),
    "aifc-f32-stereo.aifc": ("-c:a pcm_f32be -f aiff", 22050, 2, 32, True, True),
    "aifc-f64-mono.aifc": ("-c:a pcm_f64be -f aiff", 22050, 1, 32, True, True),
    "aifc-sowt-stereo.aifc": ("-c:a pcm_s16le -f aiff", 44100, 2, 16, False, True),
    "aifc-ulaw-mono.aifc": ("-c:a pcm_mulaw -f aiff", 8000, 1, 16, False, True),
    "aifc-alaw-mono.aifc": ("-c:a pcm_alaw -f aiff", 8000, 1, 16, False, True),
    "aifc-ima4-stereo.aifc": ("-c:a adpcm_ima_qt -f aiff", 22050, 2, 16, False, False),
    "aiff-tags-id3.aiff": ("-c:a pcm_s16be -write_id3v2 1 -metadata title='Corpus tone' -metadata artist=Egami -metadata album='Egami corpus'"
                           " -metadata comment='Test clip'", 22050, 1, 16, False, True),
    "wav-ulaw-mono.wav": ("-c:a pcm_mulaw", 8000, 1, 16, False, True),
    "wav-alaw-mono.wav": ("-c:a pcm_alaw", 8000, 1, 16, False, True),
    "mov-ulaw-mono.mov": ("-c:a pcm_mulaw", 8000, 1, 16, False, True),
}


def make_aiff(local):
    os.makedirs(AIFF_OUT, exist_ok=True)

    def output(command):
        if local:
            return subprocess.run(["sh", "-c", command.replace("/work/", AIFF_OUT + "/")], check=True, capture_output=True).stdout
        return subprocess.run(["docker", "run", "--rm", "-v", f"{AIFF_OUT}:/work", IMAGE, "sh", "-c", command], check=True,
                              capture_output=True).stdout

    manifest = {}
    for name, (args, rate, channels, bits, is_float, decodes) in AIFF_CLIPS.items():
        path = os.path.join(AIFF_OUT, name)
        if os.path.exists(path):
            os.remove(path)
        output("ffmpeg -y -v error -bitexact " + source(rate, channels, 0.3) + f" {args} /work/{name}")
        if not decodes:
            manifest[name] = {"decodes": False}
            print(name, os.path.getsize(path), "pass-through only")
            continue
        codec = "pcm_f32le" if is_float else {8: "pcm_s8", 16: "pcm_s16le", 24: "pcm_s24le", 32: "pcm_s32le"}[bits]
        fmt = "f32le" if is_float else {8: "s8", 16: "s16le", 24: "s24le", 32: "s32le"}[bits]
        raw = output(f"ffmpeg -v error -i /work/{name} -c:a {codec} -f {fmt} -")
        width = 4 if is_float else (bits + 7) // 8
        manifest[name] = {"samples": len(raw) // (width * channels), "rate": rate, "channels": channels, "bits": bits,
                          "float": is_float, "md5": hashlib.md5(raw).hexdigest()}
        print(name, os.path.getsize(path), manifest[name]["samples"], manifest[name]["md5"])
    with open(os.path.join(AIFF_OUT, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


# name: (FFmpeg arguments after the source, rate, channels, bits of the reference decode, float, lossless). Lossless clips get the
# MD5 of FFmpeg's decode; every clip gets ffprobe's packet count. FLAC is left out: FFmpeg 6.1 writes it with a garbage frame count
# in desc and pakt, so the tests write their own.
CAF_OUT = os.path.join(OUT, "caf")
CAF_CLIPS = {
    "caf-s16le-stereo.caf": ("-c:a pcm_s16le", 44100, 2, 16, False, True),
    "caf-s16be-stereo.caf": ("-c:a pcm_s16be", 44100, 2, 16, False, True),
    "caf-s8-mono.caf": ("-c:a pcm_s8", 8000, 1, 8, False, True),
    "caf-s24be-51.caf": ("-c:a pcm_s24be", 16000, 6, 24, False, True),
    "caf-s32le-stereo.caf": ("-c:a pcm_s32le", 22050, 2, 32, False, True),
    "caf-f32le-stereo.caf": ("-c:a pcm_f32le", 22050, 2, 32, True, True),
    "caf-f64be-mono.caf": ("-c:a pcm_f64be", 22050, 1, 32, True, True),
    "caf-ulaw-mono.caf": ("-c:a pcm_mulaw", 8000, 1, 16, False, True),
    "caf-alaw-mono.caf": ("-c:a pcm_alaw", 8000, 1, 16, False, True),
    "caf-alac-stereo.caf": ("-sample_fmt s16p -c:a alac", 44100, 2, 16, False, True),
    "caf-alac-24-51.caf": ("-sample_fmt s32p -c:a alac", 48000, 6, 24, False, True),
    "caf-opus-stereo.caf": ("-c:a libopus -b:a 96k", 48000, 2, 16, False, False),
    "caf-mp3-stereo.caf": ("-c:a libmp3lame -b:a 128k", 44100, 2, 16, False, False),
    "caf-ac3-stereo.caf": ("-c:a ac3 -b:a 192k", 48000, 2, 16, False, False),
    "caf-tags.caf": ("-c:a pcm_s16le -metadata title='Corpus tone' -metadata artist=Egami -metadata album='Egami corpus'"
                     " -metadata comment='Test clip'", 22050, 1, 16, False, True),
}


# V5.9 stage C: FFmpeg's ALAC in MP4, QuickTime (the cookie in a wave atom) and Matroska, with the MD5 of FFmpeg's decode.
ALAC_OUT = os.path.join(OUT, "alac-files")
ALAC_CLIPS = {
    "m4a-alac-stereo.m4a": ("-sample_fmt s16p -c:a alac", 44100, 2, 16, False, True),
    "m4a-alac-24-51.m4a": ("-sample_fmt s32p -c:a alac", 48000, 6, 24, False, True),
    "m4a-alac-96k.m4a": ("-sample_fmt s32p -c:a alac", 96000, 2, 24, False, True),
    "mov-alac-mono.mov": ("-sample_fmt s16p -c:a alac", 22050, 1, 16, False, True),
    "mka-alac-stereo.mka": ("-sample_fmt s16p -c:a alac", 44100, 2, 16, False, True),
    "mka-alac-24-61.mka": ("-sample_fmt s32p -c:a alac", 48000, 7, 24, False, True),
}


def make_caf(local):
    make_decoded(CAF_OUT, CAF_CLIPS, local)


def make_alac(local):
    make_decoded(ALAC_OUT, ALAC_CLIPS, local)


def make_decoded(out, clips, local):
    os.makedirs(out, exist_ok=True)

    def output(command):
        if local:
            return subprocess.run(["sh", "-c", command.replace("/work/", out + "/")], check=True, capture_output=True).stdout
        return subprocess.run(["docker", "run", "--rm", "-v", f"{out}:/work", IMAGE, "sh", "-c", command], check=True,
                              capture_output=True).stdout

    manifest = {}
    for name, (args, rate, channels, bits, is_float, lossless) in clips.items():
        path = os.path.join(out, name)
        if os.path.exists(path):
            os.remove(path)
        output("ffmpeg -y -v error -bitexact " + source(rate, channels, 0.3) + f" {args} /work/{name}")
        probe = json.loads(output(f"ffprobe -v error -count_packets -show_streams -of json /work/{name}"))["streams"][0]
        entry = {"packets": int(probe["nb_read_packets"]), "rate": rate, "channels": channels}
        if lossless:
            codec = "pcm_f32le" if is_float else {8: "pcm_s8", 16: "pcm_s16le", 24: "pcm_s24le", 32: "pcm_s32le"}[bits]
            fmt = "f32le" if is_float else {8: "s8", 16: "s16le", 24: "s24le", 32: "s32le"}[bits]
            raw = output(f"ffmpeg -v error -i /work/{name} -c:a {codec} -f {fmt} -")
            width = 4 if is_float else (bits + 7) // 8
            entry.update({"samples": len(raw) // (width * channels), "bits": bits, "float": is_float, "md5": hashlib.md5(raw).hexdigest()})
        else:
            entry["decodes"] = False
        manifest[name] = entry
        print(name, os.path.getsize(path), entry)
    with open(os.path.join(out, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


# name: (FFmpeg arguments after the source, rate, channels, elementary stream format). Each clip gets ffprobe's packet count and
# the MD5 of its frames copied out as a raw stream, so that every container's packets can be checked against the same bytes.
AC3_OUT = os.path.join(OUT, "ac3")
AC3_CLIPS = {
    "ac3-stereo-48k.ac3": ("-c:a ac3 -b:a 192k", 48000, 2, "ac3"),
    "ac3-51-48k.ac3": ("-c:a ac3 -b:a 448k", 48000, 6, "ac3"),
    "ac3-mono-44k.ac3": ("-c:a ac3 -b:a 96k", 44100, 1, "ac3"),
    "ac3-stereo-32k.ac3": ("-c:a ac3 -b:a 128k", 32000, 2, "ac3"),
    "eac3-stereo-48k.eac3": ("-c:a eac3 -b:a 128k", 48000, 2, "eac3"),
    "eac3-51-48k.eac3": ("-c:a eac3 -b:a 384k", 48000, 6, "eac3"),
    "mp4-ac3-51.mp4": ("-c:a ac3 -b:a 448k", 48000, 6, "ac3"),
    "mp4-eac3-stereo.mp4": ("-c:a eac3 -b:a 128k", 48000, 2, "eac3"),
    "mka-ac3-stereo.mka": ("-c:a ac3 -b:a 192k", 48000, 2, "ac3"),
    "mka-eac3-51.mka": ("-c:a eac3 -b:a 384k", 48000, 6, "eac3"),
}


def make_ac3(local):
    os.makedirs(AC3_OUT, exist_ok=True)

    def output(command):
        if local:
            return subprocess.run(["sh", "-c", command.replace("/work/", AC3_OUT + "/")], check=True, capture_output=True).stdout
        return subprocess.run(["docker", "run", "--rm", "-v", f"{AC3_OUT}:/work", IMAGE, "sh", "-c", command], check=True,
                              capture_output=True).stdout

    manifest = {}
    for name, (args, rate, channels, fmt) in AC3_CLIPS.items():
        path = os.path.join(AC3_OUT, name)
        if os.path.exists(path):
            os.remove(path)
        output("ffmpeg -y -v error -bitexact " + source(rate, channels, 0.5) + f" {args} /work/{name}")
        probe = json.loads(output(f"ffprobe -v error -count_packets -show_streams -of json /work/{name}"))["streams"][0]
        raw = output(f"ffmpeg -v error -i /work/{name} -c:a copy -f {fmt} -")
        manifest[name] = {"packets": int(probe["nb_read_packets"]), "rate": rate, "channels": channels, "eac3": fmt == "eac3",
                          "bytes": len(raw), "md5": hashlib.md5(raw).hexdigest()}
        print(name, os.path.getsize(path), manifest[name])
    with open(os.path.join(AC3_OUT, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


# name: (FFmpeg channel layout, rate, bit rate, extra encoder arguments). The source puts a tone, a pink-noise bed and short chirp
# bursts (transients) in every channel, each channel different, so that bit allocation, coupling and rematrixing all have work.
AC3_DECODE_OUT = os.path.join(OUT, "ac3-decode")
AC3_DECODE_CLIPS = {
    "mono-32k.ac3": ("mono", 48000, "32k", ""),
    "mono-lfe-64k-44k.ac3": ("FC+LFE", 44100, "64k", ""),
    "stereo-40k.ac3": ("stereo", 48000, "40k", ""),
    "stereo-56k-32k.ac3": ("stereo", 32000, "56k", ""),
    "stereo-96k-cpl.ac3": ("stereo", 48000, "96k", "-channel_coupling 1 -cpl_start_band 1"),
    "stereo-128k-cpl0.ac3": ("stereo", 44100, "128k", "-channel_coupling 1 -cpl_start_band 0"),
    "stereo-160k-nocpl.ac3": ("stereo", 48000, "160k", "-channel_coupling 0"),
    "stereo-192k-noremat.ac3": ("stereo", 48000, "192k", "-channel_coupling 0 -stereo_rematrixing 0"),
    "stereo-lfe-224k-44k.ac3": ("2.1", 44100, "224k", ""),
    "3-0-256k.ac3": ("3.0", 48000, "256k", ""),
    "3-0-lfe-320k-32k.ac3": ("3.1", 32000, "320k", ""),
    "2-1-112k-cpl.ac3": ("3.0(back)", 48000, "112k", "-channel_coupling 1"),
    "2-1-lfe-256k.ac3": ("FL+FR+LFE+BC", 48000, "256k", "-channel_coupling 0"),
    "3-1-384k.ac3": ("4.0", 44100, "384k", ""),
    "3-1-lfe-448k.ac3": ("4.1", 48000, "448k", ""),
    "2-2-80k-cpl.ac3": ("quad(side)", 48000, "80k", "-channel_coupling 1 -cpl_start_band 5"),
    "2-2-320k.ac3": ("quad", 48000, "320k", "-channel_coupling 0"),
    "3-2-448k.ac3": ("5.0(side)", 48000, "448k", ""),
    "3-2-lfe-192k-cpl.ac3": ("5.1(side)", 48000, "192k", "-channel_coupling 1 -center_mixlev 0.707 -surround_mixlev 0.707"),
    "3-2-lfe-384k.ac3": ("5.1(side)", 48000, "384k", ""),
    "3-2-lfe-512k-44k.ac3": ("5.1(side)", 44100, "512k", "-channel_coupling 0"),
    "3-2-lfe-576k-32k.ac3": ("5.1", 32000, "576k", ""),
    "3-2-lfe-640k.ac3": ("5.1(side)", 48000, "640k", "-channel_coupling 0"),
    "dual-mono-192k.ac3": ("stereo", 48000, "192k", "-channel_coupling 0"),
    "dither-stereo-96k.ac3": ("stereo", 48000, "96k", ""),
    "dither-3-2-lfe-448k.ac3": ("5.1(side)", 48000, "448k", ""),
}


def ac3_source(layout, rate, seconds=0.25):
    channels = {"mono": 1, "stereo": 2, "FC+LFE": 2, "2.1": 3, "3.0": 3, "3.0(back)": 3, "3.1": 4, "FL+FR+LFE+BC": 4, "4.0": 4,
                "quad": 4, "quad(side)": 4, "4.1": 5, "5.0(side)": 5, "5.1": 6, "5.1(side)": 6}[layout]
    tones = [220, 277, 330, 440, 554, 659]
    inputs = ""
    for c in range(channels):
        expr = f"0.35*sin(2*PI*{tones[c]}*t)+0.3*sin(2*PI*(300+{c}*150)*t*(1+40*t))*lt(mod(t+{c}*0.013\\,0.07)\\,0.003)"
        inputs += f"-f lavfi -i \"aevalsrc='{expr}':s={rate}:d={seconds}\" "
        inputs += f"-f lavfi -i anoisesrc=color=pink:seed={c + 1}:amplitude=0.12:sample_rate={rate}:duration={seconds} "
    mixes = "".join(f"[{2 * c}][{2 * c + 1}]amix=inputs=2:normalize=0[m{c}];" for c in range(channels))
    pads = "".join(f"[m{c}]" for c in range(channels))
    merge = f"{pads}amerge=inputs={channels}," if channels > 1 else f"{pads}anull,"
    return channels, inputs + f"-filter_complex \"{mixes}{merge}channelmap=channel_layout={layout}[out]\" -map \"[out]\""


def make_ac3_decode(local):
    os.makedirs(AC3_DECODE_OUT, exist_ok=True)

    def output(command):
        if local:
            return subprocess.run(["sh", "-c", command.replace("/work/", AC3_DECODE_OUT.replace(os.sep, "/") + "/")], check=True,
                                  capture_output=True).stdout
        return subprocess.run(["docker", "run", "--rm", "-v", f"{AC3_DECODE_OUT}:/work", IMAGE, "sh", "-c", command], check=True,
                              capture_output=True).stdout

    for name, (layout, rate, bitrate, extra) in AC3_DECODE_CLIPS.items():
        path = os.path.join(AC3_DECODE_OUT, name)
        if os.path.exists(path):
            os.remove(path)
        channels, src = ac3_source(layout, rate)
        output(f"ffmpeg -y -v error -bitexact {src} -c:a ac3 -b:a {bitrate} {extra} -f ac3 /work/{name}")

    # FFmpeg's decoder fills zero-bit mantissas with its own random dither, which no other decoder can reproduce, so the streams
    # get their dithflags cleared (and their CRCs fixed) by Egami.Video.Tests' Ac3Undither before FFmpeg decodes them. Clips named
    # dither-* keep their dither for the tests that check it statistically; dual-mono-* are rewritten from 2/0 into 1+1, which
    # FFmpeg's encoder does not write.
    env = dict(os.environ, EGAMI_AC3_UNDITHER=AC3_DECODE_OUT)
    subprocess.run(["dotnet", "test", os.path.join(ROOT, "tests", "Egami.Video.Tests"), "--filter", "FullyQualifiedName~Ac3Undither.RewritesTheFolder"],
                   check=True, env=env)

    manifest = {}
    for name, (layout, rate, bitrate, extra) in AC3_DECODE_CLIPS.items():
        path = os.path.join(AC3_DECODE_OUT, name)
        channels = ac3_source(layout, rate)[0]
        raw = output(f"ffmpeg -v error -i /work/{name} -c:a pcm_f32le -f f32le -")
        with gzip.GzipFile(path + ".f32.gz", "wb", 9, mtime=0) as f:
            f.write(raw)
        manifest[name] = {"channels": channels, "rate": rate, "bitrate": bitrate, "samples": len(raw) // 4 // channels,
                          "dither": name.startswith("dither-")}
        print(name, os.path.getsize(path), manifest[name])
    with open(os.path.join(AC3_DECODE_OUT, "manifest.json"), "w", newline="\n") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")


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
    if "--opus" in sys.argv:
        make_opus()
        sys.exit(0)
    if "--caf" in sys.argv:
        make_caf("--local" in sys.argv)
        sys.exit(0)
    if "--ac3-decode" in sys.argv:
        make_ac3_decode("--local" in sys.argv)
        sys.exit(0)
    if "--ac3" in sys.argv:
        make_ac3("--local" in sys.argv)
        sys.exit(0)
    if "--alac" in sys.argv:
        make_alac("--local" in sys.argv)
        sys.exit(0)
    if "--aiff" in sys.argv:
        make_aiff("--local" in sys.argv)
        sys.exit(0)
    if "--mp3" in sys.argv:
        make_mp3("--local" in sys.argv)
        sys.exit(0)
    if "--ietf" in sys.argv:
        fetch_ietf()
    else:
        make_corpus()
