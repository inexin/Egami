"""Builds the JPEG benchmark inputs in .cache/bench-jpeg with libjpeg-turbo's cjpeg in Docker.

The JPEG design's decode scenarios need the 4000 x 3000 benchmark photo as JPEG files in modes Egami's own encoder does not write
(progressive), so all of them come from cjpeg, made the same way on every machine:
  photo-420.jpg          quality 90, 4:2:0
  photo-444.jpg          quality 90, 4:4:4
  photo-progressive.jpg  quality 90, 4:2:0, progressive
  photo-gray.jpg         quality 90, one component
  camera-00..49.jpg      photo-420.jpg with a camera-like EXIF block (the probe and EXIF scenarios' stand-ins)
  photo-orientation-6.jpg  photo-420.jpg tagged with orientation 6 (the thumbnail scenario)

The photo itself is BenchImages.Photo, written by the benchmark project as PNG and converted to PPM by Pillow for cjpeg.

Usage, from the repo root with Docker running:
  python tools/make-jpeg-bench-files.py
The script runs alpine:3.20 and installs libjpeg-turbo-utils and Pillow inside it with apk. The files are not committed.
"""
import os
import subprocess

IMAGE = "alpine:3.20"
PACKAGES = "python3 py3-pillow libjpeg-turbo-utils"
TARGET = ".cache/bench-jpeg"

FILES = {
    "photo-420.jpg": ["-quality", "90", "-sample", "2x2"],
    "photo-444.jpg": ["-quality", "90", "-sample", "1x1"],
    "photo-progressive.jpg": ["-quality", "90", "-sample", "2x2", "-progressive"],
    "photo-gray.jpg": ["-quality", "90", "-grayscale"],
}


def main():
    os.makedirs(TARGET, exist_ok=True)
    png = os.path.join(TARGET, "photo.png")
    subprocess.run(["dotnet", "run", "-c", "Release", "--project", "benchmarks/Egami.Benchmarks", "--", "--write-photo", png], check=True)
    steps = ["python3 -c \"from PIL import Image; Image.open('/work/photo.png').convert('RGB').save('/work/photo.ppm')\""]
    for name, args in FILES.items():
        steps.append(f"cjpeg {' '.join(args)} -outfile /work/{name} /work/photo.ppm")
    steps.append("rm /work/photo.ppm")
    script = f"apk add --no-cache {PACKAGES} > /dev/null && " + " && ".join(steps)
    subprocess.run(["docker", "run", "--rm", "-v", f"{os.path.abspath(TARGET)}:/work", IMAGE, "sh", "-c", script], check=True)
    for name in FILES:
        print(f"{name}: {os.path.getsize(os.path.join(TARGET, name)):,} bytes")
    subprocess.run(["dotnet", "run", "-c", "Release", "--project", "benchmarks/Egami.Benchmarks", "--", "--write-camera-files", TARGET], check=True)
    print("camera-00..49.jpg and photo-orientation-6.jpg written.")


if __name__ == "__main__":
    main()
