using System.Buffers.Binary;

using EgamiFonts = Egami.Fonts;

namespace Egami.SystemDrawing.Internal;

/// <summary>GDI+'s cell and first baseline, as measured on Windows (2.7.6 task 28).</summary>
internal static class GdiAscent
{
    /// <summary>
    /// Where grid-fitted ClearType text puts the first line's baseline, in device pixels: the line's top rounded to a pixel, then
    /// the font's <see cref="EgamiFonts.Font.GdiPlusAscent"/> (whole pixels for a grid-fitted font) below.
    /// </summary>
    public static float FirstBaseline(float lineTop, EgamiFonts.Font device) => MathF.Floor(lineTop + 0.5f) + device.GdiPlusAscent;

    /// <summary>Whether the OS/2 table asks for its typographic metrics (fsSelection bit 7, USE_TYPO_METRICS).</summary>
    public static bool UsesTypoMetrics(EgamiFonts.Typeface face)
        => face.TryGetTable(new EgamiFonts.FontTag("OS/2"), out ReadOnlyMemory<byte> os2) && os2.Length >= 64
            && (BinaryPrimitives.ReadUInt16BigEndian(os2.Span[62..]) & 0x80) != 0;
}
