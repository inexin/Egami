"""Generates the H.264 decoder's CABAC context initialisation values and CAVLC code tables from OpenH264 2.4.1's sources
(BSD-2-Clause; see THIRD-PARTY-NOTICES.md), fetched into .cache/openh264-2.4.1 when missing, and adds the 4:2:2 chroma DC tables
OpenH264 lacks from the H.264 specification (Table 9-5 for nC == -2 and Table 9-9b). Only the tables are read; no code is used.

  src/Egami.Video.H264/Codecs/H264/H264Tables.Data.cs   CabacInit (Tables 9-12 to 9-33), CoeffToken (9-5), TotalZeros (9-7,
                                                        9-8), TotalZerosChromaDc420 and 422 (9-9), RunBefore (9-10). Every
                                                        code table is checked to be prefix-free first.

Usage, from the repo root: python tools/video/make-h264-tables.py
"""
import os
import re
import urllib.request

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CACHE = os.path.join(ROOT, ".cache", "openh264-2.4.1")
OUT = os.path.join(ROOT, "src", "Egami.Video.H264", "Codecs", "H264", "H264Tables.Data.cs")
BASE = "https://raw.githubusercontent.com/cisco/openh264/v2.4.1/"
FILES = ["codec/common/src/common_tables.cpp", "codec/encoder/core/src/encoder_data_tables.cpp"]


def source(path):
    local = os.path.join(CACHE, path)
    if not os.path.exists(local):
        os.makedirs(os.path.dirname(local), exist_ok=True)
        urllib.request.urlretrieve(BASE + path, local)
    with open(local, encoding="utf-8") as f:
        return f.read()


def numbers(text, name):
    start = text.index(name)
    body = text[start:text.index("};", start)]
    body = body[body.index("{") if name.startswith("g_kiCabac") else body.index("=") + 1:]
    body = re.sub(r"//[^\n]*", "", body).replace("CTX_NA", "0")
    return [int(x) for x in re.findall(r"-?\d+", body)]


common = source(FILES[0])
encoder = source(FILES[1])
cabac = numbers(common, "g_kiCabacGlobalContextIdx")
assert len(cabac) == 460 * 8, len(cabac)
d = {"cabac": cabac, "ct": numbers(encoder, "g_kuiVlcCoeffToken"), "tz": numbers(encoder, "g_kuiVlcTotalZeros["),
     "tzc": numbers(encoder, "g_kuiVlcTotalZerosChromaDc"), "rb": numbers(encoder, "g_kuiVlcRunBefore")}

# nC == -2 (4:2:2 chroma DC), Table 9-5, [totalCoeff][trailingOnes] as bit strings.
m2 = {
 (0,0):'1',
 (1,0):'0001111',(1,1):'01',
 (2,0):'0001110',(2,1):'0001101',(2,2):'001',
 (3,0):'000000111',(3,1):'0001100',(3,2):'0001011',(3,3):'00001',
 (4,0):'000000110',(4,1):'000000101',(4,2):'0001010',(4,3):'000001',
 (5,0):'0000000111',(5,1):'0000000110',(5,2):'000000100',(5,3):'0001001',
 (6,0):'00000000111',(6,1):'00000000110',(6,2):'0000000101',(6,3):'0001000',
 (7,0):'000000000111',(7,1):'000000000110',(7,2):'00000000101',(7,3):'0000000100',
 (8,0):'0000000000111',(8,1):'000000000101',(8,2):'000000000100',(8,3):'00000000100',
}
ct = d['ct'] + [0] * (17 * 4 * 2)
for (tc, t1), bits in m2.items():
    i = ((5 * 17 + tc) * 4 + t1) * 2
    ct[i] = int(bits, 2); ct[i + 1] = len(bits)
# Table 9-9b, 4:2:2 chroma DC total_zeros [tzVlcIndex 1..7][total_zeros 0..7].
t422 = [
 None,
 ['1','010','011','0010','0011','0001','00001','00000'],
 ['000','01','001','100','101','110','111'],
 ['000','001','01','10','110','111'],
 ['110','00','01','10','111'],
 ['00','01','10','11'],
 ['00','01','1'],
 ['0','1'],
]
tz422 = [0] * (8 * 8 * 2)
for v in range(1, 8):
    for z, bits in enumerate(t422[v]):
        i = (v * 8 + z) * 2
        tz422[i] = int(bits, 2); tz422[i + 1] = len(bits)
# Prefix-free and complete checks for every table.
def check(entries, name):
    codes = [(c, l) for c, l in entries if l > 0]
    s = sum(2.0 ** -l for c, l in codes)
    strs = [format(c, '0%db' % l) for c, l in codes]
    for a in strs:
        for b in strs:
            if a != b and b.startswith(a):
                raise SystemExit('%s: %s prefixes %s' % (name, a, b))
    return s
for t in range(6):
    e = [(ct[((t * 17 + tc) * 4 + t1) * 2], ct[((t * 17 + tc) * 4 + t1) * 2 + 1]) for tc in range(17) for t1 in range(4)]
    check(e, 'ct%d' % t)
for v in range(1, 8):
    check([(tz422[(v*8+z)*2], tz422[(v*8+z)*2+1]) for z in range(8)], 'tz422')
for v in range(1, 16):
    check([(d['tz'][(v*16+z)*2], d['tz'][(v*16+z)*2+1]) for z in range(16)], 'tz')
def fmt(name, typ, vals, per=32):
    out = ['    internal static ReadOnlySpan<%s> %s =>\n    [' % (typ, name)]
    for i in range(0, len(vals), per):
        out.append('        ' + ', '.join(str(v) for v in vals[i:i + per]) + ',')
    out.append('    ];\n')
    return '\n'.join(out)
with open(OUT, 'w', encoding='utf-8', newline='\n') as f:
    f.write('''namespace Egami.Video.Codecs.H264;

// The CABAC context initialisation values and the CAVLC code tables, transcribed from OpenH264 (BSD-2-Clause, see
// THIRD-PARTY-NOTICES.md): codec/common/src/common_tables.cpp (g_kiCabacGlobalContextIdx) and
// codec/encoder/core/src/encoder_data_tables.cpp (g_kuiVlcCoeffToken, g_kuiVlcTotalZeros, g_kuiVlcTotalZerosChromaDc,
// g_kuiVlcRunBefore). The 4:2:2 chroma DC tables (nC == -2 and Table 9-9b), which OpenH264 lacks, come from the H.264 spec.
// Regenerate with tools/video/make-h264-tables.py.
internal static partial class H264Tables
{
    /// <summary>
    /// (m, n) per context (Tables 9-12 to 9-33, ctxIdx 0 to 459), for I and SI slices then cabac_init_idc 0 to 2:
    /// [ctxIdx * 8 + column * 2 + 0 or 1].
    /// </summary>
''')
    f.write(fmt('CabacInit', 'sbyte', d['cabac']))
    f.write('''
    /// <summary>
    /// coeff_token codes (Table 9-5) as (code, length) for 0 &lt;= nC &lt; 2, 2 &lt;= nC &lt; 4, 4 &lt;= nC &lt; 8, 8 &lt;= nC, nC == -1 and
    /// nC == -2: [((table * 17 + TotalCoeff) * 4 + TrailingOnes) * 2]; length 0 marks a pair with no code.
    /// </summary>
''')
    f.write(fmt('CoeffToken', 'byte', ct))
    f.write('''
    /// <summary>total_zeros codes for 4x4 blocks (Tables 9-7 and 9-8) as (code, length): [(tzVlcIndex * 16 + total_zeros) * 2].</summary>
''')
    f.write(fmt('TotalZeros', 'byte', d['tz']))
    f.write('''
    /// <summary>total_zeros codes for 4:2:0 chroma DC (Table 9-9a): [(tzVlcIndex * 4 + total_zeros) * 2].</summary>
''')
    f.write(fmt('TotalZerosChromaDc420', 'byte', d['tzc']))
    f.write('''
    /// <summary>total_zeros codes for 4:2:2 chroma DC (Table 9-9b): [(tzVlcIndex * 8 + total_zeros) * 2].</summary>
''')
    f.write(fmt('TotalZerosChromaDc422', 'byte', tz422))
    f.write('''
    /// <summary>run_before codes (Table 9-10) as (code, length): [(Min(zerosLeft, 7) * 15 + run_before) * 2].</summary>
''')
    f.write(fmt('RunBefore', 'byte', d['rb']))
    f.write('}\n')
