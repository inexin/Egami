using Egami.Metadata;

namespace Egami.Formats.Tiff;

/// <summary>
/// An open TIFF file: its source, header and chain of IFDs, read once. Pages are found by walking the chain, which reads only
/// each IFD's entries, never pixels.
/// </summary>
internal sealed class TiffImage : IDisposable
{
    private readonly List<TiffIfd> _chain = [];

    private TiffImage(TiffSource source, TiffFile file, bool strict)
    {
        Source = source;
        File = file;
        Strict = strict;
    }

    public TiffSource Source { get; }

    public TiffFile File { get; }

    public bool Strict { get; }

    /// <summary>Gets every IFD of the main chain, in order.</summary>
    public IReadOnlyList<TiffIfd> Chain => _chain;

    /// <summary>Gets the warnings walking the chain gave.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Opens the file and walks its chain of IFDs, checking each offset against the file and against a loop.</summary>
    public static TiffImage Open(EgamiInput input, ReaderOptions options)
    {
        TiffSource source = TiffSource.Open(input);
        try
        {
            bool strict = options.Recovery == RecoveryMode.Strict;
            TiffImage image = new(source, TiffFile.Open(source, input), strict);
            image.Walk();
            return image;
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    private void Walk()
    {
        HashSet<long> seen = [];
        long offset = File.FirstIfd;
        long limit = File.Input.Limits.MaxFrames;
        while (offset != 0)
        {
            if (!seen.Add(offset))
            {
                Problem(offset, $"The IFD chain loops back to {offset}; it was read up to there.");
                return;
            }

            if (offset < 0 || offset > Source.Length - (File.Big ? 8 : 2))
            {
                if (_chain.Count == 0)
                {
                    throw File.OutOfFile(offset, $"The IFD at {offset} lies outside the {Source.Length}-byte file.");
                }

                Problem(offset, $"The next IFD at {offset} lies outside the file; the pages before it were read.");
                return;
            }

            if (_chain.Count + 1 > limit)
            {
                File.Input.CheckFrameCount(_chain.Count + 1L);
            }

            TiffIfd ifd;
            try
            {
                ifd = File.ReadIfd(offset);
            }
            catch (CorruptDataException) when (!Strict && _chain.Count > 0)
            {
                Warnings.Add($"The IFD at {offset} is damaged; the pages before it were read.");
                return;
            }

            _chain.Add(ifd);
            offset = ifd.Next;
        }
    }

    private void Problem(long offset, string problem)
    {
        if (Strict)
        {
            throw File.Input.Corrupt(offset, problem);
        }

        Warnings.Add(problem);
    }

    /// <summary>Returns whether an IFD of the main chain is a full-resolution page: not reduced, not a transparency mask.</summary>
    public bool IsFullPage(int index)
    {
        (uint subfile, int photometric) = TiffPage.Kind(File, _chain[index]);
        return (subfile & 5) == 0 && photometric != 4;
    }

    /// <summary>Gets the chain indexes of the full-resolution pages.</summary>
    public List<int> FullPages()
    {
        List<int> pages = [];
        for (int i = 0; i < _chain.Count; i++)
        {
            if (IsFullPage(i))
            {
                pages.Add(i);
            }
        }

        return pages;
    }

    /// <summary>
    /// Returns the chain index of the page Load reads: <see cref="TiffReaderOptions.Page"/> among the full-resolution pages, the
    /// first by default, or the first IFD when the file has no full-resolution page.
    /// </summary>
    public (int Chain, int Sub) PageToLoad(ReaderOptions options)
    {
        if (options is TiffReaderOptions { Directory: (int chain, int sub) } && chain >= 0 && chain < _chain.Count)
        {
            return (chain, sub);
        }

        List<int> pages = FullPages();
        int wanted = (options as TiffReaderOptions)?.Page ?? 0;
        if (wanted < 0 || (pages.Count > 0 && wanted >= pages.Count) || (pages.Count == 0 && wanted > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(options), wanted, $"TIFF: the file has {pages.Count} full-resolution page{(pages.Count == 1 ? string.Empty : "s")}.");
        }

        return (pages.Count > 0 ? pages[wanted] : 0, -1);
    }

    /// <summary>
    /// Returns every directory LoadAll reads, in order: each full-resolution page, and with <see cref="TiffReaderOptions.IncludeReducedPages"/>
    /// the reduced-resolution pages and each page's SubIFDs after it. Transparency masks are never images.
    /// </summary>
    public List<(int Chain, int Sub)> Images(ReaderOptions options)
    {
        bool reduced = options is TiffReaderOptions { IncludeReducedPages: true };
        List<(int, int)> images = [];
        for (int i = 0; i < _chain.Count; i++)
        {
            (uint subfile, int photometric) = TiffPage.Kind(File, _chain[i]);
            if ((subfile & 4) != 0 || photometric == 4)
            {
                continue;
            }

            if ((subfile & 1) == 0 || reduced)
            {
                images.Add((i, -1));
            }

            if (reduced)
            {
                for (int s = 0; s < SubIfdOffsets(i).Length; s++)
                {
                    images.Add((i, s));
                }
            }
        }

        if (images.Count == 0 && _chain.Count > 0)
        {
            images.Add((0, -1));
        }

        return images;
    }

    /// <summary>Reads a page's description: a main-chain IFD, or one of its SubIFDs.</summary>
    public TiffPage Page(int index, int sub = -1)
    {
        TiffIfd ifd = _chain[index];
        if (sub >= 0)
        {
            ifd = File.ReadIfd(SubIfdOffsets(index)[sub]);
        }

        return TiffPage.Read(File, ifd, index, sub, Strict);
    }

    /// <summary>
    /// Returns the reduced-resolution page that serves a reduced decode of <paramref name="page"/>, when the file has a pyramid:
    /// SubIFDs of the page, or reduced pages right after it in the chain, of exactly ceil(size / 2^k) for k = 1, 2 and so on, with
    /// the page's pixel type. 2.2's plan then picks the largest power of two the minimum size allows, and the box average does the
    /// rest. Returns <see langword="null"/> when no level is used.
    /// </summary>
    public (TiffPage Level, Pyramid Pyramid)? PyramidFor(TiffPage page, ReaderOptions options)
    {
        Dictionary<int, TiffPage> levels = [];
        void Consider(Func<TiffPage> read)
        {
            try
            {
                TiffPage level = read();
                for (int k = 2; k <= 1 << 16; k *= 2)
                {
                    if (level.Width == (page.Width + k - 1) / k && level.Height == (page.Height + k - 1) / k && level.Natural == page.Natural)
                    {
                        levels.TryAdd(k, level);
                        return;
                    }
                }
            }
            catch (Exception e) when (e is CorruptDataException or NotSupportedException)
            {
                // A damaged or unsupported level is simply not used.
            }
        }

        for (int s = 0; s < SubIfdOffsets(page.Index).Length && s < 32; s++)
        {
            int sub = s;
            Consider(() => Page(page.Index, sub));
        }

        for (int i = page.Index + 1; i < _chain.Count && !IsFullPage(i); i++)
        {
            int index = i;
            if ((TiffPage.Kind(File, _chain[i]).SubfileType & 1) != 0)
            {
                Consider(() => Page(index));
            }
        }

        int max = 1;
        while (levels.ContainsKey(max * 2))
        {
            max *= 2;
        }

        if (max == 1)
        {
            return null;
        }

        Streaming.ReadPlan plan = Streaming.ReadPlan.Create(page.Width, page.Height, options.Region, options.MinimumSize, max);
        return plan.NativeScale > 1 ? (levels[plan.NativeScale], new Pyramid(page.Width, page.Height, plan.NativeScale, max, page)) : null;
    }

    /// <summary>Returns the offsets of a page's SubIFDs that lie inside the file.</summary>
    public long[] SubIfdOffsets(int index)
    {
        if (!_chain[index].TryGet(TiffTags.SubIfds, out TiffEntry entry) || entry.ValueAt < 0)
        {
            return [];
        }

        ulong[] values = File.Integers(entry, 1024);
        return [.. values.Where(v => v > 0 && v < (ulong)Source.Length).Select(v => (long)v)];
    }

    /// <summary>Builds the metadata of a page: format details, resolution, ICC, XMP, IPTC, EXIF and the warnings so far.</summary>
    public RasterMetadata Metadata(TiffPage page, ReaderOptions options)
    {
        RasterMetadata metadata = new() { FormatName = TiffFormat.Info.Name };
        TiffMetadata.Read(this, page, metadata, options);
        foreach (string warning in Warnings)
        {
            metadata.WarningList.Add(warning);
        }

        foreach (string warning in page.Warnings)
        {
            metadata.WarningList.Add(warning);
        }

        return metadata;
    }

    public void Dispose()
    {
        Source.Dispose();
    }
}
