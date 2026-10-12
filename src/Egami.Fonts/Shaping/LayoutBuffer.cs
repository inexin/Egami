// Portions of this file are translated from HarfBuzz's hb-buffer.hh and hb-buffer.cc (MIT); see THIRD-PARTY-NOTICES.md.
using System.Runtime.CompilerServices;

namespace Egami.Fonts.Shaping;

/// <summary>One glyph (or, before mapping, one character) in the layout buffer, with the scratch fields shaping keeps on it.</summary>
internal struct GlyphInfo
{
    /// <summary>The code point before glyph mapping, the glyph id after.</summary>
    public uint Codepoint;

    /// <summary>Feature masks in the low bits and the glyph flags in the lowest three.</summary>
    public uint Mask;

    /// <summary>The cluster: an index into the input text.</summary>
    public int Cluster;

    /// <summary>General category, ignorable, hidden and continuation bits, and the modified combining class in the high byte.</summary>
    public ushort UnicodeProps;

    /// <summary>GDEF or synthesized glyph class bits and the mark attachment class.</summary>
    public ushort GlyphProps;

    /// <summary>Ligature id and component.</summary>
    public byte LigProps;

    /// <summary>Syllable serial for the script shapers.</summary>
    public byte Syllable;

    /// <summary>A shaper's per-glyph category.</summary>
    public byte ShaperCategory;

    /// <summary>A shaper's per-glyph position class.</summary>
    public byte ShaperPosition;

    /// <summary>The glyph the normalizer mapped the character to.</summary>
    public uint NormalizerGlyph;
}

/// <summary>One glyph's position in font units, with the attachment links GPOS keeps until the offsets are resolved.</summary>
internal struct GlyphPos
{
    public int XAdvance;
    public int YAdvance;
    public int XOffset;
    public int YOffset;

    /// <summary>The distance to the glyph this one attaches to, or 0.</summary>
    public short AttachChain;

    /// <summary>The kind of attachment: 1 mark, 2 cursive.</summary>
    public byte AttachType;
}

/// <summary>Cluster grouping, as HarfBuzz's cluster levels.</summary>
internal enum LayoutClusterLevel
{
    MonotoneGraphemes = 0,
    MonotoneCharacters = 1,
    Characters = 2,
    Graphemes = 3,
}

/// <summary>Horizontal or vertical, forward or backward.</summary>
internal enum LayoutDirection
{
    Invalid = 0,
    LeftToRight = 4,
    RightToLeft = 5,
    TopToBottom = 6,
    BottomToTop = 7,
}

/// <summary>
/// The working buffer of the layout engine, as HarfBuzz's: an input array with a cursor and an output array the lookups write
/// to, swapped at the end of each pass, plus positions once substitution is done.
/// </summary>
internal sealed class LayoutBuffer
{
    public const uint FlagUnsafeToBreak = 0x1;
    public const uint FlagUnsafeToConcat = 0x2;
    public const uint FlagSafeToInsertTatweel = 0x4;
    public const uint FlagDefined = 0x7;

    public const int ScratchHasFractionSlash = 0x1;
    public const int ScratchHasDefaultIgnorables = 0x2;
    public const int ScratchHasSpaceFallback = 0x4;
    public const int ScratchHasGposAttachment = 0x8;
    public const int ScratchHasCgj = 0x10;
    public const int ScratchHasBrokenSyllable = 0x20;
    public const int ScratchHasVariationSelectorFallback = 0x40;
    public const int ScratchHasContinuations = 0x80;
    public const int ScratchShaper0 = 0x01000000;
    public const int ScratchShaper1 = 0x02000000;

    private const int MaxLenFactor = 256;
    private const int MaxLenMin = 65536;
    private const int MaxOpsFactor = 4096;
    private const int MaxOpsMin = 65536;

    public GlyphInfo[] Info = new GlyphInfo[16];
    public GlyphInfo[] OutInfo;
    public GlyphPos[] Pos = new GlyphPos[16];

    public int Len;
    public int Idx;
    public int OutLen;
    public bool HaveOutput;
    public bool HavePositions;
    public bool Successful = true;

    public LayoutClusterLevel ClusterLevel;
    public LayoutDirection Direction = LayoutDirection.LeftToRight;
    public int ScratchFlags;
    public int MaxLen = int.MaxValue;
    public int MaxOps = int.MaxValue;
    public bool ProduceUnsafeToConcat;
    public bool ProduceSafeToInsertTatweel;

    /// <summary>The glyph default-ignorables become, or 0 to use the space glyph.</summary>
    public uint Invisible;

    /// <summary>The glyph unmapped characters become.</summary>
    public uint NotFound;

    /// <summary>The glyph an unsupported variation selector becomes, or uint.MaxValue to keep it ignorable.</summary>
    public uint NotFoundVariationSelector = uint.MaxValue;

    /// <summary>Whether the text starts at the beginning of a paragraph (for the dotted circle).</summary>
    public bool BeginningOfText = true;

    /// <summary>Whether the text ends at the end of a paragraph.</summary>
    public bool EndOfText = true;

    /// <summary>Whether to keep default-ignorables as they are, or remove them instead of hiding them.</summary>
    public bool PreserveDefaultIgnorables;
    public bool RemoveDefaultIgnorables;
    public bool DoNotInsertDottedCircle;

    /// <summary>The characters before and after the run, nearest first, for context-sensitive shapers.</summary>
    public int[] PreContext = [];
    public int[] PostContext = [];

    public byte Serial;
    public uint RandomState = 1;

    public LayoutBuffer()
    {
        OutInfo = Info;
    }

    public static bool IsHorizontal(LayoutDirection d) => ((int)d & ~1) == 4;

    public static bool IsVertical(LayoutDirection d) => ((int)d & ~1) == 6;

    public static bool IsForward(LayoutDirection d) => ((int)d & ~2) == 4;

    public static bool IsBackward(LayoutDirection d) => ((int)d & ~2) == 5;

    public static LayoutDirection Reverse(LayoutDirection d) => (LayoutDirection)((int)d ^ 1);

    public bool IsMonotone => ClusterLevel is LayoutClusterLevel.MonotoneGraphemes or LayoutClusterLevel.MonotoneCharacters;

    public bool IsGraphemes => ClusterLevel is LayoutClusterLevel.MonotoneGraphemes or LayoutClusterLevel.Graphemes;

    public void Clear()
    {
        Len = 0;
        Idx = 0;
        OutLen = 0;
        HaveOutput = false;
        HavePositions = false;
        Successful = true;
        OutInfo = Info;
        ScratchFlags = 0;
        Serial = 0;
        RandomState = 1;
    }

    /// <summary>Adds a character at the end, before shaping.</summary>
    public void Add(uint codepoint, int cluster)
    {
        Ensure(Len + 1);
        Info[Len] = new GlyphInfo { Codepoint = codepoint, Cluster = cluster };
        Len++;
    }

    /// <summary>Sets the operation and length limits for this text, as HarfBuzz's enter().</summary>
    public void Enter(int maxOpsFactor = MaxOpsFactor)
    {
        Serial = 0;
        ScratchFlags = 0;
        long len = (long)Len * MaxLenFactor;
        MaxLen = (int)Math.Min(int.MaxValue, Math.Max(len, MaxLenMin));
        long ops = (long)Len * maxOpsFactor;
        MaxOps = (int)Math.Min(int.MaxValue, Math.Max(ops, MaxOpsMin));
    }

    public byte NextSerial()
    {
        Serial++;
        if (Serial == 0)
        {
            Serial++;
        }

        return Serial;
    }

    public ref GlyphInfo Cur(int i = 0) => ref Info[Idx + i];

    public ref GlyphPos CurPos(int i = 0) => ref Pos[Idx + i];

    public ref GlyphInfo Prev() => ref OutInfo[OutLen > 0 ? OutLen - 1 : 0];

    public int BacktrackLen => HaveOutput ? OutLen : Idx;

    public int LookaheadLen => Len - Idx;

    public void Ensure(int size)
    {
        if (size <= Info.Length)
        {
            return;
        }

        if (size > MaxLen)
        {
            Successful = false;
            throw new LayoutLimitException();
        }

        int newSize = Math.Max(size, Info.Length * 2);
        bool separate = OutInfo != Info;
        Array.Resize(ref Info, newSize);
        Array.Resize(ref Pos, newSize);
        if (separate)
        {
            Array.Resize(ref OutInfo, newSize);
        }
        else
        {
            OutInfo = Info;
        }
    }

    public void ClearOutput()
    {
        HaveOutput = true;
        HavePositions = false;
        Idx = 0;
        OutLen = 0;
        OutInfo = Info;
    }

    public void ClearPositions()
    {
        HaveOutput = false;
        HavePositions = true;
        OutLen = 0;
        OutInfo = Info;
        Array.Clear(Pos, 0, Len);
    }

    private void MakeRoomFor(int numIn, int numOut)
    {
        Ensure(OutLen + numOut);
        if (OutInfo == Info && OutLen + numOut > Idx + numIn)
        {
            // Split the output from the input, as HarfBuzz does by reusing the position array.
            GlyphInfo[] split = new GlyphInfo[Info.Length];
            Array.Copy(Info, split, OutLen);
            OutInfo = split;
        }
        else if (OutInfo != Info && OutInfo.Length < OutLen + numOut)
        {
            Array.Resize(ref OutInfo, Math.Max(OutLen + numOut, OutInfo.Length * 2));
        }
    }

    private void EnsureOut(int size)
    {
        if (OutInfo != Info && OutInfo.Length < size)
        {
            Array.Resize(ref OutInfo, Math.Max(size, OutInfo.Length * 2));
        }

        if (OutInfo == Info)
        {
            Ensure(size);
        }
    }

    public void ShiftForward(int count)
    {
        Ensure(Len + count);
        MaxOps -= Len - Idx;
        if (MaxOps < 0)
        {
            Successful = false;
            throw new LayoutLimitException();
        }

        Array.Copy(Info, Idx, Info, Idx + count, Len - Idx);
        if (Idx + count > Len)
        {
            Array.Clear(Info, Len, Idx + count - Len);
        }

        Len += count;
        Idx += count;
    }

    public bool Sync()
    {
        NextGlyphs(Len - Idx);
        if (OutInfo != Info)
        {
            GlyphInfo[] old = Info;
            Info = OutInfo;
            if (Pos.Length < Info.Length)
            {
                Array.Resize(ref Pos, Info.Length);
            }

            _ = old;
        }

        Len = OutLen;
        HaveOutput = false;
        OutLen = 0;
        OutInfo = Info;
        Idx = 0;
        return true;
    }

    public bool MoveTo(int i)
    {
        if (!HaveOutput)
        {
            Idx = i;
            return true;
        }

        if (OutLen < i)
        {
            int count = i - OutLen;
            MakeRoomFor(count, count);
            MaxOps -= count;
            if (MaxOps < 0)
            {
                Successful = false;
                throw new LayoutLimitException();
            }

            Array.Copy(Info, Idx, OutInfo, OutLen, count);
            Idx += count;
            OutLen += count;
        }
        else if (OutLen > i)
        {
            int count = OutLen - i;
            if (Idx < count)
            {
                ShiftForward(count - Idx);
            }

            MaxOps -= count;
            if (MaxOps < 0)
            {
                Successful = false;
                throw new LayoutLimitException();
            }

            Idx -= count;
            OutLen -= count;
            Array.Copy(OutInfo, OutLen, Info, Idx, count);
        }

        return true;
    }

    public void ReplaceGlyphs(int numIn, ReadOnlySpan<uint> glyphs)
    {
        MakeRoomFor(numIn, glyphs.Length);
        MergeClusters(Idx, Idx + numIn);
        GlyphInfo orig = Idx < Len ? Info[Idx] : Prev();
        for (int i = 0; i < glyphs.Length; i++)
        {
            GlyphInfo info = orig;
            info.Codepoint = glyphs[i];
            OutInfo[OutLen + i] = info;
        }

        Idx += numIn;
        OutLen += glyphs.Length;
    }

    public void ReplaceGlyph(uint glyph)
    {
        if (OutInfo != Info || OutLen != Idx)
        {
            MakeRoomFor(1, 1);
            OutInfo[OutLen] = Info[Idx];
        }

        OutInfo[OutLen].Codepoint = glyph;
        Idx++;
        OutLen++;
    }

    public void OutputGlyph(uint glyph)
    {
        MakeRoomFor(0, 1);
        GlyphInfo info = Idx < Len ? Info[Idx] : Prev();
        info.Codepoint = glyph;
        OutInfo[OutLen] = info;
        OutLen++;
    }

    public void OutputInfo(in GlyphInfo info)
    {
        MakeRoomFor(0, 1);
        OutInfo[OutLen] = info;
        OutLen++;
    }

    public void CopyGlyph() => OutputInfo(Info[Idx]);

    public void NextGlyph()
    {
        if (HaveOutput)
        {
            if (OutInfo != Info || OutLen != Idx)
            {
                EnsureOut(OutLen + 1);
                OutInfo[OutLen] = Info[Idx];
            }

            OutLen++;
        }

        Idx++;
    }

    public void NextGlyphs(int n)
    {
        if (HaveOutput)
        {
            if (OutInfo != Info || OutLen != Idx)
            {
                EnsureOut(OutLen + n);
                Array.Copy(Info, Idx, OutInfo, OutLen, n);
            }

            OutLen += n;
        }

        Idx += n;
    }

    public void SkipGlyph() => Idx++;

    public void ResetMasks(uint mask)
    {
        for (int i = 0; i < Len; i++)
        {
            Info[i].Mask = mask;
        }
    }

    public void AddMasks(uint mask)
    {
        for (int i = 0; i < Len; i++)
        {
            Info[i].Mask |= mask;
        }
    }

    public void SetMasks(uint value, uint mask, int clusterStart, int clusterEnd)
    {
        if (mask == 0)
        {
            return;
        }

        uint notMask = ~mask;
        value &= mask;
        CountOps(Len);
        for (int i = 0; i < Len; i++)
        {
            if (clusterStart <= Info[i].Cluster && Info[i].Cluster < clusterEnd)
            {
                Info[i].Mask = (Info[i].Mask & notMask) | value;
            }
        }
    }

    public void ReverseRange(int start, int end)
    {
        Array.Reverse(Info, start, end - start);
        if (HavePositions)
        {
            Array.Reverse(Pos, start, end - start);
        }
    }

    public void Reverse() => ReverseRange(0, Len);

    public void ReverseClusters()
    {
        if (Len == 0)
        {
            return;
        }

        int start = 0;
        int i;
        for (i = 1; i < Len; i++)
        {
            if (Info[i - 1].Cluster != Info[i].Cluster)
            {
                ReverseRange(start, i);
                start = i;
            }
        }

        ReverseRange(start, i);
        Reverse();
    }

    public void MergeClusters(int start, int end)
    {
        if (end - start < 2)
        {
            return;
        }

        if (!IsMonotone)
        {
            UnsafeToBreak(start, end);
            return;
        }

        MergeClustersImpl(start, end);
    }

    public void MergeGraphemeClusters(int start, int end)
    {
        if (end - start < 2)
        {
            return;
        }

        if (!IsGraphemes)
        {
            UnsafeToBreak(start, end);
            return;
        }

        MergeClustersImpl(start, end);
    }

    private void MergeClustersImpl(int start, int end)
    {
        CountOps(end - start);
        int cluster = Info[start].Cluster;
        for (int i = start + 1; i < end; i++)
        {
            cluster = Math.Min(cluster, Info[i].Cluster);
        }

        if (cluster != Info[end - 1].Cluster)
        {
            while (end < Len && Info[end - 1].Cluster == Info[end].Cluster)
            {
                end++;
            }
        }

        if (cluster != Info[start].Cluster)
        {
            while (Idx < start && Info[start - 1].Cluster == Info[start].Cluster)
            {
                start--;
            }
        }

        if (Idx == start && Info[start].Cluster != cluster)
        {
            for (int i = OutLen; i > 0 && OutInfo[i - 1].Cluster == Info[start].Cluster; i--)
            {
                SetCluster(ref OutInfo[i - 1], cluster);
            }
        }

        for (int i = start; i < end; i++)
        {
            SetCluster(ref Info[i], cluster);
        }
    }

    public void MergeOutClusters(int start, int end)
    {
        if (end - start < 2 || !IsMonotone)
        {
            return;
        }

        MergeOutClustersImpl(start, end);
    }

    public void MergeOutGraphemeClusters(int start, int end)
    {
        if (end - start < 2 || !IsGraphemes)
        {
            return;
        }

        MergeOutClustersImpl(start, end);
    }

    private void MergeOutClustersImpl(int start, int end)
    {
        CountOps(end - start);
        int cluster = OutInfo[start].Cluster;
        for (int i = start + 1; i < end; i++)
        {
            cluster = Math.Min(cluster, OutInfo[i].Cluster);
        }

        while (start > 0 && OutInfo[start - 1].Cluster == OutInfo[start].Cluster)
        {
            start--;
        }

        while (end < OutLen && OutInfo[end - 1].Cluster == OutInfo[end].Cluster)
        {
            end++;
        }

        if (end == OutLen)
        {
            for (int i = Idx; i < Len && Info[i].Cluster == OutInfo[end - 1].Cluster; i++)
            {
                SetCluster(ref Info[i], cluster);
            }
        }

        for (int i = start; i < end; i++)
        {
            SetCluster(ref OutInfo[i], cluster);
        }
    }

    /// <summary>Merges clusters for deleting the current glyph, and skips it.</summary>
    public void DeleteGlyph()
    {
        int cluster = Info[Idx].Cluster;
        if ((Idx + 1 < Len && cluster == Info[Idx + 1].Cluster) || (OutLen > 0 && cluster == OutInfo[OutLen - 1].Cluster))
        {
            // The cluster survives.
        }
        else if (OutLen > 0)
        {
            if (cluster < OutInfo[OutLen - 1].Cluster)
            {
                uint mask = Info[Idx].Mask;
                int oldCluster = OutInfo[OutLen - 1].Cluster;
                for (int i = OutLen; i > 0 && OutInfo[i - 1].Cluster == oldCluster; i--)
                {
                    SetCluster(ref OutInfo[i - 1], cluster, mask);
                }
            }
        }
        else if (Idx + 1 < Len)
        {
            MergeClusters(Idx, Idx + 2);
        }

        SkipGlyph();
    }

    public void DeleteGlyphsInplace(Func<GlyphInfo, bool> filter)
    {
        int j = 0;
        int count = Len;
        for (int i = 0; i < count; i++)
        {
            if (filter(Info[i]))
            {
                int cluster = Info[i].Cluster;
                if (i + 1 < count && cluster == Info[i + 1].Cluster)
                {
                    continue;
                }

                if (j > 0)
                {
                    if (cluster < Info[j - 1].Cluster)
                    {
                        uint mask = Info[i].Mask;
                        int oldCluster = Info[j - 1].Cluster;
                        for (int k = j; k > 0 && Info[k - 1].Cluster == oldCluster; k--)
                        {
                            SetCluster(ref Info[k - 1], cluster, mask);
                        }
                    }

                    continue;
                }

                if (i + 1 < count)
                {
                    MergeClusters(i, i + 2);
                }

                continue;
            }

            if (j != i)
            {
                Info[j] = Info[i];
                Pos[j] = Pos[i];
            }

            j++;
        }

        Len = j;
    }

    /// <summary>A stable insertion sort that merges the clusters of what moves, as HarfBuzz's sort().</summary>
    public void Sort(int start, int end, Comparison<GlyphInfo> compare)
    {
        for (int i = start + 1; i < end; i++)
        {
            int j = i;
            while (j > start && compare(Info[j - 1], Info[i]) > 0)
            {
                j--;
            }

            if (i == j)
            {
                continue;
            }

            MergeClusters(j, i + 1);
            GlyphInfo t = Info[i];
            Array.Copy(Info, j, Info, j + 1, i - j);
            Info[j] = t;
        }
    }

    public static void SetCluster(ref GlyphInfo info, int cluster, uint mask = 0)
    {
        if (info.Cluster != cluster)
        {
            info.Mask = (info.Mask & ~FlagDefined) | (mask & FlagDefined);
        }

        info.Cluster = cluster;
    }

    // ---- Glyph flags ----

    public void UnsafeToBreak(int start = 0, int end = int.MaxValue) =>
        SetGlyphFlags(FlagUnsafeToBreak | FlagUnsafeToConcat, start, end, interior: true, fromOutBuffer: false);

    public void UnsafeToConcat(int start = 0, int end = int.MaxValue)
    {
        if (ProduceUnsafeToConcat)
        {
            SetGlyphFlags(FlagUnsafeToConcat, start, end, interior: false, fromOutBuffer: false);
        }
    }

    public void UnsafeToBreakFromOutbuffer(int start = 0, int end = int.MaxValue) =>
        SetGlyphFlags(FlagUnsafeToBreak | FlagUnsafeToConcat, start, end, interior: true, fromOutBuffer: true);

    public void UnsafeToConcatFromOutbuffer(int start = 0, int end = int.MaxValue)
    {
        if (ProduceUnsafeToConcat)
        {
            SetGlyphFlags(FlagUnsafeToConcat, start, end, interior: false, fromOutBuffer: true);
        }
    }

    public void SafeToInsertTatweel(int start = 0, int end = int.MaxValue)
    {
        if (!ProduceSafeToInsertTatweel)
        {
            UnsafeToBreak(start, end);
            return;
        }

        SetGlyphFlags(FlagSafeToInsertTatweel, start, end, interior: true, fromOutBuffer: false);
    }

    private void SetGlyphFlags(uint mask, int start, int end, bool interior, bool fromOutBuffer)
    {
        if (end != int.MaxValue && end - start > 255)
        {
            return;
        }

        end = Math.Min(end, Len);
        if (interior && !fromOutBuffer && end - start < 2)
        {
            return;
        }

        if (!fromOutBuffer || !HaveOutput)
        {
            if (!interior)
            {
                for (int i = start; i < end; i++)
                {
                    Info[i].Mask |= mask;
                }
            }
            else
            {
                int cluster = FindMinCluster(Info, start, end, int.MaxValue);
                SetInfosGlyphFlags(Info, start, end, cluster, mask);
            }
        }
        else
        {
            if (!interior)
            {
                for (int i = start; i < OutLen; i++)
                {
                    OutInfo[i].Mask |= mask;
                }

                for (int i = Idx; i < end; i++)
                {
                    Info[i].Mask |= mask;
                }
            }
            else
            {
                int cluster = FindMinCluster(Info, Idx, end, int.MaxValue);
                cluster = FindMinCluster(OutInfo, start, OutLen, cluster);
                SetInfosGlyphFlags(OutInfo, start, OutLen, cluster, mask);
                SetInfosGlyphFlags(Info, Idx, end, cluster, mask);
            }
        }
    }

    private void SetInfosGlyphFlags(GlyphInfo[] infos, int start, int end, int cluster, uint mask)
    {
        if (start >= end)
        {
            return;
        }

        CountOps(end - start);
        int first = infos[start].Cluster;
        int last = infos[end - 1].Cluster;
        if (ClusterLevel == LayoutClusterLevel.Characters || (cluster != first && cluster != last))
        {
            for (int i = start; i < end; i++)
            {
                if (cluster != infos[i].Cluster)
                {
                    infos[i].Mask |= mask;
                }
            }

            return;
        }

        if (cluster == first)
        {
            for (int i = end; start < i && infos[i - 1].Cluster != first; i--)
            {
                infos[i - 1].Mask |= mask;
            }
        }
        else
        {
            for (int i = start; i < end && infos[i].Cluster != last; i++)
            {
                infos[i].Mask |= mask;
            }
        }
    }

    private int FindMinCluster(GlyphInfo[] infos, int start, int end, int cluster)
    {
        if (start >= end)
        {
            return cluster;
        }

        if (ClusterLevel == LayoutClusterLevel.Characters)
        {
            for (int i = start; i < end; i++)
            {
                cluster = Math.Min(cluster, infos[i].Cluster);
            }

            return cluster;
        }

        return Math.Min(cluster, Math.Min(infos[start].Cluster, infos[end - 1].Cluster));
    }

    public void ClearGlyphFlags(uint mask = 0)
    {
        for (int i = 0; i < Len; i++)
        {
            Info[i].Mask = (Info[i].Mask & ~FlagDefined) | (mask & FlagDefined);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void CountOps(int count)
    {
        MaxOps -= count;
        if (MaxOps < 0)
        {
            Successful = false;
        }
    }
}

/// <summary>Thrown inside the engine when a font drives the buffer past its operation or length limit; the shaper stops cleanly.</summary>
internal sealed class LayoutLimitException : Exception
{
    public LayoutLimitException()
        : base("The font's layout tables exceeded the shaping limits.")
    {
    }
}
