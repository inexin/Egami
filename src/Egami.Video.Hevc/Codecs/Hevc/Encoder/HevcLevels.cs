namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>
/// HEVC's Main tier levels (H.265 Tables A.8 and A.9, general limits A.4.1): what a stream's picture size, frame rate, bitrate and
/// buffer must fit, and the lowest level they do.
/// </summary>
internal static class HevcLevels
{
    /// <summary>
    /// A level: ten times its number (41 for 4.1), MaxLumaPs, MaxCPB (Main tier, in 1000 bits), MaxLumaSr (samples a second) and
    /// MaxBR (Main tier, in 1000 bits a second).
    /// </summary>
    internal readonly record struct Level(int Number, long MaxLumaPs, long MaxCpb, long MaxLumaSr, long MaxBr)
    {
        /// <summary>Gets general_level_idc: 30 times the level number.</summary>
        internal int Idc => Number * 3;
    }

    /// <summary>What a stream asks of its level: its frame rate, and its peak bitrate and buffer in bits (0 for none).</summary>
    internal readonly record struct Demand(double FrameRate, long MaxBitrate, long Buffer);

    private static readonly Level[] Table =
    [
        new(10, 36864, 350, 552960, 128),
        new(20, 122880, 1500, 3686400, 1500),
        new(21, 245760, 3000, 7372800, 3000),
        new(30, 552960, 6000, 16588800, 6000),
        new(31, 983040, 10000, 33177600, 10000),
        new(40, 2228224, 12000, 66846720, 12000),
        new(41, 2228224, 20000, 133693440, 20000),
        new(50, 8912896, 25000, 267386880, 25000),
        new(51, 8912896, 40000, 534773760, 40000),
        new(52, 8912896, 60000, 1069547520, 60000),
        new(60, 35651584, 60000, 1069547520, 60000),
        new(61, 35651584, 120000, 2139095040, 120000),
        new(62, 35651584, 240000, 4278190080, 240000),
    ];

    /// <summary>The level of a number (ten times the level), which must be one of the table's.</summary>
    internal static Level Of(int number) => Array.Find(Table, l => l.Number == number);

    /// <summary>The lowest level a stream fits, or level 6.2 when none does (the caller then reports why).</summary>
    internal static int Choose(int width, int height, Demand demand)
    {
        foreach (Level level in Table)
        {
            if (Misfit(level, width, height, demand) is null)
            {
                return level.Number;
            }
        }

        return 62;
    }

    /// <summary>Why a stream does not fit a level, or null when it does. The coded size is the frame's rounded up to 8.</summary>
    internal static string? Misfit(Level level, int width, int height, Demand demand)
    {
        long codedWidth = (width + 7) & ~7, codedHeight = (height + 7) & ~7;
        long samples = codedWidth * codedHeight;
        double side = Math.Sqrt(level.MaxLumaPs * 8.0);
        if (samples > level.MaxLumaPs || codedWidth > side || codedHeight > side)
        {
            return $"{width}x{height} is larger than level {level.Number / 10.0:0.0} holds";
        }

        if (samples * demand.FrameRate > level.MaxLumaSr)
        {
            return $"{demand.FrameRate:0.###} frames a second at {width}x{height} are more samples than level {level.Number / 10.0:0.0} decodes";
        }

        if (demand.MaxBitrate > level.MaxBr * 1000)
        {
            return $"a peak bitrate of {demand.MaxBitrate} bits a second is above level {level.Number / 10.0:0.0}'s {level.MaxBr * 1000}";
        }

        if (demand.Buffer > level.MaxCpb * 1000)
        {
            return $"a buffer of {demand.Buffer} bits is above level {level.Number / 10.0:0.0}'s {level.MaxCpb * 1000}";
        }

        return null;
    }
}
