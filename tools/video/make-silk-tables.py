"""Transcribes the SILK decoder tables of libopus into C#.

Usage: python tools/video/make-silk-tables.py <libopus checkout> <output .cs>
Reads silk/tables_*.c, pitch_est_tables.c, table_LSF_cos.c and resampler_rom.c and writes SilkTables.cs.
"""
import re
import sys
from pathlib import Path

# Constants from silk/define.h that the tables use.
DEFINES = {"OFFSET_VL_Q10": 32, "OFFSET_VH_Q10": 100, "OFFSET_UVL_Q10": 100, "OFFSET_UVH_Q10": 240}

TYPES = {"opus_uint8": "byte", "opus_int8": "sbyte", "opus_int16": "short", "opus_int32": "int"}

# (C file, C name, C# name)
WANTED = [
    ("tables_gain.c", "silk_gain_iCDF", "GainIcdf"),
    ("tables_gain.c", "silk_delta_gain_iCDF", "DeltaGainIcdf"),
    ("tables_pitch_lag.c", "silk_pitch_lag_iCDF", "PitchLagIcdf"),
    ("tables_pitch_lag.c", "silk_pitch_delta_iCDF", "PitchDeltaIcdf"),
    ("tables_pitch_lag.c", "silk_pitch_contour_iCDF", "PitchContourIcdf"),
    ("tables_pitch_lag.c", "silk_pitch_contour_NB_iCDF", "PitchContourNbIcdf"),
    ("tables_pitch_lag.c", "silk_pitch_contour_10_ms_iCDF", "PitchContour10MsIcdf"),
    ("tables_pitch_lag.c", "silk_pitch_contour_10_ms_NB_iCDF", "PitchContour10MsNbIcdf"),
    ("tables_pulses_per_block.c", "silk_pulses_per_block_iCDF", "PulsesPerBlockIcdf"),
    ("tables_pulses_per_block.c", "silk_rate_levels_iCDF", "RateLevelsIcdf"),
    ("tables_pulses_per_block.c", "silk_shell_code_table0", "ShellCodeTable0"),
    ("tables_pulses_per_block.c", "silk_shell_code_table1", "ShellCodeTable1"),
    ("tables_pulses_per_block.c", "silk_shell_code_table2", "ShellCodeTable2"),
    ("tables_pulses_per_block.c", "silk_shell_code_table3", "ShellCodeTable3"),
    ("tables_pulses_per_block.c", "silk_shell_code_table_offsets", "ShellCodeTableOffsets"),
    ("tables_pulses_per_block.c", "silk_sign_iCDF", "SignIcdf"),
    ("tables_other.c", "silk_stereo_pred_quant_Q13", "StereoPredQuantQ13"),
    ("tables_other.c", "silk_stereo_pred_joint_iCDF", "StereoPredJointIcdf"),
    ("tables_other.c", "silk_stereo_only_code_mid_iCDF", "StereoOnlyCodeMidIcdf"),
    ("tables_other.c", "silk_LBRR_flags_2_iCDF", "LbrrFlags2Icdf"),
    ("tables_other.c", "silk_LBRR_flags_3_iCDF", "LbrrFlags3Icdf"),
    ("tables_other.c", "silk_lsb_iCDF", "LsbIcdf"),
    ("tables_other.c", "silk_LTPscale_iCDF", "LtpScaleIcdf"),
    ("tables_other.c", "silk_type_offset_VAD_iCDF", "TypeOffsetVadIcdf"),
    ("tables_other.c", "silk_type_offset_no_VAD_iCDF", "TypeOffsetNoVadIcdf"),
    ("tables_other.c", "silk_NLSF_interpolation_factor_iCDF", "NlsfInterpolationFactorIcdf"),
    ("tables_other.c", "silk_Quantization_Offsets_Q10", "QuantizationOffsetsQ10"),
    ("tables_other.c", "silk_LTPScales_table_Q14", "LtpScalesTableQ14"),
    ("tables_other.c", "silk_uniform3_iCDF", "Uniform3Icdf"),
    ("tables_other.c", "silk_uniform4_iCDF", "Uniform4Icdf"),
    ("tables_other.c", "silk_uniform5_iCDF", "Uniform5Icdf"),
    ("tables_other.c", "silk_uniform6_iCDF", "Uniform6Icdf"),
    ("tables_other.c", "silk_uniform8_iCDF", "Uniform8Icdf"),
    ("tables_other.c", "silk_NLSF_EXT_iCDF", "NlsfExtIcdf"),
    ("tables_LTP.c", "silk_LTP_per_index_iCDF", "LtpPerIndexIcdf"),
    ("tables_LTP.c", "silk_LTP_gain_iCDF_0", "LtpGainIcdf0"),
    ("tables_LTP.c", "silk_LTP_gain_iCDF_1", "LtpGainIcdf1"),
    ("tables_LTP.c", "silk_LTP_gain_iCDF_2", "LtpGainIcdf2"),
    ("tables_LTP.c", "silk_LTP_gain_vq_0", "LtpGainVq0"),
    ("tables_LTP.c", "silk_LTP_gain_vq_1", "LtpGainVq1"),
    ("tables_LTP.c", "silk_LTP_gain_vq_2", "LtpGainVq2"),
    ("tables_NLSF_CB_NB_MB.c", "silk_NLSF_CB1_NB_MB_Q8", "NlsfCb1NbMbQ8"),
    ("tables_NLSF_CB_NB_MB.c", "silk_NLSF_CB1_Wght_Q9", "NlsfCb1NbMbWghtQ9"),
    ("tables_NLSF_CB_NB_MB.c", "silk_NLSF_CB1_iCDF_NB_MB", "NlsfCb1NbMbIcdf"),
    ("tables_NLSF_CB_NB_MB.c", "silk_NLSF_CB2_SELECT_NB_MB", "NlsfCb2NbMbSelect"),
    ("tables_NLSF_CB_NB_MB.c", "silk_NLSF_CB2_iCDF_NB_MB", "NlsfCb2NbMbIcdf"),
    ("tables_NLSF_CB_NB_MB.c", "silk_NLSF_PRED_NB_MB_Q8", "NlsfPredNbMbQ8"),
    ("tables_NLSF_CB_NB_MB.c", "silk_NLSF_DELTA_MIN_NB_MB_Q15", "NlsfDeltaMinNbMbQ15"),
    ("tables_NLSF_CB_WB.c", "silk_NLSF_CB1_WB_Q8", "NlsfCb1WbQ8"),
    ("tables_NLSF_CB_WB.c", "silk_NLSF_CB1_WB_Wght_Q9", "NlsfCb1WbWghtQ9"),
    ("tables_NLSF_CB_WB.c", "silk_NLSF_CB1_iCDF_WB", "NlsfCb1WbIcdf"),
    ("tables_NLSF_CB_WB.c", "silk_NLSF_CB2_SELECT_WB", "NlsfCb2WbSelect"),
    ("tables_NLSF_CB_WB.c", "silk_NLSF_CB2_iCDF_WB", "NlsfCb2WbIcdf"),
    ("tables_NLSF_CB_WB.c", "silk_NLSF_PRED_WB_Q8", "NlsfPredWbQ8"),
    ("tables_NLSF_CB_WB.c", "silk_NLSF_DELTA_MIN_WB_Q15", "NlsfDeltaMinWbQ15"),
    ("pitch_est_tables.c", "silk_CB_lags_stage2", "CbLagsStage2"),
    ("pitch_est_tables.c", "silk_CB_lags_stage3", "CbLagsStage3"),
    ("pitch_est_tables.c", "silk_CB_lags_stage2_10_ms", "CbLagsStage210Ms"),
    ("pitch_est_tables.c", "silk_CB_lags_stage3_10_ms", "CbLagsStage310Ms"),
    ("table_LSF_cos.c", "silk_LSFCosTab_FIX_Q12", "LsfCosTabFixQ12"),
    ("resampler_rom.c", "silk_Resampler_3_4_COEFS", "Resampler34Coefs"),
    ("resampler_rom.c", "silk_Resampler_2_3_COEFS", "Resampler23Coefs"),
    ("resampler_rom.c", "silk_Resampler_1_2_COEFS", "Resampler12Coefs"),
    ("resampler_rom.c", "silk_Resampler_1_3_COEFS", "Resampler13Coefs"),
    ("resampler_rom.c", "silk_Resampler_1_4_COEFS", "Resampler14Coefs"),
    ("resampler_rom.c", "silk_Resampler_1_6_COEFS", "Resampler16Coefs"),
    ("resampler_rom.c", "silk_resampler_frac_FIR_12", "ResamplerFracFir12"),
]


def strip_comments(text):
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"//[^\n]*", "", text)


def extract(text, name):
    m = re.search(r"(opus_u?int(?:8|16|32))\s+" + re.escape(name) + r"\s*(\[[^=]*)=\s*\{", text)
    if not m:
        raise SystemExit("not found: " + name)
    ctype = m.group(1)
    start = m.end()
    depth = 1
    i = start
    while depth:
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
        i += 1
    body = text[start:i - 1].replace("{", " ").replace("}", " ")
    values = [int(eval(v, {}, DEFINES)) for v in (s.strip() for s in body.split(",")) if v]
    return TYPES[ctype], values


def main():
    root = Path(sys.argv[1]) / "silk"
    out = Path(sys.argv[2])
    texts = {}
    lines = [
        "// Transcribed from libopus 1.5.2 silk/tables_*.c, pitch_est_tables.c, table_LSF_cos.c and resampler_rom.c by",
        "// tools/video/make-silk-tables.py; do not edit by hand.",
        "namespace Egami.Video.Audio.Codecs.Opus;",
        "",
        "/// <summary>The constant tables of the SILK decoder; multi-dimensional C arrays are flattened row by row.</summary>",
        "internal static class SilkTables",
        "{",
    ]
    for file, cname, csname in WANTED:
        if file not in texts:
            texts[file] = strip_comments((root / file).read_text())
        cstype, values = extract(texts[file], cname)
        lines.append(f"    /// <summary>{cname} ({len(values)} entries).</summary>")
        lines.append(f"    public static ReadOnlySpan<{cstype}> {csname} =>")
        lines.append("    [")
        for k in range(0, len(values), 16):
            lines.append("        " + ", ".join(str(v) for v in values[k:k + 16]) + ",")
        lines.append("    ];")
        lines.append("")
    lines[-1] = "}"
    out.write_text("\n".join(lines) + "\n", newline="\n")


main()
