namespace Egami.Fonts;

public sealed partial class Typeface
{
    private static readonly FontTag s_math = new("MATH");

    private Box<TypefaceMath?>? _math;

    /// <summary>
    /// Gets the face's math layout data from its OpenType <c>MATH</c> table (constants, italics corrections, top accent
    /// attachments, math kerning, glyph variants and assemblies); null for a face without one, or with one it cannot read. Read
    /// the first time it is asked for. An instance of a variable font gives the default instance's values.
    /// </summary>
    public TypefaceMath? Math
    {
        get
        {
            Typeface root = Root;
            return (Volatile.Read(ref root._math) ?? Publish(ref root._math, new Box<TypefaceMath?>(root.TryGetTable(s_math, out ReadOnlyMemory<byte> data) ? TypefaceMath.Read(data) : null))).Value;
        }
    }
}
