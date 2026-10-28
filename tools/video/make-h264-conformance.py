"""Fetches the interlaced and FRExt H.264 conformance bitstreams (ITU-T H.264.1) and the frames FFmpeg's H.264 decoder gives
for them, for Egami.Video.H264's ConformanceTests. FFmpeg runs only as a black-box tool in Docker; its source is never read.
The streams stay in .cache and are never committed.

Inputs and outputs:
  .cache/video/h264conf/<set>.zip           the zips from the ITU JVT site, AVCv1 and FRExt sets whose names say they are
                                            interlaced (field, PAFF, MBAFF) or FRExt High profile streams
  .cache/video/h264conf/x/<set>/...         the unzipped bitstreams (.264, .jsv, .26l, .avc, .jvt, .h264)
  .cache/video/h264conf/refs.json           per bitstream, relative to the folder, the MD5 of each frame FFmpeg decodes

Usage, from the repo root:
  docker build -t egami-video-tools tools/video/docker
  python tools/video/make-h264-conformance.py           fetch, unzip and decode (about 1.3 GB)
  python tools/video/make-h264-conformance.py --refs    only decode what is already unzipped
"""
import json
import os
import re
import subprocess
import sys
import urllib.request
import zipfile

SITE = "https://www.itu.int"
SETS = ["/wftp3/av-arch/jvt-site/draft_conformance/AVCv1/", "/wftp3/av-arch/jvt-site/draft_conformance/FRExt/"]
WANTED = re.compile(r"fld|mbaff|picaff|paff|field|CAFI|CAMA|CAPA|CVFI|CVMA|CVPA|CVSE|CVNLFI|CVMP|/FI1|CANLMA|CVCANLMA|MR[6-9]_"
                    r"|HCAFF|HCAMFF|FL_|FLNL|MAPALQ|HVLC|CAPAMA|freh|FRExt[1-4]_Panasonic|FREXT0", re.IGNORECASE)
STREAMS = (".264", ".jsv", ".26l", ".avc", ".jvt", ".h264")
OUT = ".cache/video/h264conf"
ENV = dict(os.environ, MSYS_NO_PATHCONV="1")


def fetch():
    os.makedirs(OUT, exist_ok=True)
    for listing in SETS:
        page = urllib.request.urlopen(SITE + listing).read().decode("utf-8", "replace")
        for href in re.findall(r'HREF="([^"]+\.zip)"', page, re.IGNORECASE):
            if not WANTED.search(href):
                continue
            name = os.path.basename(href)
            target = os.path.join(OUT, name)
            if not os.path.exists(target):
                urllib.request.urlretrieve(SITE + href, target)
            with zipfile.ZipFile(target) as z:
                z.extractall(os.path.join(OUT, "x", name[:-4]))
            print(name)


def refs():
    root = os.path.abspath(OUT)
    files = []
    for d, _, names in os.walk(os.path.join(root, "x")):
        files += [os.path.relpath(os.path.join(d, n), root).replace("\\", "/") for n in names if n.lower().endswith(STREAMS)]
    result = {}
    for f in sorted(files):
        out = subprocess.run(["docker", "run", "--rm", "-v", f"{root}:/w", "egami-video-tools", "sh", "-c",
                              f"cd /w && ffmpeg -v error -i '{f}' -f framemd5 -"], capture_output=True, text=True, env=ENV).stdout
        result[f] = [line.split(",")[-1].strip() for line in out.splitlines() if line and not line.startswith("#")]
        print(f, len(result[f]))
    with open(os.path.join(root, "refs.json"), "w", encoding="utf-8", newline="\n") as stream:
        json.dump(result, stream, indent=1)


if __name__ == "__main__":
    if "--refs" not in sys.argv:
        fetch()
    refs()
