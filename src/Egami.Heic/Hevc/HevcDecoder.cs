namespace Egami.Heic.Hevc;

/// <summary>
/// Decodes the first picture of a sequence of HEVC NAL units (an HEIF item's hvcC parameter sets and data, or an Annex B stream):
/// its slice segments, then the in-loop filters. Only intra pictures are decoded (2.9.4 design, question 5).
/// </summary>
internal static class HevcDecoder
{
    /// <summary>Decodes the first picture of <paramref name="nals"/>.</summary>
    /// <param name="nals">The NAL units, parameter sets first.</param>
    /// <param name="limits">The limits the picture size is checked against.</param>
    /// <param name="hash">The picture's decoded picture hash SEI message, or null.</param>
    /// <param name="parallelism">How many threads tiles, wavefront rows and the loop filters may use.</param>
    /// <param name="cancellationToken">Cancels decoding.</param>
    /// <exception cref="CorruptDataException">The units are damaged.</exception>
    /// <exception cref="UnsupportedFormatException">The picture uses something 2.9.4 does not decode.</exception>
    internal static HevcPicture Decode(IEnumerable<HevcNal> nals, SafetyLimits limits, out HevcPictureHash? hash, int parallelism = 1,
        CancellationToken cancellationToken = default)
    {
        hash = null;
        HevcParameterSetStore store = new();
        HevcPicture? picture = null;
        List<HevcSegment> segments = [];
        HevcSliceHeader? slice = null;
        int sliceAddrRs = 0;
        foreach (HevcNal nal in nals)
        {
            if (picture is not null && (nal.Type is HevcNal.Vps or HevcNal.Sps or HevcNal.Pps or HevcNal.Aud or HevcNal.PrefixSei))
            {
                // A new access unit starts: the first picture is complete.
                break;
            }

            HevcSliceHeader? header = store.Parse(nal, out HevcPictureHash? found);
            if (found is not null && picture is not null)
            {
                hash ??= found;
            }

            if (header is null)
            {
                continue;
            }

            if (header.FirstSliceSegmentInPic && picture is not null)
            {
                break;
            }

            if (picture is null)
            {
                if (!header.FirstSliceSegmentInPic)
                {
                    throw HevcErrors.Corrupt("The first slice segment of the picture is missing.");
                }

                (HevcSps sps, HevcPps pps) = store.Active(header.PpsId);
                sps.CheckSupported();
                limits.CheckFrameSize(sps.Width, sps.Height);
                picture = new HevcPicture(sps, pps);
            }

            if (!header.Dependent)
            {
                slice = header;
                sliceAddrRs = header.SegmentAddress;
            }

            int firstTs = picture.Layout.RsToTs[header.SegmentAddress];
            if (segments.Count > 0)
            {
                if (firstTs <= segments[^1].FirstTs)
                {
                    throw HevcErrors.Corrupt("The slice segments of the picture are out of order.");
                }

                segments[^1].EndTs = firstTs;
            }

            segments.Add(new HevcSegment(nal, header, slice ?? header, sliceAddrRs, HevcSliceDecoder.SubstreamStarts(nal, header), firstTs));
        }

        if (picture is null)
        {
            throw HevcErrors.Corrupt("The NAL units hold no coded slice.");
        }

        segments[^1].EndTs = picture.Layout.TsToRs.Length;
        new HevcChainSet(Chains(picture, segments), parallelism, cancellationToken).Decode(picture);
        HevcLoopFilter.Apply(picture, parallelism, cancellationToken);
        return picture;
    }

    /// <summary>
    /// Splits the picture's slice segments into chains: one per tile, or with wavefronts one per CTB row of a tile, each piece
    /// of a segment one substream. When a segment has fewer entry points than substreams, the substreams can only be found one
    /// after another, so everything is one chain.
    /// </summary>
    private static List<HevcChain> Chains(HevcPicture picture, List<HevcSegment> segments)
    {
        HevcLayout layout = picture.Layout;
        HevcSps sps = picture.Sps;
        bool wavefronts = picture.Pps.EntropyCodingSync;
        int columns = layout.ColumnBoundaries.Length - 1;
        Dictionary<int, HevcChain> byKey = [];
        List<HevcChain> chains = [];
        bool entryPoints = true;
        foreach (HevcSegment segment in segments)
        {
            int substream = 0, pieceStart = segment.FirstTs, key = -1;
            for (int ts = segment.FirstTs; ts <= segment.EndTs; ts++)
            {
                int next = ts < segment.EndTs ? Key(ts) : -1;
                if (ts > segment.FirstTs && next != key)
                {
                    if (!byKey.TryGetValue(key, out HevcChain? chain))
                    {
                        int tile = layout.TileId[pieceStart];
                        bool firstRowOfTile = layout.TsToRs[pieceStart] / sps.WidthInCtbs == layout.RowBoundaries[tile / columns];
                        chain = new HevcChain
                        {
                            TileWidth = layout.ColumnBoundaries[(tile % columns) + 1] - layout.ColumnBoundaries[tile % columns],
                            Above = wavefronts && !firstRowOfTile ? byKey.GetValueOrDefault(key - 1) : null,
                        };
                        byKey[key] = chain;
                        chains.Add(chain);
                    }

                    chain.Pieces.Add((segment, substream++, pieceStart, ts));
                    pieceStart = ts;
                }

                key = next;
            }

            entryPoints &= substream == segment.Starts.Length;
        }

        if (entryPoints)
        {
            return chains;
        }

        HevcChain single = new() { TileWidth = sps.WidthInCtbs };
        foreach (HevcChain chain in chains)
        {
            single.Pieces.AddRange(chain.Pieces);
        }

        single.Pieces.Sort((a, b) => a.FirstTs.CompareTo(b.FirstTs));
        return [single];

        // The tile, and with wavefronts the CTB row: rows of one tile get consecutive keys.
        int Key(int ts) => (layout.TileId[ts] * sps.HeightInCtbs) + (wavefronts ? layout.TsToRs[ts] / sps.WidthInCtbs : 0);
    }

    /// <summary>Gets whether each component of a decoded picture (the whole picture, as D.3.19 hashes it) equals its hash.</summary>
    internal static bool Matches(HevcPicture picture, HevcPictureHash hash)
    {
        HevcSps sps = picture.Sps;
        for (int c = 0; c < picture.Planes.Length && c < hash.Values.Length; c++)
        {
            byte[] actual = hash.Compute(picture.Planes[c], picture.Widths[c], picture.Widths[c], picture.Heights[c], c == 0 ? sps.BitDepthLuma : sps.BitDepthChroma);
            if (!actual.AsSpan().SequenceEqual(hash.Values[c]))
            {
                return false;
            }
        }

        return true;
    }
}
