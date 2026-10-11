using System.Numerics;
using System.Runtime.CompilerServices;

using Egami.Pixels;

namespace Egami.Operations;

/// <summary>
/// A 3D color lookup table: an N × N × N grid of output RGB values over the input cube, with red varying fastest, as .cube files
/// and Hald images store it, and an optional 1D shaper per channel read before the grid (DaVinci Resolve's 1D-plus-3D .cube). Read
/// one with <see cref="CubeFile.Read(string, EgamiContext?)"/> or <see cref="FromHald"/>, and apply it with <see cref="ColorFilter.FromLut"/>.
/// </summary>
/// <remarks>Values are 32-bit floats and may lie outside 0..1; a filter clamps what it reads. Immutable and safe to share between threads.</remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tone.cs" region="cube"/>
/// </example>
public sealed class Lut3D
{
    /// <summary>The largest number of grid points per axis.</summary>
    internal const int MaxSize = 256;

    /// <summary>The largest shaper.</summary>
    internal const int MaxShaperSize = 65536;

    private readonly float[] _values;
    private readonly Vector3 _scale;
    private readonly Vector3 _shaperScale;

    internal Lut3D(int size, float[] values, Vector3? domainMin = null, Vector3? domainMax = null, float[]? shaper = null, Vector3? shaperMin = null, Vector3? shaperMax = null)
    {
        Size = size;
        _values = values;
        DomainMin = domainMin ?? Vector3.Zero;
        DomainMax = domainMax ?? Vector3.One;
        Shaper = shaper;
        ShaperMin = shaperMin ?? Vector3.Zero;
        ShaperMax = shaperMax ?? Vector3.One;
        _scale = new Vector3(size - 1) / (DomainMax - DomainMin);
        _shaperScale = shaper is null ? Vector3.Zero : new Vector3((shaper.Length / 3) - 1) / (ShaperMax - ShaperMin);
    }

    /// <summary>Gets the number of grid points along each axis.</summary>
    public int Size { get; }

    /// <summary>Gets the input that maps to the first grid point on each axis (DOMAIN_MIN in a .cube file). Defaults to 0.</summary>
    public Vector3 DomainMin { get; }

    /// <summary>Gets the input that maps to the last grid point on each axis (DOMAIN_MAX in a .cube file). Defaults to 1.</summary>
    public Vector3 DomainMax { get; }

    /// <summary>Gets the number of entries in the 1D shaper read before the grid, or 0 when there is none.</summary>
    public int ShaperSize => Shaper is null ? 0 : Shaper.Length / 3;

    /// <summary>Gets the shaper, RGB triples from the first entry to the last, or null.</summary>
    internal float[]? Shaper { get; }

    /// <summary>Gets the shaper's input range.</summary>
    internal Vector3 ShaperMin { get; }

    internal Vector3 ShaperMax { get; }

    /// <summary>Gets the grid, RGB triples with red fastest.</summary>
    internal ReadOnlySpan<float> Values => _values;

    /// <summary>Gets the grid's array, for kernels that index it directly.</summary>
    internal float[] Grid => _values;

    /// <summary>Gets each axis's grid steps per unit of input.</summary>
    internal Vector3 Scale => _scale;

    /// <summary>Gets one grid point's output.</summary>
    /// <param name="red">The red index, 0 to <see cref="Size"/> − 1.</param>
    /// <param name="green">The green index.</param>
    /// <param name="blue">The blue index.</param>
    /// <returns>The output color.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An index is out of range.</exception>
    public Vector3 this[int red, int green, int blue]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)red, (uint)Size, nameof(red));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)green, (uint)Size, nameof(green));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual((uint)blue, (uint)Size, nameof(blue));
            return Point(red, green, blue);
        }
    }

    /// <summary>Creates a table from its grid values, red varying fastest, then green, then blue.</summary>
    /// <param name="size">The points per axis, from 2 to 256.</param>
    /// <param name="values">Exactly <paramref name="size"/>³ finite colors.</param>
    /// <param name="domainMin">The input at the first grid point, or <see langword="null"/> for 0.</param>
    /// <param name="domainMax">The input at the last grid point, or <see langword="null"/> for 1; above <paramref name="domainMin"/> on every axis.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is outside 2 to 256.</exception>
    /// <exception cref="ArgumentException">The value count is wrong, a value is not finite, or the domain is empty.</exception>
    public static Lut3D FromValues(int size, ReadOnlySpan<Vector3> values, Vector3? domainMin = null, Vector3? domainMax = null)
    {
        CheckSize(size, nameof(size));
        if (values.Length != size * size * size)
        {
            throw new ArgumentException($"A {size}³ table needs {size * size * size} values, not {values.Length}.", nameof(values));
        }

        float[] copy = new float[values.Length * 3];
        for (int i = 0; i < values.Length; i++)
        {
            Vector3 v = values[i];
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
            {
                throw new ArgumentException($"Value {i} is not finite.", nameof(values));
            }

            copy[i * 3] = v.X;
            copy[(i * 3) + 1] = v.Y;
            copy[(i * 3) + 2] = v.Z;
        }

        CheckDomain(domainMin ?? Vector3.Zero, domainMax ?? Vector3.One, nameof(domainMax));
        return new Lut3D(size, copy, domainMin, domainMax);
    }

    /// <summary>Creates a table by sampling <paramref name="function"/> at every grid point over 0..1.</summary>
    /// <param name="function">The color function; it is called only here.</param>
    /// <param name="size">The points per axis, from 2 to 256. Defaults to 33.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="function"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="size"/> is outside 2 to 256.</exception>
    /// <exception cref="ArgumentException">The function returned a value that is not finite.</exception>
    public static Lut3D FromFunction(Func<Vector3, Vector3> function, int size = 33)
    {
        ArgumentNullException.ThrowIfNull(function);
        CheckSize(size, nameof(size));
        Vector3[] values = new Vector3[size * size * size];
        float step = 1f / (size - 1);
        int i = 0;
        for (int b = 0; b < size; b++)
        {
            for (int g = 0; g < size; g++)
            {
                for (int r = 0; r < size; r++)
                {
                    values[i++] = function(new Vector3(r * step, g * step, b * step));
                }
            }
        }

        return FromValues(size, values);
    }

    /// <summary>
    /// Reads a Hald CLUT image, as ImageMagick, G'MIC and RawTherapee write them: a level-L image is L³ × L³ pixels holding an
    /// L² table, red fastest, row by row. The image's own color profile is ignored, since it is a table rather than a picture.
    /// </summary>
    /// <param name="hald">The image; its first frame is read.</param>
    /// <returns>The table.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hald"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The image is not square with a side that is a cube from 8 to 4096.</exception>
    public static Lut3D FromHald(Raster hald)
    {
        ArgumentNullException.ThrowIfNull(hald);
        int level = HaldLevel(hald.Width, hald.Height);
        if (level == 0)
        {
            throw new ArgumentException($"A Hald image is square with a side of L³ for L from 2 to 16, not {hald.Width}×{hald.Height}.", nameof(hald));
        }

        int size = level * level;
        float[] values = new float[size * size * size * 3];
        using Raster<RgbaF32> pixels = hald.As<RgbaF32>();
        int width = hald.Width;
        for (int y = 0; y < hald.Height; y++)
        {
            ReadOnlySpan<RgbaF32> row = pixels.Row(y);
            for (int x = 0; x < width; x++)
            {
                int at = ((y * width) + x) * 3;
                values[at] = row[x].R;
                values[at + 1] = row[x].G;
                values[at + 2] = row[x].B;
            }
        }

        return new Lut3D(size, values);
    }

    /// <summary>Returns this table with a 1D shaper read before it, as the first table of a 1D-plus-3D .cube file.</summary>
    /// <param name="shaper">From 2 to 65,536 finite RGB entries, the first for <paramref name="inputMin"/> and the last for <paramref name="inputMax"/>.</param>
    /// <param name="inputMin">The input at the shaper's first entry. Defaults to 0.</param>
    /// <param name="inputMax">The input at its last entry, above <paramref name="inputMin"/>. Defaults to 1.</param>
    /// <returns>The table with the shaper, replacing any it had.</returns>
    /// <exception cref="ArgumentException">The shaper's length or values are out of range, or the range is empty.</exception>
    public Lut3D WithShaper(ReadOnlySpan<Vector3> shaper, float inputMin = 0, float inputMax = 1)
    {
        if (shaper.Length < 2 || shaper.Length > MaxShaperSize)
        {
            throw new ArgumentException("A shaper has 2 to 65,536 entries.", nameof(shaper));
        }

        float[] values = new float[shaper.Length * 3];
        for (int i = 0; i < shaper.Length; i++)
        {
            Vector3 v = shaper[i];
            if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z))
            {
                throw new ArgumentException($"Entry {i} is not finite.", nameof(shaper));
            }

            values[i * 3] = v.X;
            values[(i * 3) + 1] = v.Y;
            values[(i * 3) + 2] = v.Z;
        }

        CheckDomain(new Vector3(inputMin), new Vector3(inputMax), nameof(inputMax));
        return new Lut3D(Size, _values, DomainMin, DomainMax, values, new Vector3(inputMin), new Vector3(inputMax));
    }

    /// <summary>Returns the table's output for one color.</summary>
    /// <param name="color">The input; each channel is clamped to the shaper's range, then to the domain.</param>
    /// <param name="interpolation">How to read between grid points. Defaults to <see cref="LutInterpolation.Tetrahedral"/>.</param>
    /// <returns>The output, unclamped.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="interpolation"/> is not defined.</exception>
    public Vector3 Evaluate(Vector3 color, LutInterpolation interpolation = LutInterpolation.Tetrahedral)
    {
        if (!Enum.IsDefined(interpolation))
        {
            throw new ArgumentOutOfRangeException(nameof(interpolation), interpolation, "The interpolation is not defined.");
        }

        return EvaluateCore(color.X, color.Y, color.Z, interpolation);
    }

    /// <summary>
    /// Writes the table as a Hald image of <paramref name="level"/>: (level²)³ grid points in a level³ × level³ 16-bit image, for
    /// grading in an editor and loading back with <see cref="FromHald"/>.
    /// </summary>
    /// <param name="level">The Hald level, from 2 to 16. Defaults to 8 (a 512 × 512 image holding a 64³ table).</param>
    /// <returns>The image; the caller disposes it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside 2 to 16.</exception>
    public Raster<Rgba16> ToHald(int level = 8)
    {
        Raster<Rgba16> image = Raster.HaldIdentity<Rgba16>(level);
        int width = image.Width;
        float step = 1f / ((level * level) - 1);
        for (int y = 0; y < image.Height; y++)
        {
            Span<Rgba16> row = image.Row(y);
            for (int x = 0; x < width; x++)
            {
                (int r, int g, int b) = HaldIndex((y * width) + x, level * level);
                Vector3 c = Vector3.Clamp(EvaluateCore(r * step, g * step, b * step, LutInterpolation.Tetrahedral), Vector3.Zero, Vector3.One);
                row[x] = new Rgba16((ushort)((c.X * 65535) + 0.5f), (ushort)((c.Y * 65535) + 0.5f), (ushort)((c.Z * 65535) + 0.5f), 65535);
            }
        }

        return image;
    }

    /// <summary>Returns the grid indices of Hald pixel <paramref name="index"/>, red fastest.</summary>
    internal static (int R, int G, int B) HaldIndex(int index, int size) => (index % size, index / size % size, index / (size * size));

    /// <summary>Returns the Hald level of an image size, or 0 when it is not one.</summary>
    internal static int HaldLevel(int width, int height)
    {
        for (int level = 2; level <= 16; level++)
        {
            if (width == level * level * level && height == width)
            {
                return level;
            }
        }

        return 0;
    }

    /// <summary>Returns this table with each channel first mapped through a curve, folded into the shaper.</summary>
    internal Lut3D WithInputCurves(ToneCurve red, ToneCurve green, ToneCurve blue)
    {
        const int entries = 4096;
        float[] shaper = new float[entries * 3];
        for (int i = 0; i < entries; i++)
        {
            double x = i / (double)(entries - 1);
            Vector3 v = new((float)red.Map(x), (float)green.Map(x), (float)blue.Map(x));
            if (Shaper is not null)
            {
                v = Shape(v);
            }

            shaper[i * 3] = v.X;
            shaper[(i * 3) + 1] = v.Y;
            shaper[(i * 3) + 2] = v.Z;
        }

        return new Lut3D(Size, _values, DomainMin, DomainMax, shaper);
    }

    /// <summary>Evaluates without argument checks.</summary>
    internal Vector3 EvaluateCore(float red, float green, float blue, LutInterpolation interpolation)
    {
        Vector3 v = new(red, green, blue);
        if (Shaper is not null)
        {
            v = Shape(v);
        }

        Vector3 p = Vector3.Clamp((v - DomainMin) * _scale, Vector3.Zero, new Vector3(Size - 1));
        if (float.IsNaN(p.X) || float.IsNaN(p.Y) || float.IsNaN(p.Z))
        {
            p = Vector3.Zero;
        }

        if (interpolation == LutInterpolation.Nearest)
        {
            return Point((int)(p.X + 0.5f), (int)(p.Y + 0.5f), (int)(p.Z + 0.5f));
        }

        int r0 = Math.Min((int)p.X, Size - 2), g0 = Math.Min((int)p.Y, Size - 2), b0 = Math.Min((int)p.Z, Size - 2);
        float fr = p.X - r0, fg = p.Y - g0, fb = p.Z - b0;
        int dr = 3, dg = Size * 3, db = Size * Size * 3;
        int at = (r0 * dr) + (g0 * dg) + (b0 * db);
        Vector3 c000 = At(at);
        Vector3 c111 = At(at + dr + dg + db);
        if (interpolation == LutInterpolation.Trilinear)
        {
            Vector3 c100 = At(at + dr), c010 = At(at + dg), c110 = At(at + dr + dg);
            Vector3 c001 = At(at + db), c101 = At(at + dr + db), c011 = At(at + dg + db);
            Vector3 x00 = Vector3.Lerp(c000, c100, fr), x10 = Vector3.Lerp(c010, c110, fr);
            Vector3 x01 = Vector3.Lerp(c001, c101, fr), x11 = Vector3.Lerp(c011, c111, fr);
            return Vector3.Lerp(Vector3.Lerp(x00, x10, fg), Vector3.Lerp(x01, x11, fg), fb);
        }

        // Tetrahedral: the cell's six tetrahedra share the gray diagonal from c000 to c111.
        if (fr >= fg)
        {
            if (fg >= fb)
            {
                Vector3 c100 = At(at + dr), c110 = At(at + dr + dg);
                return c000 + (fr * (c100 - c000)) + (fg * (c110 - c100)) + (fb * (c111 - c110));
            }

            if (fr >= fb)
            {
                Vector3 c100 = At(at + dr), c101 = At(at + dr + db);
                return c000 + (fr * (c100 - c000)) + (fb * (c101 - c100)) + (fg * (c111 - c101));
            }

            Vector3 c001 = At(at + db), c101b = At(at + dr + db);
            return c000 + (fb * (c001 - c000)) + (fr * (c101b - c001)) + (fg * (c111 - c101b));
        }

        if (fb >= fg)
        {
            Vector3 c001 = At(at + db), c011 = At(at + dg + db);
            return c000 + (fb * (c001 - c000)) + (fg * (c011 - c001)) + (fr * (c111 - c011));
        }

        if (fb >= fr)
        {
            Vector3 c010 = At(at + dg), c011 = At(at + dg + db);
            return c000 + (fg * (c010 - c000)) + (fb * (c011 - c010)) + (fr * (c111 - c011));
        }

        Vector3 c010b = At(at + dg), c110b = At(at + dr + dg);
        return c000 + (fg * (c010b - c000)) + (fr * (c110b - c010b)) + (fb * (c111 - c110b));
    }

    private Vector3 Shape(Vector3 v)
    {
        float[] shaper = Shaper!;
        int last = (shaper.Length / 3) - 1;
        Vector3 p = Vector3.Clamp((v - ShaperMin) * _shaperScale, Vector3.Zero, new Vector3(last));
        return new Vector3(Read(shaper, p.X, 0, last), Read(shaper, p.Y, 1, last), Read(shaper, p.Z, 2, last));

        static float Read(float[] table, float position, int channel, int last)
        {
            if (float.IsNaN(position))
            {
                return table[channel];
            }

            int i = Math.Min((int)position, last - 1);
            float f = position - i;
            float a = table[(i * 3) + channel];
            return a + ((table[((i + 1) * 3) + channel] - a) * f);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private Vector3 At(int index) => new(_values[index], _values[index + 1], _values[index + 2]);

    private Vector3 Point(int r, int g, int b) => At(((((b * Size) + g) * Size) + r) * 3);

    private static void CheckSize(int size, string name)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 2, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, MaxSize, name);
    }

    private static void CheckDomain(Vector3 min, Vector3 max, string name)
    {
        if (!(max.X > min.X && max.Y > min.Y && max.Z > min.Z) || !float.IsFinite(min.X + min.Y + min.Z + max.X + max.Y + max.Z))
        {
            throw new ArgumentException("The range's maximum must lie above its minimum on every axis, and both must be finite.", name);
        }
    }
}
