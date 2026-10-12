using Egami.Operations;
using Egami.Pixels;

namespace Egami;

/// <summary>The direction images are laid out in by <see cref="Raster.Append"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Combining.cs" region="append"/>
/// </example>
public enum Axis
{
    /// <summary>Side by side, left to right.</summary>
    Horizontal,

    /// <summary>Stacked, top to bottom.</summary>
    Vertical,
}

/// <summary>Where a smaller image sits across the layout's direction: at the start (left or top), the center or the end.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Combining.cs" region="append"/>
/// </example>
public enum Alignment
{
    /// <summary>At the top of a row, or the left of a column. The default.</summary>
    Start,

    /// <summary>Centered, rounding toward the start.</summary>
    Center,

    /// <summary>At the bottom of a row, or the right of a column.</summary>
    End,
}

/// <summary>Building one raster from several: strips and contact sheets (sprite sheets are <see cref="SpriteSheet"/>).</summary>
public abstract partial class Raster
{
    /// <summary>
    /// Returns the images side by side or stacked, as ImageMagick's +append and -append do with -gravity: each image's first frame
    /// in order, with <paramref name="spacing"/> pixels between them, aligned across the strip and with <paramref name="background"/>
    /// around the smaller ones.
    /// </summary>
    /// <param name="images">At least one raster, of any sizes and pixel types.</param>
    /// <param name="axis">Side by side (the default) or stacked.</param>
    /// <param name="spacing">The gap between neighbours, in pixels, 0 or more.</param>
    /// <param name="background">The color of gaps and of the space around smaller images; transparent black by default.</param>
    /// <param name="alignment">Where smaller images sit across the strip.</param>
    /// <returns>The strip, with the first raster's pixel type, metadata and color profile; other images are converted to them. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> or one of them is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="images"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The spacing is negative or an enum is not defined.</exception>
    /// <exception cref="LimitExceededException">The result would break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Combining.cs" region="append"/>
    /// </example>
    public static Raster Append(IReadOnlyList<Raster> images, Axis axis = Axis.Horizontal, int spacing = 0, Rgba8 background = default, Alignment alignment = Alignment.Start)
    {
        CheckImages(images);
        ArgumentOutOfRangeException.ThrowIfNegative(spacing);
        if (!Enum.IsDefined(axis))
        {
            throw new ArgumentOutOfRangeException(nameof(axis), axis, "The axis is not defined.");
        }

        if (!Enum.IsDefined(alignment))
        {
            throw new ArgumentOutOfRangeException(nameof(alignment), alignment, "The alignment is not defined.");
        }

        bool across = axis == Axis.Horizontal;
        long along = 0;
        int thick = 0;
        foreach (Raster image in images)
        {
            along += across ? image.Width : image.Height;
            thick = Math.Max(thick, across ? image.Height : image.Width);
        }

        along += (long)spacing * (images.Count - 1);
        Size size = across ? new Size(Length(along), thick) : new Size(thick, Length(along));
        List<(Raster Image, Point At)> places = new(images.Count);
        int position = 0;
        foreach (Raster image in images)
        {
            int extent = across ? image.Height : image.Width;
            int offset = alignment switch { Alignment.Center => (thick - extent) / 2, Alignment.End => thick - extent, _ => 0 };
            places.Add((image, across ? new Point(position, offset) : new Point(offset, position)));
            position += (across ? image.Width : image.Height) + spacing;
        }

        return images[0].Accept(new Compose(size, background, places, null));
    }

    /// <summary>
    /// Returns a contact sheet, as ImageMagick's montage with -tile and -geometry makes one: the images' first frames in rows of
    /// <paramref name="columns"/> cells, each image shrunk to fit its cell if it is larger and centered in it.
    /// </summary>
    /// <param name="images">At least one raster, of any sizes and pixel types.</param>
    /// <param name="columns">The cells per row, at least 1.</param>
    /// <param name="cell">The cell size, or <see langword="null"/> (the default) for the largest image's width and height.</param>
    /// <param name="spacing">The gap between cells, in pixels, 0 or more.</param>
    /// <param name="background">The color of gaps and empty cell space; transparent black by default.</param>
    /// <param name="filter">The kernel images are shrunk with.</param>
    /// <returns>The sheet, with the first raster's pixel type, metadata and color profile. The caller must dispose it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="images"/> or one of them is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="images"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count or size is out of range, or the filter is not defined.</exception>
    /// <exception cref="LimitExceededException">The result would break <see cref="SafetyLimits.MaxTotalBytes"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Combining.cs" region="grid"/>
    /// </example>
    public static Raster Grid(IReadOnlyList<Raster> images, int columns, Size? cell = null, int spacing = 0, Rgba8 background = default, ResampleFilter filter = ResampleFilter.Bicubic)
    {
        CheckImages(images);
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(spacing);
        if (!Enum.IsDefined(filter))
        {
            throw new ArgumentOutOfRangeException(nameof(filter), filter, "The filter is not defined.");
        }

        if (cell is Size given && (given.Width < 1 || given.Height < 1))
        {
            throw new ArgumentOutOfRangeException(nameof(cell), given, "The cell must be at least 1 × 1.");
        }

        Size box = cell ?? new Size(images.Max(i => i.Width), images.Max(i => i.Height));
        int used = Math.Min(columns, images.Count);
        int rows = (images.Count + columns - 1) / columns;
        Size size = new(
            Length(((long)used * box.Width) + ((long)spacing * (used - 1))),
            Length(((long)rows * box.Height) + ((long)spacing * (rows - 1))));
        List<(Raster Image, Point At)> places = new(images.Count);
        List<Raster> shrunk = [];
        try
        {
            for (int i = 0; i < images.Count; i++)
            {
                Raster image = images[i];
                if (image.Width > box.Width || image.Height > box.Height)
                {
                    image = image.Derive(ops => ops.Fit(box.Width, box.Height, filter, enlarge: false));
                    shrunk.Add(image);
                }

                int x = ((i % columns) * (box.Width + spacing)) + ((box.Width - image.Width) / 2);
                int y = ((i / columns) * (box.Height + spacing)) + ((box.Height - image.Height) / 2);
                places.Add((image, new Point(x, y)));
            }

            return images[0].Accept(new Compose(size, background, places, null));
        }
        finally
        {
            foreach (Raster image in shrunk)
            {
                image.Dispose();
            }
        }
    }

    private static void CheckImages(IReadOnlyList<Raster> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
        {
            throw new ArgumentException("There must be at least one raster.", nameof(images));
        }

        for (int i = 0; i < images.Count; i++)
        {
            if (images[i] is null)
            {
                throw new ArgumentNullException(nameof(images), $"Raster {i} is null.");
            }
        }
    }

    private static int Length(long length)
    {
        return length <= int.MaxValue / 4 ? (int)length : throw new ArgumentException($"The combined image would be {length} pixels long.");
    }

    /// <summary>
    /// Draws each image's first frame at its place on a new canvas of the first image's pixel type, converting pixel types and,
    /// where a color profile differs from the first image's, colors. The canvas is allocated once, at its final size.
    /// </summary>
    internal sealed class Compose(Size size, Rgba8 background, IReadOnlyList<(Raster Image, Point At)> places, Action<Raster>? after) : IRasterVisitor<Raster>
    {
        public Raster Visit<TPixel>(Raster<TPixel> first)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            // SafetyLimits apply to the result before anything is drawn.
            BudgetedMemorySource memory = new(first.Context.Memory, first.Context.Limits.MaxTotalBytes);
            memory.Check((long)size.Width * size.Height * System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>());
            PixelPlane<TPixel> plane = PixelPlane.Allocate<TPixel>(size.Width, size.Height, memory);
            TPixel fill = TPixel.FromRgba8(background);
            for (int y = 0; y < size.Height; y++)
            {
                plane.Row(y).Fill(fill);
            }

            Metadata.RasterMetadata metadata = first.Metadata.Copy();
            Operations.Geometry.ExifUpkeep.Dimensions(metadata, size);
            Operations.Geometry.ExifUpkeep.DropThumbnail(metadata);
            Operations.Geometry.ExifUpkeep.Subject(metadata, null, size);
            Raster<TPixel> canvas = new(plane, metadata, first.Context);
            try
            {
                Colors.ColorProfile profile = first.Metadata.GetColorProfile();
                foreach ((Raster image, Point at) in places)
                {
                    Raster? converted = null;
                    try
                    {
                        Colors.ColorProfile own = image.Metadata.GetColorProfile();
                        Raster source = image;
                        if (!ReferenceEquals(own, profile) && (own.Name != profile.Name || own.Model != profile.Model))
                        {
                            converted = image.Derive(ops => ops.ConvertColor(profile));
                            source = converted;
                        }

                        for (int y = 0; y < source.Height; y++)
                        {
                            source.ReadRow(y, 0, canvas.Row(at.Y + y).Slice(at.X, source.Width));
                        }
                    }
                    finally
                    {
                        converted?.Dispose();
                    }
                }

                after?.Invoke(canvas);
            }
            catch
            {
                canvas.Dispose();
                throw;
            }

            return canvas;
        }
    }
}
