using Egami.Fonts.Cff;
using Egami.Fonts.Parsing;

namespace Egami.Fonts.Subsetting;

/// <summary>
/// Subsets CFF outlines: seac closure, charstrings for the kept glyphs, subroutines the kept glyphs call renumbered with the
/// spec's bias (or kept in place when a call's operand is computed), and the font written name-keyed or CID-keyed.
/// </summary>
internal static class CffSubsetter
{
    // Top DICT entries the subset replaces or drops.
    private static readonly HashSet<int> s_topReplaced =
    [
        CffFont.OpCharset, CffFont.OpEncoding, CffFont.OpCharStrings, CffFont.OpPrivate, CffFont.OpFdArray, CffFont.OpFdSelect,
        CffFont.OpCidCount, CffFont.OpRos, 13, 14, 1220, 1231, 1232, 1233, 1235,
    ];

    private static readonly HashSet<int> s_offsets = [CffFont.OpCharset, CffFont.OpCharStrings, CffFont.OpPrivate, CffFont.OpFdArray, CffFont.OpFdSelect, CffFont.OpSubrs];

    public static FontSubset Subset(Typeface face, SortedSet<ushort> wanted, SubsetOptions options, SubsetFormat format)
    {
        CffFont cff = face.Cff!;
        var usage = new SubrUsage(cff.Privates.Length);
        CharStringInterpreter interpreter = CharStringInterpreter.For(cff);
        var kept = new SortedSet<ushort>();
        var pending = new Stack<ushort>(wanted);
        while (pending.Count > 0)
        {
            ushort glyph = pending.Pop();
            if (!kept.Add(glyph))
            {
                continue;
            }

            interpreter.Draw(glyph, null, usage);
            foreach (int seac in usage.SeacGlyphs)
            {
                if (seac < face.GlyphCount && !kept.Contains((ushort)seac))
                {
                    pending.Push((ushort)seac);
                }
            }
        }

        (ushort[] oldIds, Dictionary<ushort, ushort> map) = FontSubsetter.Number(kept, options.RetainGlyphIds);
        bool cid = cff.IsCidKeyed || format == SubsetFormat.BareCff || options.RetainGlyphIds;
        byte[] cffData = Write(face, cff, oldIds, map, usage, cid, options);
        if (format == SubsetFormat.BareCff)
        {
            return new FontSubset(cffData, format, oldIds, map);
        }

        var tables = new SortedDictionary<uint, byte[]>
        {
            [Tags.Cff.Value] = cffData,
            [Tags.Head.Value] = SfntWriter.Head(face, 0),
            [Tags.Maxp.Value] = SfntWriter.Maxp(face, oldIds.Length, trueType: false),
            [Tags.Post.Value] = SfntWriter.Post(face),
            [Tags.Name.Value] = SfntWriter.Name(face, options.FontNamePrefix),
        };
        (byte[] hmtx, byte[] hhea) = SfntWriter.Metrics(face, oldIds, map, options.RetainGlyphIds);
        tables[Tags.Hmtx.Value] = hmtx;
        tables[Tags.Hhea.Value] = hhea;
        if (options.KeepCmap)
        {
            tables[Tags.Cmap.Value] = SfntWriter.Cmap(SfntWriter.MapCharacters(face, map));
        }

        SfntWriter.CopyTable(face, Tags.OS2, tables);
        foreach (FontTag tag in options.KeepTables)
        {
            if (!tables.ContainsKey(tag.Value))
            {
                SfntWriter.CopyTable(face, tag, tables);
            }
        }

        return new FontSubset(SfntWriter.Write(SfntDirectory.OpenTypeVersion, tables), format, oldIds, map);
    }

    private static byte[] Write(Typeface face, CffFont cff, ushort[] oldIds, Dictionary<ushort, ushort> map, SubrUsage usage, bool cid, SubsetOptions options)
    {
        int glyphCount = oldIds.Length;

        // Font dicts used by the kept glyphs, renumbered densely.
        var fdMap = new SortedDictionary<int, int>();
        foreach (ushort old in map.Keys)
        {
            fdMap.TryAdd(cff.FontDictOf(old), 0);
        }

        int[] fds = [.. fdMap.Keys];
        for (int i = 0; i < fds.Length; i++)
        {
            fdMap[fds[i]] = i;
        }

        // Subroutines: renumber the used ones, or keep every slot with unused bodies emptied when a call is computed.
        bool renumber = !usage.Unrewritable;
        int oldGlobalCount = cff.GlobalSubrs.Count;
        Dictionary<int, int> globalMap = SubrMap(usage.Global, oldGlobalCount, renumber);
        var localMaps = new Dictionary<int, int>[cff.Privates.Length];
        foreach (int fd in fds)
        {
            localMaps[fd] = SubrMap(usage.Local[fd], cff.Privates[fd].LocalSubrs?.Count ?? 0, renumber);
        }

        // Charstrings.
        CharStringInterpreter interpreter = CharStringInterpreter.For(cff);
        var charStrings = new List<byte[]>(glyphCount);
        for (int g = 0; g < glyphCount; g++)
        {
            ushort old = oldIds[g];
            if (!map.TryGetValue(old, out ushort now) || now != g)
            {
                charStrings.Add([14]);
                continue;
            }

            int fd = cff.FontDictOf(old);
            if (cid && usage.SeacUsers.Contains(old))
            {
                // CID-keyed fonts have no seac: draw the accented glyph's outline as a plain charstring.
                OutlineRecorder recorder = OutlineRecorder.Take();
                try
                {
                    double width = interpreter.Draw(old, recorder);
                    PrivateData priv = cff.Privates[fd];
                    charStrings.Add(CffEncoding.CharString(recorder, width == priv.DefaultWidthX ? null : width - priv.NominalWidthX));
                }
                finally
                {
                    recorder.Release();
                }

                continue;
            }

            var id = new CharStringId(CharStringKind.Glyph, fd, old);
            charStrings.Add(Rewrite(cff.CharStrings[old], id, usage, globalMap, localMaps[fd], cff, fd, renumber));
        }

        // Subroutine INDEX contents.
        List<byte[]> globalSubrs = Subrs(cff.GlobalSubrs, globalMap, i => new CharStringId(CharStringKind.Global, 0, i), usage, globalMap, null, cff, -1, renumber);
        var localSubrs = new Dictionary<int, List<byte[]>>();
        foreach (int fd in fds)
        {
            CffIndex? local = cff.Privates[fd].LocalSubrs;
            localSubrs[fd] = local is null ? [] : Subrs(local.Value, localMaps[fd], i => new CharStringId(CharStringKind.Local, fd, i), usage, globalMap, localMaps[fd], cff, fd, renumber);
        }

        // Strings: the original ones, plus Adobe and Identity for a font made CID-keyed.
        var strings = new List<byte[]>();
        for (int i = 0; i < cff.Strings.Count; i++)
        {
            strings.Add(cff.Strings[i].ToArray());
        }

        int adobeSid = -1, identitySid = -1;
        if (cid)
        {
            adobeSid = CffStandard.StandardStringCount + strings.Count;
            strings.Add("Adobe"u8.ToArray());
            identitySid = CffStandard.StandardStringCount + strings.Count;
            strings.Add("Identity"u8.ToArray());
        }

        byte[] nameIndex = CffEncoding.Index([System.Text.Encoding.Latin1.GetBytes((options.FontNamePrefix ?? string.Empty) + cff.Name)]);
        byte[] stringIndex = CffEncoding.Index(strings);
        byte[] globalIndex = CffEncoding.Index(globalSubrs);
        byte[] charStringIndex = CffEncoding.Index(charStrings);
        byte[] charset = Charset(cff, oldIds, cid);
        byte[]? fdSelect = cid ? FdSelect(cff, oldIds, fdMap) : null;

        // Private DICTs, each followed by its local subroutines.
        var privates = new List<(byte[] Dict, byte[] Subrs)>();
        foreach (int fd in fds)
        {
            PrivateData priv = cff.Privates[fd];
            List<byte[]> subrs = localSubrs[fd];
            var entries = priv.Dict.Entries.Where(e => e.Key != CffFont.OpSubrs).Select(e => (e.Key, e.Value)).ToList();
            byte[] dict = CffEncoding.Dict(entries);
            if (subrs.Count > 0)
            {
                entries.Add((CffFont.OpSubrs, [0]));
                dict = CffEncoding.Dict(entries, s_offsets);
                int size = dict.Length;
                entries[^1] = (CffFont.OpSubrs, [size]);
                dict = CffEncoding.Dict(entries, s_offsets);
            }

            privates.Add((dict, subrs.Count > 0 ? CffEncoding.Index(subrs) : []));
        }

        // Lay out: header, Name, Top DICT, String, Global Subr INDEXes, charset, FDSelect, CharStrings, FDArray, Privates.
        List<(int, double[])> TopEntries(int charsetAt, int charStringsAt, int fdSelectAt, int fdArrayAt, int privateAt)
        {
            var entries = new List<(int, double[])>();
            if (cid)
            {
                entries.Add((CffFont.OpRos, [adobeSid, identitySid, 0]));
            }

            foreach ((int op, double[] operands) in cff.Top.Entries)
            {
                if (!s_topReplaced.Contains(op))
                {
                    entries.Add((op, operands));
                }
            }

            entries.Add((CffFont.OpCharset, [charsetAt]));
            entries.Add((CffFont.OpCharStrings, [charStringsAt]));
            if (cid)
            {
                entries.Add((CffFont.OpCidCount, [glyphCount]));
                entries.Add((CffFont.OpFdSelect, [fdSelectAt]));
                entries.Add((CffFont.OpFdArray, [fdArrayAt]));
            }
            else
            {
                entries.Add((CffFont.OpPrivate, [privates[0].Dict.Length, privateAt]));
            }

            return entries;
        }

        int topSize = CffEncoding.Index([CffEncoding.Dict(TopEntries(0, 0, 0, 0, 0), s_offsets)]).Length;
        int at = 4 + nameIndex.Length + topSize + stringIndex.Length + globalIndex.Length;
        int charsetAt = at;
        at += charset.Length;
        int fdSelectAt = at;
        at += fdSelect?.Length ?? 0;
        int charStringsAt = at;
        at += charStringIndex.Length;

        // FD dicts point at the privates, which follow the FDArray; the FDArray's size does not depend on those offsets.
        List<byte[]> FdDicts(int privateStart)
        {
            var dicts = new List<byte[]>();
            int p = privateStart;
            for (int i = 0; i < fds.Length; i++)
            {
                var entries = new List<(int, double[])>();
                CffDict? source = cff.Privates[fds[i]].FontDict;
                if (source is not null)
                {
                    foreach ((int op, double[] operands) in source.Entries)
                    {
                        if (op != CffFont.OpPrivate)
                        {
                            entries.Add((op, operands));
                        }
                    }
                }

                entries.Add((CffFont.OpPrivate, [privates[i].Dict.Length, p]));
                dicts.Add(CffEncoding.Dict(entries, s_offsets));
                p += privates[i].Dict.Length + privates[i].Subrs.Length;
            }

            return dicts;
        }

        int fdArrayAt = at;
        byte[] fdArray = cid ? CffEncoding.Index(FdDicts(0)) : [];
        at += fdArray.Length;
        int privateAt = at;
        if (cid)
        {
            fdArray = CffEncoding.Index(FdDicts(privateAt));
        }

        byte[] topIndex = CffEncoding.Index([CffEncoding.Dict(TopEntries(charsetAt, charStringsAt, fdSelectAt, fdArrayAt, privateAt), s_offsets)]);
        if (topIndex.Length != topSize)
        {
            throw new InvalidOperationException("The CFF Top DICT changed size while laying out.");
        }

        var w = new ByteWriter(at + privates.Sum(p => p.Dict.Length + p.Subrs.Length));
        w.Bytes([1, 0, 4, 4]);
        w.Bytes(nameIndex);
        w.Bytes(topIndex);
        w.Bytes(stringIndex);
        w.Bytes(globalIndex);
        w.Bytes(charset);
        if (fdSelect is not null)
        {
            w.Bytes(fdSelect);
        }

        w.Bytes(charStringIndex);
        w.Bytes(fdArray);
        foreach ((byte[] dict, byte[] subrs) in privates)
        {
            w.Bytes(dict);
            w.Bytes(subrs);
        }

        return w.ToArray();
    }

    private static Dictionary<int, int> SubrMap(HashSet<int> used, int count, bool renumber)
    {
        var map = new Dictionary<int, int>();
        if (!renumber)
        {
            for (int i = 0; i < count; i++)
            {
                map[i] = i;
            }

            return map;
        }

        int next = 0;
        foreach (int old in used.Order())
        {
            map[old] = next++;
        }

        return map;
    }

    private static List<byte[]> Subrs(CffIndex index, Dictionary<int, int> subrMap, Func<int, CharStringId> idOf, SubrUsage usage, Dictionary<int, int> globalMap, Dictionary<int, int>? localMap, CffFont cff, int fd, bool renumber)
    {
        var result = new byte[subrMap.Count][];
        foreach ((int old, int now) in subrMap)
        {
            bool used = renumber || (fd < 0 ? usage.Global.Contains(old) : usage.Local[fd].Contains(old));
            result[now] = used ? Rewrite(index[old], idOf(old), usage, globalMap, localMap, cff, fd, renumber) : [11];
        }

        return [.. result];
    }

    // Replaces each recorded call's operand with the callee's new number less the new bias.
    private static byte[] Rewrite(ReadOnlySpan<byte> code, CharStringId id, SubrUsage usage, Dictionary<int, int> globalMap, Dictionary<int, int>? localMap, CffFont cff, int fd, bool renumber)
    {
        if (!renumber || !usage.Sites.TryGetValue(id, out SortedDictionary<int, CallSite>? sites))
        {
            return code.ToArray();
        }

        var w = new ByteWriter(code.Length + 8);
        int position = 0;
        foreach (CallSite site in sites.Values)
        {
            Dictionary<int, int> map = site.Global ? globalMap : localMap!;
            int newIndex = map[site.Index];
            w.Bytes(code[position..site.Start]);
            CffEncoding.CharStringInteger(w, newIndex - CffFont.Bias(map.Count));
            position = site.End;
        }

        w.Bytes(code[position..]);
        return w.ToArray();
    }


    private static byte[] Charset(CffFont cff, ushort[] oldIds, bool cid)
    {
        var w = new ByteWriter();
        if (cid)
        {
            // Identity: glyph i is CID i.
            w.U8(2);
            if (oldIds.Length > 1)
            {
                w.U16(1);
                w.U16(oldIds.Length - 2);
            }

            return w.ToArray();
        }

        w.U8(0);
        for (int i = 1; i < oldIds.Length; i++)
        {
            w.U16(cff.Charset[oldIds[i]]);
        }

        return w.ToArray();
    }

    private static byte[] FdSelect(CffFont cff, ushort[] oldIds, SortedDictionary<int, int> fdMap)
    {
        var ranges = new List<(int First, int Fd)>();
        for (int g = 0; g < oldIds.Length; g++)
        {
            int fd = fdMap.TryGetValue(cff.FontDictOf(oldIds[g]), out int mapped) ? mapped : 0;
            if (ranges.Count == 0 || ranges[^1].Fd != fd)
            {
                ranges.Add((g, fd));
            }
        }

        var w = new ByteWriter();
        w.U8(3);
        w.U16(ranges.Count);
        foreach ((int first, int fd) in ranges)
        {
            w.U16(first);
            w.U8(fd);
        }

        w.U16(oldIds.Length);
        return w.ToArray();
    }
}
