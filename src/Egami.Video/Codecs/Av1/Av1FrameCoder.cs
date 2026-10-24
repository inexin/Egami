using Egami.Formats.Av1;
using Egami.Pixels;
using Egami.Video.Codecs.Encoding;

namespace Egami.Video.Codecs.Av1;

/// <summary>
/// Codes the core's decisions with Egami's <see cref="Av1StreamEncoder"/>, keeping track of which frame each of the eight
/// reference slots holds. Each inter frame reads its three nearest past frames as LAST to LAST3, the golden frame as GOLDEN and
/// the hidden frames still to be shown, nearest first, as BWDREF, ALTREF2 and ALTREF; it refreshes a slot whose frame nothing
/// needs any more. A hidden frame's OBUs lead the next shown frame's temporal unit (the stream encoder holds them), and an
/// overlay shows its hidden frame again with show_existing_frame, so it costs a few bytes. A keyframe's unit carries the static
/// HDR metadata OBUs after the sequence header; a frame with HDR10+ gets its ITU-T T.35 metadata OBU.
/// </summary>
internal sealed class Av1FrameCoder : IFrameCoder
{
    /// <summary>The farthest in display order a reference may be: order hints of 7 bits tell apart 63 frames either way.</summary>
    private const int Reach = 60;

    private readonly Av1StreamEncoder _encoder;
    private readonly Av1SequenceOptions _sequence;
    private readonly Av1FramePlan _plan;
    private readonly byte[] _hdrObus;
    private readonly Dictionary<int, ReadOnlyMemory<byte>> _hdr10Plus;
    private readonly EgamiContext _context;
    private readonly int[] _slots = [-1, -1, -1, -1, -1, -1, -1, -1];
    private readonly List<byte> _unit = [];
    private int _golden;
    private int _lastShown = -1;
    private int _hiddenBytes;
    private readonly Dictionary<int, Av1FrameAnalysis> _analyses = [];

    public Av1FrameCoder(Av1SequenceOptions sequence, Av1FramePlan plan, byte[] hdrObus, Dictionary<int, ReadOnlyMemory<byte>> hdr10Plus, EgamiContext context)
    {
        _sequence = sequence;
        _plan = plan;
        _hdrObus = hdrObus;
        _hdr10Plus = hdr10Plus;
        _context = context;
        _encoder = new Av1StreamEncoder(sequence, context);
    }

    /// <summary>Gets the sequence header OBU, for the track's av1C.</summary>
    public ReadOnlyMemory<byte> SequenceHeader => _encoder.SequenceHeader;

    public short[] Steps => Vp9.Vp9Tables.AcQLookup8;

    public bool MergesHiddenFrames => true;

    public CodedFrame Code(YuvRaster<byte> picture, FrameDecision decision) => Code(picture, null, decision);

    public CodedFrame Code(YuvRaster<byte> picture, YuvRaster? full, FrameDecision decision)
    {
        YuvRaster source = full ?? picture;
        int index = decision.Index;
        _analyses.Remove(index, out Av1FrameAnalysis? analysis);
        Av1FramePlan common = _plan with { QIndex = decision.Quantizer, OrderHint = index, Layer = Math.Min(decision.Layer, 7), Analysis = analysis };
        switch (decision.Type)
        {
            case FrameType.Key:
                {
                    Av1EncodedFrame key = _encoder.Encode(source, common with { Kind = Av1FrameKind.Key });
                    Array.Fill(_slots, index);
                    _golden = 0;
                    return Shown(key, index, decision.Quantizer);
                }

            case FrameType.AltRef or FrameType.Hidden:
                {
                    int slot = Victim(hidden: true);
                    Av1EncodedFrame hidden = _encoder.Encode(source, common with
                    {
                        Show = false,
                        ReferenceSlots = References(index),
                        RefreshSlots = 1 << slot,
                    });
                    _slots[slot] = index;
                    int length = (int)(hidden.Bits / 8);
                    int bytes = 1 + Av1Obus.Leb128Length((ulong)length) + length;
                    _hiddenBytes += bytes;
                    return new CodedFrame(ReadOnlyMemory<byte>.Empty, null, hidden.QIndex, 0, bytes * 8L);
                }

            case FrameType.Overlay:
                {
                    int slot = Array.IndexOf(_slots, index);
                    if (slot < 0)
                    {
                        throw new InvalidOperationException($"No slot holds the hidden frame {index} to show.");
                    }

                    Av1EncodedFrame shown = _encoder.ShowExisting(slot);
                    if (decision.RefreshGolden)
                    {
                        _golden = slot;
                    }

                    return Shown(shown, index, decision.Quantizer);
                }

            default:
                {
                    if (!decision.Referenced)
                    {
                        // A pyramid's leaf: nothing predicts from it, so it takes no slot.
                        Av1EncodedFrame leaf = _encoder.Encode(source, common with { ReferenceSlots = References(index), RefreshSlots = 0, Referenced = false });
                        return Shown(leaf, index, decision.Quantizer);
                    }

                    int slot = Victim(hidden: false);
                    Av1EncodedFrame inter = _encoder.Encode(source, common with { ReferenceSlots = References(index), RefreshSlots = 1 << slot });
                    _slots[slot] = index;
                    if (decision.RefreshGolden)
                    {
                        _golden = slot;
                    }

                    return Shown(inter, index, decision.Quantizer);
                }
        }
    }

    /// <summary>A shown frame's temporal unit, with the metadata OBUs it carries, and the bytes of the hidden frames that lead it.</summary>
    private CodedFrame Shown(Av1EncodedFrame frame, int index, int quantizer)
    {
        _lastShown = index;
        int hidden = _hiddenBytes;
        _hiddenBytes = 0;
        bool hasT35 = _hdr10Plus.Remove(index, out ReadOnlyMemory<byte> t35);
        bool key = frame.Kind == Av1FrameKind.Key && !frame.IsShownExisting;
        int quantizerUsed = frame.IsShownExisting ? quantizer : frame.QIndex;
        if (!hasT35 && !(key && _hdrObus.Length > 0))
        {
            return new CodedFrame(frame.Data, null, quantizerUsed, hidden);
        }

        // Metadata OBUs go after the sequence header, else first: before the unit's frames.
        ReadOnlySpan<byte> data = frame.Data.Span;
        int at = 0;
        foreach (Av1Obu obu in Av1Obus.Split(data))
        {
            if (obu.Type == Av1Obus.SequenceHeader)
            {
                at = obu.End;
                break;
            }
        }

        _unit.Clear();
        _unit.AddRange(data[..at]);
        if (key)
        {
            _unit.AddRange(_hdrObus);
        }

        if (hasT35)
        {
            Av1VideoEncoder.MetadataObu(_unit, 4, t35.Span);
        }

        int added = _unit.Count - at;
        _unit.AddRange(data[at..]);

        // Metadata before the sequence header's end belongs to no hidden frame, so the hidden bytes stay where they were.
        return new CodedFrame(_unit.ToArray(), null, quantizerUsed, hidden + (at == 0 ? added : 0));
    }

    /// <summary>
    /// The slots LAST to ALTREF read for a frame at <paramref name="index"/>: the three nearest past frames, the golden frame, and
    /// the frames ahead nearest first (BWDREF, ALTREF2, then the farthest as ALTREF); older past frames, or LAST, where there are
    /// fewer.
    /// </summary>
    private int[] References(int index)
    {
        List<int> past = [], future = [];
        for (int slot = 0; slot < 8; slot++)
        {
            int frame = _slots[slot];
            if (frame < 0 || Math.Abs(frame - index) > Reach || Array.IndexOf(_slots, frame) != slot)
            {
                continue;
            }

            (frame < index ? past : future).Add(slot);
        }

        past.Sort((a, b) => _slots[b].CompareTo(_slots[a]));
        future.Sort((a, b) => _slots[a].CompareTo(_slots[b]));
        if (past.Count == 0)
        {
            // Only a hidden frame coded straight after a keyframe at its own place could get here; the keyframe is everywhere.
            past.Add(_golden);
        }

        int last = past[0];
        int last2 = past.Count > 1 ? past[1] : last;
        int last3 = past.Count > 2 ? past[2] : last2;
        int golden = Math.Abs(_slots[_golden] - index) <= Reach ? _golden : last;
        int older1 = past.Count > 3 ? past[3] : last3, older2 = past.Count > 4 ? past[4] : older1;
        int bwd = future.Count > 0 ? future[0] : older1;
        int alt2 = future.Count > 1 ? future[1] : future.Count > 0 ? future[0] : older2;
        int alt = future.Count > 0 ? future[^1] : golden;
        return [last, last2, last3, golden, bwd, alt2, alt];
    }

    /// <summary>
    /// The slot a new frame takes: the first whose frame nothing needs, keeping the golden frame, the hidden frames still to be
    /// shown and the most recent shown frames (two for a shown frame, which becomes the most recent itself, three for a hidden one).
    /// </summary>
    private int Victim(bool hidden)
    {
        // The most recent shown frames, newest first.
        int recent = hidden ? 3 : 2;
        Span<int> newest = stackalloc int[3];
        newest.Fill(-1);
        foreach (int frame in _slots)
        {
            if (frame < 0 || frame > _lastShown || newest.Contains(frame))
            {
                continue;
            }

            for (int i = 0; i < recent; i++)
            {
                if (newest[i] < frame)
                {
                    newest[i..(recent - 1)].CopyTo(newest[(i + 1)..recent]);
                    newest[i] = frame;
                    break;
                }
            }
        }

        int golden = _slots[_golden];
        for (int slot = 0; slot < 8; slot++)
        {
            int frame = _slots[slot];
            bool needed = frame >= 0 && (frame == golden || frame > _lastShown || newest[..recent].Contains(frame));
            bool copy = Array.IndexOf(_slots, frame) != slot && !(frame == golden && slot == _golden);
            if (!needed || (copy && slot != _golden))
            {
                return slot;
            }
        }

        throw new InvalidOperationException("Every reference slot holds a frame the stream still needs.");
    }

    public double[]? Analyze(IReadOnlyList<AnalysisFrame> frames)
    {
        if (frames.Count == 0)
        {
            return null;
        }

        Av1AnalysisFrame[] list = new Av1AnalysisFrame[frames.Count];
        for (int i = 0; i < list.Length; i++)
        {
            list[i] = new Av1AnalysisFrame { Picture = (YuvRaster?)frames[i].Full ?? frames[i].Picture, References = frames[i].References };
        }

        IReadOnlyList<Av1FrameAnalysis> result = _encoder.Analyze(list);
        double[] importance = new double[result.Count];
        for (int i = 0; i < result.Count; i++)
        {
            _analyses[frames[i].Index] = result[i];
            importance[i] = result[i].Importance;
        }

        return importance;
    }

    public (long Key, long Next) Probe(YuvRaster<byte> key, YuvRaster<byte>? next, int quantizer)
    {
        // The probe codes the core's 8-bit 4:2:0 pictures: what rate control learns is how the content's bits fall with the step.
        using Av1StreamEncoder probe = new(new Av1SequenceOptions { Width = _sequence.Width, Height = _sequence.Height }, _context);
        Av1FramePlan plan = _plan with { QIndex = quantizer };
        long keyBits = probe.Encode(key, plan with { Kind = Av1FrameKind.Key }).Bits;
        long nextBits = next is null ? 0 : probe.Encode(next, plan).Bits;
        return (keyBits, nextBits);
    }

    public void Dispose() => _encoder.Dispose();
}
