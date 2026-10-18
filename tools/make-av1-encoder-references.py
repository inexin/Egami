"""Builds the references of the AV1 encoder (Phase 2.9.6) from libaom 3.15.2, in Docker (egami-avifref:2.9.2, whose /src/aom
holds libaom's sources; see tools/avifref/Dockerfile). The tools run as oracles only.

  tests/reference/avif/av1-forward-transforms.json
      libaom's forward transform of deterministic inputs (tools/av1encref/fwdtxfm.c): for every transform size and type the
      encoder can use, at 8, 10 and 12 bits, the FNV-1a hash of the coefficients at the coded size

Usage, from the repo root with Docker running:
  python tools/make-av1-encoder-references.py
"""
import json
import os
import subprocess

IMAGE = "egami-avifref:2.9.2"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "tests", "reference", "avif")

BUILD = r"""
set -e
cmake -S /src/aom -B /tmp/aom -G Ninja -DCMAKE_BUILD_TYPE=Release -DENABLE_TESTS=0 -DENABLE_DOCS=0 -DENABLE_EXAMPLES=0     -DENABLE_TOOLS=0 > /dev/null
# hybrid_fwd_txfm.c calls libaom's run-time dispatch, so only its Walsh-Hadamard transform is taken from it.
(printf '#include "config/aom_config.h"
#include "aom_dsp/aom_dsp_common.h"
#include "aom_dsp/txfm_common.h"
';
 sed -n '/^void av1_fwht4x4_c/,/^}/p' /src/aom/av1/encoder/hybrid_fwd_txfm.c) > /tmp/fwht.c
gcc -O1 -I/src/aom -I/tmp/aom -o /tmp/fwdtxfm /t/fwdtxfm.c /tmp/fwht.c /src/aom/av1/encoder/av1_fwd_txfm2d.c     /src/aom/av1/encoder/av1_fwd_txfm1d.c /src/aom/av1/common/av1_txfm.c
/tmp/fwdtxfm
"""


def main():
    tools = os.path.join(ROOT, "tools", "av1encref")
    result = subprocess.run(["docker", "run", "--rm", "-v", f"{tools}:/t:ro", IMAGE, "bash", "-c", BUILD],
                            capture_output=True, text=True)
    if result.returncode != 0:
        raise RuntimeError(result.stderr)
    entries = [json.loads(line) for line in result.stdout.splitlines() if line.startswith("{")]
    with open(os.path.join(OUT, "av1-forward-transforms.json"), "w", newline="\n") as f:
        f.write('{"source": "libaom 3.15.2 av1_fwd_txfm2d_*_c and av1_fwht4x4_c (tools/av1encref/fwdtxfm.c)",\n"transforms": [\n')
        f.write(",\n".join(json.dumps(e) for e in entries))
        f.write("\n]}\n")
    print(f"{len(entries)} transforms")


main()
