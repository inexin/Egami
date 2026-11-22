namespace Egami.Heic.Hevc;

/// <summary>
/// The motion maps of a snapshot's region (prediction flags, skip flags, list 0 and list 1 reference indices, vectors and
/// reference ids), so an inter trial that is undone leaves no motion behind for the merge candidates and deblocking of later
/// blocks.
/// </summary>
internal sealed partial class HevcSnapshot
{
    private readonly byte[] _predFlags = new byte[16 * 16];
    private readonly byte[] _skipFlags = new byte[16 * 16];
    private readonly sbyte[][] _refIdx = [new sbyte[16 * 16], new sbyte[16 * 16]];
    private readonly int[][] _mv = [new int[16 * 16], new int[16 * 16]];
    private readonly int[][] _refId = [new int[16 * 16], new int[16 * 16]];

    /// <summary>Gets or sets whether the snapshot copies the motion maps (the pictures of P and B slices only).</summary>
    internal bool Motion { get; set; }

    partial void CopyMotion(bool capture, int x4, int y4, int w4, int h4)
    {
        if (!Motion)
        {
            return;
        }

        for (int y = 0; y < h4; y++)
        {
            int at = ((y4 + y) * picture.Width4) + x4, saved = y * 16;
            Copy(picture.PredFlags, _predFlags, at, saved, w4, capture);
            Copy(picture.SkipFlags, _skipFlags, at, saved, w4, capture);
            for (int list = 0; list < 2; list++)
            {
                Copy(picture.RefIdx[list], _refIdx[list], at, saved, w4, capture);
                Copy(picture.Mv[list], _mv[list], at, saved, w4, capture);
                Copy(picture.RefId[list], _refId[list], at, saved, w4, capture);
            }
        }
    }

    private static void Copy<T>(T[] map, T[] saved, int at, int savedAt, int count, bool capture)
        where T : unmanaged
    {
        if (capture)
        {
            HevcCopy.Run(map, at, saved, savedAt, count);
        }
        else
        {
            HevcCopy.Run(saved, savedAt, map, at, count);
        }
    }
}
