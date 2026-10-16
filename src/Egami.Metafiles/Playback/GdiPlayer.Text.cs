namespace Egami.Metafiles.Playback;

/// <summary>Text (task 9).</summary>
internal sealed partial class GdiPlayer
{
    private void PlayEmfText(int type, ReadOnlySpan<byte> r, long offset)
    {
        _ = r;
        Skip($"Text (EMF type {type})", offset);
    }

    private void PlayWmfText(int function, ReadOnlySpan<byte> p, long offset)
    {
        _ = p;
        Skip($"Text (WMF function 0x{function:X4})", offset);
    }
}
