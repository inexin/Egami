namespace Egami.Video.Audio.Codecs.Ac3;

/// <summary>
/// The MP4 configuration boxes of AC-3 and E-AC-3 (ETSI TS 102 366 Annex F): dac3 (AC3SpecificBox) and dec3 (EC3SpecificBox),
/// built from a packet's frame headers, or from the track's rate and layout when no packet is at hand.
/// </summary>
internal static class Ac3Config
{
    /// <summary>The 3-byte dac3 body: fscod, bsid, bsmod, acmod, lfeon and the bit rate code.</summary>
    public static byte[] Dac3(Ac3Header h) => Dac3(h.Fscod, h.Bsid, h.Bsmod, h.Acmod, h.Lfe, h.FrmSizeCod >> 1);

    /// <summary>A dac3 body for a track known only by its rate and channels (8 for bsid, 0 for bsmod and the bit rate code).</summary>
    public static byte[] Dac3(AudioTrackInfo track)
    {
        (int acmod, bool lfe) = Mode(track);
        return Dac3(Fscod(track.SampleRate), 8, 0, acmod, lfe, 0);
    }

    /// <summary>
    /// Builds a dec3 body from the frames of one E-AC-3 packet: the data rate, then per independent substream its fscod, bsid,
    /// bsmod, acmod, lfeon and how many dependent substreams follow it, with their channel locations.
    /// </summary>
    public static byte[] Dec3(ReadOnlySpan<byte> packet)
    {
        List<(Ac3Header Header, int Dependents, int ChannelLocations)> independents = [];
        int at = 0;
        long bytes = 0;
        int samples = 0;
        while (at + Ac3Header.Size <= packet.Length && Ac3Header.TryRead(packet[at..], out Ac3Header h) && h.IsEac3)
        {
            if (h.StreamType != Ac3Header.Dependent)
            {
                independents.Add((h, 0, 0));
                samples = h.Samples;
            }
            else if (independents.Count > 0)
            {
                (Ac3Header owner, int dependents, int locations) = independents[^1];
                independents[^1] = (owner, dependents + 1, locations | ChannelLocations(packet[at..]));
            }

            bytes += h.FrameBytes;
            at += h.FrameBytes;
        }

        if (independents.Count == 0)
        {
            return [];
        }

        Ac3Header first = independents[0].Header;
        int dataRate = samples > 0 ? (int)Math.Min(8191, bytes * 8 * first.SampleRate / samples / 1000) : 0;
        BitWriter w = new(3 + (independents.Count * 3) + 2);
        w.Write(dataRate, 13);
        w.Write(independents.Count - 1, 3);
        foreach ((Ac3Header h, int dependents, int locations) in independents)
        {
            w.Write(h.Fscod, 2);
            w.Write(h.Bsid, 5);
            w.Write(0, 1); // reserved
            w.Write(0, 1); // asvc
            w.Write(h.Bsmod, 3);
            w.Write(h.Acmod, 3);
            w.Write(h.Lfe ? 1 : 0, 1);
            w.Write(0, 3); // reserved
            w.Write(Math.Min(dependents, 15), 4);
            if (dependents > 0)
            {
                w.Write(locations, 9);
            }
            else
            {
                w.Write(0, 1); // reserved
            }
        }

        // A zero byte for the extension fields later revisions of the box add (no Atmos), as FFmpeg writes it.
        w.Write(0, 8);
        return w.ToArray();
    }

    /// <summary>A dec3 body for a track known only by its rate and channels: one independent substream, data rate 0.</summary>
    public static byte[] Dec3(AudioTrackInfo track)
    {
        (int acmod, bool lfe) = Mode(track);
        BitWriter w = new(6);
        w.Write(0, 13);
        w.Write(0, 3);
        w.Write(Fscod(track.SampleRate), 2);
        w.Write(16, 5);
        w.Write(0, 5);
        w.Write(acmod, 3);
        w.Write(lfe ? 1 : 0, 1);
        w.Write(0, 7);
        w.Write(0, 1);
        w.Write(0, 8);
        return w.ToArray();
    }

    private static byte[] Dac3(int fscod, int bsid, int bsmod, int acmod, bool lfe, int bitRateCode)
    {
        BitWriter w = new(3);
        w.Write(fscod, 2);
        w.Write(bsid, 5);
        w.Write(bsmod, 3);
        w.Write(acmod, 3);
        w.Write(lfe ? 1 : 0, 1);
        w.Write(bitRateCode, 5);
        w.Write(0, 5);
        return w.ToArray();
    }

    private static int Fscod(int rate) => rate switch { 44100 => 1, 32000 => 2, _ => 0 };

    /// <summary>The acmod and LFE flag that match a track's layout, by its full-bandwidth speakers.</summary>
    private static (int Acmod, bool Lfe) Mode(AudioTrackInfo track)
    {
        ChannelLayout layout = track.EffectiveLayout;
        bool lfe = (layout.Speakers & Speakers.LowFrequency) != 0;
        for (int acmod = 7; acmod >= 1; acmod--)
        {
            if (Ac3Header.LayoutOf(acmod, lfe).Speakers == layout.Speakers)
            {
                return (acmod, lfe);
            }
        }

        int full = track.Channels - (lfe ? 1 : 0);
        return (full switch { 1 => 1, 2 => 2, 3 => 3, 4 => 6, _ => 7 }, lfe);
    }

    /// <summary>
    /// The dec3 channel locations a dependent substream adds, from its chanmap (A/52 Annex E, Table E2.5: bit 0, the most
    /// significant, is L). dec3's nine bits are, from the top: Lc/Rc, Lrs/Rrs, Cs, Ts, Lsd/Rsd, Lw/Rw, Lvh/Rvh, Cvh and LFE2,
    /// which are chanmap bits 5 to 12 and 14.
    /// </summary>
    private static int ChannelLocations(ReadOnlySpan<byte> frame)
    {
        // After the 40 bits of syncinfo and the E-AC-3 header fields up to bsid: dialnorm, compre (compr), the same for the
        // second mono channel when acmod is 0, then chanmape and chanmap.
        if (frame.Length < 12)
        {
            return 0;
        }

        BitReader r = new(frame);
        r.Skip(16 + 2 + 3 + 11 + 2 + 2);
        int acmod = r.Read(3);
        r.Skip(1 + 5);
        r.Skip(5);
        if (r.Read(1) != 0)
        {
            r.Skip(8);
        }

        if (acmod == 0)
        {
            r.Skip(5);
            if (r.Read(1) != 0)
            {
                r.Skip(8);
            }
        }

        if (r.Read(1) == 0)
        {
            return 0;
        }

        int chanmap = r.Read(16);
        return (((chanmap >> 3) & 0xFF) << 1) | ((chanmap >> 1) & 1);
    }

    private ref struct BitReader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _bit;

        public void Skip(int bits) => _bit += bits;

        public int Read(int bits)
        {
            int value = 0;
            for (int i = 0; i < bits; i++, _bit++)
            {
                int b = (_bit >> 3) < _data.Length ? (_data[_bit >> 3] >> (7 - (_bit & 7))) & 1 : 0;
                value = (value << 1) | b;
            }

            return value;
        }
    }

    private sealed class BitWriter(int capacity)
    {
        private readonly List<byte> _bytes = new(capacity);
        private int _bits;
        private int _count;

        public void Write(int value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _bits = (_bits << 1) | ((value >> i) & 1);
                if (++_count == 8)
                {
                    _bytes.Add((byte)_bits);
                    _bits = _count = 0;
                }
            }
        }

        public byte[] ToArray()
        {
            if (_count > 0)
            {
                _bytes.Add((byte)(_bits << (8 - _count)));
                _bits = _count = 0;
            }

            return [.. _bytes];
        }
    }
}
