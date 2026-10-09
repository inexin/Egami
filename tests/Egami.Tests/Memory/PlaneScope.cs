namespace Egami.Tests.Memory;

/// <summary>Frees a plane a test allocated itself, for a using statement (planes are not disposable outside the library).</summary>
internal readonly struct PlaneScope<TPixel>(PixelPlane<TPixel> plane) : IDisposable
    where TPixel : unmanaged, Egami.Pixels.IPixelType<TPixel>
{
    public void Dispose()
    {
        plane.Dispose();
    }
}
