using Egami.Nal;
using Egami.Pixels;

namespace Egami.Video.Codecs.H264;

/// <summary>
/// Codes pictures into slices, macroblock by macroblock, and keeps the reconstructed, deblocked picture a decoder makes of them.
/// </summary>
internal sealed class H264PictureEncoder
{
    private readonly H264StreamConfig _config;
    private readonly H264MbState _mb;
    private readonly H264MbSyntax _syntax;
    private readonly H264MbEncoder _encoder;
    private readonly H264Deblock _deblock = new();
    private readonly List<H264FilterParams> _filters = [new(0, 0, 0)];
    private readonly byte[] _srcY;
    private readonly byte[] _srcCb;
    private readonly byte[] _srcCr;
    private readonly CabacEncoder _cabac = new();

    public H264PictureEncoder(H264StreamConfig config, int effort)
    {
        _config = config;
        Recon = new H264Picture(config.WidthMbs, config.HeightMbs, 1, narrow: true);
        _mb = new H264MbState(config.WidthMbs, config.HeightMbs, 1);
        _syntax = new H264MbSyntax(_mb) { Cabac = config.Cabac, Transform8x8Mode = config.Transform8x8 };
        _srcY = new byte[Recon.Y<byte>().Length];
        _srcCb = new byte[Recon.Cb<byte>().Length];
        _srcCr = new byte[Recon.Cr<byte>().Length];
        _encoder = new H264MbEncoder(Recon, _mb, _syntax, _srcY, _srcCb, _srcCr, config.Transform8x8, effort);
    }

    /// <summary>Gets the reconstructed picture of the last picture coded, deblocked.</summary>
    public H264Picture Recon { get; }

    /// <summary>
    /// Copies a picture into the source planes, its last column and row repeated out to whole macroblocks; chroma is mid-grey
    /// when the picture has none.
    /// </summary>
    public void Load(YuvRaster<byte> picture)
    {
        Copy(picture.Y, _srcY, Recon.Origin, Recon.Stride, _config.Width, _config.Height, _config.WidthMbs * 16, _config.HeightMbs * 16);
        int cw = (_config.Width + 1) >> 1, ch = (_config.Height + 1) >> 1;
        int pw = _config.WidthMbs * 8, ph = _config.HeightMbs * 8;
        if (picture.U is { } u && picture.V is { } v)
        {
            Copy(u, _srcCb, Recon.ChromaOrigin, Recon.ChromaStride, cw, ch, pw, ph);
            Copy(v, _srcCr, Recon.ChromaOrigin, Recon.ChromaStride, cw, ch, pw, ph);
        }
        else
        {
            Array.Fill(_srcCb, (byte)128);
            Array.Fill(_srcCr, (byte)128);
        }
    }

    private static void Copy(YuvPlane<byte> plane, byte[] dst, int origin, int stride, int width, int height, int paddedWidth, int paddedHeight)
    {
        for (int y = 0; y < paddedHeight; y++)
        {
            ReadOnlySpan<byte> row = plane.Row(Math.Min(y, height - 1))[..width];
            Span<byte> to = dst.AsSpan(origin + (y * stride), paddedWidth);
            row.CopyTo(to);
            to[width..].Fill(row[width - 1]);
        }
    }

    /// <summary>Codes the loaded picture as one I slice and returns its NAL unit.</summary>
    public byte[] EncodeIntra(bool idr, int idrPicId, int frameNum, int pocLsb, int qp)
    {
        _mb.Clear();
        Array.Clear(Recon.MbIntra);
        NalBitWriter w = new(_config.WidthMbs * _config.HeightMbs * 32);
        H264StreamWriter.SliceHeader(w, _config, 0, H264SliceType.I, idr, idrPicId, frameNum, pocLsb, qp);
        _syntax.StartSlice(0, H264SliceType.I, qp, 0);
        int total = _config.WidthMbs * _config.HeightMbs;
        if (_config.Cabac)
        {
            while (!w.ByteAligned)
            {
                w.U(1, 1); // cabac_alignment_one_bit
            }

            _cabac.Start();
            CabacWriteSink sink = new(_cabac);
            for (int addr = 0; addr < total; addr++)
            {
                H264MbCode code = Code(addr, qp);
                _syntax.WriteCabac(code, ref sink);
                _syntax.Commit(code);
                _cabac.Terminate(addr == total - 1 ? 1 : 0); // end_of_slice_flag
            }

            w.Bytes(_cabac.Finish());
        }
        else
        {
            BitWriteSink sink = new(w);
            for (int addr = 0; addr < total; addr++)
            {
                H264MbCode code = Code(addr, qp);
                _syntax.WriteCavlc(code, ref sink);
                _syntax.Commit(code);
            }

            w.TrailingBits();
        }

        _deblock.Filter(Recon, _mb, _filters, 8, 8, 0, _config.HeightMbs);
        return H264StreamWriter.Nal(3, idr ? 5 : 1, w);
    }

    private H264MbCode Code(int addr, int qp)
    {
        int mbX = addr % _config.WidthMbs, mbY = addr / _config.WidthMbs;
        _encoder.Encode(mbX, mbY, qp);
        H264MbCode code = _encoder.Best;
        _syntax.Begin(mbX, mbY, code);
        Recon.MbIntra[addr] = true;
        return code;
    }
}
