using System.Collections;

using Egami.Video.Audio;

namespace Egami.Video.Editing;

/// <summary>One clip on an <see cref="EditTrack"/> and the transition into it from the clip before.</summary>
/// <param name="Clip">The clip.</param>
/// <param name="Transition">The transition from the previous clip, or <see langword="null"/> for a cut. Ignored on the first clip.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-transitions"/>
/// </example>
public sealed record TrackItem(Clip Clip, Transition? Transition = null);

/// <summary>
/// The main track of a <see cref="VideoEdit"/>: clips that play one after another. A <see cref="Transition"/> between two clips
/// overlaps them by its length; without one, the next clip starts on the frame after the last one ends.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-join"/>
/// </example>
public sealed class EditTrack : IReadOnlyList<TrackItem>
{
    private readonly List<TrackItem> _items = [];

    /// <summary>Gets the number of clips.</summary>
    public int Count => _items.Count;

    /// <summary>Gets a clip and its transition.</summary>
    /// <param name="index">The position on the track.</param>
    /// <returns>The item.</returns>
    public TrackItem this[int index] => _items[index];

    /// <summary>Adds a clip at the end of the track.</summary>
    /// <param name="clip">The clip.</param>
    /// <param name="transition">The transition from the clip before, or <see langword="null"/> for a cut.</param>
    /// <returns>This track, to add more.</returns>
    public EditTrack Add(Clip clip, Transition? transition = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        _items.Add(new TrackItem(clip, transition));
        return this;
    }

    /// <summary>Replaces a clip and its transition.</summary>
    /// <param name="index">The position on the track.</param>
    /// <param name="item">The new item.</param>
    public void Set(int index, TrackItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _items[index] = item;
    }

    /// <summary>Removes a clip.</summary>
    /// <param name="index">The position on the track.</param>
    public void RemoveAt(int index) => _items.RemoveAt(index);

    /// <summary>Removes every clip.</summary>
    public void Clear() => _items.Clear();

    /// <inheritdoc/>
    public IEnumerator<TrackItem> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>How <see cref="VideoEdit.Render(string, EditRenderOptions?, IProgress{MediaProgress}?, CancellationToken)"/> encodes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-join"/>
/// </example>
public sealed record EditRenderOptions
{
    /// <summary>Gets the defaults.</summary>
    public static EditRenderOptions Default { get; } = new();

    /// <summary>
    /// Gets the encoder settings: video codec, quality, rate control and the audio codec. The frame rate, audio rate and audio
    /// layout come from the canvas. Default Motion JPEG, as for <see cref="VideoWriter"/>.
    /// </summary>
    public VideoWriterOptions Writer { get; init; } = new();

    /// <summary>
    /// Gets whether to write an audio track when a clip or layer has audio. With no <see cref="VideoWriterOptions.AudioCodec"/>
    /// set, Opus is used, or PCM in AVI. Default <see langword="true"/>.
    /// </summary>
    public bool Audio { get; init; } = true;

    /// <summary>Gets the context frames are decoded, composed and encoded with, such as one with fewer threads, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</summary>
    public MediaContext? Context { get; init; }
}

/// <summary>
/// An edit: clips on a main <see cref="Track"/> with transitions between them, <see cref="Layers"/> drawn over it, and
/// <see cref="Effects"/> on the result, all fitted to one <see cref="Canvas"/>. It is a description: <see cref="Plan"/> lays
/// out the timeline and checks it, and <see cref="Render(string, EditRenderOptions?, IProgress{MediaProgress}?, CancellationToken)"/>
/// decodes, composes and encodes it in one pass, holding only the frames in use. Save it as JSON with <see cref="ToJson"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-join"/>
/// </example>
public sealed class VideoEdit
{
    /// <summary>Creates an empty edit.</summary>
    /// <param name="canvas">The output size, rate and audio format, or <see langword="null"/> to take them from the first clip.</param>
    public VideoEdit(EditCanvas? canvas = null)
    {
        Canvas = canvas;
    }

    /// <summary>Gets or sets the output size, rate and audio format, or <see langword="null"/> to take them from the first clip.</summary>
    public EditCanvas? Canvas { get; set; }

    /// <summary>Gets the main track.</summary>
    public EditTrack Track { get; } = new();

    /// <summary>Gets the layers, drawn in order over the main track.</summary>
    public IList<Layer> Layers { get; } = [];

    /// <summary>Gets the effects that run on each composed frame, timed from the edit's start.</summary>
    public IList<EditEffect> Effects { get; } = [];

    /// <summary>
    /// Lays out the timeline: opens each clip to read its size, rate, length and audio, and works out where every clip,
    /// transition and layer falls, in whole frames. Problems that would make the render fail, such as a trim past a clip's end
    /// or a transition longer than a clip, throw; smaller ones become warnings.
    /// </summary>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The plan.</returns>
    /// <exception cref="InvalidOperationException">The edit cannot render; the message lists why.</exception>
    public EditPlan Plan(MediaContext? context = null) => EditPlanner.Plan(this, context ?? MediaContext.Default);

    /// <summary>Renders the edit to a file whose extension names the container.</summary>
    /// <param name="path">The output file.</param>
    /// <param name="options">The encoder settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="progress">Receives progress, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the render.</param>
    /// <returns>The plan the render followed.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-join"/>
    /// </example>
    public EditPlan Render(string path, EditRenderOptions? options = null, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Render(path, null, null, options, progress, cancellationToken);
    }

    /// <summary>Renders the edit to a stream.</summary>
    /// <param name="stream">The output.</param>
    /// <param name="formatNameOrExtension">The container, such as <c>webm</c> or <c>.mkv</c>.</param>
    /// <param name="options">The encoder settings, or <see langword="null"/> for the defaults.</param>
    /// <param name="progress">Receives progress, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the render.</param>
    /// <returns>The plan the render followed.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-join"/>
    /// </example>
    public EditPlan Render(Stream stream, string formatNameOrExtension, EditRenderOptions? options = null, IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrEmpty(formatNameOrExtension);
        return Render(null, stream, formatNameOrExtension, options, progress, cancellationToken);
    }

    /// <summary>Returns the edit as JSON: the CLI's edit file format.</summary>
    /// <returns>The JSON text.</returns>
    /// <exception cref="NotSupportedException">A clip reads a stream, a transition is an app's own, or an effect has no <see cref="EditEffect.Name"/>.</exception>
    public string ToJson() => EditJson.Write(this);

    /// <summary>Reads an edit from JSON written by <see cref="ToJson"/> or by hand.</summary>
    /// <param name="json">The JSON text.</param>
    /// <param name="effects">Turns an effect's <see cref="EditEffect.Name"/> back into an effect, or <see langword="null"/> when the JSON has no effects.</param>
    /// <param name="folder">The folder relative clip paths are taken from, such as the edit file's own, or <see langword="null"/> to leave them as they are.</param>
    /// <returns>The edit.</returns>
    /// <exception cref="FormatException">The JSON is not an edit; the message names the place.</exception>
    public static VideoEdit FromJson(string json, Func<string, EditEffect>? effects = null, string? folder = null) => EditJson.Read(json, effects, folder);

    /// <summary>
    /// Renders the edit's pictures one at a time without encoding, for a preview or an app's own encoder: each comes as a new
    /// frame with its time, which the caller disposes. The sound is left out; <see cref="Render(Stream, string, EditRenderOptions?, IProgress{MediaProgress}?, CancellationToken)"/> writes both.
    /// </summary>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The frames, in order.</returns>
    /// <exception cref="InvalidOperationException">The edit cannot render; the message lists why.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Editing.cs" region="edit-read-frames"/>
    /// </example>
    public IEnumerable<VideoFrame<Egami.Pixels.Rgba8>> ReadFrames(MediaContext? context = null)
    {
        EditPlan plan = Plan(context);
        long n = 0;
        foreach (Egami.Raster<Egami.Pixels.Rgba8> frame in RenderFrames(context))
        {
            yield return new VideoFrame<Egami.Pixels.Rgba8>(frame.Derive(_ => { }), plan.TimeOf(n), plan.TimeOf(1));
            n++;
        }
    }

    /// <summary>Renders the frames without encoding, for tests: each raster is valid until the next one.</summary>
    internal IEnumerable<Egami.Raster<Egami.Pixels.Rgba8>> RenderFrames(MediaContext? context = null)
    {
        EditPlan plan = Plan(context);
        using EditRenderer renderer = new(plan, audio: false, context ?? MediaContext.Default);
        foreach (Egami.Raster<Egami.Pixels.Rgba8> frame in renderer.Frames())
        {
            yield return frame;
        }
    }

    private EditPlan Render(string? path, Stream? stream, string? format, EditRenderOptions? options, IProgress<MediaProgress>? progress, CancellationToken cancellationToken)
    {
        options ??= EditRenderOptions.Default;
        MediaContext context = options.Context ?? MediaContext.Default;
        EditPlan plan = Plan(context);
        EditCanvas canvas = plan.Canvas;
        bool audio = options.Audio && plan.HasAudio;
        VideoWriterOptions writer = options.Writer with { FrameRate = canvas.FrameRate };
        if (audio)
        {
            string extension = (path is null ? format! : System.IO.Path.GetExtension(path)).TrimStart('.').ToLowerInvariant();
            writer = writer with
            {
                AudioCodec = writer.AudioCodec ?? (extension == "avi" ? AudioCodecs.Pcm : AudioCodecs.Opus),
                AudioSampleRate = canvas.SampleRate,
                AudioLayout = canvas.AudioLayout,
            };
        }
        else
        {
            writer = writer with { AudioCodec = null };
        }

        using VideoWriter output = path is not null ? VideoWriter.Create(path, writer, context) : VideoWriter.Create(stream!, format!, writer, context);
        using EditRenderer renderer = new(plan, audio, context);
        renderer.Run(output, progress, cancellationToken);
        output.Finish();
        return plan;
    }
}
