namespace Egami.Video.Hardware.Decoding;

/// <summary>
/// The slice NAL units of the picture being gathered, copied out of their packets: a picture is submitted when the next one
/// starts, by which time the caller may have disposed the packets and their pooled memory been reused.
/// </summary>
internal sealed class SliceBuffer
{
    private readonly List<ReadOnlyMemory<byte>> _slices = [];
    private readonly List<int> _lengths = [];
    private byte[] _data = new byte[1 << 16];
    private int _used;

    public int Count => _slices.Count;

    public ReadOnlyMemory<byte> this[int index] => _slices[index];

    /// <summary>Copies a slice NAL unit in.</summary>
    public void Add(ReadOnlyMemory<byte> nal)
    {
        if (_used + nal.Length > _data.Length)
        {
            // Grow and point the slices kept so far at the new array.
            byte[] larger = new byte[Math.Max(_data.Length * 2, _used + nal.Length)];
            _data.AsSpan(0, _used).CopyTo(larger);
            _data = larger;
            int at = 0;
            for (int i = 0; i < _slices.Count; i++)
            {
                _slices[i] = new ReadOnlyMemory<byte>(_data, at, _lengths[i]);
                at += _lengths[i];
            }
        }

        nal.Span.CopyTo(_data.AsSpan(_used));
        _slices.Add(new ReadOnlyMemory<byte>(_data, _used, nal.Length));
        _lengths.Add(nal.Length);
        _used += nal.Length;
    }

    public void Clear()
    {
        _slices.Clear();
        _lengths.Clear();
        _used = 0;
    }

    public List<ReadOnlyMemory<byte>>.Enumerator GetEnumerator() => _slices.GetEnumerator();
}
