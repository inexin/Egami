using Egami.Operations;
using Egami.Operations.Compose;

namespace Egami.Formats.Psd;

/// <summary>The flattener's vector masks and adjustment layers.</summary>
internal sealed partial class PsdFlattener
{
    private PsdLayerRecord? _vectorFor;
    private float[]? _vectorPlane;

    /// <summary>Multiplies a row of coverage from canvas column <paramref name="x0"/> by the layer's vector mask, thinned by its density.</summary>
    private void VectorRow(PsdLayerRecord record, int y, int x0, Span<float> shape)
    {
        if (!ReferenceEquals(_vectorFor, record))
        {
            _vectorFor = record;
            _vectorPlane = record.Blocks.TryGetValue("vmsk", out byte[]? data) || record.Blocks.TryGetValue("vsms", out data)
                ? PsdVectorMask.Rasterize(data, _width, _height)
                : null;
        }

        if (_vectorPlane is not float[] plane)
        {
            return;
        }

        float density = record.VectorMaskDensity / 255f;
        int at = (y * _width) + x0;
        for (int i = 0; i < shape.Length; i++)
        {
            shape[i] *= 1 - (density * (1 - plane[at + i]));
        }
    }

    /// <summary>Applies an adjustment layer to the canvas under it, or to its clipping base's content when it is clipped.</summary>
    /// <remarks>
    /// The adjusted color is the source of a blend with the layer's mode over the color under it, taken as opaque, through the
    /// layer's masks, opacity and fill opacity; alpha does not change.
    /// </remarks>
    private void Adjust(PsdLayer info, PsdLayerRecord record, PsdMask? mask, Pending? basis, Frame frame)
    {
        if (!_options.ApplyAdjustments)
        {
            return;
        }

        if (PsdAdjustment.Of(record, IsGray(_file)) is not PsdAdjustment adjustment)
        {
            Skip("Psd.AdjustmentSkipped", info.Name);
            return;
        }

        Note(info, record);
        float[] target = basis?.Buffer ?? frame.Canvas;
        Rect area = basis?.Area ?? new Rect(0, 0, _width, _height);
        BlendMode mode = ModeOf(info);
        float opacity = info.Opacity * info.FillOpacity;
        bool gray = IsGray(_file);
        Span<float> shape = (_shape ??= new float[_width]).AsSpan(0, area.Width);
        for (int y = area.Y; y < area.Bottom; y++)
        {
            MaskRow(record, mask, y, area.X, shape);
            for (int x = 0; x < area.Width; x++)
            {
                int at = _stride * ((y * _width) + area.X + x);
                float a = target[at + _channels], t = shape[x] * opacity;
                if (a <= 0 || t <= 0)
                {
                    continue;
                }

                float r, g, b;
                if (_channels == 4)
                {
                    // CMYK adjusts through RGB by the plain formula, black kept.
                    (r, g, b) = (1 - Math.Clamp(target[at] / a, 0, 1), 1 - Math.Clamp(target[at + 1] / a, 0, 1), 1 - Math.Clamp(target[at + 2] / a, 0, 1));
                }
                else
                {
                    (r, g, b) = (target[at] / a, target[at + 1] / a, target[at + 2] / a);
                }

                float ar = r, ag = g, ab = b;
                adjustment.Apply(ref ar, ref ag, ref ab);
                if (gray)
                {
                    ar = ag = ab = (0.3f * ar) + (0.59f * ag) + (0.11f * ab);
                }

                if (mode != BlendMode.SourceOver)
                {
                    (double R, double G, double B, double A) m = BlendReference.Blend(mode, (ar, ag, ab, 1), (r, g, b, 1), 1, area.X + x, y);
                    (ar, ag, ab) = ((float)m.R, (float)m.G, (float)m.B);
                }

                (ar, ag, ab) = (r + ((ar - r) * t), g + ((ag - g) * t), b + ((ab - b) * t));
                if (_channels == 4)
                {
                    (ar, ag, ab) = (1 - Math.Clamp(ar, 0, 1), 1 - Math.Clamp(ag, 0, 1), 1 - Math.Clamp(ab, 0, 1));
                }

                target[at] = ar * a;
                target[at + 1] = ag * a;
                target[at + 2] = ab * a;
            }
        }
    }
}
