namespace Egami.Formats.Av1;

/// <summary>
/// The state of an AV1 stream across frames (sections 7.5, 7.20 and 7.21): the sequence header, the eight reference slots and the
/// operating point. Each temporal unit decodes its frames in order, updates the slots and gives the frames it shows.
/// </summary>
internal sealed class Av1Stream
{
    private readonly string _formatName;
    private byte[] _sequenceBytes = [];

    /// <summary>Starts a stream.</summary>
    /// <param name="formatName">The format named in errors.</param>
    internal Av1Stream(string formatName) => _formatName = formatName;

    /// <summary>Gets or sets the operating point whose layers decode (Q4: 0 unless asked).</summary>
    internal int OperatingPoint { get; set; }

    /// <summary>Gets or sets whether every shown frame of a temporal unit is output, not only the last (the highest spatial layer).</summary>
    internal bool OutputAllLayers { get; set; }

    /// <summary>Gets or sets whether frames reconstruct their samples, or only parse.</summary>
    internal bool Reconstruct { get; set; } = true;

    /// <summary>Gets or sets whether the in-loop filters run.</summary>
    internal bool Filter { get; set; } = true;

    /// <summary>Gets or sets how many threads tiles and filters may use.</summary>
    internal int Parallelism { get; set; } = 1;

    /// <summary>Gets or sets the limits a frame's size is checked against, or null.</summary>
    internal SafetyLimits? Limits { get; set; }

    /// <summary>Gets the active sequence header, or null before the first.</summary>
    internal Av1SequenceHeader? Sequence { get; private set; }

    /// <summary>Gets the fields of the sequence and frame headers of the last temporal unit, in order.</summary>
    internal List<KeyValuePair<string, long>> LastHeaders { get; } = [];

    /// <summary>Gets the reference slots.</summary>
    internal Av1Frame?[] Slots { get; } = new Av1Frame?[8];

    /// <summary>Drops the references and the sequence header; the next temporal unit must start a sequence.</summary>
    internal void Reset()
    {
        Array.Clear(Slots);
        Sequence = null;
        _sequenceBytes = [];
    }

    /// <summary>
    /// Decodes a temporal unit of low-overhead OBUs (section 5) and returns the frames it shows, in order: the last one only, unless
    /// <see cref="OutputAllLayers"/>.
    /// </summary>
    internal List<Av1Frame> Decode(byte[] data, int offset, int length, CancellationToken cancellationToken = default) =>
        [.. DecodeUnit(data, offset, length, cancellationToken).Select(o => o.Frame)];

    /// <summary>
    /// Decodes a temporal unit like <see cref="Decode"/>, saying of each frame whether show_existing_frame showed it again.
    /// </summary>
    internal List<(Av1Frame Frame, bool Existing)> DecodeUnit(byte[] data, int offset, int length, CancellationToken cancellationToken = default)
    {
        List<(Av1Frame Frame, bool Existing)> shown = [];
        LastHeaders.Clear();
        Av1Frame? frame = null;
        int tilesLeft = 0;
        List<(int Tile, int Offset, int Size)> tiles = [];
        foreach (Av1Obu o in Av1Obu.Split(data.AsSpan(offset, length), _formatName))
        {
            Av1Obu obu = o with { PayloadStart = o.PayloadStart + offset };
            ReadOnlySpan<byte> payload = data.AsSpan(obu.PayloadStart, obu.PayloadLength);
            if (obu.Type is not (Av1ObuType.SequenceHeader or Av1ObuType.TemporalDelimiter) && obu.HasExtension && Sequence is not null)
            {
                int idc = Sequence.OperatingPoints[Math.Min(OperatingPoint, Sequence.OperatingPoints.Length - 1)].Idc;
                if (idc != 0 && (((idc >> obu.TemporalId) & 1) == 0 || ((idc >> (obu.SpatialId + 8)) & 1) == 0))
                {
                    continue;
                }
            }

            switch (obu.Type)
            {
                case Av1ObuType.SequenceHeader:
                    if (Sequence is null || !payload.SequenceEqual(_sequenceBytes))
                    {
                        // A new coded video sequence: its first frame is a key frame, which refreshes every slot.
                        Sequence = Av1SequenceHeader.Parse(payload, _formatName);
                        _sequenceBytes = payload.ToArray();
                        Array.Clear(Slots);
                    }

                    Describe(Sequence.Describe());

                    break;
                case Av1ObuType.FrameHeader or Av1ObuType.Frame when frame is null:
                    if (Sequence is null)
                    {
                        throw new CorruptDataException(_formatName, obu.PayloadStart, "The AV1 stream has a frame before its sequence header.");
                    }

                    Av1FrameHeader header = Av1FrameHeader.Parse(payload, Sequence, obu.TemporalId, obu.SpatialId, _formatName, Slots);
                    Describe(header.Describe());
                    if (header.ShowExistingFrame)
                    {
                        ShowExisting(header, shown, obu.PayloadStart);
                        break;
                    }

                    Limits?.CheckFrameSize(header.UpscaledWidth, header.FrameHeight);
                    frame = new Av1Frame(header, _formatName, Reconstruct)
                    {
                        Parallelism = Math.Max(1, Parallelism),
                        CancellationToken = cancellationToken,
                        Showable = header.ShowableFrame,
                        TemporalId = obu.TemporalId,
                        SpatialId = obu.SpatialId,
                    };
                    if (header.UseRefFrameMvs)
                    {
                        Av1MotionField.Setup(frame);
                    }

                    tilesLeft = header.TileCols * header.TileRows;
                    tiles.Clear();
                    if (obu.Type == Av1ObuType.Frame)
                    {
                        int skip = (header.BitLength + 7) >> 3;
                        tilesLeft -= AddTileGroup(frame, tiles, data, obu.PayloadStart + skip, obu.PayloadLength - skip);
                    }

                    break;
                case Av1ObuType.TileGroup when frame is not null && tilesLeft > 0:
                    tilesLeft -= AddTileGroup(frame, tiles, data, obu.PayloadStart, obu.PayloadLength);
                    break;
            }

            if (frame is not null && tilesLeft <= 0)
            {
                Finish(frame, data, tiles, shown);
                frame = null;
            }
        }

        if (frame is not null && shown.Count == 0)
        {
            // A unit cut off after a frame it showed gives that frame, as dav1d does; the cut-off frame is dropped.
            throw new CorruptDataException(_formatName, offset + length, "The AV1 temporal unit ends before its frame's last tile.") { IsTruncation = true };
        }

        if (!OutputAllLayers && shown.Count > 1)
        {
            shown.RemoveRange(0, shown.Count - 1);
        }

        return shown;
    }

    private void Describe(List<(string Name, long Value)> fields)
    {
        foreach ((string name, long value) in fields)
        {
            LastHeaders.Add(new(name, value));
        }
    }

    private void ShowExisting(Av1FrameHeader header, List<(Av1Frame Frame, bool Existing)> shown, int at)
    {
        Av1Frame existing = Slots[header.FrameToShowMapIdx]
            ?? throw new CorruptDataException(_formatName, at, "An AV1 frame shows an empty reference slot.") { IsMissingReference = true };
        if (!existing.Showable)
        {
            throw new CorruptDataException(_formatName, at, "An AV1 frame shows a frame that is not showable.");
        }

        shown.Add((existing, true));
        if (existing.Header.FrameType == Av1FrameType.Key)
        {
            // The reference frame loading process, then an update of every slot; a key frame shows this way only once.
            existing.Showable = false;
            Slots.AsSpan().Fill(existing);
        }
    }

    private void Finish(Av1Frame frame, byte[] data, List<(int Tile, int Offset, int Size)> tiles, List<(Av1Frame Frame, bool Existing)> shown)
    {
        frame.DecodeTiles(data, tiles);
        if (Filter)
        {
            frame.ApplyFilters();
        }

        Av1MotionField.Save(frame);
        for (int i = 0; i < 8; i++)
        {
            if (((frame.Header.RefreshFrameFlags >> i) & 1) != 0)
            {
                Slots[i] = frame;
            }
        }

        if (frame.Header.ShowFrame)
        {
            shown.Add((frame, false));
        }
    }

    private static int AddTileGroup(Av1Frame frame, List<(int Tile, int Offset, int Size)> tiles, byte[] data, int start, int length)
    {
        List<(int Tile, int Offset, int Size)> group = frame.TileGroupTiles(data, start, length);
        tiles.AddRange(group);
        return group.Count;
    }
}
