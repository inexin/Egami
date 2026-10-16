namespace Egami.Metafiles.Playback;

/// <summary>Bitmaps and raster operations (task 8).</summary>
internal sealed partial class GdiPlayer
{
    private void PlayEmfBitmap(int type, ReadOnlySpan<byte> r, long offset)
    {
        _ = r;
        Skip($"Bitmap (EMF type {type})", offset);
    }

    private void PlayWmfBitmap(int function, ReadOnlySpan<byte> p, long offset)
    {
        _ = p;
        Skip($"Bitmap (WMF function 0x{function:X4})", offset);
    }

    private void GradientFill(ReadOnlySpan<byte> r, long offset)
    {
        _ = r;
        Skip("Gradient fill", offset);
    }
}
