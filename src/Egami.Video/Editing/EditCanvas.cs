using Egami.Pixels;
using Egami.Video.Audio;

namespace Egami.Video.Editing;

/// <summary>How a clip whose size differs from the canvas is fitted to it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-join"/>
/// </example>
public enum ClipFit
{
    /// <summary>Scaled to fit inside the canvas with its aspect ratio kept, the rest filled with the background (letterbox or pillarbox). The default.</summary>
    Contain,

    /// <summary>Scaled to cover the whole canvas with its aspect ratio kept, the overflow cropped from the centre.</summary>
    Cover,

    /// <summary>Scaled to the canvas size, changing its aspect ratio.</summary>
    Stretch,

    /// <summary>Kept at its own size, centred, cropped or padded with the background.</summary>
    None,
}

/// <summary>
/// The output of a <see cref="VideoEdit"/>: picture size, frame rate, how clips are fitted, the background, and the audio
/// format every clip's audio is resampled and remixed to. When an edit has no canvas, it takes the first clip's size and
/// frame rate.
/// </summary>
/// <param name="Width">The width in pixels.</param>
/// <param name="Height">The height in pixels.</param>
/// <param name="FrameRate">The frames per second.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-join"/>
/// </example>
public sealed record EditCanvas(int Width, int Height, Rational FrameRate)
{
    /// <summary>Gets how clips of another size are fitted. Default <see cref="ClipFit.Contain"/>.</summary>
    public ClipFit Fit { get; init; }

    /// <summary>Gets the colour behind the clips, seen where a fitted clip does not cover the canvas. Default black.</summary>
    public Rgba8 Background { get; init; } = Rgba8.Black;

    /// <summary>Gets the audio sample rate in hertz. Default 48000.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Gets the audio channels. Default stereo.</summary>
    public ChannelLayout AudioLayout { get; init; } = ChannelLayout.Stereo;

    internal void Validate()
    {
        if (Width < 1 || Height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), $"The canvas must be at least 1 by 1, not {Width} by {Height}.");
        }

        if (FrameRate.Numerator <= 0 || FrameRate.Denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FrameRate), FrameRate, "The frame rate must be above 0.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(SampleRate, 1, nameof(SampleRate));
        ArgumentOutOfRangeException.ThrowIfLessThan(AudioLayout.ChannelCount, 1, nameof(AudioLayout));
    }
}
