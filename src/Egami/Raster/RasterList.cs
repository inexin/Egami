using System.Collections;

namespace Egami;

/// <summary>
/// The images of a file that holds several, each its own raster with its own size, pixel type and metadata: a TIFF's pages, an
/// icon's entries. The list owns them: disposing it disposes every raster.
/// </summary>
/// <remarks>
/// Frames are for animation and share one size; images of different sizes come as a list. Take a raster out of the list with
/// <see cref="Detach"/> to keep it after the list is disposed.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
/// </example>
public sealed class RasterList : IReadOnlyList<Raster>, IDisposable
{
    private readonly List<Raster?> _rasters;

    internal RasterList(List<Raster?> rasters)
    {
        _rasters = rasters;
    }

    /// <summary>Gets the number of images.</summary>
    public int Count => _rasters.Count;

    /// <summary>Gets an image.</summary>
    /// <param name="index">Its position in the file's order.</param>
    /// <returns>The raster, still owned by the list.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the list.</exception>
    /// <exception cref="InvalidOperationException">The raster was taken with <see cref="Detach"/>.</exception>
    public Raster this[int index] => _rasters[index] ?? throw new InvalidOperationException($"Image {index} was detached from the list.");

    /// <summary>Takes an image out of the list's ownership; the caller must dispose it. The list keeps an empty place.</summary>
    /// <param name="index">The image's position.</param>
    /// <returns>The raster, now owned by the caller.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside the list.</exception>
    /// <exception cref="InvalidOperationException">The raster was already taken.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Tiff.cs" region="tiff-pages"/>
    /// </example>
    public Raster Detach(int index)
    {
        Raster raster = this[index];
        _rasters[index] = null;
        return raster;
    }

    /// <inheritdoc/>
    public IEnumerator<Raster> GetEnumerator()
    {
        for (int i = 0; i < _rasters.Count; i++)
        {
            yield return this[i];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Disposes every raster still in the list.</summary>
    public void Dispose()
    {
        for (int i = 0; i < _rasters.Count; i++)
        {
            _rasters[i]?.Dispose();
            _rasters[i] = null;
        }
    }
}
