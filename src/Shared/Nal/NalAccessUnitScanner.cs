namespace Egami.Nal;

/// <summary>What a NAL unit means for the access unit boundary (H.264 7.4.1.2.3, H.265 7.4.2.4.4), as the codec classifies it.</summary>
internal enum NalUnitRole
{
    /// <summary>Belongs to the access unit it arrives in: suffix SEI, filler data, end of sequence or stream, and the like.</summary>
    Continues,

    /// <summary>
    /// May only come before a picture's first slice (access unit delimiter, parameter sets, prefix SEI, and the like): after a
    /// slice it starts the next access unit.
    /// </summary>
    Prefix,

    /// <summary>The first slice of a picture: after a slice it starts the next access unit.</summary>
    FirstSlice,

    /// <summary>A later slice of the current picture.</summary>
    Slice,
}

/// <summary>
/// Groups NAL units into access units, one coded picture each, for streams that do not mark them, such as a raw Annex B file. The
/// codec classifies each unit (<see cref="NalUnitRole"/>), since H.264 needs its slice headers compared to find a picture's
/// first slice; the scanner applies the rule both standards share: the first prefix unit or first slice after a picture's slices
/// starts the next access unit.
/// </summary>
internal sealed class NalAccessUnitScanner
{
    private readonly int _maxUnits;
    private List<ReadOnlyMemory<byte>> _units = [];
    private bool _hasSlice;

    /// <summary>Initializes a scanner that refuses an access unit of more than <paramref name="maxUnits"/> NAL units.</summary>
    internal NalAccessUnitScanner(int maxUnits = 1 << 16)
    {
        _maxUnits = maxUnits;
    }

    /// <summary>Gets the number of NAL units of the access unit being gathered.</summary>
    internal int Pending => _units.Count;

    /// <summary>Adds the next NAL unit of the stream.</summary>
    /// <returns>The access unit this unit completes (it does not hold this unit), or <see langword="null"/>.</returns>
    /// <exception cref="CorruptDataException">The access unit has more NAL units than the limit.</exception>
    internal List<ReadOnlyMemory<byte>>? Add(ReadOnlyMemory<byte> nal, NalUnitRole role)
    {
        List<ReadOnlyMemory<byte>>? completed = null;
        if (_hasSlice && role is NalUnitRole.Prefix or NalUnitRole.FirstSlice)
        {
            completed = _units;
            _units = [];
            _hasSlice = false;
        }

        if (_units.Count >= _maxUnits)
        {
            throw NalErrors.Corrupt($"An access unit has more than {_maxUnits} NAL units.");
        }

        _units.Add(nal);
        _hasSlice |= role is NalUnitRole.FirstSlice or NalUnitRole.Slice;
        return completed;
    }

    /// <summary>Ends the stream: returns the last access unit, or <see langword="null"/> when no unit is pending.</summary>
    /// <remarks>Units after the last slice (an end of stream unit, a parameter set with no picture) end up in the last access unit.</remarks>
    internal List<ReadOnlyMemory<byte>>? Flush()
    {
        if (_units.Count == 0)
        {
            return null;
        }

        List<ReadOnlyMemory<byte>> completed = _units;
        _units = [];
        _hasSlice = false;
        return completed;
    }

    /// <summary>Drops the pending units, for a seek.</summary>
    internal void Reset()
    {
        _units.Clear();
        _hasSlice = false;
    }
}
