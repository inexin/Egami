namespace Egami.Video.Editing;

/// <summary>How <see cref="Slideshow.Create"/> builds a slideshow.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-stills"/>
/// </example>
public sealed record SlideshowOptions
{
    /// <summary>Gets the defaults: 4 s an image, 0.5 s fades, no pan and zoom.</summary>
    public static SlideshowOptions Default { get; } = new();

    /// <summary>Gets how long each image shows, its transitions included. Default 4 s.</summary>
    public MediaTime Each { get; init; } = MediaTime.FromSeconds(4);

    /// <summary>Gets the transition between images, or <see langword="null"/> for cuts. Default a 0.5 s fade.</summary>
    public Transition? Transition { get; init; } = Transition.Fade(MediaTime.FromSeconds(0.5));

    /// <summary>Gets whether each image slowly pans and zooms (Ken Burns), in a direction chosen from <see cref="Seed"/>.</summary>
    public bool PanZoom { get; init; }

    /// <summary>Gets the seed the pan and zoom directions come from, so the same seed gives the same show. Default 1.</summary>
    public int Seed { get; init; } = 1;

    /// <summary>Gets the canvas, or <see langword="null"/> to take the first image's size at 30 fps.</summary>
    public EditCanvas? Canvas { get; init; }
}

/// <summary>Builds a <see cref="VideoEdit"/> that shows images one after another, with transitions and an optional pan and zoom.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-stills"/>
/// </example>
public static class Slideshow
{
    /// <summary>Returns an edit that shows the images in order.</summary>
    /// <param name="images">The image files.</param>
    /// <param name="options">The timing, transitions and motion, or <see langword="null"/> for the defaults.</param>
    /// <returns>The edit, to render or change further.</returns>
    /// <exception cref="ArgumentException">There are no images, or a transition is as long as an image.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-stills"/>
    /// </example>
    public static VideoEdit Create(IEnumerable<string> images, SlideshowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(images);
        options ??= SlideshowOptions.Default;
        string[] files = [.. images];
        if (files.Length == 0)
        {
            throw new ArgumentException("A slideshow needs at least one image.", nameof(images));
        }

        if (options.Transition is Transition t && files.Length > 1 && t.Duration >= options.Each)
        {
            throw new ArgumentException("The transition must be shorter than each image's time.", nameof(options));
        }

        EditCanvas? canvas = options.Canvas;
        if (canvas is null)
        {
            using Raster first = Raster.Load(files[0], options: new Egami.Formats.ReaderOptions { Frames = ..1 });
            canvas = new EditCanvas(first.Width + (first.Width & 1), first.Height + (first.Height & 1), new Rational(30, 1)) { Fit = ClipFit.Cover };
        }

        VideoEdit edit = new(canvas);
        Random random = new(options.Seed);
        foreach (string file in files)
        {
            Clip clip = Clip.Still(file, options.Each);
            if (options.PanZoom)
            {
                // A zoom of 8 to 15 %, in or out, drifting towards a random corner.
                float zoom = 1 / (1.08f + ((float)random.NextDouble() * 0.07f));
                float x = (float)random.NextDouble() * (1 - zoom), y = (float)random.NextDouble() * (1 - zoom);
                System.Drawing.RectangleF whole = new(0, 0, 1, 1), part = new(x, y, zoom, zoom);
                clip = random.Next(2) == 0 ? clip.PanZoom(whole, part, Easing.EaseInOut) : clip.PanZoom(part, whole, Easing.EaseInOut);
            }

            edit.Track.Add(clip, edit.Track.Count == 0 ? null : options.Transition);
        }

        return edit;
    }
}
