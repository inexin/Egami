// Follows the Vorbis I specification (Xiph.Org): section 4.2 "Header decode and decode setup" (identification 4.2.2, comment
// 4.2.3 with section 5, setup 4.2.4: codebooks, time domain transforms, floors, residues, mappings 4.2.4.4 and modes), and
// section 4.3 "Audio packet decode and synthesis": mode and window (4.3.1), floor decode (4.3.2), nonzero propagation (4.3.3),
// residue decode (4.3.4), inverse coupling (4.3.5), dot product (4.3.6), inverse MDCT (4.3.7) and overlap-add (4.3.8).
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Video.Audio.Codecs.Flac;

namespace Egami.Video.Audio.Codecs.Vorbis;

/// <summary>Decodes a Vorbis I stream: three header packets, then audio packets one at a time.</summary>
/// <remarks>
/// <see cref="Decode"/> returns the number of finished samples per channel the packet adds and leaves them in buffers that
/// <see cref="GetSamples"/> exposes, planar and in Vorbis channel order, as floats in the -1..1 range libvorbis's ov_read_float
/// gives. The first packet (and the first after <see cref="Reset"/>) only primes the overlap and returns no samples. Trimming
/// the stream's start and end to the Ogg granule positions is the caller's job.
/// </remarks>
internal sealed class VorbisDecoder
{
    // The total size of all codebook tables (entries plus expanded VQ values) one stream may declare.
    private const long CodebookBudget = 1 << 24;

    private readonly VorbisCodebook[] _books;
    private readonly VorbisFloor[] _floors;
    private readonly VorbisResidue[] _residues;
    private readonly Mapping[] _mappings;
    private readonly Mode[] _modes;
    private readonly int _modeBits;
    private readonly float[] _slope0;
    private readonly float[] _slope1;
    private readonly VorbisMdct _mdct0;
    private readonly VorbisMdct _mdct1;

    // Per-channel working buffers, sized once for the long block.
    private readonly float[][] _spectrum;
    private readonly int[][] _floorState;
    private readonly float[][] _floorScratch;
    private readonly bool[] _floorUsed;
    private readonly bool[] _decodeResidue;
    private readonly float[][] _previous;
    private readonly float[][] _output;
    private readonly float[] _dct;
    private readonly float[] _left;
    private readonly float[] _interleaved;
    private readonly int[][] _classes;
    private readonly float[][] _bundle;
    private readonly bool[] _doNotDecode;

    private int _previousBlockSize;
    private int _outputCount;

    /// <summary>Reads the identification, comment and setup headers.</summary>
    /// <exception cref="CorruptDataException">A header breaks the specification.</exception>
    /// <exception cref="UnsupportedFormatException">The stream is not Vorbis I, or declares tables beyond this decoder's limits.</exception>
    public VorbisDecoder(IReadOnlyList<byte[]> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);
        if (headers.Count != 3)
        {
            throw Vorbis.Corrupt($"A stream needs 3 header packets, not {headers.Count}.");
        }

        ReadIdentification(headers[0]);
        (Vendor, Comments) = ReadComment(headers[1]);

        VorbisBitReader r = new(Body(headers[2], 5), strict: true);
        long budget = CodebookBudget;
        _books = new VorbisCodebook[r.ReadBits(8) + 1];
        for (int i = 0; i < _books.Length; i++)
        {
            _books[i] = VorbisCodebook.Read(ref r, ref budget);
        }

        int times = (int)r.ReadBits(6) + 1;
        for (int i = 0; i < times; i++)
        {
            if (r.ReadBits(16) != 0)
            {
                throw Vorbis.Corrupt("A time domain transform is not the placeholder 0.");
            }
        }

        _floors = new VorbisFloor[r.ReadBits(6) + 1];
        for (int i = 0; i < _floors.Length; i++)
        {
            _floors[i] = VorbisFloor.Read(ref r, _books, BlockSize0, BlockSize1);
        }

        _residues = new VorbisResidue[r.ReadBits(6) + 1];
        for (int i = 0; i < _residues.Length; i++)
        {
            _residues[i] = VorbisResidue.Read(ref r, _books);
        }

        _mappings = new Mapping[r.ReadBits(6) + 1];
        for (int i = 0; i < _mappings.Length; i++)
        {
            _mappings[i] = Mapping.Read(ref r, Channels, _floors.Length, _residues.Length);
        }

        _modes = new Mode[r.ReadBits(6) + 1];
        for (int i = 0; i < _modes.Length; i++)
        {
            bool blockFlag = r.ReadBit();
            if (r.ReadBits(16) != 0 || r.ReadBits(16) != 0)
            {
                throw Vorbis.Corrupt("A mode has a window or transform type other than 0.");
            }

            int mapping = (int)r.ReadBits(8);
            if (mapping >= _mappings.Length)
            {
                throw Vorbis.Corrupt("A mode names a mapping that does not exist.");
            }

            _modes[i] = new Mode(blockFlag, mapping);
        }

        if (!r.ReadBit())
        {
            throw Vorbis.Corrupt("The setup header's framing bit is not set.");
        }

        _modeBits = Vorbis.ILog(_modes.Length - 1);
        _slope0 = Slope(BlockSize0 / 2);
        _slope1 = Slope(BlockSize1 / 2);
        _mdct0 = new VorbisMdct(BlockSize0);
        _mdct1 = BlockSize1 == BlockSize0 ? _mdct0 : new VorbisMdct(BlockSize1);

        int half = BlockSize1 / 2;
        int stateSize = _floors.Max(f => f.StateSize);
        int scratchSize = _floors.Max(f => f is VorbisFloor0 f0 ? f0.ScratchSize : 0);
        int classes = _residues.Max(x => x.ClassificationsSize(half));
        int classes2 = _residues.Max(x => x.Type == 2 ? x.ClassificationsSize(half * Channels) : 0);
        _spectrum = Buffers(Channels, half);
        _floorState = new int[Channels][];
        _floorScratch = Buffers(Channels, scratchSize);
        _previous = Buffers(Channels, half);
        _output = Buffers(Channels, half);
        _classes = new int[Channels][];
        for (int c = 0; c < Channels; c++)
        {
            _floorState[c] = new int[stateSize];
            _classes[c] = new int[c == 0 ? Math.Max(classes, classes2) : classes];
        }

        _floorUsed = new bool[Channels];
        _decodeResidue = new bool[Channels];
        _doNotDecode = new bool[Channels];
        _bundle = new float[Channels][];
        _dct = new float[half];
        _left = new float[half];
        _interleaved = _residues.Any(x => x.Type == 2) && Channels > 1 ? new float[half * Channels] : [];
    }

    public int Channels { get; private set; }

    public int SampleRate { get; private set; }

    public int BlockSize0 { get; private set; }

    public int BlockSize1 { get; private set; }

    /// <summary>Gets the most samples per channel one packet can return: half a long block.</summary>
    public int MaxSamplesPerPacket => BlockSize1 / 2;

    public string Vendor { get; }

    /// <summary>Gets the user comments, keys upper-cased; repeated keys are joined with "; ".</summary>
    public IReadOnlyDictionary<string, string> Comments { get; }

    /// <summary>Gets one channel's samples from the last <see cref="Decode"/>, valid until the next call.</summary>
    public ReadOnlySpan<float> GetSamples(int channel) => _output[channel].AsSpan(0, _outputCount);

    /// <summary>Forgets the overlap (after a seek): the next packet primes the decoder and returns no samples.</summary>
    public void Reset()
    {
        _previousBlockSize = 0;
        _outputCount = 0;
    }

    /// <summary>
    /// Returns the samples an audio packet adds, from its mode alone, and sets <paramref name="previousBlockSize"/> to its block
    /// size; 0 for the first packet (<paramref name="previousBlockSize"/> 0) or an unreadable one, which leaves it unchanged.
    /// </summary>
    public int PacketSamples(ReadOnlySpan<byte> packet, ref int previousBlockSize)
    {
        if (packet.IsEmpty || (packet[0] & 1) != 0)
        {
            return 0;
        }

        VorbisBitReader r = new(packet, strict: false);
        r.ReadBits(1);
        int mode = (int)r.ReadBits(_modeBits);
        if (r.Eop || mode >= _modes.Length)
        {
            return 0;
        }

        int n = _modes[mode].BlockFlag ? BlockSize1 : BlockSize0;
        int samples = previousBlockSize == 0 ? 0 : (previousBlockSize / 4) + (n / 4);
        previousBlockSize = n;
        return samples;
    }

    /// <summary>
    /// Decodes one audio packet and returns the finished samples per channel it adds (read them with <see cref="GetSamples"/>).
    /// An empty packet or a header packet returns 0 and changes nothing.
    /// </summary>
    /// <exception cref="CorruptDataException">The packet's mode or window flags cannot be read; the decoder state is unchanged.</exception>
    public int Decode(ReadOnlySpan<byte> packet)
    {
        _outputCount = 0;
        if (packet.IsEmpty)
        {
            return 0;
        }

        VorbisBitReader r = new(packet, strict: false);
        if (r.ReadBit())
        {
            return 0;
        }

        int modeNumber = (int)r.ReadBits(_modeBits);
        if (r.Eop || modeNumber >= _modes.Length)
        {
            throw Vorbis.Corrupt("An audio packet names a mode that does not exist.");
        }

        Mode mode = _modes[modeNumber];
        bool longBlock = mode.BlockFlag;
        bool previousLong = false;
        bool nextLong = false;
        if (longBlock)
        {
            previousLong = r.ReadBit();
            nextLong = r.ReadBit();
            if (r.Eop)
            {
                throw Vorbis.Corrupt("An audio packet ends in its window flags.");
            }
        }

        int n = longBlock ? BlockSize1 : BlockSize0;
        int half = n / 2;
        Mapping mapping = _mappings[mode.Mapping];
        int channels = Channels;

        // Floor decode (4.3.2).
        for (int c = 0; c < channels; c++)
        {
            VorbisFloor floor = _floors[mapping.SubmapFloor[mapping.Mux[c]]];
            _floorUsed[c] = floor.Decode(ref r, _floorState[c], _floorScratch[c]);
            _decodeResidue[c] = _floorUsed[c];
            _spectrum[c].AsSpan(0, half).Clear();
        }

        // Nonzero vector propagation (4.3.3).
        for (int i = 0; i < mapping.Magnitude.Length; i++)
        {
            int m = mapping.Magnitude[i];
            int a = mapping.Angle[i];
            if (_decodeResidue[m] || _decodeResidue[a])
            {
                _decodeResidue[m] = true;
                _decodeResidue[a] = true;
            }
        }

        // Residue decode (4.3.4), in submap order.
        for (int s = 0; s < mapping.SubmapResidue.Length; s++)
        {
            int count = 0;
            for (int c = 0; c < channels; c++)
            {
                if (mapping.Mux[c] == s)
                {
                    _doNotDecode[count] = !_decodeResidue[c];
                    _bundle[count] = _spectrum[c];
                    count++;
                }
            }

            if (count > 0)
            {
                _residues[mapping.SubmapResidue[s]].Decode(ref r, _bundle, _doNotDecode, count, half, _interleaved, _classes);
            }
        }

        // Inverse coupling (4.3.5).
        for (int i = mapping.Magnitude.Length - 1; i >= 0; i--)
        {
            Uncouple(_spectrum[mapping.Magnitude[i]].AsSpan(0, half), _spectrum[mapping.Angle[i]].AsSpan(0, half));
        }

        // Dot product (4.3.6), inverse MDCT (4.3.7) and overlap-add (4.3.8).
        VorbisMdct mdct = longBlock ? _mdct1 : _mdct0;
        int previous = _previousBlockSize;
        int outputCount = previous == 0 ? 0 : (previous / 4) + (n / 4);
        for (int c = 0; c < channels; c++)
        {
            Span<float> spectrum = _spectrum[c].AsSpan(0, half);
            Span<float> u = _dct.AsSpan(0, half);
            if (_floorUsed[c])
            {
                _floors[mapping.SubmapFloor[mapping.Mux[c]]].Apply(_floorState[c], _floorScratch[c], spectrum, longBlock);
                mdct.Dct4(spectrum, u);
            }
            else
            {
                u.Clear();
            }

            if (previous != 0)
            {
                Overlap(c, u, n, previous, longBlock && !previousLong, outputCount);
            }

            StoreRight(c, u, n, longBlock && !nextLong);
        }

        _previousBlockSize = n;
        _outputCount = outputCount;
        return outputCount;
    }

    private static void Uncouple(Span<float> magnitude, Span<float> angle)
    {
        for (int j = 0; j < magnitude.Length; j++)
        {
            float m = magnitude[j];
            float a = angle[j];
            if (m > 0)
            {
                if (a > 0)
                {
                    angle[j] = m - a;
                }
                else
                {
                    angle[j] = m;
                    magnitude[j] = m + a;
                }
            }
            else
            {
                if (a > 0)
                {
                    angle[j] = m + a;
                }
                else
                {
                    angle[j] = m;
                    magnitude[j] = m - a;
                }
            }
        }
    }

    /// <summary>
    /// Windows the current block's left half and adds the previous block's stored right half, aligning the previous 3/4 point
    /// with the current 1/4 point; writes the span from the previous center to the current center.
    /// </summary>
    private void Overlap(int channel, ReadOnlySpan<float> u, int n, int previous, bool shortLeft, int count)
    {
        int m = n / 2;
        int h = m / 2;

        // The windowed left half: y[x] = u[x + m/2] below m/2, -u[3m/2 - 1 - x] above.
        Span<float> left = _left.AsSpan(0, m);
        int start;
        float[] slope;
        if (shortLeft)
        {
            start = (n / 4) - (BlockSize0 / 4);
            slope = _slope0;
        }
        else
        {
            start = 0;
            slope = n == BlockSize1 ? _slope1 : _slope0;
        }

        int end = start + slope.Length;
        left[..start].Clear();
        for (int x = start; x < end; x++)
        {
            float y = x < h ? u[x + h] : -u[m + h - 1 - x];
            left[x] = y * slope[x - start];
        }

        for (int x = end; x < m; x++)
        {
            left[x] = x < h ? u[x + h] : -u[m + h - 1 - x];
        }

        // out[o] = previous[o] (while o < previous / 2) + left[o + n/4 - previous/4] (where that is >= 0).
        Span<float> output = _output[channel].AsSpan(0, count);
        ReadOnlySpan<float> stored = _previous[channel].AsSpan(0, previous / 2);
        int shift = (n / 4) - (previous / 4);
        if (shift >= 0)
        {
            // Current block at least as long: left[shift..] lines up with output[0..].
            ReadOnlySpan<float> cur = left[shift..];
            int both = Math.Min(stored.Length, count);
            Add(stored[..both], cur[..both], output);
            cur[both..count].CopyTo(output[both..]);
        }
        else
        {
            // Shorter current block: the previous block's flat part comes first.
            int lead = -shift;
            stored[..lead].CopyTo(output);
            Add(stored.Slice(lead, count - lead), left[..(count - lead)], output[lead..]);
        }
    }

    private static void Add(ReadOnlySpan<float> a, ReadOnlySpan<float> b, Span<float> output)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            ref float ra = ref MemoryMarshal.GetReference(a);
            ref float rb = ref MemoryMarshal.GetReference(b);
            ref float ro = ref MemoryMarshal.GetReference(output);
            for (; i + Vector<float>.Count <= a.Length; i += Vector<float>.Count)
            {
                (Vector.LoadUnsafe(ref ra, (nuint)i) + Vector.LoadUnsafe(ref rb, (nuint)i)).StoreUnsafe(ref ro, (nuint)i);
            }
        }

        for (; i < a.Length; i++)
        {
            output[i] = a[i] + b[i];
        }
    }

    /// <summary>Windows and stores the current block's right half for the next overlap.</summary>
    private void StoreRight(int channel, ReadOnlySpan<float> u, int n, bool shortRight)
    {
        int m = n / 2;
        int h = m / 2;

        // y[m + i] = -u[m/2 - 1 - i] below m/2, -u[i - m/2] above.
        Span<float> stored = _previous[channel].AsSpan(0, m);
        int start;
        float[] slope;
        if (shortRight)
        {
            start = (n / 4) - (BlockSize0 / 4);
            slope = _slope0;
        }
        else
        {
            start = 0;
            slope = n == BlockSize1 ? _slope1 : _slope0;
        }

        int end = start + slope.Length;
        for (int i = 0; i < start; i++)
        {
            stored[i] = i < h ? -u[h - 1 - i] : -u[i - h];
        }

        int last = slope.Length - 1;
        for (int i = start; i < end; i++)
        {
            float y = i < h ? -u[h - 1 - i] : -u[i - h];
            stored[i] = y * slope[last - (i - start)];
        }

        stored[end..].Clear();
    }

    /// <summary>The rising half of the power-sine window (4.3.1): sin(pi/2 sin^2((i + 1/2) / k pi/2)) for i below k.</summary>
    private static float[] Slope(int k)
    {
        float[] w = new float[k];
        for (int i = 0; i < k; i++)
        {
            double s = Math.Sin((i + 0.5) / k * Math.PI / 2);
            w[i] = (float)Math.Sin(Math.PI / 2 * s * s);
        }

        return w;
    }

    private static float[][] Buffers(int count, int size)
    {
        float[][] b = new float[count][];
        for (int i = 0; i < count; i++)
        {
            b[i] = new float[size];
        }

        return b;
    }

    private static ReadOnlySpan<byte> Body(byte[] packet, int type)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Length < 7 || packet[0] != type || !packet.AsSpan(1, 6).SequenceEqual("vorbis"u8))
        {
            throw Vorbis.Corrupt($"Header packet {(type + 1) / 2} is not a Vorbis header of type {type}.");
        }

        return packet.AsSpan(7);
    }

    private void ReadIdentification(byte[] packet)
    {
        ReadOnlySpan<byte> b = Body(packet, 1);
        if (b.Length < 23)
        {
            throw Vorbis.Corrupt("The identification header is too short.");
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(b) != 0)
        {
            throw Vorbis.Unsupported($"Vorbis version {BinaryPrimitives.ReadUInt32LittleEndian(b)} is not Vorbis I.");
        }

        Channels = b[4];
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(b[5..]);
        int exp0 = b[21] & 15;
        int exp1 = b[21] >> 4;
        if (Channels == 0 || rate == 0 || rate > int.MaxValue)
        {
            throw Vorbis.Corrupt("The identification header has no channels or no sample rate.");
        }

        if (exp0 < 6 || exp1 > 13 || exp0 > exp1)
        {
            throw Vorbis.Corrupt("The identification header's block sizes are out of range.");
        }

        if ((b[22] & 1) == 0)
        {
            throw Vorbis.Corrupt("The identification header's framing bit is not set.");
        }

        SampleRate = (int)rate;
        BlockSize0 = 1 << exp0;
        BlockSize1 = 1 << exp1;
    }

    private static (string Vendor, Dictionary<string, string> Comments) ReadComment(byte[] packet)
    {
        ReadOnlySpan<byte> b = Body(packet, 3);

        // Check the whole structure (section 5.2.1) first; FlacMetadata.ReadComments then fills the tags.
        long p = 0;
        long vendor = Length(b, ref p);
        p += vendor;
        long count = Length(b, ref p);
        for (long i = 0; i < count; i++)
        {
            long length = Length(b, ref p);
            p += length;
        }

        if (p >= b.Length || (b[(int)p] & 1) == 0)
        {
            throw Vorbis.Corrupt("The comment header's framing bit is not set.");
        }

        Dictionary<string, string> tags = new(StringComparer.Ordinal);
        FlacMetadata.ReadComments(b, tags);
        return (System.Text.Encoding.UTF8.GetString(b.Slice(4, (int)vendor)), tags);
    }

    private static long Length(ReadOnlySpan<byte> b, ref long p)
    {
        if (p + 4 > b.Length)
        {
            throw Vorbis.Corrupt("The comment header ends early.");
        }

        long length = BinaryPrimitives.ReadUInt32LittleEndian(b[(int)p..]);
        p += 4;
        if (length > b.Length - p)
        {
            throw Vorbis.Corrupt("A comment runs past the end of the comment header.");
        }

        return length;
    }

    private sealed record Mode(bool BlockFlag, int Mapping);

    /// <summary>A mapping (4.2.4.4): channel coupling steps and which submap (floor and residue) each channel uses.</summary>
    private sealed class Mapping
    {
        public required int[] Magnitude { get; init; }

        public required int[] Angle { get; init; }

        public required int[] Mux { get; init; }

        public required int[] SubmapFloor { get; init; }

        public required int[] SubmapResidue { get; init; }

        public static Mapping Read(ref VorbisBitReader r, int channels, int floors, int residues)
        {
            if (r.ReadBits(16) != 0)
            {
                throw Vorbis.Corrupt("A mapping has a type other than 0.");
            }

            int submaps = r.ReadBit() ? (int)r.ReadBits(4) + 1 : 1;
            int steps = r.ReadBit() ? (int)r.ReadBits(8) + 1 : 0;
            int[] magnitude = new int[steps];
            int[] angle = new int[steps];
            int bits = Vorbis.ILog(channels - 1);
            for (int i = 0; i < steps; i++)
            {
                magnitude[i] = (int)r.ReadBits(bits);
                angle[i] = (int)r.ReadBits(bits);
                if (magnitude[i] == angle[i] || magnitude[i] >= channels || angle[i] >= channels)
                {
                    throw Vorbis.Corrupt("A mapping couples a channel with itself or with one that does not exist.");
                }
            }

            if (r.ReadBits(2) != 0)
            {
                throw Vorbis.Corrupt("A mapping's reserved field is not zero.");
            }

            int[] mux = new int[channels];
            if (submaps > 1)
            {
                for (int c = 0; c < channels; c++)
                {
                    mux[c] = (int)r.ReadBits(4);
                    if (mux[c] >= submaps)
                    {
                        throw Vorbis.Corrupt("A mapping puts a channel in a submap that does not exist.");
                    }
                }
            }

            int[] floor = new int[submaps];
            int[] residue = new int[submaps];
            for (int s = 0; s < submaps; s++)
            {
                r.ReadBits(8);
                floor[s] = (int)r.ReadBits(8);
                residue[s] = (int)r.ReadBits(8);
                if (floor[s] >= floors || residue[s] >= residues)
                {
                    throw Vorbis.Corrupt("A mapping names a floor or residue that does not exist.");
                }
            }

            return new Mapping { Magnitude = magnitude, Angle = angle, Mux = mux, SubmapFloor = floor, SubmapResidue = residue };
        }
    }
}
