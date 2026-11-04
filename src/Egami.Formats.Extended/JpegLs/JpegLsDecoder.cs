namespace Egami.Formats.Extended.JpegLs;

/// <summary>
/// Decodes a whole JPEG-LS stream held in memory: the header, then its scans one after another. When one scan holds every
/// component at full size the lines can be taken group by group as they decode (<see cref="Streams"/>); otherwise every scan
/// is decoded into planes first.
/// </summary>
internal sealed class JpegLsDecoder
{
    private readonly byte[] _data;
    private readonly int _length;
    private readonly Action<string, string> _warn;
    private JpegLsScanDecoder? _scan;
    private int _at;

    /// <summary>Reads the header and the first scan header of the stream.</summary>
    public JpegLsDecoder(byte[] data, int length, Action<string, string> warn)
    {
        _data = data;
        _length = length;
        _warn = warn;
        if (length < 2 || data[0] != 0xFF || data[1] != JpegLsMarker.Soi)
        {
            throw new JpegLsDataException("The data does not start with SOI.");
        }

        _at = 2;
        int sos = JpegLsSegments.NextScan(data, length, ref _at, Header, false, warn);
        if (sos < 0)
        {
            throw new JpegLsDataException("The stream has no scan.");
        }

        if (Header.HeightLater && Header.Oversize is not { Height: > 0 })
        {
            Header.Height = FindHeight(sos);
        }

        JpegLsSegments.Finish(Header);
        if (Header.Width <= 0 || Header.Height <= 0)
        {
            throw new JpegLsDataException($"The image size {Header.Width}×{Header.Height} is invalid.");
        }

        Validate();
        _scan = new JpegLsScanDecoder(data, length, sos, Header, warn);
        FirstScan = _scan;

        // The colour transforms apply to interleaved scans only, as CharLS writes and reads them.
        if (_scan.Interleave == 0)
        {
            Header.Transform = JpegLsColorTransform.None;
        }

        Streams = !Header.Subsampled && _scan.Components.Count() == Header.Components.Length;
    }

    /// <summary>
    /// Returns the header up to the first scan when its frame gives no height (0 lines and no LSE size), so the height comes
    /// from a DNL segment after the scan data; otherwise <see langword="null"/>.
    /// </summary>
    public static JpegLsHeader? HeightAfterScan(byte[] data, int length)
    {
        JpegLsHeader header = new();
        int at = 2;
        int sos = JpegLsSegments.NextScan(data, length, ref at, header, false, static (_, _) => { });
        return sos >= 0 && header.HeightLater && header.Oversize is not { Height: > 0 } ? header : null;
    }

    /// <summary>Gets what the header says.</summary>
    public JpegLsHeader Header { get; } = new();

    /// <summary>Gets the first scan, which gives NEAR and the interleave mode the details report.</summary>
    public JpegLsScanDecoder FirstScan { get; }

    /// <summary>Gets whether the first scan holds every component at full size, so its line groups are whole rows.</summary>
    public bool Streams { get; }

    /// <summary>Decodes the next row of every component into <paramref name="sink"/>; only when <see cref="Streams"/>.</summary>
    public void NextRow(JpegLsLineSink sink, bool strict)
    {
        if (_scan is null || _scan.Done)
        {
            throw new JpegLsDataException("The scan has no more lines.");
        }

        _scan.DecodeGroup(sink, strict);
    }

    /// <summary>Decodes every scan into <paramref name="sink"/>; components no scan holds are left as they are.</summary>
    public void DecodeAll(JpegLsLineSink sink, bool strict)
    {
        HashSet<int> seen = [];
        while (_scan is not null)
        {
            foreach (JpegLsComponent component in _scan.Components)
            {
                if (!seen.Add(component.Id))
                {
                    _warn("ScanRepeated", $"Component {component.Id} is in more than one scan; the later one wins.");
                }
            }

            while (!_scan.Done)
            {
                _scan.DecodeGroup(sink, strict);
            }

            _at = _scan.End;
            _scan = null;
            int sos = JpegLsSegments.NextScan(_data, _length, ref _at, Header, true, _warn);
            if (sos >= 0)
            {
                _scan = new JpegLsScanDecoder(_data, _length, sos, Header, _warn);
            }
        }

        foreach (JpegLsComponent component in Header.Components)
        {
            if (!seen.Contains(component.Id))
            {
                if (strict)
                {
                    throw new JpegLsDataException($"No scan holds component {component.Id}.");
                }

                _warn("ComponentMissing", $"No scan holds component {component.Id}; it is left black.");
            }
        }
    }

    private void Validate()
    {
        if (Header.Transform != JpegLsColorTransform.None && Header.Components.Length < 3)
        {
            _warn("TransformIgnored", $"The colour transform needs 3 components; this image has {Header.Components.Length}, so it was left off.");
            Header.Transform = JpegLsColorTransform.None;
        }
    }

    /// <summary>Finds the DNL segment that gives the height when the frame header said 0 lines (T.87 C.2.5).</summary>
    private int FindHeight(int sos)
    {
        for (int at = sos; at + 5 < _length; at++)
        {
            if (_data[at] == 0xFF && _data[at + 1] == JpegLsMarker.Dnl && _data[at + 2] == 0 && _data[at + 3] == 4)
            {
                return (_data[at + 4] << 8) | _data[at + 5];
            }
        }

        throw new JpegLsDataException("The frame header gives no height and no DNL segment follows the first scan.");
    }
}
