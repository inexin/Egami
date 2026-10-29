using System.ComponentModel;
using System.Numerics;

using Egami.GdiPlus;
using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing;

/// <summary>The pen settings beyond width and brush: caps, joins, dashes, alignment, compound lines and the transform.</summary>
public sealed partial class Pen
{
    private LineCap _startCap;
    private LineCap _endCap;
    private DashCap _dashCap;
    private LineJoin _join;
    private float _miterLimit = 10;
    private DashStyle _dashStyle;
    private float _dashOffset;
    private float[]? _dashPattern;
    private PenAlignment _alignment;
    private float[] _compound = [];
    private CustomLineCap? _customStart;
    private CustomLineCap? _customEnd;
    private Matrix3x2 _transform = Matrix3x2.Identity;

    /// <summary>Gets or sets how the line starts.</summary>
    public LineCap StartCap
    {
        get => _startCap;
        set => _startCap = Mutable(Cap(value));
    }

    /// <summary>Gets or sets how the line ends.</summary>
    public LineCap EndCap
    {
        get => _endCap;
        set => _endCap = Mutable(Cap(value));
    }

    /// <summary>Gets or sets how each dash ends; triangle dash caps are drawn flat.</summary>
    public DashCap DashCap
    {
        get => _dashCap;
        set => _dashCap = Mutable(value is DashCap.Flat or DashCap.Round or DashCap.Triangle ? value : throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(DashCap)));
    }

    /// <summary>Gets or sets how segments meet.</summary>
    public LineJoin LineJoin
    {
        get => _join;
        set => _join = Mutable(value);
    }

    /// <summary>Gets or sets how far a mitered corner reaches, in half widths, before it is beveled; at least 1.</summary>
    public float MiterLimit
    {
        get => _miterLimit;
        set => _miterLimit = Mutable(Math.Max(1, value));
    }

    /// <summary>Gets or sets the dash style; setting <see cref="DashPattern"/> makes it <see cref="DashStyle.Custom"/>.</summary>
    public DashStyle DashStyle
    {
        get => _dashStyle;
        set
        {
            CheckMutable();
            DashStyle previous = _dashStyle;
            _dashStyle = value is >= DashStyle.Solid and <= DashStyle.Custom ? value : throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(DashStyle));
            // A custom style keeps the pattern of the style before it, as in GDI+.
            if (value == DashStyle.Custom && _dashPattern is null && previous is not DashStyle.Solid and not DashStyle.Custom)
            {
                _dashPattern = [.. GdiPlusPens.DashArray(new GdiPlusPen { DashStyle = (int)previous })!.Take(Pattern(previous))];
            }

            _dashPattern ??= value == DashStyle.Custom ? [1] : _dashPattern;
        }
    }

    /// <summary>Gets or sets where the dash pattern starts, in multiples of the width.</summary>
    public float DashOffset
    {
        get => _dashOffset;
        set => _dashOffset = Mutable(value);
    }

#pragma warning disable CA1819 // System.Drawing's Pen exposes arrays.
    /// <summary>Gets or sets the dash and gap lengths, in multiples of the width: the style's own pattern, or a custom one.</summary>
    /// <exception cref="ArgumentException">A length is zero or less, or the pattern is empty.</exception>
    /// <exception cref="InvalidOperationException">The pen is solid, as in GDI+.</exception>
    public float[] DashPattern
    {
        get => _dashStyle switch
        {
            DashStyle.Custom => (float[])_dashPattern!.Clone(),
            DashStyle.Solid => throw new InvalidOperationException("This operation is not supported for a solid pen."),
            _ => [.. GdiPlusPens.DashArray(new GdiPlusPen { DashStyle = (int)_dashStyle })!.Take(Pattern(_dashStyle))],
        };
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            CheckMutable();
            if (value.Length == 0 || Array.Exists(value, v => !(v > 0)))
            {
                throw GdiErrors.InvalidParameter();
            }

            _dashPattern = (float[])value.Clone();
            _dashStyle = DashStyle.Custom;
        }
    }

    /// <summary>Gets or sets the positions across the width, from 0 to 1 in increasing pairs, of the strips a compound line draws.</summary>
    /// <exception cref="ArgumentException">The positions are not increasing pairs from 0 to 1.</exception>
    public float[] CompoundArray
    {
        get => (float[])_compound.Clone();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            CheckMutable();
            if (value.Length == 0 || value.Length % 2 != 0 || Array.Exists(value, v => v is < 0 or > 1))
            {
                throw GdiErrors.InvalidParameter();
            }

            for (int i = 1; i < value.Length; i++)
            {
                if (value[i] < value[i - 1])
                {
                    throw GdiErrors.InvalidParameter();
                }
            }

            _compound = (float[])value.Clone();
        }
    }
#pragma warning restore CA1819

    /// <summary>Gets or sets where the pen lies along a closed outline; GDI+ draws everything but <see cref="PenAlignment.Inset"/> centered.</summary>
    public PenAlignment Alignment
    {
        get => _alignment;
        set => _alignment = Mutable(value is >= PenAlignment.Center and <= PenAlignment.Right ? value : throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(PenAlignment)));
    }

    /// <summary>Gets or sets a copy of the cap drawn at the start; setting it makes <see cref="StartCap"/> custom.</summary>
    public CustomLineCap CustomStartCap
    {
        get => (CustomLineCap)(_customStart ?? throw GdiErrors.InvalidParameter()).Clone();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            CheckMutable();
            _customStart = (CustomLineCap)value.Clone();
            _startCap = LineCap.Custom;
        }
    }

    /// <summary>Gets or sets a copy of the cap drawn at the end; setting it makes <see cref="EndCap"/> custom.</summary>
    public CustomLineCap CustomEndCap
    {
        get => (CustomLineCap)(_customEnd ?? throw GdiErrors.InvalidParameter()).Clone();
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            CheckMutable();
            _customEnd = (CustomLineCap)value.Clone();
            _endCap = LineCap.Custom;
        }
    }

    /// <summary>Gets the kind of brush the pen paints with.</summary>
    public PenType PenType => _brush switch
    {
        HatchBrush => PenType.HatchFill,
        TextureBrush => PenType.TextureFill,
        PathGradientBrush => PenType.PathGradient,
        LinearGradientBrush => PenType.LinearGradient,
        _ => PenType.SolidColor,
    };

    /// <summary>Gets or sets a copy of the pen's own transform, which shapes its width.</summary>
    /// <exception cref="ArgumentException">The transform cannot be inverted.</exception>
    public Matrix Transform
    {
        get => new(_transform);
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            CheckMutable();
            _transform = Matrix3x2.Invert(value.MatrixElements, out _) ? value.MatrixElements : throw GdiErrors.InvalidParameter();
        }
    }

    /// <summary>Sets both line caps and the dash cap.</summary>
    /// <param name="startCap">The start cap.</param>
    /// <param name="endCap">The end cap.</param>
    /// <param name="dashCap">The dash cap.</param>
    public void SetLineCap(LineCap startCap, LineCap endCap, DashCap dashCap)
    {
        StartCap = startCap;
        EndCap = endCap;
        DashCap = dashCap;
    }

    /// <summary>Makes the pen's transform the identity.</summary>
    public void ResetTransform() => _transform = Mutable(Matrix3x2.Identity);

    /// <summary>Multiplies the pen's transform by a matrix, applied before it.</summary>
    /// <param name="matrix">The matrix.</param>
    public void MultiplyTransform(Matrix matrix) => MultiplyTransform(matrix, MatrixOrder.Prepend);

    /// <summary>Multiplies the pen's transform by a matrix.</summary>
    /// <param name="matrix">The matrix.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void MultiplyTransform(Matrix matrix, MatrixOrder order)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        Apply(matrix.MatrixElements, order);
    }

    /// <summary>Translates the pen's transform, which does not change how it draws.</summary>
    /// <param name="dx">The horizontal distance.</param>
    /// <param name="dy">The vertical distance.</param>
    public void TranslateTransform(float dx, float dy) => TranslateTransform(dx, dy, MatrixOrder.Prepend);

    /// <summary>Translates the pen's transform, which does not change how it draws.</summary>
    /// <param name="dx">The horizontal distance.</param>
    /// <param name="dy">The vertical distance.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void TranslateTransform(float dx, float dy, MatrixOrder order) => Apply(Matrix3x2.CreateTranslation(dx, dy), order);

    /// <summary>Scales the pen's transform, and so its width.</summary>
    /// <param name="sx">The horizontal factor.</param>
    /// <param name="sy">The vertical factor.</param>
    public void ScaleTransform(float sx, float sy) => ScaleTransform(sx, sy, MatrixOrder.Prepend);

    /// <summary>Scales the pen's transform, and so its width.</summary>
    /// <param name="sx">The horizontal factor.</param>
    /// <param name="sy">The vertical factor.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void ScaleTransform(float sx, float sy, MatrixOrder order) => Apply(Matrix3x2.CreateScale(sx, sy), order);

    /// <summary>Rotates the pen's transform.</summary>
    /// <param name="angle">The angle in degrees.</param>
    public void RotateTransform(float angle) => RotateTransform(angle, MatrixOrder.Prepend);

    /// <summary>Rotates the pen's transform.</summary>
    /// <param name="angle">The angle in degrees.</param>
    /// <param name="order">Whether it applies before or after.</param>
    public void RotateTransform(float angle, MatrixOrder order) => Apply(Matrix.Rotation(angle), order);

    private static int Pattern(DashStyle style) => style switch
    {
        DashStyle.Dash or DashStyle.Dot => 2,
        DashStyle.DashDot => 4,
        _ => 6,
    };

    private static LineCap Cap(LineCap value) => value is LineCap.Flat or LineCap.Square or LineCap.Round or LineCap.Triangle or LineCap.NoAnchor
        or LineCap.SquareAnchor or LineCap.RoundAnchor or LineCap.DiamondAnchor or LineCap.ArrowAnchor or LineCap.Custom or LineCap.AnchorMask
        ? value : throw new InvalidEnumArgumentException(nameof(value), (int)value, typeof(LineCap));

    private void Apply(Matrix3x2 m, MatrixOrder order)
    {
        CheckMutable();
        _transform = order == MatrixOrder.Append ? _transform * m : m * _transform;
    }

    private T Mutable<T>(T value)
    {
        CheckMutable();
        return value;
    }

    private partial GdiPlusPen GeometryCore(GdiPlusPen pen) => new()
    {
        Width = pen.Width,
        Transform = _transform,
        StartCap = (int)_startCap,
        EndCap = (int)_endCap,
        Join = (int)_join,
        MiterLimit = _miterLimit,
        DashStyle = (int)_dashStyle,
        DashCap = (int)_dashCap,
        DashOffset = _dashOffset,
        Dashes = _dashStyle == DashStyle.Custom ? _dashPattern : null,
        Alignment = _alignment == PenAlignment.Inset ? GdiPlusPen.AlignInset : GdiPlusPen.AlignCenter,
        Compound = _compound.Length >= 2 ? _compound : null,
        CustomStartCap = _startCap == LineCap.Custom ? _customStart?.Geometry() : null,
        CustomEndCap = _endCap == LineCap.Custom ? _customEnd?.Geometry() : null,
    };

    private partial void CopyTo(Pen copy)
    {
        copy._startCap = _startCap;
        copy._endCap = _endCap;
        copy._dashCap = _dashCap;
        copy._join = _join;
        copy._miterLimit = _miterLimit;
        copy._dashStyle = _dashStyle;
        copy._dashOffset = _dashOffset;
        copy._dashPattern = (float[]?)_dashPattern?.Clone();
        copy._alignment = _alignment;
        copy._compound = (float[])_compound.Clone();
        copy._customStart = (CustomLineCap?)_customStart?.Clone();
        copy._customEnd = (CustomLineCap?)_customEnd?.Clone();
        copy._transform = _transform;
    }
}
