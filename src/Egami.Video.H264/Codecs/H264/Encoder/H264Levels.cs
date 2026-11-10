namespace Egami.Video.Codecs.H264;

/// <summary>The level limits of H.264's Table A-1 that the encoder picks levels by and keeps to.</summary>
internal static class H264Levels
{
    /// <summary>
    /// One level's limits: macroblocks per second and per frame, the DPB in macroblocks, and the bitrate and coded picture buffer
    /// in 1000 bits per second and 1000 bits (Baseline and Main; High allows 1.25 times, A.3.3 and Table A-2).
    /// </summary>
    internal readonly record struct Limits(int Idc, long MaxMbps, int MaxFs, int MaxDpbMbs, int MaxBr, int MaxCpb);

    internal static readonly Limits[] Table =
    [
        new(10, 1485, 99, 396, 64, 175),
        new(11, 3000, 396, 900, 192, 500),
        new(12, 6000, 396, 2376, 384, 1000),
        new(13, 11880, 396, 2376, 768, 2000),
        new(20, 11880, 396, 2376, 2000, 2000),
        new(21, 19800, 792, 4752, 4000, 4000),
        new(22, 20250, 1620, 8100, 4000, 4000),
        new(30, 40500, 1620, 8100, 10000, 10000),
        new(31, 108000, 3600, 18000, 14000, 14000),
        new(32, 216000, 5120, 20480, 20000, 20000),
        new(40, 245760, 8192, 32768, 20000, 25000),
        new(41, 245760, 8192, 32768, 50000, 62500),
        new(42, 522240, 8704, 34816, 50000, 62500),
        new(50, 589824, 22080, 110400, 135000, 135000),
        new(51, 983040, 36864, 184320, 240000, 240000),
        new(52, 2073600, 36864, 184320, 240000, 240000),
        new(60, 4177920, 139264, 696320, 240000, 240000),
        new(61, 8355840, 139264, 696320, 480000, 480000),
        new(62, 16711680, 139264, 696320, 800000, 800000),
    ];

    /// <summary>
    /// A stream's demands on its level beyond the picture size: frames per second, the frames the DPB holds, and for the
    /// bitrate modes the peak bitrate and the buffer in bits (0 when the quality modes leave them open); High (cpbBrVclFactor
    /// 1250) gets 1.25 times the bitrate and buffer of Baseline and Main (1000).
    /// </summary>
    internal readonly record struct Demand(double Fps, int DpbFrames, long MaxBitrate = 0, long Buffer = 0, int BrFactor = 1000);

    /// <summary>The largest vertical motion vector component a level allows (Table A-1 MaxVmvR), in whole samples either way.</summary>
    internal static int MaxVerticalMv(int idc) => idc switch { <= 10 => 64, <= 20 => 128, <= 30 => 256, _ => 512 };

    /// <summary>The limits of a level_idc.</summary>
    internal static Limits Of(int idc) => Array.Find(Table, l => l.Idc == idc);

    /// <summary>
    /// Why a stream of this size, rate and DPB does not fit a level (A.3.1, A.3.2), or <see langword="null"/> when it does:
    /// frame size and each side (no side longer than √(8 MaxFS) macroblocks), macroblock rate, and the frames the DPB holds.
    /// </summary>
    internal static string? Misfit(Limits level, int widthMbs, int heightMbs, double fps, int dpbFrames) =>
        Misfit(level, widthMbs, heightMbs, new Demand(fps, dpbFrames));

    /// <summary>
    /// Why a stream does not fit a level, as <see cref="Misfit(Limits, int, int, double, int)"/> also checking the peak bitrate
    /// and buffer against MaxBR and MaxCPB (A.3.1 item j, A.3.3). The slice count needs no check: at most 32 slices stay far
    /// under every level's SliceRate limit (Table A-4) at the frame rates the level allows.
    /// </summary>
    internal static string? Misfit(Limits level, int widthMbs, int heightMbs, Demand demand)
    {
        (double fps, int dpbFrames) = (demand.Fps, demand.DpbFrames);
        int frame = widthMbs * heightMbs;
        double side = Math.Sqrt(level.MaxFs * 8.0);
        if (frame > level.MaxFs || widthMbs > side || heightMbs > side)
        {
            return $"{widthMbs}x{heightMbs} macroblocks is larger than level {level.Idc / 10.0:0.0} allows ({level.MaxFs} macroblocks, no side over {(int)side})";
        }

        if (frame * fps > level.MaxMbps)
        {
            return $"{frame * fps:0} macroblocks a second is more than level {level.Idc / 10.0:0.0} allows ({level.MaxMbps})";
        }

        if (dpbFrames * frame > level.MaxDpbMbs)
        {
            return $"{dpbFrames} reference frames of {frame} macroblocks are more than level {level.Idc / 10.0:0.0}'s buffer holds ({level.MaxDpbMbs} macroblocks)";
        }

        if (demand.MaxBitrate > (long)level.MaxBr * demand.BrFactor)
        {
            return $"{demand.MaxBitrate} bits a second is more than level {level.Idc / 10.0:0.0} allows ({(long)level.MaxBr * demand.BrFactor})";
        }

        if (demand.Buffer > (long)level.MaxCpb * demand.BrFactor)
        {
            return $"a buffer of {demand.Buffer} bits is more than level {level.Idc / 10.0:0.0} allows ({(long)level.MaxCpb * demand.BrFactor})";
        }

        return null;
    }

    /// <summary>The lowest level a stream fits, or the highest when it fits none.</summary>
    internal static int Choose(int widthMbs, int heightMbs, double fps, int dpbFrames) => Choose(widthMbs, heightMbs, new Demand(fps, dpbFrames));

    /// <summary>The lowest level a stream fits, bitrate and buffer included, or the highest when it fits none.</summary>
    internal static int Choose(int widthMbs, int heightMbs, Demand demand)
    {
        foreach (Limits level in Table)
        {
            if (Misfit(level, widthMbs, heightMbs, demand) is null)
            {
                return level.Idc;
            }
        }

        return Table[^1].Idc;
    }
}
