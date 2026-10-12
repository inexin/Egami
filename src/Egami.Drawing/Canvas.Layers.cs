using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing;

public abstract partial class Canvas
{
    private static readonly LayerOptions DefaultLayer = new();
    private static readonly ShadowOptions DefaultShadow = new();

    /// <summary>
    /// Starts a layer: later drawing goes into a temporary image, which the matching <see cref="Restore"/> filters, masks and
    /// merges into what lies below with the layer's opacity and blend mode, through the clip of this call. That gives group opacity
    /// (two overlapping shapes at half opacity look like one), group blend modes, filters on a group and soft masks, which PDF calls
    /// transparency groups. Like <see cref="Save"/>, it also saves the transform and clip.
    /// </summary>
    /// <param name="options">How the layer is merged; an isolated, opaque, source-over layer when <see langword="null"/>.</param>
    /// <returns>The save count before this call, to pass to <see cref="RestoreToCount"/>.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="layers"/>
    /// </example>
    public int SaveLayer(LayerOptions? options = null)
    {
        CheckLive();
        options ??= DefaultLayer;
        int count = SaveCount;
        _saved.Push(_state);
        RectF clip = _state.ClipBounds;
        if (options.Bounds is RectF bounds)
        {
            clip = bounds.IsEmpty ? RectF.Empty : clip.Intersect(MapRect(bounds, _state.Matrix));
        }

        _state = _state with { ClipBounds = clip, IsLayer = true };
        OnSaveLayer(options);
        return count;
    }

    /// <summary>Starts a layer merged with an opacity: group opacity, the most common layer.</summary>
    /// <param name="opacity">The opacity the layer is merged with, 0 to 1.</param>
    /// <returns>The save count before this call.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="opacity"/> is outside 0 to 1.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="layers"/>
    /// </example>
    public int SaveLayer(float opacity) => SaveLayer(new LayerOptions { Opacity = opacity });

    /// <summary>
    /// Called by <see cref="SaveLayer(LayerOptions?)"/> after the state is pushed; <see cref="DeviceClipBounds"/> is already limited
    /// by the options' bounds. The raster backend allocates the layer; a PDF backend would begin a transparency group. By default it
    /// calls <see cref="OnSave"/>, so a backend without layers draws their contents directly.
    /// </summary>
    /// <param name="options">How the layer is merged.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="layers"/>
    /// </example>
    protected virtual void OnSaveLayer(LayerOptions options) => OnSave();

    /// <summary>
    /// Called by <see cref="Restore"/> for a state saved by <see cref="SaveLayer(LayerOptions?)"/>, after it is popped, so
    /// <see cref="Matrix"/> and <see cref="DeviceClipBounds"/> are those the layer was started with: the raster backend merges the
    /// layer through that clip. By default it calls <see cref="OnRestore"/>.
    /// </summary>
    protected virtual void OnRestoreLayer() => OnRestore();

    /// <summary>
    /// Draws the shadow a shape casts when it floats above the canvas, after Skia's shadow utilities: an ambient shadow (the shape
    /// grown and blurred by its elevation) and a spot shadow (the shape moved away from the light and scaled, blurred by the light's
    /// size), each in its own color. Draw the shape itself afterwards. Every backend gets it through ordinary calls: a layer with a
    /// blur filter for the ambient shadow and a blurred fill for the spot shadow.
    /// </summary>
    /// <param name="path">The shape, in local coordinates.</param>
    /// <param name="options">The elevation, light and colors; Material Design's defaults when <see langword="null"/>.</param>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Paints.cs" region="shadows"/>
    /// </example>
    public void DrawShadow(Path path, ShadowOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        CheckLive();
        options ??= DefaultShadow;
        float z = options.Elevation;
        if (path.IsEmpty || z <= 0)
        {
            return;
        }

        Vector3 light = options.LightPosition ?? new Vector3(Width / 2, 0, 600);
        int count = Save();
        try
        {
            if (!options.Transparent)
            {
                ClipPath(path, ClipOp.Difference);
            }

            // Ambient: the shape grown by half a stroke and blurred, both from the elevation, as Skia's blur-based shadow does.
            RgbaF32 ambient = options.AmbientColor;
            if (ambient.A > 0)
            {
                float outset = MathF.Min(z * AmbientHeightFactor * AmbientGeometryFactor, MaxAmbientRadius);
                float blurRadius = 0.5f * outset * (1 + MathF.Max(z * AmbientHeightFactor, 0));
                float strokeWidth = 0.5f * (outset - blurRadius);
                float sigma = RadiusToSigma(blurRadius);
                SolidPaint opaque = new(new RgbaF32(ambient.R, ambient.G, ambient.B, 1));
                SaveLayer(new LayerOptions
                {
                    Opacity = Math.Clamp(ambient.A, 0, 1),
                    Filter = sigma > 0 ? o => o.Blur(sigma, ColorSpace.Encoded, BlurMethod.Fast) : null,
                });
                FillPath(path, opaque);
                float scale = MathF.Sqrt(MathF.Abs(Matrix.GetDeterminant()));
                if (strokeWidth > 0 && scale > 0)
                {
                    StrokePath(path, new Stroke(strokeWidth / scale), opaque);
                }

                Restore();
            }

            // Spot: the shape projected from the light onto the canvas, blurred by the light's size.
            RgbaF32 spot = options.SpotColor;
            if (spot.A > 0)
            {
                float zRatio = light.Z > z ? Math.Clamp(z / (light.Z - z), 0, 0.95f) : 0.95f;
                float spotScale = light.Z > z ? Math.Clamp(light.Z / (light.Z - z), 1, 1.95f) : 1.95f;
                float sigma = RadiusToSigma(options.LightRadius * zRatio);
                SetMatrix(Matrix * Matrix3x2.CreateScale(spotScale) * Matrix3x2.CreateTranslation(-zRatio * light.X, -zRatio * light.Y));
                FillPath(path, new SolidPaint(spot) { Blur = sigma > 0 ? new MaskBlur(MathF.Min(sigma, MaskBlur.MaxSigma)) : null });
            }
        }
        finally
        {
            RestoreToCount(count);
        }
    }

    /// <summary>Skia's ambient shadow constants: the blur grows by 1/128 of the elevation, times 64, up to 300 · 64 / 128.</summary>
    private const float AmbientHeightFactor = 1 / 128f;

    private const float AmbientGeometryFactor = 64;

    private const float MaxAmbientRadius = 300 * AmbientHeightFactor * AmbientGeometryFactor;

    /// <summary>Skia's conversion from a blur radius to a Gaussian's sigma: 1/√3 of the radius plus a half.</summary>
    private static float RadiusToSigma(float radius) => radius > 0 ? MathF.Min((0.57735f * radius) + 0.5f, MaskBlur.MaxSigma) : 0;
}
