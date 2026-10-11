using System.Buffers.Binary;
using System.Text;

namespace Egami.Formats.Jbig2;

/// <summary>A segment refers to a segment that is neither earlier in the stream nor in the globals; never recovered from.</summary>
internal sealed class Jbig2MissingSegmentException(string message) : Exception(message);

/// <summary>A symbol dictionary's exported symbols and, when it retained them, its generic and refinement contexts (7.4.2.2).</summary>
internal sealed class Jbig2SymbolDictionary(Jbig2Bitmap[] symbols, byte[]? gb, byte[]? gr)
{
    internal Jbig2Bitmap[] Symbols { get; } = symbols;

    internal byte[]? RetainedGb { get; } = gb;

    internal byte[]? RetainedGr { get; } = gr;
}

/// <summary>What decoding a page gives: the page bitmap and what Jbig2Details reports.</summary>
internal sealed class Jbig2Page
{
    internal required Jbig2Bitmap Bitmap { get; set; }

    internal required Jbig2PageInfo Info { get; init; }

    internal Jbig2RegionKinds Regions { get; set; }

    internal List<KeyValuePair<string, string>> Comments { get; } = [];

    internal List<string> Warnings { get; } = [];
}

/// <summary>
/// Decodes JBIG2 pages from a parsed stream (T.88 clause 8): dictionaries are decoded once each, when a page needs them, and
/// regions are composed onto the page buffer or kept in auxiliary buffers as 8.2 says. A decoder serves one thread.
/// </summary>
internal sealed class Jbig2Decoder
{
    private readonly byte[] _data;
    private readonly List<Jbig2Segment> _segments = [];
    private readonly Dictionary<Jbig2Segment, object> _results = [];
    private readonly Dictionary<uint, List<int>> _byNumber = [];
    private readonly Jbig2Budget _budget;
    private readonly bool _strict;
    private readonly int _firstOwn;
    private readonly Jbig2Globals? _globals;

    internal Jbig2Decoder(Jbig2Stream stream, Jbig2Globals? globals, Jbig2Budget budget, bool strict)
    {
        Stream = stream;
        _data = stream.Data;
        _globals = globals;
        _budget = budget;
        _strict = strict;
        if (globals is not null)
        {
            _segments.AddRange(globals.Stream.Segments);
        }

        _firstOwn = _segments.Count;
        _segments.AddRange(stream.Segments);
        for (int i = 0; i < _segments.Count; i++)
        {
            if (!_byNumber.TryGetValue(_segments[i].Number, out List<int>? list))
            {
                _byNumber[_segments[i].Number] = list = [];
            }

            list.Add(i);
        }
    }

    internal Jbig2Stream Stream { get; }

    /// <summary>Gets the page information segments of the stream, in order: one per page.</summary>
    internal List<int> PageSegments()
    {
        List<int> pages = [];
        for (int i = _firstOwn; i < _segments.Count; i++)
        {
            if (_segments[i].Type == Jbig2Segment.PageInformation)
            {
                pages.Add(i);
            }
        }

        return pages;
    }

    internal Jbig2PageInfo PageInfo(int index) => Jbig2PageInfo.Read(_data, _segments[index]);

    /// <summary>
    /// Returns a page's size from headers alone: the page information segment's, or for a page of unknown height the last end of
    /// stripe's end row plus one.
    /// </summary>
    internal (int Width, int Height) PageSize(int pageSegment)
    {
        Jbig2PageInfo info = PageInfo(pageSegment);
        if (info.Width > int.MaxValue)
        {
            throw new InvalidDataException("The page is too wide.");
        }

        if (!info.HeightUnknown)
        {
            return info.Height > int.MaxValue ? throw new InvalidDataException("The page is too tall.") : ((int)info.Width, (int)info.Height);
        }

        uint page = _segments[pageSegment].Page;
        long height = 0;
        for (int i = pageSegment + 1; i < _segments.Count; i++)
        {
            Jbig2Segment segment = _segments[i];
            if (segment.Page != page)
            {
                continue;
            }

            if (segment.Type == Jbig2Segment.EndOfStripe && segment.DataLength >= 4)
            {
                height = Math.Max(height, BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(segment.DataStart)) + 1L);
            }
            else if (segment.Type is Jbig2Segment.EndOfPage or Jbig2Segment.PageInformation)
            {
                break;
            }
        }

        return ((int)info.Width, (int)Math.Min(height, int.MaxValue));
    }

    /// <summary>Gets whether any segment of the page refers to the globals.</summary>
    internal bool UsesGlobals(int pageSegment)
    {
        if (_globals is null)
        {
            return false;
        }

        uint page = _segments[pageSegment].Page;
        for (int i = pageSegment; i < _segments.Count && _segments[i].Page == page; i++)
        {
            foreach (uint reference in _segments[i].References)
            {
                if (Find(i, reference) < _firstOwn)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Decodes the page whose information segment is at <paramref name="pageSegment"/>.</summary>
    internal Jbig2Page DecodePage(int pageSegment, CancellationToken cancellationToken)
    {
        Jbig2PageSession session = OpenPage(pageSegment, striped: false);
        while (session.Step(cancellationToken))
        {
        }

        return session.Finish();
    }

    /// <summary>
    /// Starts decoding a page. With <paramref name="striped"/> a page coded in stripes is kept one stripe at a time, each
    /// stripe's rows final once <see cref="Jbig2PageSession.Step"/> returns; otherwise the whole page is kept.
    /// </summary>
    internal Jbig2PageSession OpenPage(int pageSegment, bool striped)
    {
        uint page = _segments[pageSegment].Page;
        int end = pageSegment + 1;
        while (end < _segments.Count && !(_segments[end].Page == page && _segments[end].Type == Jbig2Segment.EndOfPage)
            && !(_segments[end].Type == Jbig2Segment.PageInformation && _segments[end].Page != page))
        {
            end++;
        }

        // The page's own segments, and the dictionaries they need (transitively), are decoded in stream order.
        bool[] needed = new bool[_segments.Count];
        for (int i = end - 1; i >= 0; i--)
        {
            Jbig2Segment segment = _segments[i];
            if ((i > pageSegment && segment.Page == page && !segment.IsDictionary) || needed[i])
            {
                foreach (uint reference in segment.References)
                {
                    needed[Find(i, reference)] = true;
                }
            }
        }

        Jbig2PageInfo info = PageInfo(pageSegment);
        (int width, int height) = PageSize(pageSegment);
        return new Jbig2PageSession(this, pageSegment, end, needed, info, width, height, striped && info.Striped && info.MaxStripeSize > 0);
    }

    /// <summary>Processes segment <paramref name="i"/> of a page being decoded; returns whether it ended a stripe.</summary>
    private bool Process(Jbig2PageSession session, int i)
    {
        Jbig2Segment segment = _segments[i];
        if (segment.IsDictionary)
        {
            if (session.Needed[i] && !_results.ContainsKey(segment) && (_globals is null || i >= _firstOwn || !_globals.Results.ContainsKey(segment)))
            {
                _results[segment] = DecodeDictionary(i);
            }

            return false;
        }

        if (i <= session.PageSegment || segment.Page != session.Info.Number)
        {
            return false;
        }

        Jbig2Page page = session.Page;
        switch (segment.Type)
        {
            case Jbig2Segment.EndOfStripe:
                if (segment.DataLength >= 4)
                {
                    session.EndStripe((int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(segment.DataStart)), int.MaxValue - 1L));
                    return true;
                }

                return false;
            case Jbig2Segment.ColourPalette:
                page.Warnings.Add("A colour palette segment (T.88 Amendment 3) was skipped; the page decodes as bilevel.");
                return false;
            case Jbig2Segment.Extension:
                Extension(segment, page);
                return false;
            case Jbig2Segment.PageInformation or Jbig2Segment.EndOfPage or Jbig2Segment.EndOfFile or Jbig2Segment.Profiles:
                return false;
        }

        if (!segment.IsRegion)
        {
            if (segment.Type is not (1 or 2 or 3 or 5 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15))
            {
                page.Warnings.Add($"Segment {segment.Number} has the reserved type {segment.Type} and was skipped.");
            }

            return false;
        }

        try
        {
            Region(i, session);
        }
        catch (InvalidDataException e) when (!_strict)
        {
            page.Warnings.Add($"Region segment {segment.Number} is damaged and was skipped: {e.Message}");
        }
        catch (EndOfStreamException e) when (!_strict)
        {
            page.Warnings.Add($"Region segment {segment.Number} is damaged and was skipped: {e.Message}");
        }

        return false;
    }

    /// <summary>Decodes a region segment and composes it as T.88 8.2 step 5 says.</summary>
    private void Region(int index, Jbig2PageSession session)
    {
        Jbig2Page page = session.Page;
        Jbig2Segment segment = _segments[index];
        Jbig2RegionInfo info = Jbig2RegionInfo.Read(_data, segment);
        if (info.ColourExtension)
        {
            page.Warnings.Add($"Region segment {segment.Number} uses the colour extension; it decodes as bilevel.");
        }

        Jbig2Bitmap bitmap;
        if (segment.IsRefinement)
        {
            page.Regions |= Jbig2RegionKinds.Refinement;
            int referred = segment.References.Length == 0 ? -1 : Find(index, segment.References[0]);
            Jbig2Bitmap reference;
            Jbig2RegionInfo at = info;
            if (referred >= 0)
            {
                if (!session.Auxiliary.Remove(referred, out (Jbig2Bitmap Bitmap, Jbig2RegionInfo Info) aux))
                {
                    throw new InvalidDataException("A refinement region refers to a segment that has no intermediate region to refine.");
                }

                reference = aux.Bitmap;
                at = aux.Info with { Combine = info.Combine };
            }
            else
            {
                reference = session.Crop(info);
            }

            bitmap = RefinementRegion(segment, info, reference);
            if (segment.IsIntermediate)
            {
                session.Auxiliary[index] = (bitmap, at);
                return;
            }

            session.Compose(bitmap, at.X, at.Y, referred >= 0 ? info.Combine : Jbig2Combine.Replace);
            return;
        }

        if (segment.Type is 6 or 7 && !info.ColourExtension && info.Combine == Jbig2Combine.Or && TextDrawsDirectly(segment))
        {
            // Symbols drawn straight onto the page, as composing the region with OR would.
            session.EnsureRows(info.Y, info.Height);
            TextRegion(index, info, page, session.Window, info.X, info.Y - session.Top);
            return;
        }

        bitmap = segment.Type switch
        {
            4 or 6 or 7 => TextRegion(index, info, page),
            20 or 22 or 23 => HalftoneRegion(index, info, page),
            _ => GenericRegion(segment, info, page),
        };
        if (segment.IsIntermediate)
        {
            session.Auxiliary[index] = (bitmap, info);
            return;
        }

        if (info.X < 0 || info.Y < 0 || (long)info.X + bitmap.Width > session.Width || (long)info.Y + bitmap.Height > session.Height)
        {
            page.Warnings.Add($"Region segment {segment.Number} lies partly outside the page and was clipped.");
        }

        session.Compose(bitmap, info.X, info.Y, info.Combine);
    }

    /// <summary>
    /// One page being decoded: its segments run in order, regions composed onto a window of the page buffer. The window is the
    /// whole page, or, for a striped page read in stripes, the rows below the last end row down to the maximum stripe size,
    /// cleared to the default pixel at each new stripe (the second strategy of T.88 8.2 step 2).
    /// </summary>
    internal sealed class Jbig2PageSession
    {
        private readonly Jbig2Decoder _decoder;
        private readonly int _end;
        private readonly bool _striped;
        private int _next;

        internal Jbig2PageSession(Jbig2Decoder decoder, int pageSegment, int end, bool[] needed, Jbig2PageInfo info, int width, int height, bool striped)
        {
            _decoder = decoder;
            PageSegment = pageSegment;
            _end = end;
            Needed = needed;
            Info = info;
            Width = width;
            Height = height;
            _striped = striped;
            int rows = striped ? Math.Min(info.MaxStripeSize, height) : info.HeightUnknown ? 0 : height;
            Window = decoder._budget.Bitmap(width, rows);
            if (info.DefaultPixel != 0)
            {
                Window.Fill(1);
            }

            Page = new Jbig2Page { Bitmap = Window, Info = info };
        }

        internal int PageSegment { get; }

        internal bool[] Needed { get; }

        internal Jbig2PageInfo Info { get; }

        internal Jbig2Page Page { get; }

        /// <summary>Gets the page width.</summary>
        internal int Width { get; }

        /// <summary>Gets the page height, from the page information or, when it was unknown, the last end of stripe.</summary>
        internal int Height { get; }

        internal Dictionary<int, (Jbig2Bitmap Bitmap, Jbig2RegionInfo Info)> Auxiliary { get; } = [];

        /// <summary>Gets the rows of the page buffer kept now: page rows <see cref="Top"/> on.</summary>
        internal Jbig2Bitmap Window { get; private set; }

        /// <summary>Gets the page row of the window's first row.</summary>
        internal int Top { get; private set; }

        /// <summary>Gets the last page row that no later segment can change, or -1.</summary>
        internal int Final { get; private set; } = -1;

        /// <summary>Gets the last end row of a stripe, or -1.</summary>
        internal int EndRow { get; private set; } = -1;

        /// <summary>Gets whether every segment of the page has run.</summary>
        internal bool Done { get; private set; }

        /// <summary>
        /// Runs segments up to the next end of stripe (when kept in stripes) or the end of the page. Returns
        /// <see langword="false"/> once the page is done.
        /// </summary>
        internal bool Step(CancellationToken cancellationToken)
        {
            if (Done)
            {
                return false;
            }

            if (_striped && EndRow >= Top)
            {
                // The rows down to the last end row are final and have been served: start the next stripe.
                Top = EndRow + 1;
                Window.Fill(Info.DefaultPixel);
            }

            while (_next < _end)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int i = _next++;
                if (_decoder.Process(this, i) && _striped)
                {
                    Final = EndRow;
                    return true;
                }
            }

            Done = true;
            Final = Height - 1;
            if (_decoder.Stream.Truncated)
            {
                if (_decoder._strict)
                {
                    throw new EndOfStreamException("The JBIG2 data ends inside a segment.");
                }

                Page.Warnings.Add("The JBIG2 data ends inside a segment; what it holds was decoded.");
            }

            return true;
        }

        /// <summary>Notes an end of stripe; a page of unknown height kept whole grows to it.</summary>
        internal void EndStripe(int endRow)
        {
            EndRow = Math.Max(EndRow, endRow);
            if (!_striped && Info.HeightUnknown)
            {
                Grow(Math.Min(endRow + 1, Height));
            }
        }

        /// <summary>Combines a region with the page buffer at page coordinates, growing the window to hold it if it must.</summary>
        internal void Compose(Jbig2Bitmap bitmap, int x, int y, Jbig2Combine op)
        {
            long bottom = Math.Min((long)y + bitmap.Height, Height);
            if (bottom - Top > Window.Height)
            {
                Grow((int)(bottom - Top));
            }

            Window.Compose(bitmap, x, y - Top, op);
        }

        /// <summary>Grows the window, if it must, to hold page rows <paramref name="y"/> to <paramref name="y"/> + <paramref name="height"/> - 1.</summary>
        internal void EnsureRows(int y, int height)
        {
            long bottom = Math.Min((long)y + height, Height);
            if (bottom - Top > Window.Height)
            {
                Grow((int)(bottom - Top));
            }
        }

        /// <summary>Returns a copy of the page buffer under a region, for a refinement of the page.</summary>
        internal Jbig2Bitmap Crop(Jbig2RegionInfo info)
        {
            _decoder._budget.Reserve(info.Width, info.Height);
            return Window.Crop(info.X, info.Y - Top, info.Width, info.Height);
        }

        /// <summary>Gets page row <paramref name="y"/>, which must be final and still kept, as packed words.</summary>
        internal ReadOnlySpan<ulong> Row(int y) => Window.Row(y - Top);

        /// <summary>Returns the decoded page, after <see cref="Step"/> has run every segment.</summary>
        internal Jbig2Page Finish()
        {
            if (Window.Height > Height)
            {
                Window = Window.Crop(0, 0, Width, Height);
            }
            else if (Window.Height < Height)
            {
                Grow(Height);
            }

            Page.Bitmap = Window;
            return Page;
        }

        private void Grow(int rows)
        {
            if (rows <= Window.Height)
            {
                return;
            }

            _decoder._budget.Reserve(Width, rows - Window.Height);
            Window.GrowTo(rows, Info.DefaultPixel);
        }
    }

    private Jbig2Bitmap GenericRegion(Jbig2Segment segment, Jbig2RegionInfo info, Jbig2Page page)
    {
        page.Regions |= Jbig2RegionKinds.Generic;
        int at = segment.DataStart + Jbig2RegionInfo.Length;
        int end = segment.DataEnd;
        Need(segment, at + 1);
        byte flags = _data[at++];
        bool mmr = (flags & 1) != 0;
        int template = (flags >> 1) & 3;
        bool tpgdon = (flags & 8) != 0;
        bool extended = (flags & 16) != 0 && template == 0;
        int height = info.Height;
        if (info.ColourExtension)
        {
            end -= 4;
        }

        if (segment.Type is 38 or 39 && end - at >= 6 && (mmr ? _data[end - 6] == 0 && _data[end - 5] == 0 : _data[end - 6] == 0xFF && _data[end - 5] == 0xAC))
        {
            // A segment whose length was unknown when written ends with a row count (7.4.6.4); trust it only if it fits.
            uint rows = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(end - 4));
            if (rows < (uint)height && IsUnknownLength(segment))
            {
                height = (int)rows;
            }
        }

        if (mmr)
        {
            _budget.Reserve(info.Width, height);
            return Jbig2Generic.DecodeMmr(info.Width, height, _data.AsSpan(at, Math.Max(0, end - at))).Bitmap;
        }

        int atBytes = template == 0 ? (extended ? 32 : 8) : 2;
        Need(segment, at + atBytes);
        int[] atPixels = new int[template == 0 ? (extended ? 24 : 8) : 2];
        for (int i = 0; i < atPixels.Length; i++)
        {
            atPixels[i] = (sbyte)_data[at + i];
        }

        at += atBytes;
        _budget.Reserve(info.Width, height);
        Jbig2ArithState arith = new() { Gb = new byte[Jbig2Generic.ContextCount(template)] };
        arith.Mq.Start(_data, at, Math.Max(0, end - at));
        Jbig2GenericParams p = new()
        {
            Width = info.Width,
            Height = height,
            Template = template,
            TypicalPrediction = tpgdon,
            ExtendedTemplate = extended,
            At = atPixels,
        };
        Jbig2Bitmap bitmap = Jbig2Generic.DecodeArithmetic(p, arith.Mq, arith.Gb);
        CheckOverrun(arith, segment, page);
        return bitmap;
    }

    private bool IsUnknownLength(Jbig2Segment segment)
    {
        // The header's data length, just before the data, said 0xFFFFFFFF: the parser found the end, at the row count.
        return segment.DataStart >= 4 && BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(segment.DataStart - 4)) == 0xFFFFFFFF;
    }

    private Jbig2Bitmap RefinementRegion(Jbig2Segment segment, Jbig2RegionInfo info, Jbig2Bitmap reference)
    {
        int at = segment.DataStart + Jbig2RegionInfo.Length;
        Need(segment, at + 1);
        byte flags = _data[at++];
        int template = flags & 1;
        bool tpgron = (flags & 2) != 0;
        int[] atPixels = [-1, -1, -1, -1];
        if (template == 0)
        {
            Need(segment, at + 4);
            for (int i = 0; i < 4; i++)
            {
                atPixels[i] = (sbyte)_data[at + i];
            }

            at += 4;
        }

        _budget.Reserve(info.Width, info.Height);
        Jbig2ArithState arith = new() { Gr = new byte[Jbig2Refinement.ContextCount(template)] };
        arith.Mq.Start(_data, at, Math.Max(0, segment.DataEnd - at));
        Jbig2RefinementParams p = new()
        {
            Width = info.Width,
            Height = info.Height,
            Template = template,
            Reference = reference,
            TypicalPrediction = tpgron,
            At = atPixels,
        };
        return Jbig2Refinement.Decode(p, arith.Mq, arith.Gr);
    }

    /// <summary>Gets whether a text region's default pixel is 0 and its symbols combine with OR, so it can be drawn onto the page directly.</summary>
    private bool TextDrawsDirectly(Jbig2Segment segment)
    {
        if (segment.DataLength < Jbig2RegionInfo.Length + 2)
        {
            return false;
        }

        int flags = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(segment.DataStart + Jbig2RegionInfo.Length));
        return ((flags >> 7) & 3) == (int)Jbig2Combine.Or && ((flags >> 9) & 1) == 0;
    }

    private Jbig2Bitmap TextRegion(int index, Jbig2RegionInfo info, Jbig2Page page, Jbig2Bitmap? target = null, int targetX = 0, int targetY = 0)
    {
        page.Regions |= Jbig2RegionKinds.Text;
        Jbig2Segment segment = _segments[index];
        int at = segment.DataStart + Jbig2RegionInfo.Length;
        int end = segment.DataEnd;
        Need(segment, at + 2);
        int flags = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at));
        at += 2;
        bool huffman = (flags & 1) != 0;
        bool refine = (flags & 2) != 0;
        int strips = 1 << ((flags >> 2) & 3);
        int refCorner = (flags >> 4) & 3;
        bool transposed = (flags & 0x40) != 0;
        Jbig2Combine combine = (Jbig2Combine)((flags >> 7) & 3);
        int defaultPixel = (flags >> 9) & 1;
        int dsOffset = (flags >> 10) & 0x1F;
        if (dsOffset > 15)
        {
            dsOffset -= 32;
        }

        int refTemplate = (flags >> 15) & 1;
        int huffFlags = 0;
        if (huffman)
        {
            Need(segment, at + 2);
            huffFlags = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at));
            at += 2;
        }

        int[] refAt = [-1, -1, -1, -1];
        if (refine && refTemplate == 0)
        {
            Need(segment, at + 4);
            for (int i = 0; i < 4; i++)
            {
                refAt[i] = (sbyte)_data[at + i];
            }

            at += 4;
        }

        Need(segment, at + 4);
        uint instances = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at));
        at += 4;
        if (info.ColourExtension && end - 4 >= at)
        {
            uint section = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(end - 4));
            end = (int)Math.Max(at, end - Math.Min(section, (uint)(end - at)));
        }

        // The symbols of the referred dictionaries, in reference order, and the user tables.
        List<Jbig2Bitmap[]> dictionaries = [];
        List<Jbig2HuffmanTable> tables = [];
        long total = 0;
        foreach (uint number in segment.References)
        {
            int j = Find(index, number);
            object? referred = ResultOf(j);
            if (referred is Jbig2SymbolDictionary symbols)
            {
                dictionaries.Add(symbols.Symbols);
                total += symbols.Symbols.Length;
            }
            else if (referred is Jbig2HuffmanTable table)
            {
                tables.Add(table);
            }
        }

        // The sum FORCEDENTRY overflowed: count it wide and bound it before anything is sized from it.
        if (total > int.MaxValue / 2)
        {
            throw new InvalidDataException("A text region refers to more symbols than can exist.");
        }

        _budget.Charge(total * 8);
        Jbig2Bitmap[] all = new Jbig2Bitmap[total];
        int k = 0;
        foreach (Jbig2Bitmap[] dictionary in dictionaries)
        {
            dictionary.CopyTo(all, k);
            k += dictionary.Length;
        }

        Jbig2ArithState arith = new() { Gr = new byte[Jbig2Refinement.ContextCount(refTemplate)] };
        Jbig2BitReader? bits = null;
        Jbig2HuffmanTable? symbolCodes = null;
        int codeLength = Jbig2Dictionaries.CeilLog2(total);
        Jbig2TextParams p;
        if (huffman)
        {
            bits = new Jbig2BitReader(_data, at, end);
            symbolCodes = Jbig2Text.ReadSymbolCodes(bits, (int)total);
            int user = 0;
            Jbig2HuffmanTable Pick(int selection, int a, int b, int c)
            {
                return selection switch
                {
                    0 => Jbig2HuffmanTable.Standard(a),
                    1 => Jbig2HuffmanTable.Standard(b),
                    2 when c > 0 => Jbig2HuffmanTable.Standard(c),
                    3 => user < tables.Count ? tables[user++] : throw new InvalidDataException("A text region names more user tables than it refers to."),
                    _ => throw new InvalidDataException("A text region selects a Huffman table T.88 does not allow."),
                };
            }

            Jbig2HuffmanTable fs = Pick(huffFlags & 3, 6, 7, 0);
            Jbig2HuffmanTable ds = Pick((huffFlags >> 2) & 3, 8, 9, 10);
            Jbig2HuffmanTable dt = Pick((huffFlags >> 4) & 3, 11, 12, 13);
            Jbig2HuffmanTable rdw = Pick((huffFlags >> 6) & 3, 14, 15, 0);
            Jbig2HuffmanTable rdh = Pick((huffFlags >> 8) & 3, 14, 15, 0);
            Jbig2HuffmanTable rdx = Pick((huffFlags >> 10) & 3, 14, 15, 0);
            Jbig2HuffmanTable rdy = Pick((huffFlags >> 12) & 3, 14, 15, 0);
            Jbig2HuffmanTable rsize = (huffFlags & 0x4000) != 0 ? Pick(3, 0, 0, 0) : Jbig2HuffmanTable.Standard(1);
            p = new Jbig2TextParams
            {
                Huffman = true,
                HuffFs = fs,
                HuffDs = ds,
                HuffDt = dt,
                HuffRdw = rdw,
                HuffRdh = rdh,
                HuffRdx = rdx,
                HuffRdy = rdy,
                HuffRsize = rsize,
                Refine = refine,
                Width = info.Width,
                Height = info.Height,
                Instances = instances,
                Strips = strips,
                Symbols = all,
                SymbolCodes = symbolCodes,
                DefaultPixel = defaultPixel,
                Combine = combine,
                Transposed = transposed,
                RefCorner = refCorner,
                DsOffset = dsOffset,
                RefinementTemplate = refTemplate,
                RefinementAt = refAt,
            };
        }
        else
        {
            arith.Iaid = new Jbig2SymbolIdDecoder(codeLength);
            arith.Mq.Start(_data, at, Math.Max(0, end - at));
            p = new Jbig2TextParams
            {
                Refine = refine,
                Width = info.Width,
                Height = info.Height,
                Instances = instances,
                Strips = strips,
                Symbols = all,
                SymbolCodeLength = codeLength,
                DefaultPixel = defaultPixel,
                Combine = combine,
                Transposed = transposed,
                RefCorner = refCorner,
                DsOffset = dsOffset,
                RefinementTemplate = refTemplate,
                RefinementAt = refAt,
            };
        }

        Jbig2Bitmap bitmap = Jbig2Text.Decode(p, arith, bits, _budget, target, targetX, targetY);
        if (!huffman)
        {
            CheckOverrun(arith, segment, page);
        }

        return bitmap;
    }

    private Jbig2Bitmap HalftoneRegion(int index, Jbig2RegionInfo info, Jbig2Page page)
    {
        page.Regions |= Jbig2RegionKinds.Halftone;
        Jbig2Segment segment = _segments[index];
        int at = segment.DataStart + Jbig2RegionInfo.Length;
        Need(segment, at + 21);
        byte flags = _data[at];
        ReadOnlySpan<byte> d = _data.AsSpan(at + 1, 20);
        if (segment.References.Length != 1 || ResultOf(Find(index, segment.References[0])) is not Jbig2Bitmap[] patterns)
        {
            throw new InvalidDataException("A halftone region does not refer to exactly one pattern dictionary.");
        }

        uint gridWidth = BinaryPrimitives.ReadUInt32BigEndian(d);
        uint gridHeight = BinaryPrimitives.ReadUInt32BigEndian(d[4..]);
        if (gridWidth > int.MaxValue || gridHeight > int.MaxValue)
        {
            throw new InvalidDataException("A halftone grid is too large.");
        }

        _budget.Reserve(gridWidth, gridHeight);
        Jbig2HalftoneParams p = new()
        {
            Width = info.Width,
            Height = info.Height,
            Mmr = (flags & 1) != 0,
            Template = (flags >> 1) & 3,
            EnableSkip = (flags & 8) != 0,
            Combine = (Jbig2Combine)Math.Min((flags >> 4) & 7, 4),
            DefaultPixel = (flags >> 7) & 1,
            GridWidth = (int)gridWidth,
            GridHeight = (int)gridHeight,
            GridX = (int)BinaryPrimitives.ReadUInt32BigEndian(d[8..]),
            GridY = (int)BinaryPrimitives.ReadUInt32BigEndian(d[12..]),
            VectorX = BinaryPrimitives.ReadUInt16BigEndian(d[16..]),
            VectorY = BinaryPrimitives.ReadUInt16BigEndian(d[18..]),
            Patterns = patterns,
        };
        Jbig2ArithState arith = new() { Gb = new byte[Jbig2Generic.ContextCount(p.Template)] };
        Jbig2Bitmap bitmap = Jbig2Halftone.Decode(p, _data, at + 21, segment.DataEnd, arith, _budget);
        if (!p.Mmr)
        {
            CheckOverrun(arith, segment, page);
        }

        return bitmap;
    }

    /// <summary>Decodes every dictionary segment of the stream, in order, and returns the results (for globals).</summary>
    internal Dictionary<Jbig2Segment, object> DecodeDictionaries()
    {
        for (int i = _firstOwn; i < _segments.Count; i++)
        {
            if (_segments[i].IsDictionary && !_results.ContainsKey(_segments[i]))
            {
                _results[_segments[i]] = DecodeDictionary(i);
            }
        }

        return _results;
    }

    /// <summary>Decodes a dictionary segment: a symbol dictionary, pattern dictionary or code table.</summary>
    internal object DecodeDictionary(int index)
    {
        Jbig2Segment segment = _segments[index];
        return segment.Type switch
        {
            Jbig2Segment.Tables => Jbig2HuffmanTable.Read(_data, segment.DataStart, segment.DataLength),
            Jbig2Segment.PatternDictionary => PatternDictionary(segment),
            _ => SymbolDictionary(index),
        };
    }

    private Jbig2Bitmap[] PatternDictionary(Jbig2Segment segment)
    {
        Need(segment, segment.DataStart + 7);
        int at = segment.DataStart;
        byte flags = _data[at];
        int width = _data[at + 1];
        int height = _data[at + 2];
        uint grayMax = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at + 3));
        int template = (flags >> 1) & 3;
        Jbig2ArithState arith = new() { Gb = new byte[Jbig2Generic.ContextCount(template)] };
        return Jbig2Dictionaries.DecodePatterns((flags & 1) != 0, template, width, height, grayMax, _data, at + 7, segment.DataEnd, arith, _budget);
    }

    private Jbig2SymbolDictionary SymbolDictionary(int index)
    {
        Jbig2Segment segment = _segments[index];
        int at = segment.DataStart;
        int end = segment.DataEnd;
        Need(segment, at + 2);
        int flags = BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at));
        at += 2;
        bool huffman = (flags & 1) != 0;
        bool refAgg = (flags & 2) != 0;
        int template = (flags >> 10) & 3;
        int refTemplate = (flags >> 12) & 1;
        bool contextUsed = (flags & 0x100) != 0;
        bool contextRetained = (flags & 0x200) != 0;
        int[] atPixels = [];
        if (!huffman)
        {
            int count = template == 0 ? 8 : 2;
            Need(segment, at + count);
            atPixels = new int[count];
            for (int i = 0; i < count; i++)
            {
                atPixels[i] = (sbyte)_data[at + i];
            }

            at += count;
        }

        int[] refAt = [-1, -1, -1, -1];
        if (refAgg && refTemplate == 0)
        {
            Need(segment, at + 4);
            for (int i = 0; i < 4; i++)
            {
                refAt[i] = (sbyte)_data[at + i];
            }

            at += 4;
        }

        Need(segment, at + 8);
        uint exported = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at));
        uint created = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at + 4));
        at += 8;

        List<Jbig2Bitmap> input = [];
        List<Jbig2HuffmanTable> tables = [];
        Jbig2SymbolDictionary? last = null;
        long inputCount = 0;
        foreach (uint number in segment.References)
        {
            object? referred = ResultOf(Find(index, number));
            if (referred is Jbig2SymbolDictionary dictionary)
            {
                inputCount += dictionary.Symbols.Length;
                if (inputCount > int.MaxValue / 2)
                {
                    throw new InvalidDataException("A symbol dictionary refers to more symbols than can exist.");
                }

                input.AddRange(dictionary.Symbols);
                last = dictionary;
            }
            else if (referred is Jbig2HuffmanTable table)
            {
                tables.Add(table);
            }
        }

        long total = inputCount + created;
        if (created > int.MaxValue / 2 || exported > total)
        {
            throw new InvalidDataException("A symbol dictionary's symbol counts are impossible.");
        }

        Jbig2ArithState arith = new()
        {
            Gb = huffman ? [] : new byte[Jbig2Generic.ContextCount(template)],
            Gr = refAgg ? new byte[Jbig2Refinement.ContextCount(refTemplate)] : [],
        };
        if (contextUsed)
        {
            if (last?.RetainedGb is not null && arith.Gb.Length == last.RetainedGb.Length)
            {
                last.RetainedGb.CopyTo(arith.Gb, 0);
            }

            if (last?.RetainedGr is not null && arith.Gr.Length == last.RetainedGr.Length)
            {
                last.RetainedGr.CopyTo(arith.Gr, 0);
            }
        }

        Jbig2BitReader? bits = null;
        Jbig2HuffmanTable? dh = null;
        Jbig2HuffmanTable? dw = null;
        Jbig2HuffmanTable? bmSize = null;
        Jbig2HuffmanTable? aggInst = null;
        if (huffman)
        {
            int user = 0;
            Jbig2HuffmanTable User() => user < tables.Count ? tables[user++] : throw new InvalidDataException("A symbol dictionary names more user tables than it refers to.");
            dh = ((flags >> 2) & 3) switch
            {
                0 => Jbig2HuffmanTable.Standard(4),
                1 => Jbig2HuffmanTable.Standard(5),
                3 => User(),
                _ => throw new InvalidDataException("A symbol dictionary selects a height table T.88 does not allow."),
            };
            dw = ((flags >> 4) & 3) switch
            {
                0 => Jbig2HuffmanTable.Standard(2),
                1 => Jbig2HuffmanTable.Standard(3),
                3 => User(),
                _ => throw new InvalidDataException("A symbol dictionary selects a width table T.88 does not allow."),
            };
            bmSize = (flags & 0x40) != 0 ? User() : Jbig2HuffmanTable.Standard(1);
            aggInst = (flags & 0x80) != 0 ? User() : Jbig2HuffmanTable.Standard(1);
            bits = new Jbig2BitReader(_data, at, end);
            if (refAgg)
            {
                // Refinements inside a Huffman dictionary still decode arithmetically, each from its own byte run.
                arith.Gr = new byte[Jbig2Refinement.ContextCount(refTemplate)];
            }
        }
        else
        {
            arith.Mq.Start(_data, at, Math.Max(0, end - at));
        }

        Jbig2SymbolParams p = new()
        {
            Huffman = huffman,
            RefAgg = refAgg,
            Input = input,
            NewSymbols = (int)created,
            Exported = (int)exported,
            HuffDh = dh,
            HuffDw = dw,
            HuffBmSize = bmSize,
            HuffAggInst = aggInst,
            Template = template,
            At = atPixels,
            RefinementTemplate = refTemplate,
            RefinementAt = refAt,
        };
        Jbig2Bitmap[] symbols = Jbig2Dictionaries.DecodeSymbols(p, arith, bits, _budget);
        if (!huffman && arith.Mq.Overrun > OverrunAllowance && _strict)
        {
            throw new EndOfStreamException($"Symbol dictionary segment {segment.Number}'s data ends early.");
        }

        return new Jbig2SymbolDictionary(
            symbols,
            contextRetained && !huffman ? (byte[])arith.Gb.Clone() : null,
            contextRetained && refAgg ? (byte[])arith.Gr.Clone() : null);
    }

    /// <summary>The bytes the MQ decoder may read past a segment's end before the data counts as cut short.</summary>
    private const int OverrunAllowance = 2;

    private void CheckOverrun(Jbig2ArithState arith, Jbig2Segment segment, Jbig2Page page)
    {
        if (arith.Mq.Overrun <= OverrunAllowance)
        {
            return;
        }

        if (_strict)
        {
            throw new EndOfStreamException($"Region segment {segment.Number}'s data ends early.");
        }

        page.Warnings.Add($"Region segment {segment.Number}'s data ends early; the rest of it is guessed.");
    }

    /// <summary>Reads comments (7.4.15) and refuses unknown extensions marked necessary.</summary>
    private void Extension(Jbig2Segment segment, Jbig2Page page)
    {
        if (segment.DataLength < 4)
        {
            return;
        }

        uint type = BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(segment.DataStart));
        int at = segment.DataStart + 4;
        int end = segment.DataEnd;
        if (type is 0x20000000 or 0x20000002)
        {
            bool wide = type == 0x20000002;
            Encoding encoding = wide ? Encoding.BigEndianUnicode : Encoding.Latin1;
            int unit = wide ? 2 : 1;
            while (at < end)
            {
                string name = Text(ref at);
                if (name.Length == 0)
                {
                    break;
                }

                page.Comments.Add(new KeyValuePair<string, string>(name, Text(ref at)));
            }

            string Text(ref int position)
            {
                int start = position;
                while (position + unit <= end && (_data[position] != 0 || (wide && _data[position + 1] != 0)))
                {
                    position += unit;
                }

                string text = encoding.GetString(_data, start, position - start);
                position = Math.Min(end, position + unit);
                return text;
            }

            return;
        }

        if ((type & 0x80000000) != 0)
        {
            throw new NotSupportedException($"The JBIG2 data needs extension 0x{type:X8}, which Egami does not support.");
        }
    }

    /// <summary>Returns the decoded result of a dictionary segment, from this decoder or the globals.</summary>
    private object? ResultOf(int index)
    {
        Jbig2Segment segment = _segments[index];
        if (_results.TryGetValue(segment, out object? result))
        {
            return result;
        }

        if (_globals is not null && _globals.Results.TryGetValue(segment, out result))
        {
            return result;
        }

        if (!segment.IsDictionary)
        {
            return null;
        }

        // Not marked needed (a stream whose references were rewritten); decode now, its own references precede it.
        result = DecodeDictionary(index);
        _results[segment] = result;
        return result;
    }

    /// <summary>Finds the latest segment before <paramref name="index"/> numbered <paramref name="number"/>.</summary>
    private int Find(int index, uint number)
    {
        if (_byNumber.TryGetValue(number, out List<int>? list))
        {
            for (int i = list.Count - 1; i >= 0; i--)
            {
                if (list[i] < index)
                {
                    return list[i];
                }
            }
        }

        throw new Jbig2MissingSegmentException($"Segment {_segments[index].Number} refers to segment {number}, which is neither earlier in the stream nor in the globals.");
    }

    private static void Need(Jbig2Segment segment, long through)
    {
        if (through > segment.DataEnd)
        {
            throw new EndOfStreamException($"Segment {segment.Number}'s header runs past its data.");
        }
    }
}
