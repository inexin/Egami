"""Checks that OpenEXR 3.5.2 reads Egami's OpenEXR files to the pixels Egami wrote (Phase 2.9.1 task 8).

Runs ExrWriteTests.WritesInteropFiles with EGAMI_INTEROP_DIR=.cache/interop, which writes every compression (NONE, RLE, ZIPS,
ZIP, PIZ) in half and float for a synthetic pattern and a photograph, each with the expected pixels as float32 RGBA; then, in
the egami-hdrref image, reads each file with the OpenEXR Python bindings and compares bit for bit.

Usage, from the repo root with Docker running:
  python tools/check-exr-interop.py
"""
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, ".cache", "interop")
env = dict(os.environ, EGAMI_INTEROP_DIR=OUT)
subprocess.run(["dotnet", "test", os.path.join(ROOT, "tests", "Egami.Tests"), "-c", "Release", "--filter",
                "FullyQualifiedName~ExrWriteTests.WritesInteropFiles"], check=True, env=env, capture_output=True)

CHECK = r"""
import glob, sys
import numpy as np
import OpenEXR
failed = 0
files = sorted(glob.glob('/w/.cache/interop/exr/*.exr'))
for path in files:
    ch = OpenEXR.File(path, separate_channels=True).channels()
    rgba = np.stack([ch[n].pixels.astype(np.float32) for n in 'RGBA'], axis=-1)
    want = np.fromfile(path[:-4] + '.f32', dtype='<f4').reshape(rgba.shape)
    same = np.array_equal(rgba.view(np.uint32), want.view(np.uint32))
    failed += not same
    print(('ok   ' if same else 'FAIL ') + path.rsplit('/', 1)[1])
print(f'{len(files) - failed} of {len(files)} files read back bit for bit')
sys.exit(1 if failed or not files else 0)
"""
r = subprocess.run(["docker", "run", "--rm", "-v", f"{ROOT}:/w", "egami-hdrref:2.9.1", "python3", "-c", CHECK],
                   env=dict(os.environ, MSYS_NO_PATHCONV="1"))
sys.exit(r.returncode)
