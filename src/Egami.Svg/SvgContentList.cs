using System.Collections.ObjectModel;

namespace Egami.Svg;

/// <summary>
/// An element's content in file order: child elements, text, comments and processing instructions. Changes mark the document's
/// render tree for rebuilding.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="edit"/>
/// </example>
public sealed class SvgContentList : Collection<SvgContent>
{
    private readonly SvgElement _owner;

    internal SvgContentList(SvgElement owner) => _owner = owner;

    /// <inheritdoc/>
    protected override void InsertItem(int index, SvgContent item)
    {
        Adopt(item);
        base.InsertItem(index, item);
        _owner.ContentChanged();
    }

    /// <inheritdoc/>
    protected override void SetItem(int index, SvgContent item)
    {
        if (ReferenceEquals(this[index], item))
        {
            return;
        }

        Adopt(item);
        this[index].Parent = null;
        base.SetItem(index, item);
        _owner.ContentChanged();
    }

    /// <inheritdoc/>
    protected override void RemoveItem(int index)
    {
        this[index].Parent = null;
        base.RemoveItem(index);
        _owner.ContentChanged();
    }

    /// <inheritdoc/>
    protected override void ClearItems()
    {
        foreach (SvgContent item in this)
        {
            item.Parent = null;
        }

        base.ClearItems();
        _owner.ContentChanged();
    }

    /// <summary>Appends an item read from a file, without telling the document (it is still being built).</summary>
    internal void AddRead(SvgContent item)
    {
        item.Parent = _owner;
        Items.Add(item);
    }

    private void Adopt(SvgContent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Parent is not null)
        {
            throw new InvalidOperationException("The item already belongs to an element; remove or clone it first.");
        }

        if (item is SvgElement element)
        {
            if (element.OwnerDocument is not null)
            {
                throw new InvalidOperationException("A document's root element cannot be added to another element.");
            }

            for (SvgElement? e = _owner; e is not null; e = e.Parent)
            {
                if (ReferenceEquals(e, element))
                {
                    throw new InvalidOperationException("An element cannot be added to itself or its own descendants.");
                }
            }
        }

        item.Parent = _owner;
    }
}
