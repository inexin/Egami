namespace Egami.Formats.Av1;

/// <summary>Decodes the frames of an AV1 stream as an AVIF item holds it.</summary>
internal static class Av1Decoder
{
    /// <summary>Gets the size of a stream's first frame from its sequence and frame headers alone, or null when it has none.</summary>
    internal static (int Width, int Height)? FirstFrameSize(byte[] data, string formatName)
    {
        Av1SequenceHeader? sequence = null;
        foreach (Av1Obu obu in Av1Obu.Split(data, formatName))
        {
            ReadOnlySpan<byte> payload = data.AsSpan(obu.PayloadStart, obu.PayloadLength);
            int idc = sequence?.OperatingPoints[0].Idc ?? 0;
            if (obu.Type is not (Av1ObuType.SequenceHeader or Av1ObuType.TemporalDelimiter) && obu.HasExtension && idc != 0
                && (((idc >> obu.TemporalId) & 1) == 0 || ((idc >> (obu.SpatialId + 8)) & 1) == 0))
            {
                continue;
            }

            if (obu.Type == Av1ObuType.SequenceHeader && sequence is null)
            {
                sequence = Av1SequenceHeader.Parse(payload, formatName);
            }
            else if (obu.Type is Av1ObuType.FrameHeader or Av1ObuType.Frame && sequence is not null)
            {
                Av1FrameHeader header = Av1FrameHeader.Parse(payload, sequence, obu.TemporalId, obu.SpatialId, formatName);
                return (header.UpscaledWidth, header.FrameHeight);
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes the first frame of a low-overhead (Section 5) stream, with operating point 0: its sequence header, frame header
    /// and tile groups, reconstructing its samples unless <paramref name="reconstruct"/> is false, and applying the in-loop
    /// filters unless <paramref name="filter"/> is false. Only key and intra-only frames decode; the frame header throws
    /// <see cref="UnsupportedFormatException"/> for others. Tiles, and the filters' rows and units, decode on up to
    /// <paramref name="parallelism"/> threads. A frame larger than <paramref name="limits"/> allow throws before its planes are made.
    /// </summary>
    internal static Av1Frame DecodeFirstFrame(byte[] data, string formatName, bool reconstruct = true, bool filter = true, int parallelism = 1,
        SafetyLimits? limits = null, CancellationToken cancellationToken = default)
    {
        Av1SequenceHeader? sequence = null;
        Av1Frame? frame = null;
        int tilesLeft = 0;
        List<(int Tile, int Offset, int Size)> tiles = [];
        foreach (Av1Obu obu in Av1Obu.Split(data, formatName))
        {
            ReadOnlySpan<byte> payload = data.AsSpan(obu.PayloadStart, obu.PayloadLength);
            int idc = sequence?.OperatingPoints[0].Idc ?? 0;
            if (obu.Type is not (Av1ObuType.SequenceHeader or Av1ObuType.TemporalDelimiter) && obu.HasExtension && idc != 0
                && (((idc >> obu.TemporalId) & 1) == 0 || ((idc >> (obu.SpatialId + 8)) & 1) == 0))
            {
                continue;
            }

            switch (obu.Type)
            {
                case Av1ObuType.SequenceHeader when sequence is null:
                    sequence = Av1SequenceHeader.Parse(payload, formatName);
                    break;
                case Av1ObuType.FrameHeader or Av1ObuType.Frame when frame is null && sequence is not null:
                    Av1FrameHeader header = Av1FrameHeader.Parse(payload, sequence, obu.TemporalId, obu.SpatialId, formatName);
                    if (header.ShowExistingFrame)
                    {
                        throw new CorruptDataException(formatName, obu.PayloadStart, "An AV1 stream shows an existing frame before decoding one.");
                    }

                    limits?.CheckFrameSize(header.UpscaledWidth, header.FrameHeight);
                    frame = new Av1Frame(header, formatName, reconstruct) { Parallelism = Math.Max(1, parallelism), CancellationToken = cancellationToken };
                    tilesLeft = header.TileCols * header.TileRows;
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
                frame.DecodeTiles(data, tiles);
                if (filter)
                {
                    frame.ApplyFilters();
                }

                return frame;
            }
        }

        throw new CorruptDataException(formatName, data.Length, sequence is null
            ? "The AV1 stream has no sequence header."
            : frame is null ? "The AV1 stream has no frame." : "The AV1 stream ends before the frame's last tile.")
        { IsTruncation = sequence is not null };
    }

    private static int AddTileGroup(Av1Frame frame, List<(int Tile, int Offset, int Size)> tiles, byte[] data, int start, int length)
    {
        List<(int Tile, int Offset, int Size)> group = frame.TileGroupTiles(data, start, length);
        tiles.AddRange(group);
        return group.Count;
    }
}
