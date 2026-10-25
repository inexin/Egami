using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Egami.Video.Editing;

/// <summary>
/// How the camera moved from one frame to the next, as <see cref="Stabilizer.Analyze(string, MotionAnalysisOptions?, IProgress{MediaProgress}?, CancellationToken)"/>
/// measures it: a shift, a rotation and a scale per frame, in the clip's own pixels. It is pass 1 of stabilization; pass 2,
/// <see cref="Clip.Stabilize"/>, smooths the path and warps the frames. Save it as JSON to stabilize again with other settings
/// without measuring again.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Editing.cs" region="edit-stabilize"/>
/// </example>
public sealed class CameraMotion
{
    /// <summary>Creates a camera motion.</summary>
    /// <param name="width">The clip's width.</param>
    /// <param name="height">The clip's height.</param>
    /// <param name="frames">The motion into each frame from the one before, in order; the first frame's is none.</param>
    public CameraMotion(int width, int height, IEnumerable<FrameMotion> frames)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentNullException.ThrowIfNull(frames);
        Width = width;
        Height = height;
        Frames = [.. frames];
    }

    /// <summary>Gets the clip's width in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the clip's height in pixels.</summary>
    public int Height { get; }

    /// <summary>Gets the motion into each frame from the one before, in order.</summary>
    public IReadOnlyList<FrameMotion> Frames { get; }

    /// <summary>Reads a camera motion written by <see cref="ToJson"/>.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The motion.</returns>
    /// <exception cref="FormatException">The JSON is not a camera motion.</exception>
    public static CameraMotion FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return FromElement(document.RootElement);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new FormatException($"The camera motion is not valid: {e.Message}", e);
        }
    }

    /// <summary>Returns the motion as JSON: the size, then a [time, dx, dy, angle, scale] row per frame.</summary>
    /// <returns>The JSON text.</returns>
    public string ToJson()
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer))
        {
            Write(json);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    internal void Write(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteNumber("width", Width);
        json.WriteNumber("height", Height);
        json.WriteStartArray("frames");
        foreach (FrameMotion frame in Frames)
        {
            json.WriteStartArray();
            json.WriteNumberValue(Math.Round(frame.Time.Seconds, 6));
            json.WriteNumberValue(Math.Round(frame.Dx, 4));
            json.WriteNumberValue(Math.Round(frame.Dy, 4));
            json.WriteNumberValue(Math.Round(frame.Angle, 7));
            json.WriteNumberValue(Math.Round(frame.Scale, 7));
            json.WriteEndArray();
        }

        json.WriteEndArray();
        json.WriteEndObject();
    }

    internal static CameraMotion FromElement(JsonElement e)
    {
        List<FrameMotion> frames = [];
        foreach (JsonElement row in e.GetProperty("frames").EnumerateArray())
        {
            double[] v = [.. row.EnumerateArray().Select(x => x.GetDouble())];
            if (v.Length != 5)
            {
                throw new FormatException("Each camera motion frame is [time, dx, dy, angle, scale].");
            }

            frames.Add(new FrameMotion(MediaTime.FromSeconds(v[0]), v[1], v[2], v[3], v[4]));
        }

        return new CameraMotion(e.GetProperty("width").GetInt32(), e.GetProperty("height").GetInt32(), frames);
    }

    /// <inheritdoc/>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Frames.Count} frames of {Width}x{Height}");
}

/// <summary>The camera's motion into one frame from the one before: about the frame's centre, in its pixels.</summary>
/// <param name="Time">The frame's timestamp.</param>
/// <param name="Dx">The shift to the right in pixels.</param>
/// <param name="Dy">The shift down in pixels.</param>
/// <param name="Angle">The clockwise rotation in radians.</param>
/// <param name="Scale">The zoom factor, 1 for none.</param>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Editing.cs" region="edit-stabilize"/>
/// </example>
public readonly record struct FrameMotion(MediaTime Time, double Dx, double Dy, double Angle, double Scale);

/// <summary>What <see cref="Clip.Stabilize"/> does with the edges a correction uncovers.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Editing.cs" region="edit-stabilize"/>
/// </example>
public enum StabilizeEdges
{
    /// <summary>Zooms in the least amount that hides every uncovered edge over the whole clip, at most 1.5 times. The default.</summary>
    Zoom,

    /// <summary>Leaves the frame's size and fills uncovered edges with black.</summary>
    Black,

    /// <summary>Leaves the frame's size and fills uncovered edges with a blurred copy of the frame.</summary>
    Blur,
}

/// <summary>How <see cref="Clip.Stabilize"/> smooths the camera path and treats the edges.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Editing.cs" region="edit-stabilize"/>
/// </example>
public sealed record StabilizeOptions
{
    /// <summary>Gets the defaults: a Gaussian over 15 frames either way, edges hidden by zooming.</summary>
    public static StabilizeOptions Default { get; } = new();

    /// <summary>Gets how many frames either way the camera path is smoothed over: more keeps slow intended motion only. Default 15.</summary>
    public int Smoothing { get; init; } = 15;

    /// <summary>Gets what happens to uncovered edges. Default <see cref="StabilizeEdges.Zoom"/>.</summary>
    public StabilizeEdges Edges { get; init; }

    /// <summary>
    /// Gets the most <see cref="StabilizeEdges.Zoom"/> zooms in to hide the edges, as a factor of at least 1; a frame that needs
    /// more keeps some edge showing. Default 1.5, like vidstab's <c>maxzoom</c> of 50 %.
    /// </summary>
    public double MaxZoom { get; init; } = 1.5;
}

/// <summary>How <see cref="Stabilizer.Analyze(string, MotionAnalysisOptions?, IProgress{MediaProgress}?, CancellationToken)"/> measures.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Editing.cs" region="edit-stabilize"/>
/// </example>
public sealed record MotionAnalysisOptions
{
    /// <summary>Gets the defaults.</summary>
    public static MotionAnalysisOptions Default { get; } = new();

    /// <summary>Gets the width the luma is downscaled to, at most, before measuring: smaller is faster, larger finer. Default 480.</summary>
    public int AnalysisWidth { get; init; } = 480;
}
