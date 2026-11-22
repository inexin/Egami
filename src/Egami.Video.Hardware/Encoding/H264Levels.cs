namespace Egami.Video.Hardware.Encoding;

/// <summary>The limits of H.264's levels (ITU-T H.264 Table A-1), to pick the lowest level a stream fits.</summary>
internal static class H264Levels
{
    /// <summary>One level's limits.</summary>
    /// <param name="Idc">level_idc, ten times the level number.</param>
    /// <param name="MaxMbps">Macroblocks a second.</param>
    /// <param name="MaxFs">Macroblocks a frame.</param>
    /// <param name="MaxDpbMbs">Macroblocks in the decoded picture buffer.</param>
    /// <param name="MaxBr">Bitrate in 1000 bits a second for the Baseline and Main profiles (High: 1.25 times).</param>
    /// <param name="MaxCpb">Coded picture buffer in 1000 bits, likewise.</param>
    internal readonly record struct Level(int Idc, long MaxMbps, int MaxFs, int MaxDpbMbs, long MaxBr, long MaxCpb);

    /// <summary>Every level, lowest first.</summary>
    internal static readonly Level[] All =
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

    /// <summary>Returns the index of a level_idc in <see cref="All"/>, which is also its <c>StdVideoH264LevelIdc</c> value, or -1.</summary>
    internal static int IndexOf(int idc) => Array.FindIndex(All, l => l.Idc == idc);

    /// <summary>Returns why a stream does not fit a level, or <see langword="null"/> when it does.</summary>
    /// <param name="level">The level.</param>
    /// <param name="widthMbs">The picture width in macroblocks.</param>
    /// <param name="heightMbs">The picture height in macroblocks.</param>
    /// <param name="fps">Frames a second.</param>
    /// <param name="refFrames">The reference frames the stream keeps (max_num_ref_frames).</param>
    /// <param name="bitrate">The peak bitrate in bits a second, or 0 for none given.</param>
    /// <param name="high">Whether the profile is High, whose bitrate limits are 1.25 times the others'.</param>
    internal static string? Misfit(Level level, int widthMbs, int heightMbs, double fps, int refFrames, long bitrate, bool high)
    {
        int frameMbs = widthMbs * heightMbs;
        double side = Math.Sqrt(8.0 * level.MaxFs);
        if (frameMbs > level.MaxFs || widthMbs > side || heightMbs > side)
        {
            return $"a {widthMbs * 16}x{heightMbs * 16} picture is larger than level {level.Idc / 10.0:0.0} allows";
        }

        if (frameMbs * fps > level.MaxMbps * 1.0000001)
        {
            return $"{fps:0.###} frames a second of this size are more than level {level.Idc / 10.0:0.0} allows";
        }

        if ((long)refFrames * frameMbs > level.MaxDpbMbs)
        {
            return $"{refFrames} reference frames of this size are more than level {level.Idc / 10.0:0.0} holds";
        }

        if (bitrate > level.MaxBr * (high ? 1250 : 1000))
        {
            return $"{bitrate} bits a second are more than level {level.Idc / 10.0:0.0} allows";
        }

        return null;
    }

    /// <summary>Returns the lowest level a stream fits, or the highest level when none does.</summary>
    internal static Level Choose(int widthMbs, int heightMbs, double fps, int refFrames, long bitrate, bool high) =>
        All.FirstOrDefault(l => Misfit(l, widthMbs, heightMbs, fps, refFrames, bitrate, high) is null, All[^1]);
}
