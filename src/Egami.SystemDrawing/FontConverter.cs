using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.Globalization;

namespace Egami.SystemDrawing;

/// <summary>
/// Converts fonts to and from strings such as "Arial, 10pt, style=Bold, Italic" for designers and serializers, and to
/// constructor calls for code generation.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
/// </example>
public class FontConverter : TypeConverter
{
    private static readonly (string Suffix, GraphicsUnit Unit)[] s_units =
    [
        ("world", GraphicsUnit.World), ("display", GraphicsUnit.Display), ("px", GraphicsUnit.Pixel), ("pt", GraphicsUnit.Point),
        ("in", GraphicsUnit.Inch), ("doc", GraphicsUnit.Document), ("mm", GraphicsUnit.Millimeter),
    ];

    /// <inheritdoc/>
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

    /// <inheritdoc/>
    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) => destinationType == typeof(string) || destinationType == typeof(InstanceDescriptor) || base.CanConvertTo(context, destinationType);

    /// <inheritdoc/>
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string text)
        {
            return base.ConvertFrom(context, culture, value);
        }

        text = text.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        culture ??= CultureInfo.CurrentCulture;
        char separator = culture.TextInfo.ListSeparator[0];
        string[] parts = text.Split(separator);
        string name = parts[0].Trim();
        float size = 8.25f;
        GraphicsUnit unit = GraphicsUnit.Point;
        FontStyle style = FontStyle.Regular;
        int i = 1;
        if (parts.Length > 1 && !parts[1].Trim().StartsWith("style", StringComparison.OrdinalIgnoreCase))
        {
            string s = parts[1].Trim();
            foreach ((string suffix, GraphicsUnit u) in s_units)
            {
                if (s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    unit = u;
                    s = s[..^suffix.Length];
                    break;
                }
            }

            if (!float.TryParse(s, NumberStyles.Float, culture, out size))
            {
                throw new ArgumentException($"Text \"{text}\" cannot be parsed. The expected text format is \"name{separator} size[units[{separator} style=style1[{separator} style2{separator} ...]]]\".", nameof(value));
            }

            i = 2;
        }

        for (; i < parts.Length; i++)
        {
            string s = parts[i].Trim();
            int eq = s.IndexOf('=', StringComparison.Ordinal);
            if (eq >= 0)
            {
                s = s[(eq + 1)..];
            }

            if (s.Length > 0)
            {
                style |= Enum.Parse<FontStyle>(s.Trim(), ignoreCase: true);
            }
        }

        return new Font(name, size, style, unit);
    }

    /// <inheritdoc/>
    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        ArgumentNullException.ThrowIfNull(destinationType);
        if (value is Font font)
        {
            if (destinationType == typeof(string))
            {
                culture ??= CultureInfo.CurrentCulture;
                string separator = culture.TextInfo.ListSeparator + " ";
                string unit = s_units.First(u => u.Unit == font.Unit).Suffix;
                string text = font.Name + separator + font.Size.ToString(culture) + unit;
                FontStyle style = font.Style;
                if (style != FontStyle.Regular)
                {
                    text += separator + "style=" + string.Join(separator, Enum.GetValues<FontStyle>().Where(s => s != FontStyle.Regular && style.HasFlag(s)));
                }

                return text;
            }

            if (destinationType == typeof(InstanceDescriptor))
            {
                return new InstanceDescriptor(
                    typeof(Font).GetConstructor([typeof(string), typeof(float), typeof(FontStyle), typeof(GraphicsUnit)]),
                    new object[] { font.Name, font.Size, font.Style, font.Unit });
            }
        }

        if (value is null && destinationType == typeof(string))
        {
            return string.Empty;
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }

    /// <inheritdoc/>
    public override object CreateInstance(ITypeDescriptorContext? context, IDictionary propertyValues)
    {
        ArgumentNullException.ThrowIfNull(propertyValues);
        string name = propertyValues["Name"] as string ?? "Microsoft Sans Serif";
        float size = propertyValues["Size"] is float s ? s : 8.25f;
        GraphicsUnit unit = propertyValues["Unit"] is GraphicsUnit u ? u : GraphicsUnit.Point;
        FontStyle style = (propertyValues["Bold"] is true ? FontStyle.Bold : 0) | (propertyValues["Italic"] is true ? FontStyle.Italic : 0)
            | (propertyValues["Underline"] is true ? FontStyle.Underline : 0) | (propertyValues["Strikeout"] is true ? FontStyle.Strikeout : 0);
        byte charSet = propertyValues["GdiCharSet"] is byte c ? c : (byte)1;
        bool vertical = propertyValues["GdiVerticalFont"] is true;
        return new Font(name, size, style, unit, charSet, vertical);
    }

    /// <inheritdoc/>
    public override bool GetCreateInstanceSupported(ITypeDescriptorContext? context) => true;

    /// <inheritdoc/>
    [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("The properties of Font are found by reflection.")]
    public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext? context, object? value, Attribute[]? attributes) =>
        TypeDescriptor.GetProperties(typeof(Font), attributes).Sort(["Name", "Size", "Unit"]);

    /// <inheritdoc/>
    public override bool GetPropertiesSupported(ITypeDescriptorContext? context) => true;

    /// <summary>Converts family names, offering the installed families as the standard values.</summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
    /// </example>
    public sealed class FontNameConverter : TypeConverter, IDisposable
    {
        /// <inheritdoc/>
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        /// <inheritdoc/>
        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            if (value is not string name)
            {
                return base.ConvertFrom(context, culture, value);
            }

            foreach (FontFamily family in FontFamily.Families)
            {
                if (string.Equals(family.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return family.Name;
                }
            }

            return name;
        }

        /// <inheritdoc/>
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) => new(FontFamily.Families.Select(f => f.Name).ToArray());

        /// <inheritdoc/>
        public override bool GetStandardValuesExclusive(ITypeDescriptorContext? context) => false;

        /// <inheritdoc/>
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;

        /// <summary>Releases nothing.</summary>
        void IDisposable.Dispose()
        {
        }
    }

    /// <summary>Converts font units, offering every <see cref="GraphicsUnit"/> but Display as the standard values.</summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="fonts"/>
    /// </example>
    public class FontUnitConverter : EnumConverter
    {
        /// <summary>Initializes a new instance of the <see cref="FontUnitConverter"/> class.</summary>
        public FontUnitConverter()
            : base(typeof(GraphicsUnit))
        {
        }

        /// <inheritdoc/>
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) =>
            new(Enum.GetValues<GraphicsUnit>().Where(u => u != GraphicsUnit.Display).ToArray());
    }
}
