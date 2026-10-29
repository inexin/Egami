using System.Collections;
using System.ComponentModel;
using System.Globalization;

using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing
{
    /// <summary>Converts images to and from bytes (the file they are saved as) for designers and serializers.</summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
    /// </example>
    public class ImageConverter : TypeConverter
    {
        /// <inheritdoc/>
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(byte[]) || sourceType == typeof(Icon) || base.CanConvertFrom(context, sourceType);

        /// <inheritdoc/>
        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) => destinationType == typeof(byte[]) || base.CanConvertTo(context, destinationType);

        /// <inheritdoc/>
        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) => value switch
        {
            Icon icon => icon.ToBitmap(),
            byte[] bytes => Image.FromStream(new MemoryStream(bytes)),
            _ => base.ConvertFrom(context, culture, value),
        };

        /// <inheritdoc/>
        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        {
            ArgumentNullException.ThrowIfNull(destinationType);
            if (destinationType == typeof(byte[]))
            {
                if (value is null)
                {
                    return null;
                }

                if (value is Image image)
                {
                    using MemoryStream stream = new();
                    ImageFormat format = image.RawFormat.Guid == ImageFormat.MemoryBmp.Guid || image is Metafile ? ImageFormat.Png : image.RawFormat;
                    image.Save(stream, format);
                    return stream.ToArray();
                }
            }

            if (destinationType == typeof(string) && value is Image)
            {
                return value.ToString();
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }

        /// <inheritdoc/>
        [System.Diagnostics.CodeAnalysis.RequiresUnreferencedCode("The properties of Image are found by reflection.")]
        public override PropertyDescriptorCollection GetProperties(ITypeDescriptorContext? context, object? value, Attribute[]? attributes) =>
            TypeDescriptor.GetProperties(typeof(Image), attributes);

        /// <inheritdoc/>
        public override bool GetPropertiesSupported(ITypeDescriptorContext? context) => true;
    }

    /// <summary>Converts icons to and from bytes (the ICO file) for designers and serializers.</summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
    /// </example>
    public class IconConverter : ExpandableObjectConverter
    {
        /// <inheritdoc/>
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(byte[]) || base.CanConvertFrom(context, sourceType);

        /// <inheritdoc/>
        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
            destinationType == typeof(byte[]) || destinationType == typeof(Image) || destinationType == typeof(Bitmap) || base.CanConvertTo(context, destinationType);

        /// <inheritdoc/>
        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) =>
            value is byte[] bytes ? new Icon(new MemoryStream(bytes)) : base.ConvertFrom(context, culture, value);

        /// <inheritdoc/>
        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        {
            ArgumentNullException.ThrowIfNull(destinationType);
            if (value is Icon icon)
            {
                if (destinationType == typeof(byte[]))
                {
                    using MemoryStream stream = new();
                    icon.Save(stream);
                    return stream.ToArray();
                }

                if (destinationType == typeof(Image) || destinationType == typeof(Bitmap))
                {
                    return icon.ToBitmap();
                }
            }

            if (destinationType == typeof(byte[]) && value is null)
            {
                return null;
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }
    }

    /// <summary>Converts image formats to and from their names, and lists the standard ones, for designers.</summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
    /// </example>
    public class ImageFormatConverter : TypeConverter
    {
        private static readonly ImageFormat[] Standard =
        [
            ImageFormat.MemoryBmp, ImageFormat.Bmp, ImageFormat.Emf, ImageFormat.Wmf, ImageFormat.Gif, ImageFormat.Jpeg, ImageFormat.Png,
            ImageFormat.Tiff, ImageFormat.Exif, ImageFormat.Icon, ImageFormat.Heif, ImageFormat.Webp,
        ];

        /// <inheritdoc/>
        public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);

        /// <inheritdoc/>
        public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) => destinationType == typeof(string) || base.CanConvertTo(context, destinationType);

        /// <inheritdoc/>
        public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
        {
            if (value is not string text)
            {
                return base.ConvertFrom(context, culture, value);
            }

            if (text.Length == 0)
            {
                return null;
            }

            ImageFormat? named = Array.Find(Standard, f => string.Equals(f.ToString(), text, StringComparison.OrdinalIgnoreCase));
            if (named is not null)
            {
                return named;
            }

            return Guid.TryParse(text.Trim('[', ']', ' ').Replace("ImageFormat:", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(), out Guid guid)
                ? new ImageFormat(guid)
                : throw new FormatException($"The value '{text}' is not a valid ImageFormat.");
        }

        /// <inheritdoc/>
        public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
        {
            ArgumentNullException.ThrowIfNull(destinationType);
            if (destinationType == typeof(string) && value is ImageFormat format)
            {
                return format.ToString();
            }

            return base.ConvertTo(context, culture, value, destinationType);
        }

        /// <inheritdoc/>
        public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? context) => new(Standard);

        /// <inheritdoc/>
        public override bool GetStandardValuesSupported(ITypeDescriptorContext? context) => true;
    }
}

namespace Egami.SystemDrawing.Design
{
    /// <summary>A read-only list of toolbox category names.</summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="extras"/>
    /// </example>
#pragma warning disable CA1010 // System.Drawing's collection is the non-generic ReadOnlyCollectionBase.
    public sealed class CategoryNameCollection : ReadOnlyCollectionBase
#pragma warning restore CA1010
    {
        /// <summary>Initializes a new instance of the <see cref="CategoryNameCollection"/> class.</summary>
        /// <param name="value">The names.</param>
        public CategoryNameCollection(string[] value)
        {
            ArgumentNullException.ThrowIfNull(value);
            InnerList.AddRange(value);
        }

        /// <summary>Initializes a new instance of the <see cref="CategoryNameCollection"/> class from another.</summary>
        /// <param name="value">The other collection.</param>
        public CategoryNameCollection(CategoryNameCollection value)
        {
            ArgumentNullException.ThrowIfNull(value);
            InnerList.AddRange(value);
        }

        /// <summary>Gets a name.</summary>
        /// <param name="index">Its index.</param>
        public string this[int index] => (string)InnerList[index]!;

        /// <summary>Whether a name is in the collection.</summary>
        /// <param name="value">The name.</param>
        /// <returns><see langword="true"/> when it is.</returns>
        public bool Contains(string? value) => InnerList.Contains(value);

        /// <summary>Copies the names into an array.</summary>
        /// <param name="array">The array.</param>
        /// <param name="index">Where to start.</param>
        public void CopyTo(string[] array, int index) => InnerList.CopyTo(array, index);

        /// <summary>The index of a name, or -1.</summary>
        /// <param name="value">The name.</param>
        /// <returns>The index.</returns>
        public int IndexOf(string? value) => InnerList.IndexOf(value);
    }
}
