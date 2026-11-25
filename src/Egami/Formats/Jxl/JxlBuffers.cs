namespace Egami.Formats.Jxl;

/// <summary>
/// Large arrays of the lossy encoder (planes and coefficients) kept from one encode for the next, as encodes of one size ask
/// for the same lengths again. A rented array is not cleared: it is for code that writes every element before it reads one.
/// A full (generation 2) collection lets go of what is kept once no encode has used it for a few seconds, so an idle process
/// holds none of it for long.
/// </summary>
/// <typeparam name="T">The element type.</typeparam>
internal static class JxlBuffers<T>
    where T : unmanaged
{
    // A few encodes' worth: an encode returns three planes, three linear planes and its groups' coefficients.
    private const int MaxKept = 64;

    // Below this an array is cheap to make; above it a new one costs clearing and fresh pages.
    private const int MinLength = 1 << 14;

    // How long the kept arrays stay unused before a full collection drops them.
    private const long IdleMilliseconds = 5000;

    private static readonly List<T[]> Kept = [];
    private static long _lastUse;

    static JxlBuffers() => _ = new Trimmer();

    /// <summary>Gets an array of exactly <paramref name="length"/> elements whose contents are undefined.</summary>
    internal static T[] Rent(int length)
    {
        if (length >= MinLength)
        {
            lock (Kept)
            {
                _lastUse = Environment.TickCount64;
                for (int i = Kept.Count - 1; i >= 0; i--)
                {
                    if (Kept[i].Length == length)
                    {
                        T[] found = Kept[i];
                        Kept.RemoveAt(i);
                        return found;
                    }
                }
            }
        }

        return GC.AllocateUninitializedArray<T>(length);
    }

    /// <summary>Keeps <paramref name="array"/> for a later <see cref="Rent"/>; the caller no longer uses it.</summary>
    internal static void Return(T[]? array)
    {
        if (array is null || array.Length < MinLength)
        {
            return;
        }

        lock (Kept)
        {
            _lastUse = Environment.TickCount64;
            if (Kept.Count == MaxKept)
            {
                Kept.RemoveAt(0);
            }

            Kept.Add(array);
        }
    }

    /// <summary>Empties the kept arrays, if idle, whenever the collector finalizes it, which once it is old is at full collections.</summary>
    private sealed class Trimmer
    {
        ~Trimmer()
        {
            lock (Kept)
            {
                if (Environment.TickCount64 - _lastUse > IdleMilliseconds)
                {
                    Kept.Clear();
                }
            }

            if (!Environment.HasShutdownStarted)
            {
                GC.ReRegisterForFinalize(this);
            }
        }
    }
}

/// <summary>
/// Token lists of the lossy encoder's pass groups kept from one chunk or encode for the next, with the capacity they grew to.
/// A full collection lets go of them once none has been used for a few seconds.
/// </summary>
internal static class JxlTokenLists
{
    private const int MaxKept = 64;
    private const long IdleMilliseconds = 5000;
    private static readonly List<List<JxlToken>> Kept = [];
    private static long _lastUse;

    static JxlTokenLists() => _ = new Trimmer();

    /// <summary>Gets an empty list with room for at least <paramref name="capacity"/> tokens.</summary>
    internal static List<JxlToken> Rent(int capacity)
    {
        List<JxlToken>? list = null;
        lock (Kept)
        {
            _lastUse = Environment.TickCount64;
            if (Kept.Count > 0)
            {
                list = Kept[^1];
                Kept.RemoveAt(Kept.Count - 1);
            }
        }

        if (list is null)
        {
            return new List<JxlToken>(capacity);
        }

        list.Clear();
        list.EnsureCapacity(capacity);
        return list;
    }

    /// <summary>Keeps <paramref name="list"/> for a later <see cref="Rent"/>; the caller no longer uses it.</summary>
    internal static void Return(List<JxlToken>? list)
    {
        if (list is null)
        {
            return;
        }

        lock (Kept)
        {
            _lastUse = Environment.TickCount64;
            if (Kept.Count < MaxKept)
            {
                Kept.Add(list);
            }
        }
    }

    /// <summary>Empties the kept lists, if idle, whenever the collector finalizes it, which once it is old is at full collections.</summary>
    private sealed class Trimmer
    {
        ~Trimmer()
        {
            lock (Kept)
            {
                if (Environment.TickCount64 - _lastUse > IdleMilliseconds)
                {
                    Kept.Clear();
                }
            }

            if (!Environment.HasShutdownStarted)
            {
                GC.ReRegisterForFinalize(this);
            }
        }
    }
}
