namespace Egami.Formats.Psd;

/// <summary>
/// The flattener's knockout: a layer that knocks out draws onto the backdrop its group started from (shallow) or the Background
/// layer (deep) instead of the layers under it in its group, and cuts through them by its shape and opacity; its fill opacity
/// thins what it draws, so a layer with no fill leaves a hole showing that backdrop.
/// </summary>
internal sealed partial class PsdFlattener
{
    private float[]? _background;

    /// <summary>Blends a frame's waiting layer that knocks out onto the frame's canvas.</summary>
    private void Knock(Frame frame, Pending pending)
    {
        Rect area = pending.Area;
        if (area.Width <= 0 || area.Height <= 0 || pending.Clip is not float[] shape)
        {
            return;
        }

        // A shallow knockout in a pass-through group reaches the canvas under the group, which stays as it was while the group
        // is open; in an isolated group it reaches transparency. At the top level, and for a deep knockout, it reaches the
        // Background layer, or transparency in a document without one.
        float[]? backdrop = pending.Record.Knockout == 1 && frame.Info is not null
            ? (frame.PassThrough ? _frames[^2].Canvas : null)
            : _background;
        float[] drawn = Scratch();
        for (int y = area.Y; y < area.Bottom; y++)
        {
            int at = _stride * ((y * _width) + area.X);
            Span<float> row = drawn.AsSpan(at, _stride * area.Width);
            if (backdrop is null)
            {
                row.Clear();
            }
            else
            {
                backdrop.AsSpan(at, _stride * area.Width).CopyTo(row);
            }
        }

        // The shape cuts through and the layer's content, at its fill alone, draws over the backdrop within it: the layer's
        // pixels are divided by their coverage, which the mix below applies once.
        float[] content = pending.Buffer!;
        for (int y = area.Y; y < area.Bottom; y++)
        {
            for (int x = area.X; x < area.Right; x++)
            {
                float s = shape[(y * _width) + x];
                int at = _stride * ((y * _width) + x);
                for (int c = 0; c < _stride; c++)
                {
                    content[at + c] = s > 0 ? content[at + c] / s : 0;
                }
            }
        }

        BlendDown(content, area, pending.Info with { Opacity = 1 }, pending.Record.BlendRanges, drawn);
        float opacity = pending.Info.Opacity;
        float[] canvas = frame.Canvas;
        for (int y = area.Y; y < area.Bottom; y++)
        {
            for (int x = area.X; x < area.Right; x++)
            {
                float t = shape[(y * _width) + x] * opacity;
                int at = _stride * ((y * _width) + x);
                for (int c = 0; c < _stride; c++)
                {
                    canvas[at + c] += (drawn[at + c] - canvas[at + c]) * t;
                }
            }
        }

        Clear(drawn, area);
    }

    /// <summary>Keeps the canvas as it is once the Background layer is flattened in, for deep knockouts.</summary>
    private void KeepBackground(Frame frame, Pending pending)
    {
        if (_knocks && frame.Info is null && _frames.Count == 1 && _background is null && IsBackground(pending.Record))
        {
            _background = Allocate((long)_stride * _width * _height);
            frame.Canvas.AsSpan(0, _stride * _width * _height).CopyTo(_background);
        }
    }

    /// <summary>Returns whether a record is the document's Background layer: the bottom one, without transparency.</summary>
    private bool IsBackground(PsdLayerRecord record) =>
        _file.Records.Count > 0 && ReferenceEquals(_file.Records[0], record) && record.Info.Kind == PsdLayerKind.Layer && !Array.Exists(record.Channels, c => c.Id == -1);
}
