using System.IO.Compression;
using System.Text;

using Egami.Metafiles.Reading;

namespace Egami.Metafiles.Writing;

/// <summary>
/// Writes an EMF file ([MS-EMF] 2.2.9 and 2.3) with EMF+ records ([MS-EMFPLUS] 2.3) inside EMR_COMMENT records: the header is
/// written last, once the bounds, record count, size and handle count are known. EMF+ records gather until the next GDI record
/// and go into one comment, so the two record sets keep the order they were drawn in. Objects of both kinds are written once and
/// reused while they stay equal: the 64-entry EMF+ object table by least recent use, the GDI handle table up to
/// <see cref="MaxHandles"/> live objects, deleting the least recently used one that is not selected.
/// </summary>
internal sealed class EmfWriter
{
    /// <summary>The live GDI objects kept before the least recently used one is deleted.</summary>
    internal const int MaxHandles = 1024;

    /// <summary>The EMF+ object table's size ([MS-EMFPLUS] 3.1.1.1).</summary>
    internal const int PlusSlots = 64;

    /// <summary>The most object bytes one EmfPlusObject record carries; larger objects continue over several records, as GDI+ writes them.</summary>
    internal const int MaxObjectPiece = 0x7F00;

    /// <summary>How many EMF+ bytes a comment gathers before it is written.</summary>
    private const int CommentFlush = 0x8000;

    private readonly RecordBuffer _body = new(4096);
    private readonly RecordBuffer _plus = new(1024);
    private readonly RecordBuffer _scratch = new(256);
    private readonly Dictionary<byte[], int> _plusObjects = new(ByteComparer.Instance);
    private readonly byte[]?[] _plusSlots = new byte[PlusSlots][];
    private readonly long[] _plusUsed = new long[PlusSlots];
    private readonly Dictionary<byte[], uint> _gdiObjects = new(ByteComparer.Instance);
    private readonly Dictionary<uint, (byte[] Key, long Used)> _gdiHandles = [];
    private readonly Stack<uint> _freeHandles = new();
    private long _clock;
    private int _records;
    private uint _nextHandle = 1;

    /// <summary>Gets the number of EMF records written after the header.</summary>
    public int RecordCount => _records;

    // ---- EMF+ ----

    /// <summary>Adds an EMF+ record: type, flags, size, data size, data padded to four bytes.</summary>
    public void Plus(ushort type, ushort flags, ReadOnlySpan<byte> data)
    {
        int padded = (data.Length + 3) & ~3;
        if (_plus.Length > 0 && _plus.Length + 12 + padded > CommentFlush)
        {
            FlushPlus();
        }

        _plus.U16(type).U16(flags).U32((uint)(12 + padded)).U32((uint)data.Length).Bytes(data).Align();
    }

    /// <summary>Starts a scratch buffer for a record's data; valid until the next call that takes one.</summary>
    public RecordBuffer Data()
    {
        _scratch.Clear();
        return _scratch;
    }

    /// <summary>
    /// Returns the table index of an EMF+ object, writing an EmfPlusObject record when no equal object is in the table (or it was
    /// replaced since): pieces of at most <see cref="MaxObjectPiece"/> bytes with the continue flag and the total size when large.
    /// </summary>
    /// <param name="type">The ObjectType: 1 brush, 2 pen, 3 path, 4 region, 5 image.</param>
    /// <param name="data">The serialized object.</param>
    public int PlusObject(int type, byte[] data)
    {
        byte[] key = new byte[data.Length + 1];
        key[0] = (byte)type;
        data.CopyTo(key, 1);
        if (_plusObjects.TryGetValue(key, out int slot))
        {
            _plusUsed[slot] = ++_clock;
            return slot;
        }

        slot = 0;
        for (int i = 0; i < PlusSlots; i++)
        {
            if (_plusSlots[i] is null)
            {
                slot = i;
                break;
            }

            if (_plusUsed[i] < _plusUsed[slot])
            {
                slot = i;
            }
        }

        if (_plusSlots[slot] is byte[] old)
        {
            _plusObjects.Remove(old);
        }

        _plusSlots[slot] = key;
        _plusUsed[slot] = ++_clock;
        _plusObjects[key] = slot;
        ushort flags = (ushort)((type << 8) | slot);
        if (data.Length <= MaxObjectPiece)
        {
            Plus(0x4008, flags, data);
            return slot;
        }

        // A continued object: every piece carries the continue flag and TotalObjectSize, as GDI+ writes and Egami's reader joins them.
        for (int at = 0; at < data.Length; at += MaxObjectPiece)
        {
            int n = Math.Min(MaxObjectPiece, data.Length - at);
            RecordBuffer piece = new(n + 4);
            piece.U32((uint)data.Length).Bytes(data.AsSpan(at, n));
            Plus(0x4008, (ushort)(flags | 0x8000), piece.Span);
        }

        return slot;
    }

    /// <summary>Writes the EMF+ records gathered so far into an EMR_COMMENT (EMR_COMMENT_EMFPLUS).</summary>
    public void FlushPlus()
    {
        if (_plus.Length == 0)
        {
            return;
        }

        _body.U32(MetafileReader.EmrComment).U32((uint)(16 + _plus.Length)).U32((uint)(4 + _plus.Length)).U32(MetafileReader.EmfPlusIdentifier).Bytes(_plus.Span);
        _records++;
        _plus.Clear();
    }

    // ---- GDI ----

    /// <summary>Writes an EMF record: the EMF+ records before it first, then type, size and the data padded to four bytes.</summary>
    public void Gdi(int type, ReadOnlySpan<byte> data)
    {
        FlushPlus();
        int padded = (data.Length + 3) & ~3;
        _body.U32((uint)type).U32((uint)(8 + padded)).Bytes(data).Align();
        _records++;
    }

    /// <summary>
    /// Returns the handle of a GDI object, writing its creation record when no equal object is live: the record's type and its data
    /// after the handle field. When <see cref="MaxHandles"/> objects are live, the least recently used one that
    /// <paramref name="inUse"/> does not protect is deleted first.
    /// </summary>
    public uint GdiObject(int type, ReadOnlySpan<byte> afterHandle, Func<uint, bool> inUse)
    {
        byte[] key = new byte[afterHandle.Length + 4];
        BitConverter.TryWriteBytes(key, type);
        afterHandle.CopyTo(key.AsSpan(4));
        if (_gdiObjects.TryGetValue(key, out uint handle))
        {
            _gdiHandles[handle] = (key, ++_clock);
            return handle;
        }

        if (_gdiHandles.Count >= MaxHandles)
        {
            uint oldest = 0;
            long when = long.MaxValue;
            foreach ((uint h, (byte[] _, long used)) in _gdiHandles)
            {
                if (used < when && !inUse(h))
                {
                    (oldest, when) = (h, used);
                }
            }

            if (oldest != 0)
            {
                _gdiObjects.Remove(_gdiHandles[oldest].Key);
                _gdiHandles.Remove(oldest);
                Gdi(40, Data().U32(oldest).Span); // EMR_DELETEOBJECT
                _freeHandles.Push(oldest);
            }
        }

        handle = _freeHandles.Count > 0 ? _freeHandles.Pop() : _nextHandle++;
        _gdiObjects[key] = handle;
        _gdiHandles[handle] = (key, ++_clock);
        RecordBuffer record = new(afterHandle.Length + 4);
        record.U32(handle).Bytes(key.AsSpan(4)); // afterHandle may be the scratch buffer the deletion above reused
        Gdi(type, record.Span);
        return handle;
    }

    // ---- File ----

    /// <summary>The header's fields that only the canvas knows.</summary>
    /// <param name="Bounds">The drawn device pixels, inclusive, or an empty rectangle (right below left).</param>
    /// <param name="Frame">The picture's frame in 0.01 mm, inclusive.</param>
    /// <param name="DevicePixels">The reference device's size in pixels.</param>
    /// <param name="DeviceMicrometers">The reference device's size in micrometres.</param>
    /// <param name="Description">The application and picture names, or null.</param>
    internal readonly record struct Header((int L, int T, int R, int B) Bounds, (int L, int T, int R, int B) Frame, (int W, int H) DevicePixels, (int W, int H) DeviceMicrometers, string? Description);

    /// <summary>Writes EMR_EOF, then the whole file (header first) to the stream, gzip-compressed for EMZ.</summary>
    public void Finish(Stream stream, Header header, bool compress)
    {
        Gdi(MetafileReader.EmrEof, Data().U32(0).U32(16).U32(20).Span); // no palette; nSizeLast = this record's size

        RecordBuffer head = new(128);
        byte[] description = header.Description is null ? [] : Encoding.Unicode.GetBytes(header.Description);
        const int Fixed = 108;
        int size = Fixed + ((description.Length + 3) & ~3);
        head.U32(MetafileReader.EmrHeader).U32((uint)size);
        head.I32(header.Bounds.L).I32(header.Bounds.T).I32(header.Bounds.R).I32(header.Bounds.B);
        head.I32(header.Frame.L).I32(header.Frame.T).I32(header.Frame.R).I32(header.Frame.B);
        head.U32(MetafileReader.EmfSignature).U32(0x00010000);
        head.U32(checked((uint)(size + _body.Length))).U32((uint)(_records + 1));
        head.U16((int)Math.Min(_nextHandle, ushort.MaxValue)).U16(0);
        head.U32((uint)(description.Length / 2)).U32(description.Length > 0 ? Fixed : 0u).U32(0);
        head.I32(header.DevicePixels.W).I32(header.DevicePixels.H);
        head.I32((header.DeviceMicrometers.W + 500) / 1000).I32((header.DeviceMicrometers.H + 500) / 1000);
        head.U32(0).U32(0).U32(0); // no pixel format, no OpenGL
        head.I32(header.DeviceMicrometers.W).I32(header.DeviceMicrometers.H);
        head.Bytes(description).Align();

        if (compress)
        {
            using GZipStream gzip = new(stream, CompressionLevel.Optimal, leaveOpen: true);
            gzip.Write(head.Span);
            gzip.Write(_body.Span);
        }
        else
        {
            stream.Write(head.Span);
            stream.Write(_body.Span);
        }

        stream.Flush();
    }

    /// <summary>Compares byte arrays by content.</summary>
    private sealed class ByteComparer : IEqualityComparer<byte[]>
    {
        public static readonly ByteComparer Instance = new();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj)
        {
            HashCode hash = default;
            hash.AddBytes(obj);
            return hash.ToHashCode();
        }
    }
}
