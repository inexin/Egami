using Egami.Pixels;

namespace Egami;

/// <summary>How <see cref="SpriteSheet.Pack"/> lays sprites out.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Combining.cs" region="sprite-sheet"/>
/// </example>
public sealed record class SpritePackOptions
{
    private readonly int _maxWidth = 4096;
    private readonly int _padding;
    private readonly int _extrude;

    /// <summary>Gets the widest the sheet may be, in pixels; sprites wider than this throw. Defaults to 4,096, a safe GPU texture size.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is less than 1.</exception>
    public int MaxWidth
    {
        get => _maxWidth;
        init => _maxWidth = value >= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The width must be at least 1.");
    }

    /// <summary>Gets the transparent gap between sprites, in pixels. Defaults to 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Padding
    {
        get => _padding;
        init => _padding = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The padding must be 0 or more.");
    }

    /// <summary>
    /// Gets how many pixels each sprite's edge pixels are copied outward, so a GPU's filtering at the sprite's border reads the
    /// sprite's own colors instead of its neighbour's. Defaults to 0.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Extrude
    {
        get => _extrude;
        init => _extrude = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The extrusion must be 0 or more.");
    }

    /// <summary>Gets a value indicating whether the sheet's width and height are rounded up to powers of two. Defaults to <see langword="false"/>.</summary>
    public bool PowerOfTwo { get; init; }

    /// <summary>
    /// Gets a value indicating whether each sprite's fully transparent border is trimmed away before packing; <see cref="SpriteSheet.Offsets"/>
    /// then says where the trimmed sprite sat in its original. Defaults to <see langword="false"/>.
    /// </summary>
    public bool Trim { get; init; }
}

/// <summary>
/// A sprite sheet: many images packed into one, with the rectangle of each. Packing uses the MaxRects algorithm with the best
/// short side fit (Jylänki, "A Thousand Ways to Pack the Bin", 2010), the images taken by height, tallest first, and then by
/// input order, so the same inputs always give the same sheet. Sprites are never rotated.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Combining.cs" region="sprite-sheet"/>
/// </example>
public sealed class SpriteSheet : IDisposable
{
    private SpriteSheet(Raster image, Rect[] frames, Point[] offsets)
    {
        Image = image;
        Frames = frames;
        Offsets = offsets;
    }

    /// <summary>Gets the sheet. It has the first input's pixel type, metadata and color profile, and a transparent background.</summary>
    public Raster Image { get; }

    /// <summary>Gets where each input sits on the sheet, in input order.</summary>
    public IReadOnlyList<Rect> Frames { get; }

    /// <summary>
    /// Gets, for each input, where its packed rectangle sat in the original image: (0, 0) unless <see cref="SpritePackOptions.Trim"/>
    /// cut a transparent border away.
    /// </summary>
    public IReadOnlyList<Point> Offsets { get; }

    /// <summary>Packs the images' first frames into as small a sheet as it finds.</summary>
    /// <param name="images">At least one raster, of any sizes and pixel types.</param>
    /// <param name="options">The layout options, or <see langword="null"/> for the defaults.</param>
    /// <returns>The sheet and the rectangles. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> or one of them is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="images"/> is empty, or an image (with its padding and extrusion) is wider than <see cref="SpritePackOptions.MaxWidth"/>, naming its index.</exception>
    /// <exception cref="LimitExceededException">The sheet would break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Combining.cs" region="sprite-sheet"/>
    /// </example>
    public static SpriteSheet Pack(IReadOnlyList<Raster> images, SpritePackOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            throw new ArgumentException("There must be at least one raster.", nameof(images));
        }

        options ??= new SpritePackOptions();
        int n = images.Count;
        Rect[] areas = new Rect[n];
        Point[] offsets = new Point[n];
        for (int i = 0; i < n; i++)
        {
            Raster image = images[i] ?? throw new ArgumentNullException(nameof(images), $"Raster {i} is null.");
            Rect area = new(0, 0, image.Width, image.Height);
            if (options.Trim)
            {
                Rect content = image.TrimBounds(0, default(Rgba8));
                area = content.IsEmpty ? new Rect(0, 0, 1, 1) : content;
            }

            areas[i] = area;
            offsets[i] = area.Position;
        }

        int margin = options.Padding + (2 * options.Extrude);
        for (int i = 0; i < n; i++)
        {
            if (areas[i].Width + margin > options.MaxWidth)
            {
                throw new ArgumentException($"Image {i} is {areas[i].Width} pixels wide, which with padding and extrusion does not fit the sheet's {options.MaxWidth}.", nameof(images));
            }
        }

        (Point[] places, Size size) = Packer.Pack([.. areas.Select(a => new Size(a.Width + margin, a.Height + margin))], options.MaxWidth);
        if (options.PowerOfTwo)
        {
            size = new Size(NextPowerOfTwo(size.Width), NextPowerOfTwo(size.Height));
        }

        Rect[] frames = new Rect[n];
        List<(Raster Image, Point At)> draws = new(n);
        List<Raster> crops = [];
        try
        {
            for (int i = 0; i < n; i++)
            {
                Point at = new(places[i].X + options.Extrude, places[i].Y + options.Extrude);
                frames[i] = new Rect(at, areas[i].Size);
                Raster image = images[i];
                if (areas[i] != new Rect(0, 0, image.Width, image.Height))
                {
                    Rect area = areas[i];
                    image = image.Derive(ops => ops.Crop(area));
                    crops.Add(image);
                }

                draws.Add((image, at));
            }

            Raster sheet = images[0].Accept(new Raster.Compose(size, default, draws, options.Extrude > 0 ? canvas => Extrude(canvas, frames, options.Extrude) : null));
            return new SpriteSheet(sheet, frames, offsets);
        }
        finally
        {
            foreach (Raster crop in crops)
            {
                crop.Dispose();
            }
        }
    }

    /// <summary>Frees the sheet.</summary>
    public void Dispose()
    {
        Image.Dispose();
    }

    private static int NextPowerOfTwo(int value)
    {
        int p = 1;
        while (p < value)
        {
            p <<= 1;
        }

        return p;
    }

    private static void Extrude(Raster canvas, Rect[] frames, int extrude)
    {
        canvas.Accept(new Extruder(frames, extrude));
    }

    /// <summary>Copies each sprite's edge rows and columns outward by the extrusion, corners included.</summary>
    private sealed class Extruder(Rect[] frames, int extrude) : IRasterVisitor<bool>
    {
        public bool Visit<TPixel>(Raster<TPixel> canvas)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            foreach (Rect f in frames)
            {
                for (int y = f.Y; y < f.Bottom; y++)
                {
                    Span<TPixel> row = canvas.Row(y);
                    row.Slice(f.X - extrude, extrude).Fill(row[f.X]);
                    row.Slice(f.Right, extrude).Fill(row[f.Right - 1]);
                }

                for (int e = 1; e <= extrude; e++)
                {
                    canvas.Row(f.Y).Slice(f.X - extrude, f.Width + (2 * extrude)).CopyTo(canvas.Row(f.Y - e).Slice(f.X - extrude));
                    canvas.Row(f.Bottom - 1).Slice(f.X - extrude, f.Width + (2 * extrude)).CopyTo(canvas.Row(f.Bottom - 1 + e).Slice(f.X - extrude));
                }
            }

            return true;
        }
    }

    /// <summary>
    /// MaxRects with the best short side fit, on a sheet of a fixed width and unbounded height; several widths are tried and the
    /// one giving the smallest sheet kept.
    /// </summary>
    internal static class Packer
    {
        internal static (Point[] Places, Size Size) Pack(Size[] sizes, int maxWidth)
        {
            int[] order = [.. Enumerable.Range(0, sizes.Length).OrderByDescending(i => sizes[i].Height).ThenBy(i => i)];
            long area = sizes.Sum(s => (long)s.Width * s.Height);
            int widest = sizes.Max(s => s.Width);
            int low = Math.Max(widest, (int)Math.Min(maxWidth, Math.Ceiling(Math.Sqrt(area))));
            (Point[] Places, Size Size)? best = null;
            foreach (int width in Candidates(low, maxWidth, widest))
            {
                (Point[] places, Size size) = PackAt(sizes, order, width);
                if (best is null || (long)size.Width * size.Height < (long)best.Value.Size.Width * best.Value.Size.Height)
                {
                    best = (places, size);
                }
            }

            return best!.Value;
        }

        /// <summary>The widths tried: from the square root of the total area (or the widest sprite) to the maximum, in a few steps.</summary>
        private static IEnumerable<int> Candidates(int low, int maxWidth, int widest)
        {
            HashSet<int> seen = [];
            for (int i = 0; i <= 12; i++)
            {
                double f = 1 + (i * 0.1);
                int w = Math.Clamp((int)(low * f), widest, maxWidth);
                if (seen.Add(w))
                {
                    yield return w;
                }
            }
        }

        private static (Point[] Places, Size Size) PackAt(Size[] sizes, int[] order, int width)
        {
            int height = (int)Math.Min(int.MaxValue / 2, sizes.Sum(s => (long)s.Height));
            List<Rect> free = [new Rect(0, 0, width, height)];
            Point[] places = new Point[sizes.Length];
            int used = 0, right = 0;
            foreach (int i in order)
            {
                Size s = sizes[i];
                int bestShort = int.MaxValue, bestLong = int.MaxValue;
                Rect chosen = default;
                foreach (Rect r in free)
                {
                    if (r.Width >= s.Width && r.Height >= s.Height)
                    {
                        int shortSide = Math.Min(r.Width - s.Width, r.Height - s.Height);
                        int longSide = Math.Max(r.Width - s.Width, r.Height - s.Height);

                        // Best short side fit, ties to the topmost then leftmost spot so the sheet stays short.
                        if (shortSide < bestShort || (shortSide == bestShort && (longSide < bestLong || (longSide == bestLong && (r.Y < chosen.Y || (r.Y == chosen.Y && r.X < chosen.X))))))
                        {
                            (bestShort, bestLong, chosen) = (shortSide, longSide, new Rect(r.X, r.Y, s.Width, s.Height));
                        }
                    }
                }

                places[i] = chosen.Position;
                used = Math.Max(used, chosen.Bottom);
                right = Math.Max(right, chosen.Right);
                Place(free, chosen);
            }

            return (places, new Size(Math.Max(1, right), Math.Max(1, used)));
        }

        /// <summary>Splits every free rectangle the placed one overlaps into the up to four parts around it, then drops contained ones.</summary>
        private static void Place(List<Rect> free, Rect placed)
        {
            List<Rect> added = [];
            for (int i = free.Count - 1; i >= 0; i--)
            {
                Rect r = free[i];
                if (r.Intersect(placed).IsEmpty)
                {
                    continue;
                }

                free.RemoveAt(i);
                if (placed.X > r.X)
                {
                    added.Add(new Rect(r.X, r.Y, placed.X - r.X, r.Height));
                }

                if (placed.Right < r.Right)
                {
                    added.Add(new Rect(placed.Right, r.Y, r.Right - placed.Right, r.Height));
                }

                if (placed.Y > r.Y)
                {
                    added.Add(new Rect(r.X, r.Y, r.Width, placed.Y - r.Y));
                }

                if (placed.Bottom < r.Bottom)
                {
                    added.Add(new Rect(r.X, placed.Bottom, r.Width, r.Bottom - placed.Bottom));
                }
            }

            free.AddRange(added);
            for (int i = free.Count - 1; i >= 0; i--)
            {
                for (int j = 0; j < free.Count; j++)
                {
                    if (i != j && free[j].Contains(free[i]) && (free[j] != free[i] || j < i))
                    {
                        free.RemoveAt(i);
                        break;
                    }
                }
            }
        }
    }
}
