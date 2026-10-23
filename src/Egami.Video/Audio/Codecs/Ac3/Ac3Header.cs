namespace Egami.Video.Audio.Codecs.Ac3;

/// <summary>
/// An AC-3 or E-AC-3 sync frame's header: the syncinfo and the start of the bit stream information (ATSC A/52:2018 sections 5.3.1
/// and 5.3.2, and E.1.2 for E-AC-3). bsid 0 to 8 is AC-3 (9 and 10 are its half and quarter rates), 11 to 16 is E-AC-3.
/// </summary>
internal readonly record struct Ac3Header(
    bool IsEac3,
    int FrameBytes,
    int SampleRate,
    int Blocks,
    int Acmod,
    bool Lfe,
    int Bsid,
    int Bsmod,
    int Fscod,
    int FrmSizeCod,
    int StreamType,
    int SubstreamId)
{
    /// <summary>The bytes a header needs: syncinfo and the BSI up to lfeon.</summary>
    public const int Size = 8;

    /// <summary>The sync word, 0x0B77.</summary>
    public const ushort SyncWord = 0x0B77;

    /// <summary>E-AC-3's stream types (strmtyp): independent, dependent, and independent transcoded from AC-3.</summary>
    public const int Dependent = 1;

    // The nominal bit rates in kbit/s, by frmsizecod / 2 (Table 5.18).
    private static ReadOnlySpan<short> BitRates => [32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 448, 512, 576, 640];

    // Sample rates by fscod; E-AC-3's fscod2 gives the half rates.
    private static ReadOnlySpan<int> Rates => [48000, 44100, 32000];

    // Full-bandwidth channels by acmod (Table 5.8); acmod 0 is two independent mono channels (1+1).
    private static ReadOnlySpan<byte> FullChannels => [2, 1, 2, 3, 3, 4, 4, 5];

    /// <summary>Gets the audio samples one frame holds per channel: 256 per block.</summary>
    public int Samples => Blocks * 256;

    /// <summary>Gets the channels, LFE included.</summary>
    public int Channels => FullChannels[Acmod] + (Lfe ? 1 : 0);

    /// <summary>Gets the nominal bit rate in kbit/s (AC-3), or the one the frame size gives (E-AC-3).</summary>
    public int BitRate => IsEac3 ? (int)((long)FrameBytes * 8 * SampleRate / Samples / 1000) : BitRates[FrmSizeCod >> 1];

    /// <summary>Gets the channel layout in Egami.Video's (WAV's) order: surrounds are side speakers, a single surround is back center.</summary>
    public ChannelLayout Layout => LayoutOf(Acmod, Lfe);

    public static ChannelLayout LayoutOf(int acmod, bool lfe)
    {
        Speakers speakers = acmod switch
        {
            0 or 2 => Speakers.FrontLeft | Speakers.FrontRight,
            1 => Speakers.FrontCenter,
            3 => Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter,
            4 => Speakers.FrontLeft | Speakers.FrontRight | Speakers.BackCenter,
            5 => Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter | Speakers.BackCenter,
            6 => Speakers.FrontLeft | Speakers.FrontRight | Speakers.SideLeft | Speakers.SideRight,
            _ => Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter | Speakers.SideLeft | Speakers.SideRight,
        };
        return new ChannelLayout(speakers | (lfe ? Speakers.LowFrequency : Speakers.None));
    }

    /// <summary>Returns the frame size in bytes of an AC-3 frame (Table 5.18), or 0 for a code with none.</summary>
    public static int FrameSize(int fscod, int frmsizecod)
    {
        if (fscod > 2 || frmsizecod > 37)
        {
            return 0;
        }

        int rate = BitRates[frmsizecod >> 1];
        int words = fscod switch
        {
            0 => rate * 2,
            2 => rate * 3,
            // 44.1 kHz: 1536 samples at the bit rate, in 16-bit words, rounded down; odd codes add a word.
            _ => (rate * 1000 * 1536 / 44100 / 16) + (frmsizecod & 1),
        };
        return words * 2;
    }

    /// <summary>Reads a header, or returns false when the bytes are not one.</summary>
    public static bool TryRead(ReadOnlySpan<byte> h, out Ac3Header header)
    {
        header = default;
        if (h.Length < Size || h[0] != 0x0B || h[1] != 0x77)
        {
            return false;
        }

        int bsid = h[5] >> 3;
        if (bsid <= 10)
        {
            int fscod = h[4] >> 6;
            int frmsizecod = h[4] & 0x3F;
            int bytes = FrameSize(fscod, frmsizecod);
            if (bytes == 0)
            {
                return false;
            }

            int acmod = h[6] >> 5;
            int bit = 3;
            if ((acmod & 1) != 0 && acmod != 1)
            {
                bit += 2; // cmixlev
            }

            if ((acmod & 4) != 0)
            {
                bit += 2; // surmixlev
            }

            if (acmod == 2)
            {
                bit += 2; // dsurmod
            }

            int at = 48 + bit;
            bool lfe = ((h[at >> 3] >> (7 - (at & 7))) & 1) != 0;

            // bsid 9 and 10 (A/52 Annex, "AC-3 at half and quarter rates") halve the rate once or twice.
            int shift = Math.Max(0, bsid - 8);
            header = new Ac3Header(false, bytes, Rates[fscod] >> shift, 6, acmod, lfe, bsid, h[5] & 7, fscod, frmsizecod, 0, 0);
            return true;
        }

        if (bsid is < 11 or > 16)
        {
            return false;
        }

        int strmtyp = h[2] >> 6;
        int substreamid = (h[2] >> 3) & 7;
        int frmsiz = ((h[2] & 7) << 8) | h[3];
        int fscodE = h[4] >> 6;
        int numblkscod = (h[4] >> 4) & 3;
        int rate;
        int blocks;
        if (fscodE == 3)
        {
            // fscod2 picks a half rate; such frames always hold six blocks.
            if (numblkscod == 3)
            {
                return false;
            }

            rate = Rates[numblkscod] / 2;
            blocks = 6;
        }
        else
        {
            rate = Rates[fscodE];
            blocks = numblkscod switch { 0 => 1, 1 => 2, 2 => 3, _ => 6 };
        }

        if (strmtyp == 3 || frmsiz < 2)
        {
            return false;
        }

        header = new Ac3Header(true, (frmsiz + 1) * 2, rate, blocks, (h[4] >> 1) & 7, (h[4] & 1) != 0, bsid, 0, fscodE, 0, strmtyp, substreamid);
        return true;
    }
}
