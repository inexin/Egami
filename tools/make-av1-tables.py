"""Generates the AV1 decoder's constant tables from libaom 3.15.2's sources (BSD 2-clause, AOM Patent License 1.0; see
THIRD-PARTY-NOTICES), in .cache/avif/aom-3.15.2 (python tools/make-avif-references.py fetch clones it).

  src/Egami/Formats/Av1/Av1DefaultCdfs.cs   every default CDF of av1/common/entropymode.c and token_cdfs.h, each table a list
                                            of CDFs in libaom's array order, each CDF as libaom stores it (AOM_ICDF): 32768
                                            minus the cumulative probabilities (times 32768) of all but the last symbol, then 0,
                                            then the adaptation counter (0)
  src/Egami/Formats/Av1/Av1Tables.Data.cs   the numeric tables named in NUMERIC below (scan orders, quantizer lookups and so on),
                                            flattened in array order

Usage, from the repo root: python tools/make-av1-tables.py
"""
import os
import re

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
AOM = os.path.join(ROOT, ".cache", "avif", "aom-3.15.2")
OUT = os.path.join(ROOT, "src", "Egami", "Formats", "Av1")

# (C# name, file, libaom array name, C# element type). Enum names (BLOCK_*, TX_*, ...) become their values.
CD = "av1/common/common_data.h"
NUMERIC = [
    ("MiSizeWideLog2", CD, "mi_size_wide_log2", "byte"),
    ("MiSizeHighLog2", CD, "mi_size_high_log2", "byte"),
    ("MiSizeWide", CD, "mi_size_wide", "byte"),
    ("MiSizeHigh", CD, "mi_size_high", "byte"),
    ("BlockSizeWide", CD, "block_size_wide", "byte"),
    ("BlockSizeHigh", CD, "block_size_high", "byte"),
    ("SizeGroupLookup", CD, "size_group_lookup", "byte"),
    ("NumPelsLog2Lookup", CD, "num_pels_log2_lookup", "byte"),
    ("SubsizeLookup", CD, "subsize_lookup", "byte"),
    ("MaxTxSizeLookup", CD, "max_txsize_lookup", "byte"),
    ("MaxTxSizeRectLookup", CD, "max_txsize_rect_lookup", "byte"),
    ("VtxTab", CD, "vtx_tab", "byte"),
    ("HtxTab", CD, "htx_tab", "byte"),
    ("SubTxSizeMap", CD, "sub_tx_size_map", "byte"),
    ("TxSizeWide", CD, "tx_size_wide", "short"),
    ("TxSizeHigh", CD, "tx_size_high", "short"),
    ("TxSizeWideUnit", CD, "tx_size_wide_unit", "byte"),
    ("TxSizeHighUnit", CD, "tx_size_high_unit", "byte"),
    ("TxSizeWideLog2", CD, "tx_size_wide_log2", "byte"),
    ("TxSizeHighLog2", CD, "tx_size_high_log2", "byte"),
    ("TxSizeToBlockSize", CD, "txsize_to_bsize", "byte"),
    ("TxSizeSqrMap", CD, "txsize_sqr_map", "byte"),
    ("TxSizeSqrUpMap", CD, "txsize_sqr_up_map", "byte"),
    ("TxSizeLog2Minus4", CD, "txsize_log2_minus4", "sbyte"),
    ("IntraModeContext", CD, "intra_mode_context", "byte"),
    ("SsSizeLookup", "av1/common/common_data.c", "av1_ss_size_lookup", "byte"),
    ("EobGroupStart", "av1/common/txb_common.c", "av1_eob_group_start", "short"),
    ("EobOffsetBits", "av1/common/txb_common.c", "av1_eob_offset_bits", "short"),
    ("DcQLookup8", "av1/common/quant_common.c", "dc_qlookup_QTX", "short"),
    ("DcQLookup10", "av1/common/quant_common.c", "dc_qlookup_10_QTX", "short"),
    ("DcQLookup12", "av1/common/quant_common.c", "dc_qlookup_12_QTX", "short"),
    ("AcQLookup8", "av1/common/quant_common.c", "ac_qlookup_QTX", "short"),
    ("AcQLookup10", "av1/common/quant_common.c", "ac_qlookup_10_QTX", "short"),
    ("AcQLookup12", "av1/common/quant_common.c", "ac_qlookup_12_QTX", "short"),
    ("InverseQuantizerMatrices", "av1/common/quant_common.c", "iwt_matrix_ref", "byte"),
] + [
    (f"NzMapCtxOffset{size}", "av1/common/txb_common.c", f"av1_nz_map_ctx_offset_{size}", "sbyte")
    for size in ("4x4", "8x8", "16x16", "32x32", "4x8", "8x16", "16x32", "32x16", "32x64", "64x32", "4x16", "16x4", "8x32", "32x8")
] + [
    (f"{kind.capitalize()}Scan{size}", "av1/common/scan.c", f"{kind}_scan_{size}", "short")
    for size in ("4x4", "8x8", "16x16", "32x32", "4x8", "8x4", "8x16", "16x8", "16x32", "32x16", "4x16", "16x4", "8x32", "32x8")
    for kind in ("default", "mrow", "mcol") if not (kind != "default" and size == "32x32" and False)
]


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
    return re.sub(r"//[^\n]*", " ", text)


def initializer(text, name):
    """Returns the declaration (up to '=') and the brace-enclosed initializer of the array called name."""
    match = re.search(r"\b" + re.escape(name) + r"\s*(\[[^=]*)=\s*\{", text)
    if not match:
        raise KeyError(name)
    start = match.end() - 1
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return match.group(1).strip(), text[start:i + 1]
    raise ValueError(name)


def pascal(name):
    name = re.sub(r"^(av1_)?default_", "", name)
    name = re.sub(r"_cdfs?$", "", name)
    return "".join(part[:1].upper() + part[1:] for part in name.split("_"))


def cdf_tables():
    tables = []
    for file in ("av1/common/entropymode.c", "av1/common/token_cdfs.h"):
        text = strip_comments(open(os.path.join(AOM, file)).read())
        for match in re.finditer(r"static const aom_cdf_prob\s+(\w+)", text):
            name = match.group(1)
            dims, body = initializer(text, name)
            cdfs = []
            for cdf in re.finditer(r"AOM_CDF(\d+)\s*\(([^)]*)\)", body):
                count = int(cdf.group(1))
                values = [int(eval(v.strip(), {}, {})) for v in cdf.group(2).split(",") if v.strip()]
                if len(values) != count - 1:
                    raise ValueError(f"{name}: AOM_CDF{count} with {len(values)} values")
                cdfs.append([32768 - v for v in values] + [0, 0])
            tables.append((pascal(name), name, " ".join(dims.split()), cdfs))
    return tables


ENUMS = {}


def load_enums():
    """Reads the enum constants of libaom's enums.h and txfm_common.h (BLOCK_SIZE, TX_SIZE, TX_TYPE and the rest)."""
    for file in ("av1/common/enums.h", "aom_dsp/txfm_common.h"):
        text = strip_comments(open(os.path.join(AOM, file)).read())
        for body in re.findall(r"enum[^{;]*\{([^}]*)\}", text):
            value = -1
            for entry in body.split(","):
                entry = entry.strip()
                if not entry:
                    continue
                name, _, expr = entry.partition("=")
                name = name.strip()
                if expr.strip():
                    try:
                        value = int(eval(re.sub(r"[A-Za-z_]\w*", lambda m: str(ENUMS.get(m.group(0), 0)), expr), {}, {}))
                    except Exception:
                        continue
                else:
                    value += 1
                ENUMS[name] = value


def numeric(file, name):
    text = strip_comments(open(os.path.join(AOM, file)).read())
    dims, body = initializer(text, name)
    values = []
    for token in re.findall(r"(?:-\s*)?(?:0x[0-9a-fA-F]+|\d+|[A-Za-z_]\w*)", body):
        token = "".join(token.split())
        sign = -1 if token.startswith("-") else 1
        token = token.lstrip("-")
        values.append(sign * (int(token, 0) if token[0].isdigit() else ENUMS[token]))
    return " ".join(dims.split()), values


def scan_orders():
    """libaom's av1_scan_orders[TX_SIZES_ALL][TX_TYPES]: the scan array each pair names, as C# names."""
    text = strip_comments(open(os.path.join(AOM, "av1/common/scan.c")).read())
    _, body = initializer(text, "av1_scan_orders")
    names = re.findall(r"\{\s*(\w+)\s*,\s*\w+\s*\}", body)
    return ["".join(part.capitalize() for part in n.split("_")[:-1]) + n.split("_")[-1] for n in names]


def wrap(values, indent, width=128):
    lines, line = [], indent
    for i, v in enumerate(values):
        piece = str(v) + ("," if i < len(values) - 1 else "")
        if len(line) + len(piece) + 1 > width:
            lines.append(line.rstrip())
            line = indent
        line += piece + " "
    lines.append(line.rstrip())
    return "\n".join(lines)


HEADER = """// <auto-generated>
// Generated by tools/make-av1-tables.py from libaom 3.15.2 (BSD 2-clause, AOM Patent License 1.0; see THIRD-PARTY-NOTICES).
// Do not edit; run the script again.
// </auto-generated>
"""


def main():
    out = [HEADER, "namespace Egami.Formats.Av1;", "",
           "/// <summary>The default CDFs of the AV1 specification, as libaom's entropymode.c and token_cdfs.h hold them (inverse form).</summary>",
           "internal static class Av1DefaultCdfs", "{"]
    for csharp, name, dims, cdfs in cdf_tables():
        out.append(f"    /// <summary>libaom's {name}{dims}: {len(cdfs)} CDFs.</summary>")
        out.append(f"    internal static readonly ushort[][] {csharp} =")
        out.append("    [")
        for cdf in cdfs:
            out.append(f"        [{', '.join(map(str, cdf))}],")
        out.append("    ];")
        out.append("")
    out[-1] = "}"
    open(os.path.join(OUT, "Av1DefaultCdfs.cs"), "w", newline="\n").write("\n".join(out) + "\n")

    load_enums()
    out = [HEADER, "namespace Egami.Formats.Av1;", "", "/// <summary>Numeric tables of libaom, flattened in array order.</summary>",
           "internal static partial class Av1Tables", "{"]
    for csharp, file, name, kind in NUMERIC:
        dims, values = numeric(file, name)
        out.append(f"    /// <summary>libaom's {name}{dims} ({file}).</summary>")
        out.append(f"    internal static readonly {kind}[] {csharp} =")
        out.append("    [")
        out.append(wrap(values, "        "))
        out.append("    ];")
        out.append("")

    # After the scans it names: static fields initialize in textual order.
    out.append("    /// <summary>libaom's av1_scan_orders[TX_SIZES_ALL][TX_TYPES]: the scan of each transform size and type.</summary>")
    out.append("    internal static readonly short[][] ScanOrders =")
    out.append("    [")
    out.append(wrap(scan_orders(), "        "))
    out.append("    ];")
    out.append("}")
    open(os.path.join(OUT, "Av1Tables.Data.cs"), "w", newline="\n").write("\n".join(out) + "\n")


main()
