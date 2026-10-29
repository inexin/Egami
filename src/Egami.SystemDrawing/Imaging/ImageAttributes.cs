using Egami.GdiPlus;
using Egami.Pixels;
using Egami.SystemDrawing.Drawing2D;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Imaging;

/// <summary>
/// A 5 by 5 color matrix: a color's red, green, blue, alpha and 1, as a row from 0 to 1, times the matrix, as GDI+ applies it.
/// Starts as the identity.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="images"/>
/// </example>
public sealed class ColorMatrix
{
    private readonly float[] _m = new float[25];

    /// <summary>Initializes a new instance of the <see cref="ColorMatrix"/> class: the identity.</summary>
    public ColorMatrix()
    {
        for (int i = 0; i < 5; i++)
        {
            _m[(i * 5) + i] = 1;
        }
    }

    /// <summary>Initializes a new instance of the <see cref="ColorMatrix"/> class from its rows.</summary>
    /// <param name="newColorMatrix">Up to 5 rows of up to 5 values.</param>
    public ColorMatrix(float[][] newColorMatrix)
    {
        ArgumentNullException.ThrowIfNull(newColorMatrix);
        for (int r = 0; r < Math.Min(5, newColorMatrix.Length); r++)
        {
            for (int c = 0; c < Math.Min(5, newColorMatrix[r]?.Length ?? 0); c++)
            {
                _m[(r * 5) + c] = newColorMatrix[r][c];
            }
        }
    }

    /// <summary>Initializes a new instance of the <see cref="ColorMatrix"/> class from 25 values, row by row.</summary>
    /// <param name="newColorMatrix">The values.</param>
    /// <exception cref="ArgumentException">There are not 25 values.</exception>
    public ColorMatrix(ReadOnlySpan<float> newColorMatrix)
    {
        if (newColorMatrix.Length != 25)
        {
            throw new ArgumentException("A color matrix has 25 values.", nameof(newColorMatrix));
        }

        newColorMatrix.CopyTo(_m);
    }

    /// <summary>Gets or sets a value by row and column, 0 to 4.</summary>
    /// <param name="row">The row.</param>
    /// <param name="column">The column.</param>
    /// <returns>The value.</returns>
    public float this[int row, int column]
    {
        get => _m[Index(row, column)];
        set => _m[Index(row, column)] = value;
    }

    /// <summary>Gets or sets the value in row 0, column 0.</summary>
    public float Matrix00
    {
        get => _m[0];
        set => _m[0] = value;
    }

    /// <summary>Gets or sets the value in row 0, column 1.</summary>
    public float Matrix01
    {
        get => _m[1];
        set => _m[1] = value;
    }

    /// <summary>Gets or sets the value in row 0, column 2.</summary>
    public float Matrix02
    {
        get => _m[2];
        set => _m[2] = value;
    }

    /// <summary>Gets or sets the value in row 0, column 3.</summary>
    public float Matrix03
    {
        get => _m[3];
        set => _m[3] = value;
    }

    /// <summary>Gets or sets the value in row 0, column 4.</summary>
    public float Matrix04
    {
        get => _m[4];
        set => _m[4] = value;
    }

    /// <summary>Gets or sets the value in row 1, column 0.</summary>
    public float Matrix10
    {
        get => _m[5];
        set => _m[5] = value;
    }

    /// <summary>Gets or sets the value in row 1, column 1.</summary>
    public float Matrix11
    {
        get => _m[6];
        set => _m[6] = value;
    }

    /// <summary>Gets or sets the value in row 1, column 2.</summary>
    public float Matrix12
    {
        get => _m[7];
        set => _m[7] = value;
    }

    /// <summary>Gets or sets the value in row 1, column 3.</summary>
    public float Matrix13
    {
        get => _m[8];
        set => _m[8] = value;
    }

    /// <summary>Gets or sets the value in row 1, column 4.</summary>
    public float Matrix14
    {
        get => _m[9];
        set => _m[9] = value;
    }

    /// <summary>Gets or sets the value in row 2, column 0.</summary>
    public float Matrix20
    {
        get => _m[10];
        set => _m[10] = value;
    }

    /// <summary>Gets or sets the value in row 2, column 1.</summary>
    public float Matrix21
    {
        get => _m[11];
        set => _m[11] = value;
    }

    /// <summary>Gets or sets the value in row 2, column 2.</summary>
    public float Matrix22
    {
        get => _m[12];
        set => _m[12] = value;
    }

    /// <summary>Gets or sets the value in row 2, column 3.</summary>
    public float Matrix23
    {
        get => _m[13];
        set => _m[13] = value;
    }

    /// <summary>Gets or sets the value in row 2, column 4.</summary>
    public float Matrix24
    {
        get => _m[14];
        set => _m[14] = value;
    }

    /// <summary>Gets or sets the value in row 3, column 0.</summary>
    public float Matrix30
    {
        get => _m[15];
        set => _m[15] = value;
    }

    /// <summary>Gets or sets the value in row 3, column 1.</summary>
    public float Matrix31
    {
        get => _m[16];
        set => _m[16] = value;
    }

    /// <summary>Gets or sets the value in row 3, column 2.</summary>
    public float Matrix32
    {
        get => _m[17];
        set => _m[17] = value;
    }

    /// <summary>Gets or sets the value in row 3, column 3.</summary>
    public float Matrix33
    {
        get => _m[18];
        set => _m[18] = value;
    }

    /// <summary>Gets or sets the value in row 3, column 4.</summary>
    public float Matrix34
    {
        get => _m[19];
        set => _m[19] = value;
    }

    /// <summary>Gets or sets the value in row 4, column 0.</summary>
    public float Matrix40
    {
        get => _m[20];
        set => _m[20] = value;
    }

    /// <summary>Gets or sets the value in row 4, column 1.</summary>
    public float Matrix41
    {
        get => _m[21];
        set => _m[21] = value;
    }

    /// <summary>Gets or sets the value in row 4, column 2.</summary>
    public float Matrix42
    {
        get => _m[22];
        set => _m[22] = value;
    }

    /// <summary>Gets or sets the value in row 4, column 3.</summary>
    public float Matrix43
    {
        get => _m[23];
        set => _m[23] = value;
    }

    /// <summary>Gets or sets the value in row 4, column 4.</summary>
    public float Matrix44
    {
        get => _m[24];
        set => _m[24] = value;
    }

    internal float[] Values => (float[])_m.Clone();

    private static int Index(int row, int column)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)row, 4u, nameof(row));
        ArgumentOutOfRangeException.ThrowIfGreaterThan((uint)column, 4u, nameof(column));
        return (row * 5) + column;
    }
}

/// <summary>A color a remap table replaces, and its replacement.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="images"/>
/// </example>
public sealed class ColorMap
{
    /// <summary>Gets or sets the color to replace.</summary>
    public Color OldColor { get; set; }

    /// <summary>Gets or sets the replacement.</summary>
    public Color NewColor { get; set; }
}

/// <summary>
/// How images are recolored when drawn and what is drawn beyond their edges: color matrices, gamma, threshold, a color key, remap
/// tables and an output channel per <see cref="ColorAdjustType"/>, and a wrap mode. Without a wrap mode set, nothing is drawn
/// beyond an image, as in GDI+.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="images"/>
/// </example>
public sealed class ImageAttributes : ICloneable, IDisposable
{
    internal GdiPlusImageAttributes State { get; private set; } = new();

    /// <summary>A copy.</summary>
    /// <returns>The copy.</returns>
    public object Clone()
    {
        ImageAttributes copy = new() { State = new GdiPlusImageAttributes { WrapMode = State.WrapMode, ClampColor = State.ClampColor }, WrapSet = WrapSet };
        for (int t = 0; t < 5; t++)
        {
            GdiPlusColorAdjust a = State[t], b = copy.State[t];
            b.NoOp = a.NoOp;
            b.ColorMatrix = (float[]?)a.ColorMatrix?.Clone();
            b.GrayMatrix = (float[]?)a.GrayMatrix?.Clone();
            b.MatrixFlags = a.MatrixFlags;
            b.Gamma = a.Gamma;
            b.Threshold = a.Threshold;
            b.ColorKeyLow = a.ColorKeyLow;
            b.ColorKeyHigh = a.ColorKeyHigh;
            b.RemapTable = ((Rgba8 Old, Rgba8 New)[]?)a.RemapTable?.Clone();
            b.OutputChannel = a.OutputChannel;
        }

        return copy;
    }

    /// <summary>Does nothing; the attributes hold no unmanaged memory.</summary>
    public void Dispose()
    {
    }

    /// <summary>Sets the color matrix for every type.</summary>
    /// <param name="newColorMatrix">The matrix.</param>
    public void SetColorMatrix(ColorMatrix newColorMatrix) => SetColorMatrix(newColorMatrix, ColorMatrixFlag.Default, ColorAdjustType.Default);

    /// <summary>Sets the color matrix for every type.</summary>
    /// <param name="newColorMatrix">The matrix.</param>
    /// <param name="flags">Which colors it changes.</param>
    public void SetColorMatrix(ColorMatrix newColorMatrix, ColorMatrixFlag flags) => SetColorMatrix(newColorMatrix, flags, ColorAdjustType.Default);

    /// <summary>Sets the color matrix for one type.</summary>
    /// <param name="newColorMatrix">The matrix.</param>
    /// <param name="mode">Which colors it changes.</param>
    /// <param name="type">The type.</param>
    public void SetColorMatrix(ColorMatrix newColorMatrix, ColorMatrixFlag mode, ColorAdjustType type)
    {
        ArgumentNullException.ThrowIfNull(newColorMatrix);
        GdiPlusColorAdjust a = Adjust(type);
        a.ColorMatrix = newColorMatrix.Values;
        a.MatrixFlags = (int)mode;
    }

    /// <summary>Sets the color matrix and the gray matrix for every type.</summary>
    /// <param name="newColorMatrix">The color matrix.</param>
    /// <param name="grayMatrix">The gray matrix, used with <see cref="ColorMatrixFlag.AltGrays"/>.</param>
    public void SetColorMatrices(ColorMatrix newColorMatrix, ColorMatrix? grayMatrix) => SetColorMatrices(newColorMatrix, grayMatrix, ColorMatrixFlag.Default, ColorAdjustType.Default);

    /// <summary>Sets the color matrix and the gray matrix for every type.</summary>
    /// <param name="newColorMatrix">The color matrix.</param>
    /// <param name="grayMatrix">The gray matrix, used with <see cref="ColorMatrixFlag.AltGrays"/>.</param>
    /// <param name="flags">Which colors the color matrix changes.</param>
    public void SetColorMatrices(ColorMatrix newColorMatrix, ColorMatrix? grayMatrix, ColorMatrixFlag flags) => SetColorMatrices(newColorMatrix, grayMatrix, flags, ColorAdjustType.Default);

    /// <summary>Sets the color matrix and the gray matrix for one type.</summary>
    /// <param name="newColorMatrix">The color matrix.</param>
    /// <param name="grayMatrix">The gray matrix, used with <see cref="ColorMatrixFlag.AltGrays"/>.</param>
    /// <param name="mode">Which colors the color matrix changes.</param>
    /// <param name="type">The type.</param>
    public void SetColorMatrices(ColorMatrix newColorMatrix, ColorMatrix? grayMatrix, ColorMatrixFlag mode, ColorAdjustType type)
    {
        SetColorMatrix(newColorMatrix, mode, type);
        Adjust(type).GrayMatrix = grayMatrix?.Values;
    }

    /// <summary>Removes the color matrices of every type.</summary>
    public void ClearColorMatrix() => ClearColorMatrix(ColorAdjustType.Default);

    /// <summary>Removes the color matrices of one type.</summary>
    /// <param name="type">The type.</param>
    public void ClearColorMatrix(ColorAdjustType type)
    {
        GdiPlusColorAdjust a = Adjust(type);
        a.ColorMatrix = null;
        a.GrayMatrix = null;
    }

    /// <summary>Sets gamma for every type: each channel is raised to it.</summary>
    /// <param name="gamma">The gamma, 0.1 to 5.</param>
    public void SetGamma(float gamma) => SetGamma(gamma, ColorAdjustType.Default);

    /// <summary>Sets gamma for one type.</summary>
    /// <param name="gamma">The gamma, 0.1 to 5.</param>
    /// <param name="type">The type.</param>
    public void SetGamma(float gamma, ColorAdjustType type) => Adjust(type).Gamma = gamma;

    /// <summary>Removes gamma for every type.</summary>
    public void ClearGamma() => ClearGamma(ColorAdjustType.Default);

    /// <summary>Removes gamma for one type.</summary>
    /// <param name="type">The type.</param>
    public void ClearGamma(ColorAdjustType type) => Adjust(type).Gamma = null;

    /// <summary>Turns color adjustment off for every type.</summary>
    public void SetNoOp() => SetNoOp(ColorAdjustType.Default);

    /// <summary>Turns color adjustment off for one type.</summary>
    /// <param name="type">The type.</param>
    public void SetNoOp(ColorAdjustType type) => Adjust(type).NoOp = true;

    /// <summary>Turns color adjustment back on for every type.</summary>
    public void ClearNoOp() => ClearNoOp(ColorAdjustType.Default);

    /// <summary>Turns color adjustment back on for one type.</summary>
    /// <param name="type">The type.</param>
    public void ClearNoOp(ColorAdjustType type) => Adjust(type).NoOp = false;

    /// <summary>Sets the threshold for every type: a channel above it becomes full, else zero.</summary>
    /// <param name="threshold">The threshold, 0 to 1.</param>
    public void SetThreshold(float threshold) => SetThreshold(threshold, ColorAdjustType.Default);

    /// <summary>Sets the threshold for one type.</summary>
    /// <param name="threshold">The threshold, 0 to 1.</param>
    /// <param name="type">The type.</param>
    public void SetThreshold(float threshold, ColorAdjustType type) => Adjust(type).Threshold = threshold;

    /// <summary>Removes the threshold for every type.</summary>
    public void ClearThreshold() => ClearThreshold(ColorAdjustType.Default);

    /// <summary>Removes the threshold for one type.</summary>
    /// <param name="type">The type.</param>
    public void ClearThreshold(ColorAdjustType type) => Adjust(type).Threshold = null;

    /// <summary>Sets a color key for every type: colors whose channels all lie in the range become transparent.</summary>
    /// <param name="colorLow">The lowest color of the range.</param>
    /// <param name="colorHigh">The highest color of the range.</param>
    public void SetColorKey(Color colorLow, Color colorHigh) => SetColorKey(colorLow, colorHigh, ColorAdjustType.Default);

    /// <summary>Sets a color key for one type.</summary>
    /// <param name="colorLow">The lowest color of the range.</param>
    /// <param name="colorHigh">The highest color of the range.</param>
    /// <param name="type">The type.</param>
    public void SetColorKey(Color colorLow, Color colorHigh, ColorAdjustType type)
    {
        GdiPlusColorAdjust a = Adjust(type);
        a.ColorKeyLow = SolidBrush.Rgba(colorLow);
        a.ColorKeyHigh = SolidBrush.Rgba(colorHigh);
    }

    /// <summary>Removes the color key for every type.</summary>
    public void ClearColorKey() => ClearColorKey(ColorAdjustType.Default);

    /// <summary>Removes the color key for one type.</summary>
    /// <param name="type">The type.</param>
    public void ClearColorKey(ColorAdjustType type) => Adjust(type).ColorKeyLow = null;

    /// <summary>Sets the CMYK channel shown as gray for every type.</summary>
    /// <param name="flags">The channel.</param>
    public void SetOutputChannel(ColorChannelFlag flags) => SetOutputChannel(flags, ColorAdjustType.Default);

    /// <summary>Sets the CMYK channel shown as gray for one type.</summary>
    /// <param name="flags">The channel.</param>
    /// <param name="type">The type.</param>
    public void SetOutputChannel(ColorChannelFlag flags, ColorAdjustType type) => Adjust(type).OutputChannel = (int)flags;

    /// <summary>Removes the output channel for every type.</summary>
    public void ClearOutputChannel() => ClearOutputChannel(ColorAdjustType.Default);

    /// <summary>Removes the output channel for one type.</summary>
    /// <param name="type">The type.</param>
    public void ClearOutputChannel(ColorAdjustType type) => Adjust(type).OutputChannel = null;

    /// <summary>Accepted for compatibility; Egami computes the output channel without a color profile.</summary>
    /// <param name="colorProfileFilename">The profile.</param>
    public void SetOutputChannelColorProfile(string colorProfileFilename) => ArgumentNullException.ThrowIfNull(colorProfileFilename);

    /// <summary>Accepted for compatibility; Egami computes the output channel without a color profile.</summary>
    /// <param name="colorProfileFilename">The profile.</param>
    /// <param name="type">The type.</param>
    public void SetOutputChannelColorProfile(string colorProfileFilename, ColorAdjustType type) => ArgumentNullException.ThrowIfNull(colorProfileFilename);

    /// <summary>Does nothing, as nothing keeps a color profile.</summary>
    public void ClearOutputChannelColorProfile()
    {
    }

    /// <summary>Does nothing, as nothing keeps a color profile.</summary>
    /// <param name="type">The type.</param>
    public void ClearOutputChannelColorProfile(ColorAdjustType type)
    {
    }

    /// <summary>Sets the remap table for every type: colors replaced exactly by others.</summary>
    /// <param name="map">The replacements.</param>
    public void SetRemapTable(ColorMap[] map) => SetRemapTable(ColorAdjustType.Default, (ReadOnlySpan<ColorMap>)map);

    /// <summary>Sets the remap table for one type.</summary>
    /// <param name="map">The replacements.</param>
    /// <param name="type">The type.</param>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public void SetRemapTable(ColorMap[] map, ColorAdjustType type) => SetRemapTable(type, (ReadOnlySpan<ColorMap>)map);

    /// <summary>Sets the remap table for every type.</summary>
    /// <param name="map">The replacements.</param>
    public void SetRemapTable(ReadOnlySpan<ColorMap> map) => SetRemapTable(ColorAdjustType.Default, map);

    /// <summary>Sets the remap table for every type.</summary>
    /// <param name="map">The replacements, old color then new.</param>
    public void SetRemapTable(ReadOnlySpan<(Color OldColor, Color NewColor)> map) => SetRemapTable(ColorAdjustType.Default, map);

    /// <summary>Sets the remap table for one type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="map">The replacements.</param>
    public void SetRemapTable(ColorAdjustType type, ReadOnlySpan<ColorMap> map)
    {
        (Rgba8, Rgba8)[] table = new (Rgba8, Rgba8)[map.Length];
        for (int i = 0; i < map.Length; i++)
        {
            table[i] = (SolidBrush.Rgba(map[i].OldColor), SolidBrush.Rgba(map[i].NewColor));
        }

        Adjust(type).RemapTable = table;
    }

    /// <summary>Sets the remap table for one type.</summary>
    /// <param name="type">The type.</param>
    /// <param name="map">The replacements, old color then new.</param>
    public void SetRemapTable(ColorAdjustType type, ReadOnlySpan<(Color OldColor, Color NewColor)> map)
    {
        (Rgba8, Rgba8)[] table = new (Rgba8, Rgba8)[map.Length];
        for (int i = 0; i < map.Length; i++)
        {
            table[i] = (SolidBrush.Rgba(map[i].OldColor), SolidBrush.Rgba(map[i].NewColor));
        }

        Adjust(type).RemapTable = table;
    }

    /// <summary>Removes the remap table for every type.</summary>
    public void ClearRemapTable() => ClearRemapTable(ColorAdjustType.Default);

    /// <summary>Removes the remap table for one type.</summary>
    /// <param name="type">The type.</param>
    public void ClearRemapTable(ColorAdjustType type) => Adjust(type).RemapTable = null;

    /// <summary>Sets the remap table for brushes.</summary>
    /// <param name="map">The replacements.</param>
    public void SetBrushRemapTable(ColorMap[] map) => SetRemapTable(ColorAdjustType.Brush, (ReadOnlySpan<ColorMap>)map);

    /// <summary>Sets the remap table for brushes.</summary>
    /// <param name="map">The replacements.</param>
    public void SetBrushRemapTable(ReadOnlySpan<ColorMap> map) => SetRemapTable(ColorAdjustType.Brush, map);

    /// <summary>Sets the remap table for brushes.</summary>
    /// <param name="map">The replacements, old color then new.</param>
    public void SetBrushRemapTable(ReadOnlySpan<(Color OldColor, Color NewColor)> map) => SetRemapTable(ColorAdjustType.Brush, map);

    /// <summary>Removes the remap table for brushes.</summary>
    public void ClearBrushRemapTable() => ClearRemapTable(ColorAdjustType.Brush);

    /// <summary>Sets how images repeat beyond their edges.</summary>
    /// <param name="mode">The wrap mode.</param>
    public void SetWrapMode(WrapMode mode) => SetWrapMode(mode, default, false);

    /// <summary>Sets how images repeat beyond their edges, and the color beyond them with <see cref="WrapMode.Clamp"/>.</summary>
    /// <param name="mode">The wrap mode.</param>
    /// <param name="color">The color beyond the edges.</param>
    public void SetWrapMode(WrapMode mode, Color color) => SetWrapMode(mode, color, false);

    /// <summary>Sets how images repeat beyond their edges, and the color beyond them.</summary>
    /// <param name="mode">The wrap mode.</param>
    /// <param name="color">The color beyond the edges.</param>
    /// <param name="clamp">Ignored, as in GDI+.</param>
    public void SetWrapMode(WrapMode mode, Color color, bool clamp)
    {
        State.WrapMode = (int)mode;
        State.ClampColor = SolidBrush.Rgba(color);
        WrapSet = true;
    }

    /// <summary>Changes a palette's colors as a type's adjustments change colors.</summary>
    /// <param name="palette">The palette to change in place.</param>
    /// <param name="type">The type.</param>
    public void GetAdjustedPalette(ColorPalette palette, ColorAdjustType type)
    {
        ArgumentNullException.ThrowIfNull(palette);
        Span<Rgba8> colors = palette.Entries.Select(SolidBrush.Rgba).ToArray();
        GdiPlusImageAttributes.Apply(colors, State.For((int)type));
        for (int i = 0; i < colors.Length; i++)
        {
            palette.Entries[i] = Color.FromArgb(colors[i].A, colors[i].R, colors[i].G, colors[i].B);
        }
    }

    /// <summary>Gets a value indicating whether a wrap mode was set.</summary>
    internal bool WrapSet { get; private set; }

    private GdiPlusColorAdjust Adjust(ColorAdjustType type)
    {
        if (type is < ColorAdjustType.Default or >= ColorAdjustType.Count)
        {
            throw GdiErrors.InvalidParameter();
        }

        return State[(int)type];
    }
}
