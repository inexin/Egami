"""Generates the VP9 decoder's constant tables from libvpx 1.14.1's sources (BSD-3, Google patent grant; see
THIRD-PARTY-NOTICES), in .cache/libvpx-1.14.1 (cloned here when missing).

  src/Egami.Video/Codecs/Vp9/Vp9Tables.Data.cs   the tables named in TABLES below: default probabilities, scans and their
                                                 neighbors, quantizer lookups, interpolation kernels, block size lookups and
                                                 the coding trees, each flattened in array order. Arrays whose initializer
                                                 leaves entries out (the coefficient probabilities list three contexts for band
                                                 0) are padded with zeros to the declared dimensions.

Usage, from the repo root: python tools/video/make-vp9-tables.py
"""
import os
import re
import subprocess

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
VPX = os.path.join(ROOT, ".cache", "libvpx-1.14.1")
OUT = os.path.join(ROOT, "src", "Egami.Video", "Codecs", "Vp9", "Vp9Tables.Data.cs")

C = "vp9/common/"
COEF = [2, 2, 6, 6, 3]

# (C# name, file, libvpx name, C# element type, dimensions to pad to or None to flatten as written)
TABLES = [
    ("Cat6Prob", C + "vp9_entropy.c", "vp9_cat6_prob", "byte", None),
    ("Cat6ProbHigh12", C + "vp9_entropy.c", "vp9_cat6_prob_high12", "byte", None),
    ("CoefbandTrans8x8Plus", C + "vp9_entropy.c", "vp9_coefband_trans_8x8plus", "byte", None),
    ("CoefbandTrans4x4", C + "vp9_entropy.c", "vp9_coefband_trans_4x4", "byte", None),
    ("Pareto8Full", C + "vp9_entropy.c", "vp9_pareto8_full", "byte", [255, 8]),
    ("DefaultCoefProbs4x4", C + "vp9_entropy.c", "default_coef_probs_4x4", "byte", COEF),
    ("DefaultCoefProbs8x8", C + "vp9_entropy.c", "default_coef_probs_8x8", "byte", COEF),
    ("DefaultCoefProbs16x16", C + "vp9_entropy.c", "default_coef_probs_16x16", "byte", COEF),
    ("DefaultCoefProbs32x32", C + "vp9_entropy.c", "default_coef_probs_32x32", "byte", COEF),
    ("CoefConTree", C + "vp9_entropy.c", "vp9_coef_con_tree", "sbyte", None),
    ("KfYModeProb", C + "vp9_entropymode.c", "vp9_kf_y_mode_prob", "byte", [10, 10, 9]),
    ("KfUVModeProb", C + "vp9_entropymode.c", "vp9_kf_uv_mode_prob", "byte", [10, 9]),
    ("DefaultIfYProbs", C + "vp9_entropymode.c", "default_if_y_probs", "byte", [4, 9]),
    ("DefaultIfUVProbs", C + "vp9_entropymode.c", "default_if_uv_probs", "byte", [10, 9]),
    ("KfPartitionProbs", C + "vp9_entropymode.c", "vp9_kf_partition_probs", "byte", [16, 3]),
    ("DefaultPartitionProbs", C + "vp9_entropymode.c", "default_partition_probs", "byte", [16, 3]),
    ("DefaultInterModeProbs", C + "vp9_entropymode.c", "default_inter_mode_probs", "byte", [7, 3]),
    ("DefaultIntraInterP", C + "vp9_entropymode.c", "default_intra_inter_p", "byte", [4]),
    ("DefaultCompInterP", C + "vp9_entropymode.c", "default_comp_inter_p", "byte", [5]),
    ("DefaultCompRefP", C + "vp9_entropymode.c", "default_comp_ref_p", "byte", [5]),
    ("DefaultSingleRefP", C + "vp9_entropymode.c", "default_single_ref_p", "byte", [5, 2]),
    ("DefaultTxProbs", C + "vp9_entropymode.c", "default_tx_probs", "byte", None),
    ("DefaultSkipProbs", C + "vp9_entropymode.c", "default_skip_probs", "byte", [3]),
    ("DefaultSwitchableInterpProb", C + "vp9_entropymode.c", "default_switchable_interp_prob", "byte", [4, 2]),
    ("IntraModeTree", C + "vp9_entropymode.c", "vp9_intra_mode_tree", "sbyte", None),
    ("InterModeTree", C + "vp9_entropymode.c", "vp9_inter_mode_tree", "sbyte", None),
    ("PartitionTree", C + "vp9_entropymode.c", "vp9_partition_tree", "sbyte", None),
    ("SwitchableInterpTree", C + "vp9_entropymode.c", "vp9_switchable_interp_tree", "sbyte", None),
    ("DefaultNmvContext", C + "vp9_entropymv.c", "default_nmv_context", "byte", None),
    ("MvJointTree", C + "vp9_entropymv.c", "vp9_mv_joint_tree", "sbyte", None),
    ("MvClassTree", C + "vp9_entropymv.c", "vp9_mv_class_tree", "sbyte", None),
    ("MvClass0Tree", C + "vp9_entropymv.c", "vp9_mv_class0_tree", "sbyte", None),
    ("MvFpTree", C + "vp9_entropymv.c", "vp9_mv_fp_tree", "sbyte", None),
    ("SegmentTree", C + "vp9_seg_common.c", "vp9_segment_tree", "sbyte", None),
    ("DcQLookup8", C + "vp9_quant_common.c", "dc_qlookup", "short", None),
    ("DcQLookup10", C + "vp9_quant_common.c", "dc_qlookup_10", "short", None),
    ("DcQLookup12", C + "vp9_quant_common.c", "dc_qlookup_12", "short", None),
    ("AcQLookup8", C + "vp9_quant_common.c", "ac_qlookup", "short", None),
    ("AcQLookup10", C + "vp9_quant_common.c", "ac_qlookup_10", "short", None),
    ("AcQLookup12", C + "vp9_quant_common.c", "ac_qlookup_12", "short", None),
    ("SubPelFilters8", C + "vp9_filter.c", "sub_pel_filters_8", "short", [16, 8]),
    ("SubPelFilters8Lp", C + "vp9_filter.c", "sub_pel_filters_8lp", "short", [16, 8]),
    ("SubPelFilters8S", C + "vp9_filter.c", "sub_pel_filters_8s", "short", [16, 8]),
    ("BilinearFilters", C + "vp9_filter.c", "bilinear_filters", "short", [16, 8]),
    ("BWidthLog2Lookup", C + "vp9_common_data.c", "b_width_log2_lookup", "byte", None),
    ("BHeightLog2Lookup", C + "vp9_common_data.c", "b_height_log2_lookup", "byte", None),
    ("Num4x4BlocksWideLookup", C + "vp9_common_data.c", "num_4x4_blocks_wide_lookup", "byte", None),
    ("Num4x4BlocksHighLookup", C + "vp9_common_data.c", "num_4x4_blocks_high_lookup", "byte", None),
    ("Num8x8BlocksWideLookup", C + "vp9_common_data.c", "num_8x8_blocks_wide_lookup", "byte", None),
    ("Num8x8BlocksHighLookup", C + "vp9_common_data.c", "num_8x8_blocks_high_lookup", "byte", None),
    ("SizeGroupLookup", C + "vp9_common_data.c", "size_group_lookup", "byte", None),
    ("SubsizeLookup", C + "vp9_common_data.c", "subsize_lookup", "byte", [4, 13]),
    ("MaxTxSizeLookup", C + "vp9_common_data.c", "max_txsize_lookup", "byte", None),
    ("TxModeToBiggestTxSize", C + "vp9_common_data.c", "tx_mode_to_biggest_tx_size", "byte", None),
    ("SsSizeLookup", C + "vp9_common_data.c", "ss_size_lookup", "byte", [13, 2, 2]),
    ("UVTxSizeLookup", C + "vp9_common_data.c", "uv_txsize_lookup", "byte", [13, 4, 2, 2]),
    ("PartitionContextLookup", C + "vp9_common_data.c", "partition_context_lookup", "byte", [13, 2]),
    ("IntraModeToTxTypeLookup", C + "vp9_reconintra.c", "intra_mode_to_tx_type_lookup", "byte", None),
    ("ExtendModes", C + "vp9_reconintra.c", "extend_modes", "byte", None),
    ("ModeToCounter", C + "vp9_mvref_common.h", "mode_2_counter", "byte", None),
    ("CounterToContext", C + "vp9_mvref_common.h", "counter_to_context", "byte", None),
    ("MvRefBlocks", C + "vp9_mvref_common.h", "mv_ref_blocks", "sbyte", [13, 8, 2]),
    ("Left64x64TxformMask", C + "vp9_loopfilter.c", "left_64x64_txform_mask", "ulong", None),
    ("Above64x64TxformMask", C + "vp9_loopfilter.c", "above_64x64_txform_mask", "ulong", None),
    ("LeftPredictionMask", C + "vp9_loopfilter.c", "left_prediction_mask", "ulong", None),
    ("AbovePredictionMask", C + "vp9_loopfilter.c", "above_prediction_mask", "ulong", None),
    ("SizeMask", C + "vp9_loopfilter.c", "size_mask", "ulong", None),
    ("Left64x64TxformMaskUV", C + "vp9_loopfilter.c", "left_64x64_txform_mask_uv", "ushort", None),
    ("Above64x64TxformMaskUV", C + "vp9_loopfilter.c", "above_64x64_txform_mask_uv", "ushort", None),
    ("LeftPredictionMaskUV", C + "vp9_loopfilter.c", "left_prediction_mask_uv", "ushort", None),
    ("AbovePredictionMaskUV", C + "vp9_loopfilter.c", "above_prediction_mask_uv", "ushort", None),
    ("SizeMaskUV", C + "vp9_loopfilter.c", "size_mask_uv", "ushort", None),
    ("ModeLfLut", C + "vp9_loopfilter.c", "mode_lf_lut", "byte", None),
    ("InvMapTable", "vp9/decoder/vp9_dsubexp.c", "inv_map_table", "byte", None),
] + [
    (f"{kind.capitalize()}Scan{size}", C + "vp9_scan.c", f"{kind}_scan_{size}", "short", None)
    for size in ("4x4", "8x8", "16x16") for kind in ("default", "col", "row")
] + [
    ("DefaultScan32x32", C + "vp9_scan.c", "default_scan_32x32", "short", None),
] + [
    (f"{kind.capitalize()}Scan{size}Neighbors", C + "vp9_scan.c", f"{kind}_scan_{size}_neighbors", "short", None)
    for size in ("4x4", "8x8", "16x16") for kind in ("default", "col", "row")
] + [
    ("DefaultScan32x32Neighbors", C + "vp9_scan.c", "default_scan_32x32_neighbors", "short", None),
]

ENUM_FILES = ["vp9/common/vp9_enums.h", "vp9/common/vp9_blockd.h", "vp9/common/vp9_entropymv.h", "vp9/common/vp9_mvref_common.h",
              "vp9/common/vp9_reconintra.c", "vp9/common/vp9_filter.h", "vp9/common/vp9_seg_common.h", "vpx_dsp/vpx_filter.h",
              "vp9/common/vp9_common_data.h", "vp9/common/vp9_entropymode.h", "vp9/common/vp9_entropy.h"]
ENUMS = {}


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", " ", text, flags=re.S)
    return re.sub(r"//[^\n]*", " ", text)


def evaluate(expr):
    expr = re.sub(r"INTER_OFFSET\((\w+)\)", r"(\1 - NEARESTMV)", expr)
    expr = re.sub(r"(0x[0-9a-fA-F]+|\d+)U?L*L*", r"\1", expr)
    expr = re.sub(r"\((?:TX_SIZE|BLOCK_SIZE|PREDICTION_MODE|int|uint8_t)\)", "", expr)
    expr = re.sub(r"(?<!\w)[A-Za-z_]\w*",lambda m: str(ENUMS[m.group(0)]), expr)
    return int(eval(expr, {}, {}))


def load_enums():
    for file in ENUM_FILES:
        text = strip_comments(open(os.path.join(VPX, file)).read())
        for body in re.findall(r"(?<![\w.])enum(?![\w.])[^{;]*\{([^}]*)\}", text):
            value = -1
            for entry in body.split(","):
                entry = entry.strip()
                if not entry:
                    continue
                name, _, expr = entry.partition("=")
                name = name.strip()
                value = evaluate(expr) if expr.strip() else value + 1
                ENUMS[name] = value
        for name, expr in re.findall(r"^#define\s+([A-Z_][A-Z0-9_]*)\s+\(?([-\w <+*()]+?)\)?\s*$", text, flags=re.M):
            try:
                ENUMS.setdefault(name, evaluate(expr))
            except (KeyError, SyntaxError, NameError):
                pass


def initializer(text, name):
    match = re.search(r"\b" + re.escape(name) + r"\s*(\[[^=]*)?=\s*\{", text)
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
                return text[start:i + 1]
    raise ValueError(name)


def parse(body):
    """Parses a brace initializer into nested lists of values."""
    pos = 0

    def item():
        nonlocal pos
        while body[pos].isspace():
            pos += 1
        if body[pos] == "{":
            pos += 1
            items = []
            while True:
                while body[pos].isspace() or body[pos] == ",":
                    pos += 1
                if body[pos] == "}":
                    pos += 1
                    return items
                items.append(item())
        start = pos
        depth = 0
        while depth > 0 or body[pos] not in ",}":
            depth += {"(": 1, ")": -1}.get(body[pos], 0)
            pos += 1
        return evaluate(body[start:pos].strip())

    return item()


def flatten(value, dims):
    if not isinstance(value, list):
        return [value]
    if dims is None:
        return [v for item in value for v in flatten(item, None)]
    if len(value) > dims[0]:
        raise ValueError("initializer larger than its dimensions")
    out = []
    for i in range(dims[0]):
        if i < len(value):
            out += flatten(value[i], dims[1:] if len(dims) > 1 else None)
        else:
            size = 1
            for d in dims[1:]:
                size *= d
            out += [0] * size
    return out


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
// Generated by tools/video/make-vp9-tables.py from libvpx 1.14.1 (BSD-3, Google patent grant; see THIRD-PARTY-NOTICES).
// Do not edit; run the script again.
// </auto-generated>
#nullable enable
"""


def main():
    if not os.path.isdir(VPX):
        subprocess.run(["git", "clone", "-q", "--depth", "1", "--branch", "v1.14.1", "https://chromium.googlesource.com/webm/libvpx", VPX],
                       check=True)
    load_enums()
    out = [HEADER, "namespace Egami.Video.Codecs.Vp9;", "", "/// <summary>Numeric tables of libvpx's VP9 decoder, flattened in array order.</summary>",
           "internal static partial class Vp9Tables", "{"]
    for csharp, file, name, kind, dims in TABLES:
        text = strip_comments(open(os.path.join(VPX, file)).read())
        values = flatten(parse(initializer(text, name)), dims)
        if kind == "ulong":
            values = [f"0x{v:016X}UL" for v in values]
        shape = "".join(f"[{d}]" for d in dims) if dims else f"[{len(values)}]"
        out.append(f"    /// <summary>libvpx's {name}{shape} ({file}).</summary>")
        out.append(f"    internal static readonly {kind}[] {csharp} =")
        out.append("    [")
        out.append(wrap(values, "        "))
        out.append("    ];")
        out.append("")
    out[-1] = "}"
    open(OUT, "w", newline="\n").write("\n".join(out) + "\n")
    print(f"{len(TABLES)} tables written to {os.path.relpath(OUT, ROOT)}")


if __name__ == "__main__":
    main()
