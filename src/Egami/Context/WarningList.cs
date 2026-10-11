using System.Collections;

namespace Egami;

/// <summary>
/// The Warnings a load collects: what <see cref="Metadata.RasterMetadata.Warnings"/> holds, and what readers add to. Each id is kept
/// at most <see cref="RepeatCap"/> times; past that, one summary entry counts the rest. A list bound to a handler delivers its new
/// entries in order each time it is flushed, which every call does as it ends, so entries a reader takes back while it waits for
/// more input are never delivered.
/// </summary>
/// <remarks>
/// It is the size of the <c>List&lt;string&gt;</c> it replaced, and allocates nothing more until something is added, an id goes
/// past the cap or a handler is bound, so a clean load costs what it did before diagnostics.
/// </remarks>
internal sealed class WarningList : IReadOnlyList<EgamiDiagnostic>
{
    /// <summary>How many times one id is kept, per list.</summary>
    internal const int RepeatCap = 10;

    // The entries (an EgamiDiagnostic[]) or, once an id is capped or a handler bound, an Extras holding them.
    private object? _state;
    private int _count;
    private int _delivered;

    /// <inheritdoc/>
    public int Count => _count;

    /// <summary>Gets whether a handler is bound.</summary>
    internal bool IsBound => (_state as Extras)?.Sink is not null;

    private EgamiDiagnostic[] Items => _state switch
    {
        EgamiDiagnostic[] items => items,
        Extras extras => extras.Items,
        _ => [],
    };

    private List<Capped>? CappedIds => (_state as Extras)?.Capped;

    /// <inheritdoc/>
    public EgamiDiagnostic this[int index] => (uint)index < (uint)_count ? Items[index] : throw new ArgumentOutOfRangeException(nameof(index));

    /// <inheritdoc/>
    public IEnumerator<EgamiDiagnostic> GetEnumerator()
    {
        EgamiDiagnostic[] items = Items;
        for (int i = 0; i < _count; i++)
        {
            yield return items[i];
        }
    }

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Adds a Warning with the id, message and, when known, the byte offset and frame it is about.</summary>
    internal void Add(string id, string message, long? offset = null, int? frame = null)
    {
        Add(EgamiDiagnostic.Create(DiagnosticLevel.Warning, id, message, offset, frame));
    }

    /// <summary>Adds a Warning for damage a lenient read recovered from, at the offset <paramref name="cause"/> gives, if any.</summary>
    internal void Add(string id, string message, CorruptDataException cause, int? frame = null)
    {
        Add(EgamiDiagnostic.Create(DiagnosticLevel.Warning, id, message, cause.Offset >= 0 ? cause.Offset : null, frame));
    }

    /// <summary>Adds an entry, or counts it in its id's summary once the id has <see cref="RepeatCap"/> entries.</summary>
    internal void Add(EgamiDiagnostic item)
    {
        if (!TryCap(item))
        {
            InsertCore(_count, item);
        }
    }

    /// <summary>
    /// Adds every entry of <paramref name="other"/>, in order, each counted against the cap, with what its summaries count; with
    /// <paramref name="frame"/>, each is marked as about that frame or page.
    /// </summary>
    internal void AddRange(WarningList other, int? frame = null)
    {
        EgamiDiagnostic[] items = other.Items;
        for (int i = 0; i < other._count; i++)
        {
            if (!other.IsSummary(i))
            {
                Add(frame is null ? items[i] : items[i] with { Frame = frame });
            }
        }

        if (other.CappedIds is not List<Capped> capped)
        {
            return;
        }

        foreach (Capped id in capped)
        {
            EgamiDiagnostic summary = items[id.Index];
            for (int i = 0; i < id.More; i++)
            {
                Add(summary);
            }
        }
    }

    /// <summary>Puts every entry of <paramref name="other"/> before this list's, as when header warnings join a decoder's.</summary>
    internal void Prepend(WarningList other)
    {
        if (other._count == 0)
        {
            return;
        }

        WarningList joined = new();
        joined.AddRange(other);
        joined.AddRange(this);
        Action<EgamiDiagnostic>? sink = (_state as Extras)?.Sink;
        _state = joined._state;
        _count = joined._count;
        if (sink is not null)
        {
            Bind(sink);
        }

        _delivered = Math.Min(_delivered, _count);
    }

    /// <summary>Inserts an entry at <paramref name="index"/>; one inserted among entries already delivered is delivered at once.</summary>
    internal void Insert(int index, EgamiDiagnostic item)
    {
        if (TryCap(item))
        {
            return;
        }

        InsertCore(index, item);
        if (CappedIds is List<Capped> capped)
        {
            foreach (Capped id in capped)
            {
                if (id.Index >= index)
                {
                    id.Index++;
                }
            }
        }

        if (index < _delivered)
        {
            _delivered++;
            (_state as Extras)?.Sink?.Invoke(item);
        }
    }

    /// <summary>Takes back entries, as a reader does when it must wait for more input and will read them again.</summary>
    internal void RemoveRange(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index + count, _count);
        if (count == 0)
        {
            return;
        }

        EgamiDiagnostic[] items = Items;
        Array.Copy(items, index + count, items, index, _count - index - count);
        _count -= count;
        Array.Clear(items, _count, count);
        if (index < _delivered)
        {
            _delivered -= Math.Min(count, _delivered - index);
        }

        CappedIds?.RemoveAll(capped =>
        {
            if (capped.Index >= index + count)
            {
                capped.Index -= count;
                return false;
            }

            return capped.Index >= index;
        });
    }

    /// <summary>Sends the entries added since the last flush to the bound handler, then a summary for each id capped since.</summary>
    internal void Flush()
    {
        if (_state is not Extras { Sink: Action<EgamiDiagnostic> sink } extras)
        {
            return;
        }

        while (_delivered < _count)
        {
            int index = _delivered++;
            if (!IsSummary(index))
            {
                sink(extras.Items[index]);
            }
        }

        if (extras.Capped is null)
        {
            return;
        }

        foreach (Capped capped in extras.Capped)
        {
            if (capped.More > capped.Reported)
            {
                EgamiDiagnostic first = extras.Items[capped.Index];
                int more = capped.More - capped.Reported;
                capped.Reported = capped.More;
                sink(Summary(first.Level, first.Id, more));
            }
        }
    }

    /// <summary>Sends this list's entries to <paramref name="sink"/> from now on, unless it already has a handler.</summary>
    internal void Bind(Action<EgamiDiagnostic> sink)
    {
        GetExtras().Sink ??= sink;
    }

    /// <summary>Takes the entries of <paramref name="source"/> as already delivered, bound to the same handler, as a copy of its metadata.</summary>
    internal void CopyOf(WarningList source)
    {
        CopyFrom(source);
    }

    /// <summary>Replaces this list's entries, cap, handler and delivery state with <paramref name="source"/>'s.</summary>
    internal void ReplaceWith(WarningList source)
    {
        if (ReferenceEquals(source, this))
        {
            return;
        }

        CopyFrom(source);
        _delivered = source._delivered;
    }

    /// <summary>Builds the summary entry for an id past the cap.</summary>
    internal static EgamiDiagnostic Summary(DiagnosticLevel level, string id, int more)
    {
        string times = more == 1 ? "time" : "times";
        return EgamiDiagnostic.Create(level, id, $"The same problem came up {more} more {times}; only the first {RepeatCap} are listed.");
    }

    private void CopyFrom(WarningList source)
    {
        _state = null;
        _count = 0;
        if (source._count > 0)
        {
            _state = source.Items.AsSpan(0, source._count).ToArray();
            _count = source._count;
        }

        if (source._state is Extras extras)
        {
            Extras mine = GetExtras();
            mine.Sink = extras.Sink;
            mine.Capped = extras.Capped?.ConvertAll(c => new Capped(c.Id, c.Index) { More = c.More, Reported = c.More });
        }

        _delivered = _count;
    }

    private Extras GetExtras()
    {
        if (_state is Extras extras)
        {
            return extras;
        }

        extras = new Extras { Items = _state as EgamiDiagnostic[] ?? [] };
        _state = extras;
        return extras;
    }

    private void InsertCore(int index, EgamiDiagnostic item)
    {
        EgamiDiagnostic[] items = Items;
        if (_count == items.Length)
        {
            EgamiDiagnostic[] larger = new EgamiDiagnostic[Math.Max(4, items.Length * 2)];
            Array.Copy(items, larger, _count);
            items = larger;
            if (_state is Extras extras)
            {
                extras.Items = items;
            }
            else
            {
                _state = items;
            }
        }

        Array.Copy(items, index, items, index + 1, _count - index);
        items[index] = item;
        _count++;
    }

    private bool IsSummary(int index)
    {
        if (CappedIds is List<Capped> capped)
        {
            foreach (Capped id in capped)
            {
                if (id.Index == index)
                {
                    return true;
                }
            }
        }

        return false;
    }

    // Counts the item in its id's summary when the id already has RepeatCap entries, adding the summary on the first overflow.
    private bool TryCap(EgamiDiagnostic item)
    {
        if (CappedIds is List<Capped> cappedIds)
        {
            foreach (Capped capped in cappedIds)
            {
                if (string.Equals(capped.Id, item.Id, StringComparison.Ordinal))
                {
                    capped.More++;
                    Items[capped.Index] = Summary(item.Level, item.Id, capped.More);
                    return true;
                }
            }
        }

        if (_count < RepeatCap)
        {
            return false;
        }

        int same = 0;
        EgamiDiagnostic[] items = Items;
        for (int i = 0; i < _count; i++)
        {
            if (string.Equals(items[i].Id, item.Id, StringComparison.Ordinal))
            {
                same++;
            }
        }

        if (same < RepeatCap)
        {
            return false;
        }

        InsertCore(_count, Summary(item.Level, item.Id, 1));
        (GetExtras().Capped ??= []).Add(new Capped(item.Id, _count - 1) { More = 1 });
        return true;
    }

    private sealed class Extras
    {
        public EgamiDiagnostic[] Items { get; set; } = [];

        public List<Capped>? Capped { get; set; }

        public Action<EgamiDiagnostic>? Sink { get; set; }
    }

    // An id past the cap: where its summary entry is, how many entries it stands for, and how many of those were delivered.
    private sealed class Capped(string id, int index)
    {
        public string Id { get; } = id;

        public int Index { get; set; } = index;

        public int More { get; set; }

        public int Reported { get; set; }
    }
}
