using System.Runtime.CompilerServices;

namespace Egami.Fonts.Subsetting;

/// <summary>An offset in an <see cref="OtObject"/> to another, counted from the start of the object holding it.</summary>
internal readonly record struct OtLink(int At, int Width, OtObject Target);

/// <summary>
/// One structure of an OpenType table being written, such as a coverage, a lookup or a script: its bytes, with the offset fields
/// left 0, and the links that fill them in once <see cref="OtGraph.Pack"/> has placed every object.
/// </summary>
internal sealed class OtObject
{
    public OtObject(byte[] data, OtLink[] links, int group)
    {
        Data = data;
        Links = links;
        Group = group;
    }

    public byte[] Data { get; }

    public OtLink[] Links { get; }

    /// <summary>Gets the packing group: lower groups are placed first, each group's objects together.</summary>
    public int Group { get; }

    internal int Position { get; set; }

    internal int Level { get; set; }

    internal int Order { get; set; }

    internal int Pending { get; set; }
}

/// <summary>Writes an object's bytes and offsets; <see cref="OtGraph.Add"/> turns it into an <see cref="OtObject"/>.</summary>
internal sealed class OtWriter
{
    private readonly ByteWriter _bytes = new(64);
    private readonly List<OtLink> _links = [];

    public int Length => _bytes.Length;

    public void U8(int value) => _bytes.U8(value);

    public void U16(int value) => _bytes.U16(value);

    public void U24(int value) => _bytes.U24(value);

    public void U32(uint value) => _bytes.U32(value);

    public void Bytes(ReadOnlySpan<byte> bytes) => _bytes.Bytes(bytes);

    public void PatchU16(int at, int value) => _bytes.PatchU16(at, value);

    /// <summary>A 16-bit offset to <paramref name="target"/>, or a null offset.</summary>
    public void Offset16(OtObject? target) => Offset(2, target);

    public void Offset32(OtObject? target) => Offset(4, target);

    private void Offset(int width, OtObject? target)
    {
        if (target is not null)
        {
            _links.Add(new OtLink(_bytes.Length, width, target));
        }

        for (int i = 0; i < width; i++)
        {
            _bytes.U8(0);
        }
    }

    internal byte[] TakeBytes() => _bytes.ToArray();

    internal OtLink[] TakeLinks() => [.. _links];
}

/// <summary>
/// The objects of one OpenType table being written, with identical objects (same bytes, same links) kept once, and the packer
/// that orders them so every offset fits its field, as HarfBuzz's serializer and repacker do (hb-serialize.hh, hb-repacker.hh).
/// </summary>
internal sealed class OtGraph
{
    private readonly Stack<Dictionary<OtObject, OtObject>> _pools = new([new Dictionary<OtObject, OtObject>(ObjectComparer.Instance)]);

    /// <summary>Gets or sets the group new objects are put in.</summary>
    public int Group { get; set; }

    public static OtWriter Writer() => new();

    /// <summary>The object the writer holds, or an identical one added before in the current scope.</summary>
    public OtObject Add(OtWriter writer)
    {
        var candidate = new OtObject(writer.TakeBytes(), writer.TakeLinks(), Group);
        Dictionary<OtObject, OtObject> pool = _pools.Peek();
        if (pool.TryGetValue(candidate, out OtObject? existing))
        {
            return existing;
        }

        pool.Add(candidate, candidate);
        return candidate;
    }

    /// <summary>Starts a scope whose objects are shared only with each other (an extension subtable's, so it packs on its own).</summary>
    public void PushScope() => _pools.Push(new Dictionary<OtObject, OtObject>(ObjectComparer.Instance));

    public void PopScope() => _pools.Pop();

    /// <summary>
    /// Lays the objects reachable from <paramref name="root"/> out after it, parents before children, by group and then breadth
    /// first (or depth first), and fills in the offsets; null when an offset does not fit its field in that order.
    /// </summary>
    public static byte[]? Pack(OtObject root, bool breadthFirst)
    {
        List<OtObject> all = [];
        var seen = new HashSet<OtObject>(ReferenceEqualityComparer.Instance);
        Preorder(root, seen, all);
        foreach (OtObject o in all)
        {
            o.Pending = 0;
            o.Level = int.MaxValue;
        }

        foreach (OtObject o in all)
        {
            foreach (OtLink link in o.Links)
            {
                link.Target.Pending++;
            }
        }

        // Levels: the shortest number of links from the root.
        var queue = new Queue<OtObject>();
        root.Level = 0;
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            OtObject o = queue.Dequeue();
            foreach (OtLink link in o.Links)
            {
                if (link.Target.Level == int.MaxValue)
                {
                    link.Target.Level = o.Level + 1;
                    queue.Enqueue(link.Target);
                }
            }
        }

        // Kahn's topological order: an object is placed once every object whose offsets lead to it has been.
        var ready = new PriorityQueue<OtObject, (int Group, int Level, int Order)>();
        ready.Enqueue(root, Key(root, breadthFirst));
        int position = 0;
        int placed = 0;
        var order = new List<OtObject>(all.Count);
        while (ready.Count > 0)
        {
            OtObject o = ready.Dequeue();
            o.Position = position;
            position += o.Data.Length;
            order.Add(o);
            placed++;
            foreach (OtLink link in o.Links)
            {
                Release(link.Target, ready, breadthFirst);
            }
        }

        if (placed != all.Count)
        {
            return null;
        }

        byte[] output = new byte[position];
        foreach (OtObject o in order)
        {
            o.Data.CopyTo(output, o.Position);
            foreach (OtLink link in o.Links)
            {
                long delta = link.Target.Position - o.Position;
                if (delta < 0 || delta >= 1L << (8 * link.Width))
                {
                    return null;
                }

                int at = o.Position + link.At;
                for (int i = link.Width - 1; i >= 0; i--)
                {
                    output[at + i] = (byte)delta;
                    delta >>= 8;
                }
            }
        }

        return output;
    }

    private static (int, int, int) Key(OtObject o, bool breadthFirst) => (o.Group, breadthFirst ? o.Level : 0, o.Order);

    private static void Release(OtObject target, PriorityQueue<OtObject, (int, int, int)> ready, bool breadthFirst)
    {
        if (--target.Pending == 0)
        {
            ready.Enqueue(target, Key(target, breadthFirst));
        }
    }

    private static void Preorder(OtObject root, HashSet<OtObject> seen, List<OtObject> all)
    {
        var stack = new Stack<OtObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            OtObject o = stack.Pop();
            if (!seen.Add(o))
            {
                continue;
            }

            o.Order = all.Count;
            all.Add(o);
            for (int i = o.Links.Length - 1; i >= 0; i--)
            {
                if (!seen.Contains(o.Links[i].Target))
                {
                    stack.Push(o.Links[i].Target);
                }
            }
        }
    }

    /// <summary>Objects are equal when their bytes and links are; children are already shared, so links compare by reference.</summary>
    private sealed class ObjectComparer : IEqualityComparer<OtObject>
    {
        public static readonly ObjectComparer Instance = new();

        public bool Equals(OtObject? x, OtObject? y)
        {
            if (ReferenceEquals(x, y))
            {
                return true;
            }

            if (x is null || y is null || x.Links.Length != y.Links.Length || !x.Data.AsSpan().SequenceEqual(y.Data))
            {
                return false;
            }

            for (int i = 0; i < x.Links.Length; i++)
            {
                OtLink a = x.Links[i], b = y.Links[i];
                if (a.At != b.At || a.Width != b.Width || !ReferenceEquals(a.Target, b.Target))
                {
                    return false;
                }
            }

            return true;
        }

        public int GetHashCode(OtObject o)
        {
            var hash = default(HashCode);
            hash.AddBytes(o.Data);
            foreach (OtLink link in o.Links)
            {
                hash.Add(link.At);
                hash.Add(RuntimeHelpers.GetHashCode(link.Target));
            }

            return hash.ToHashCode();
        }
    }
}
