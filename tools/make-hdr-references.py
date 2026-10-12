"""Builds the Phase 2.9.1 HDR corpora and references, in Docker.

The image tools/hdrref/Dockerfile (egami-hdrref:2.9.1) pins OpenEXR 3.5.2 (library, tools and Python bindings), stb_image at
commit 2c980bb5, libultrahdr 2.0.2, ExifTool and ImageMagick; tools/hdrref/hdrcorpus.py writes the files (see its docstring).

Local inputs (never committed):
  .cache/openexr-images        https://github.com/AcademySoftwareFoundation/openexr-images at e38ffb07 (cloned when missing)

Usage, from the repo root with Docker running:
  python tools/make-hdr-references.py radiance
"""
import os
import subprocess
import sys

IMAGE = "egami-hdrref:2.9.1"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

images = os.path.join(ROOT, ".cache", "openexr-images")
if not os.path.isdir(images):
    subprocess.run(["git", "clone", "-q", "https://github.com/AcademySoftwareFoundation/openexr-images", images], check=True)
    subprocess.run(["git", "-C", images, "checkout", "-q", "e38ffb0790f62f05a6f083a6fa4cac150b3b7452"], check=True)

if subprocess.run(["docker", "image", "inspect", IMAGE], capture_output=True).returncode != 0:
    subprocess.run(["docker", "build", "-t", IMAGE, os.path.join(ROOT, "tools", "hdrref")], check=True)

env = dict(os.environ, MSYS_NO_PATHCONV="1")
subprocess.run(["docker", "run", "--rm", "-v", f"{ROOT}:/w", "-v", f"{os.path.join(ROOT, 'tools', 'hdrref')}:/t:ro", IMAGE,
                "python3", "/t/hdrcorpus.py", *sys.argv[1:]], check=True, env=env)
