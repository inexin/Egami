using System.Diagnostics.CodeAnalysis;

using Egami.Video.Filters;

namespace Egami.Video.Subtitles.Rendering;

/// <summary>
/// A video filter that burns subtitles into the frames passing through it, as FFmpeg's <c>subtitles</c> filter does: each
/// frame comes out as a copy with the cues showing at its time drawn into its planes. It holds no frames back.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/SubtitleBurnIn.cs" region="burnin-filter"/>
/// </example>
public sealed class SubtitleBurnIn : IVideoFilter
{
    private readonly SubtitleRenderer _renderer;
    private readonly bool _ownsRenderer;
    private readonly Queue<YuvVideoFrame> _ready = new();
    private bool _disposed;

    /// <summary>Creates a filter that draws with a renderer, which the caller keeps and disposes.</summary>
    /// <param name="renderer">The renderer.</param>
    /// <exception cref="ArgumentNullException"><paramref name="renderer"/> is <see langword="null"/>.</exception>
    public SubtitleBurnIn(SubtitleRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _renderer = renderer;
    }

    /// <summary>Creates a filter with a renderer of its own for a document, disposed with the filter.</summary>
    /// <param name="document">The subtitles.</param>
    /// <param name="options">Where fonts come from and how styles change, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">Where warnings go, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/SubtitleBurnIn.cs" region="burnin-filter"/>
    /// </example>
    public SubtitleBurnIn(SubtitleDocument document, SubtitleRenderOptions? options = null, MediaContext? context = null)
    {
        _renderer = new SubtitleRenderer(document, options, context);
        _ownsRenderer = true;
    }

    /// <summary>Gets the renderer the filter draws with.</summary>
    public SubtitleRenderer Renderer => _renderer;

    /// <inheritdoc/>
    public MediaTime Latency => MediaTime.Zero;

    /// <inheritdoc/>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">The filter is disposed.</exception>
    public void Send(YuvVideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        YuvRaster copy = frame.Raster.Crop(new Rect(0, 0, frame.Width, frame.Height));
        var output = new YuvVideoFrame(copy, frame.Timestamp, frame.Duration, frame.Color)
        {
            Hdr = frame.Hdr,
            Hdr10Plus = frame.Hdr10Plus,
            RenderSize = frame.RenderSize,
            FieldOrder = frame.FieldOrder,
            RepeatFields = frame.RepeatFields,
        };
        _renderer.Draw(output);
        _ready.Enqueue(output);
    }

    /// <inheritdoc/>
    public bool TryReceive([NotNullWhen(true)] out YuvVideoFrame? frame) => _ready.TryDequeue(out frame);

    /// <inheritdoc/>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc/>
    public void Reset()
    {
        while (_ready.TryDequeue(out YuvVideoFrame? frame))
        {
            frame.Dispose();
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Reset();
        if (_ownsRenderer)
        {
            _renderer.Dispose();
        }
    }
}
