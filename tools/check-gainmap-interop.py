"""Checks that libultrahdr 2.0.2 decodes Egami's gain map JPEGs to the HDR image Egami decodes (Phase 2.9.1 tasks 12 and 13).

Runs GainMapWriteTests.WritesInteropFiles with EGAMI_INTEROP_DIR=.cache/interop, which writes Ultra HDR JPEGs Egami made from
HDR rasters, the gain map corpus saved again, and saved after a crop and turn, each with Egami's decode at a display headroom
of 4; then, in the egami-hdrref image, decodes each with libultrahdr at the same headroom and compares, in libultrahdr's output
gamut. A map smaller than the image is interpolated differently (libultrahdr weighs the nearest four map pixels by inverse
squared distance, Egami bilinearly), so those files get a looser bound.

Usage, from the repo root with Docker running:
  python tools/check-gainmap-interop.py
"""
import os
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, ".cache", "interop")
env = dict(os.environ, EGAMI_INTEROP_DIR=OUT)
subprocess.run(["dotnet", "test", os.path.join(ROOT, "tests", "Egami.Tests"), "-c", "Release", "--filter",
                "FullyQualifiedName~GainMapWriteTests.WritesInteropFiles"], check=True, env=env, capture_output=True)

CHECK = r"""
import glob, json, struct, subprocess, sys
import numpy as np

def to_xyz(p):
    (rx, ry), (gx, gy), (bx, by), (wx, wy) = p
    m = np.array([[rx / ry, gx / gy, bx / by], [1, 1, 1], [(1 - rx - ry) / ry, (1 - gx - gy) / gy, (1 - bx - by) / by]])
    return m * np.linalg.solve(m, [wx / wy, 1, (1 - wx - wy) / wy])

def bradford(src, dst):
    b = np.array([[0.8951, 0.2664, -0.1614], [-0.7502, 1.7135, 0.0367], [0.0389, -0.0685, 1.0296]])
    f = lambda w: np.array([w[0] / w[1], 1, (1 - w[0] - w[1]) / w[1]])
    return np.linalg.inv(b) @ np.diag((b @ f(dst)) / (b @ f(src))) @ b

GAMUTS = {0: ((.64, .33), (.30, .60), (.15, .06), (.3127, .3290)), 1: ((.68, .32), (.265, .69), (.15, .06), (.3127, .3290)),
          2: ((.708, .292), (.170, .797), (.131, .046), (.3127, .3290))}

def raw(path):
    d = open(path, 'rb').read()
    w, h = struct.unpack('<ii', d[:8])
    return np.frombuffer(d[8:], dtype=np.float16).astype(np.float32).reshape(h, w, 4)[..., :3]

failed = 0
files = sorted(glob.glob('/w/.cache/interop/gainmap/*.jpg'))
for path in files:
    name = path.rsplit('/', 1)[1]
    meta = json.loads(subprocess.run(['uhdrref', 'meta', path], capture_output=True, text=True, check=True).stdout)
    subprocess.run(['uhdrref', 'decode', path, '4', '/tmp/u.raw'], check=True)
    v = [float(x) for x in open(path + '.primaries').read().split()]
    src = tuple(zip(v[0::2], v[1::2]))
    dst = GAMUTS[meta['outputGamut']]
    m = np.linalg.inv(to_xyz(dst)) @ bradford(src[3], dst[3]) @ to_xyz(src)
    mine = raw(path + '.egami.raw') @ m.T
    theirs = raw('/tmp/u.raw')
    error = np.abs(mine - theirs) / np.maximum(np.maximum(mine, theirs), 0.05)
    scaled = meta['gainmapWidth'] != meta['width']
    bound = 0.06 if scaled else 0.02
    ok = error.mean() < bound
    failed += not ok
    print(f"{'ok  ' if ok else 'FAIL'} {name}: mean relative error {error.mean():.2%} (bound {bound:.0%}), map {meta['gainmapWidth']}x{meta['gainmapHeight']}")
print(f'{len(files) - failed} of {len(files)} files decode alike')
sys.exit(1 if failed or not files else 0)
"""
r = subprocess.run(["docker", "run", "--rm", "-v", f"{ROOT}:/w", "egami-hdrref:2.9.1", "python3", "-c", CHECK],
                   env=dict(os.environ, MSYS_NO_PATHCONV="1"))
sys.exit(r.returncode)
