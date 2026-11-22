namespace Egami.Heic.Hevc;

/// <summary>
/// A copy of a square region of the picture being encoded, so the search can try one choice, keep or undo it, and try another:
/// the reconstructed samples of the components it names, the 4x4 block maps (intra mode, depth, QpY, flags), and the rate
/// contexts and QP state, which the search keeps with it.
/// </summary>
internal sealed partial class HevcSnapshot(HevcPicture picture)
{
    private readonly ushort[][] _samples = [new ushort[64 * 64], new ushort[64 * 64], new ushort[64 * 64]];
    private readonly byte[] _modes = new byte[16 * 16];
    private readonly byte[] _depths = new byte[16 * 16];
    private readonly sbyte[] _qps = new sbyte[16 * 16];
    private readonly byte[] _flags = new byte[16 * 16];
    private int _x0;
    private int _y0;
    private int _size;
    private bool _maps;
    private bool _luma;
    private bool _chroma;

    /// <summary>Gets the rate contexts saved with the region.</summary>
    internal byte[] Contexts { get; } = new byte[HevcContexts.Count];

    /// <summary>Gets or sets the QP state saved with the region.</summary>
    internal HevcCtuSearch.QpState Qp { get; set; }

    /// <summary>Gets or sets IsCuQpDeltaCoded, saved with the region.</summary>
    internal bool QpDeltaCoded { get; set; }

    /// <summary>Copies the region of luma samples (x0, y0, size) of the components asked for, and the block maps.</summary>
    internal void Capture(int x0, int y0, int size, bool maps, bool chroma, bool luma = true)
    {
        (_x0, _y0, _size, _maps, _luma, _chroma) = (x0, y0, size, maps, luma, chroma && picture.Planes.Length > 1);
        for (int c = _luma ? 0 : 1; c < (_chroma ? 3 : 1); c++)
        {
            Copy(c, true);
        }

        if (maps)
        {
            CopyMaps(true);
        }
    }

    /// <summary>Puts the region back as it was captured.</summary>
    internal void Restore()
    {
        for (int c = _luma ? 0 : 1; c < (_chroma ? 3 : 1); c++)
        {
            Copy(c, false);
        }

        if (_maps)
        {
            CopyMaps(false);
        }
    }

    private void Copy(int c, bool capture)
    {
        HevcSps sps = picture.Sps;
        int sx = c == 0 ? 1 : sps.SubWidthC, sy = c == 0 ? 1 : sps.SubHeightC;
        int x0 = _x0 / sx, y0 = _y0 / sy;
        int stride = picture.Widths[c];
        int width = Math.Min(_size / sx, stride - x0), height = Math.Min(_size / sy, picture.Heights[c] - y0);
        ushort[] plane = picture.Planes[c], saved = _samples[c];
        for (int y = 0; y < height; y++)
        {
            int at = ((y0 + y) * stride) + x0;
            if (capture)
            {
                HevcCopy.Run(plane, at, saved, y * 64, width);
            }
            else
            {
                HevcCopy.Run(saved, y * 64, plane, at, width);
            }
        }
    }

    private void CopyMaps(bool capture)
    {
        int x4 = _x0 >> 2, y4 = _y0 >> 2;
        int w4 = Math.Min(_size >> 2, picture.Width4 - x4), h4 = Math.Min(_size >> 2, picture.Height4 - y4);
        for (int y = 0; y < h4; y++)
        {
            int at = ((y4 + y) * picture.Width4) + x4;
            if (capture)
            {
                HevcCopy.Run(picture.IntraMode, at, _modes, y * 16, w4);
                HevcCopy.Run(picture.CtDepth, at, _depths, y * 16, w4);
                HevcCopy.Run(picture.QpY, at, _qps, y * 16, w4);
                HevcCopy.Run(picture.Flags, at, _flags, y * 16, w4);
            }
            else
            {
                HevcCopy.Run(_modes, y * 16, picture.IntraMode, at, w4);
                HevcCopy.Run(_depths, y * 16, picture.CtDepth, at, w4);
                HevcCopy.Run(_qps, y * 16, picture.QpY, at, w4);
                HevcCopy.Run(_flags, y * 16, picture.Flags, at, w4);
            }
        }

        CopyMotion(capture, x4, y4, w4, h4);
    }

    /// <summary>Copies the region's motion maps too, in an encoder with inter coding compiled in (Egami.Video.Hevc).</summary>
    partial void CopyMotion(bool capture, int x4, int y4, int w4, int h4);
}
