using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace Egami.Formats.Extended.Dicom;

/// <summary>
/// A DICOM dataset: data elements in file order, read-only, found by tag or by PS3.6 keyword. The file's main dataset, its file
/// meta information and each item of a sequence are datasets.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Dicom.cs" region="dicom-load"/>
/// </example>
[Experimental(DicomFile.DiagnosticId, UrlFormat = DicomFile.DiagnosticUrl)]
public sealed class DicomDataset : IReadOnlyList<DicomElement>
{
    private readonly DicomElement[] _elements;
    private readonly Dictionary<uint, DicomElement> _byTag;

    internal DicomDataset(List<DicomElement> elements)
    {
        _elements = [.. elements];
        _byTag = new Dictionary<uint, DicomElement>(_elements.Length);
        foreach (DicomElement element in _elements)
        {
            _byTag.TryAdd(element.Tag.Value, element);
        }
    }

    /// <summary>Gets an empty dataset.</summary>
    internal static DicomDataset Empty { get; } = new([]);

    /// <summary>Gets the number of elements.</summary>
    public int Count => _elements.Length;

    /// <summary>Gets an element by its position in the file.</summary>
    /// <param name="index">The position, from 0.</param>
    /// <returns>The element.</returns>
    /// <exception cref="IndexOutOfRangeException"><paramref name="index"/> is outside the dataset.</exception>
    public DicomElement this[int index] => _elements[index];

    /// <summary>Gets the element with a tag, or <see langword="null"/> when the dataset has none.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>The element; the first one when a damaged file repeats a tag.</returns>
    public DicomElement? this[DicomTag tag] => _byTag.GetValueOrDefault(tag.Value);

    /// <summary>Gets the element a PS3.6 keyword names, such as <c>PatientName</c>, or <see langword="null"/> when the dataset has none.</summary>
    /// <param name="keyword">The keyword; case matters.</param>
    /// <returns>The element.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="keyword"/> is <see langword="null"/>.</exception>
    /// <exception cref="KeyNotFoundException">PS3.6 has no such keyword.</exception>
    public DicomElement? this[string keyword] => this[DicomTag.FromKeyword(keyword)];

    /// <summary>Returns whether the dataset has an element with a tag.</summary>
    /// <param name="tag">The tag.</param>
    /// <returns>Whether it is there.</returns>
    public bool Contains(DicomTag tag) => _byTag.ContainsKey(tag.Value);

    /// <summary>Returns the text of an element, or <see langword="null"/> when the dataset has none (see <see cref="DicomElement.GetString"/>).</summary>
    /// <param name="keyword">The PS3.6 keyword, such as <c>Modality</c>.</param>
    /// <returns>The text, or <see langword="null"/>.</returns>
    /// <exception cref="KeyNotFoundException">PS3.6 has no such keyword.</exception>
    /// <exception cref="InvalidOperationException">The element holds bytes or items, not values.</exception>
    public string? GetString(string keyword) => this[keyword]?.GetString();

    /// <summary>
    /// Returns the first number of an element, or <see langword="null"/> when the dataset has none or it is empty (see
    /// <see cref="DicomElement.GetNumbers"/>).
    /// </summary>
    /// <param name="keyword">The PS3.6 keyword, such as <c>Rows</c>.</param>
    /// <returns>The number, or <see langword="null"/>.</returns>
    /// <exception cref="KeyNotFoundException">PS3.6 has no such keyword.</exception>
    /// <exception cref="InvalidOperationException">The element holds no numbers.</exception>
    /// <exception cref="FormatException">A DS or IS value is not a number.</exception>
    public double? GetNumber(string keyword)
    {
        double[]? numbers = this[keyword]?.GetNumbers();
        return numbers is { Length: > 0 } ? numbers[0] : null;
    }

    /// <inheritdoc/>
    public IEnumerator<DicomElement> GetEnumerator() => ((IEnumerable<DicomElement>)_elements).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
