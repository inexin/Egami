// Translated to C# from oxideav-aac (MIT), modified: the AAC LC decode path of its src/ics_info.rs, section_data.rs,
// scale_factor_data.rs, spectral_data.rs, spectral_codebook.rs, pulse_data.rs, dequant.rs, decoded_spectrum.rs, ms_stereo.rs,
// intensity_stereo.rs, pns.rs, tns_data.rs, tns_coef.rs, tns_frame.rs, filterbank.rs and element_decode.rs, rewritten as one
// allocation-free decoder over float buffers. Checked against ISO/IEC 14496-3 4.4.2, 4.5.2 and 4.6, with libxaac (Apache-2.0)
// as a second reading.

using Egami.Video.Audio.Transforms;

namespace Egami.Video.Audio.Codecs.Aac;

/// <summary>
/// Decodes the raw data blocks of an AAC LC stream (ISO/IEC 14496-3 4.4.2.1) into planar float samples, one block of 1024 per
/// channel at a time: section data, scalefactors, Huffman-coded spectra with pulses, inverse quantization, perceptual noise
/// substitution, mid/side and intensity stereo, temporal noise shaping and the windowed inverse MDCT with block switching. The
/// SBR and parametric stereo data of HE-AAC are skipped; once a stream carries them, the core decodes through an inverse MDCT
/// twice as long, which gives it at twice the rate with nothing above the core's band. Coupling channel elements, AAC Main
/// prediction, LTP and gain control are not decoded.
/// </summary>
internal sealed class AacFrameDecoder
{
    /// <summary>The most channels a channel configuration (7, for 7.1) holds.</summary>
    public const int MaxChannels = 8;

    /// <summary>The samples a block gives per channel at the core rate.</summary>
    public const int CoreFrameLength = 1024;

    private const int MaxBands = 64;
    private const int MaxScale = 8191 + 16;
    private const int EightShort = 2;
    private const int LongStart = 1;
    private const int LongStop = 3;
    private const int NoiseCodebook = 13;
    private const int IntensityCodebook2 = 14;
    private const int IntensityCodebook = 15;
    private const int EscapeCodebook = 11;

    private static readonly AacHuffman[] SpectrumCodebooks = CreateSpectrumCodebooks();
    private static readonly AacHuffman ScaleFactorCodebook = new(AacTables.ScaleFactorCodes, AacTables.ScaleFactorLengths);
    private static readonly sbyte[][] Tuples = CreateTuples();
    private static readonly float[] Pow43 = CreatePow43();

    private readonly int _rateIndex;
    private readonly (int Type, int First, int Second)[] _elements;
    private readonly Channel[] _channels;
    private readonly int[] _quant = new int[CoreFrameLength];
    private readonly bool[] _msUsed = new bool[8 * MaxBands];
    private readonly float[] _coefficients = new float[2 * CoreFrameLength];
    private readonly float[] _block = new float[4 * CoreFrameLength];
    private readonly float[] _shortBlock = new float[512];
    private Filterbank _filterbank;
    private float _scale;
    private int _msMaskPresent;
    private uint _random = 0x12345;

    /// <summary>Initializes a decoder for a stream's rate and channel configuration.</summary>
    /// <param name="rateIndex">The sampling_frequency_index of the core, 0 to 12, which picks the band tables.</param>
    /// <param name="channelConfiguration">The channel configuration, 1 to 7.</param>
    /// <param name="highEfficiency">Whether the stream signals SBR explicitly, so it decodes at twice the core rate from the start.</param>
    public AacFrameDecoder(int rateIndex, int channelConfiguration, bool highEfficiency)
    {
        _rateIndex = rateIndex;
        _elements = channelConfiguration switch
        {
            1 => [(0, 0, 0)],
            2 => [(1, 0, 1)],
            3 => [(0, 2, 0), (1, 0, 1)],
            4 => [(0, 2, 0), (1, 0, 1), (0, 3, 0)],
            5 => [(0, 2, 0), (1, 0, 1), (1, 3, 4)],
            6 => [(0, 2, 0), (1, 0, 1), (1, 4, 5), (3, 3, 0)],
            7 => [(0, 2, 0), (1, 0, 1), (1, 6, 7), (1, 4, 5), (3, 3, 0)],
            _ => throw new ArgumentOutOfRangeException(nameof(channelConfiguration)),
        };

        ChannelCount = channelConfiguration == 7 ? 8 : channelConfiguration < 6 ? channelConfiguration : 6;
        _channels = new Channel[ChannelCount];
        for (int c = 0; c < _channels.Length; c++)
        {
            _channels[c] = new Channel();
        }

        HighEfficiency = highEfficiency;
        _filterbank = Filterbank.For(highEfficiency ? 2 * CoreFrameLength : CoreFrameLength);
        _scale = highEfficiency ? 2f / 32768 : 1f / 32768;
    }

    /// <summary>Gets the channels a block decodes to, in Egami.Video's order for the configuration.</summary>
    public int ChannelCount { get; }

    /// <summary>Gets whether the stream carries SBR, so blocks come out at twice the core rate.</summary>
    public bool HighEfficiency { get; private set; }

    /// <summary>Gets whether the last block filled any band with noise (perceptual noise substitution).</summary>
    public bool UsedNoise { get; private set; }

    /// <summary>Gets whether a block carried SBR or parametric stereo data, which is skipped.</summary>
    public bool SbrSeen { get; private set; }

    /// <summary>Gets the samples per channel a block gives: 1024, or 2048 for HE-AAC.</summary>
    public int FrameLength => _filterbank.Length;

    /// <summary>Clears the overlap and window state, as after a seek or a lost packet.</summary>
    public void Reset()
    {
        foreach (Channel channel in _channels)
        {
            channel.Overlap.AsSpan().Clear();
            channel.PreviousShape = 0;
        }
    }

    /// <summary>Decodes one raw data block into <paramref name="output"/>, <see cref="FrameLength"/> samples per channel.</summary>
    /// <param name="reader">The reader, at the start of the block; left after its byte alignment.</param>
    /// <param name="output">At least <see cref="ChannelCount"/> buffers of 2048 samples.</param>
    /// <param name="error">Why the block cannot be decoded.</param>
    /// <returns>Whether the block was decoded; on false the decoder is reset.</returns>
    public bool TryDecode(ref AacBitReader reader, float[][] output, out string? error)
    {
        try
        {
            DecodeBlock(ref reader, output);
            error = null;
            return true;
        }
        catch (InvalidDataException e)
        {
            Reset();
            error = e.Message;
            return false;
        }
    }

    private static AacHuffman[] CreateSpectrumCodebooks()
    {
        AacHuffman[] books = new AacHuffman[12];
        for (int cb = 1; cb <= 11; cb++)
        {
            ReadOnlySpan<ushort> codes = cb switch
            {
                1 => AacTables.Codes1,
                2 => AacTables.Codes2,
                3 => AacTables.Codes3,
                4 => AacTables.Codes4,
                5 => AacTables.Codes5,
                6 => AacTables.Codes6,
                7 => AacTables.Codes7,
                8 => AacTables.Codes8,
                9 => AacTables.Codes9,
                10 => AacTables.Codes10,
                _ => AacTables.Codes11,
            };
            ReadOnlySpan<byte> lengths = cb switch
            {
                1 => AacTables.Lengths1,
                2 => AacTables.Lengths2,
                3 => AacTables.Lengths3,
                4 => AacTables.Lengths4,
                5 => AacTables.Lengths5,
                6 => AacTables.Lengths6,
                7 => AacTables.Lengths7,
                8 => AacTables.Lengths8,
                9 => AacTables.Lengths9,
                10 => AacTables.Lengths10,
                _ => AacTables.Lengths11,
            };
            uint[] wide = new uint[codes.Length];
            for (int i = 0; i < codes.Length; i++)
            {
                wide[i] = codes[i];
            }

            books[cb] = new AacHuffman(wide, lengths);
        }

        return books;
    }

    /// <summary>The values of each codebook's symbols, four per symbol (two for pairs), before sign bits (4.6.3.3).</summary>
    private static sbyte[][] CreateTuples()
    {
        sbyte[][] tuples = new sbyte[12][];
        for (int cb = 1; cb <= 11; cb++)
        {
            bool quad = cb <= 4;
            bool unsigned = IsUnsigned(cb);
            int lav = cb switch { 1 or 2 => 1, 3 or 4 => 2, 5 or 6 => 4, 7 or 8 => 7, 9 or 10 => 12, _ => 16 };
            int modulus = unsigned ? lav + 1 : (2 * lav) + 1;
            int offset = unsigned ? 0 : lav;
            int count = quad ? modulus * modulus * modulus * modulus : modulus * modulus;
            sbyte[] values = new sbyte[count * 4];
            for (int idx = 0; idx < count; idx++)
            {
                if (quad)
                {
                    values[(idx * 4) + 0] = (sbyte)((idx / (modulus * modulus * modulus)) - offset);
                    values[(idx * 4) + 1] = (sbyte)((idx / (modulus * modulus) % modulus) - offset);
                    values[(idx * 4) + 2] = (sbyte)((idx / modulus % modulus) - offset);
                    values[(idx * 4) + 3] = (sbyte)((idx % modulus) - offset);
                }
                else
                {
                    values[(idx * 4) + 0] = (sbyte)((idx / modulus) - offset);
                    values[(idx * 4) + 1] = (sbyte)((idx % modulus) - offset);
                }
            }

            tuples[cb] = values;
        }

        return tuples;
    }

    /// <summary>|q|^(4/3) for every magnitude a block can hold, escapes plus pulse amplitudes (4.6.1.3).</summary>
    private static float[] CreatePow43()
    {
        float[] table = new float[MaxScale + 1];
        for (int i = 0; i < table.Length; i++)
        {
            table[i] = (float)(i * Math.Cbrt(i));
        }

        return table;
    }

    private static bool IsUnsigned(int cb) => cb is 3 or 4 or 7 or 8 or 9 or 10 or 11;

    private static bool IsIntensity(int cb) => cb is IntensityCodebook or IntensityCodebook2;

    private static InvalidDataException Corrupt(string message) => new(message);

    private ReadOnlySpan<short> Offsets(bool shortWindows) => shortWindows
        ? AacTables.ShortOffsets.Slice(AacTables.ShortStart[_rateIndex], AacTables.ShortBands[_rateIndex] + 1)
        : AacTables.LongOffsets.Slice(AacTables.LongStart[_rateIndex], AacTables.LongBands[_rateIndex] + 1);

    private void DecodeBlock(ref AacBitReader reader, float[][] output)
    {
        int element = 0;
        bool sbr = false;
        UsedNoise = false;
        long start = reader.Position;
        while (true)
        {
            int id = (int)reader.Read(3);
            if (reader.Overrun)
            {
                throw Corrupt("The block ends before its END element.");
            }

            switch (id)
            {
                case 0 or 3:
                    {
                        reader.Read(4);
                        Channel channel = _channels[Expect(element++, id).First];
                        ReadChannelStream(ref reader, channel, null, commonWindow: false);
                        break;
                    }

                case 1:
                    reader.Read(4);
                    (int _, int first, int second) = Expect(element++, 1);
                    ReadChannelPair(ref reader, _channels[first], _channels[second]);
                    break;
                case 2:
                    throw Corrupt("Coupling channel elements are not supported.");
                case 4:
                    SkipDataStream(ref reader, start);
                    break;
                case 5:
                    SkipProgramConfig(ref reader, start);
                    break;
                case 6:
                    sbr |= SkipFill(ref reader);
                    break;
                default:
                    if (element != _elements.Length)
                    {
                        throw Corrupt($"The block holds {element} of the {_elements.Length} channel elements its configuration needs.");
                    }

                    reader.Skip((int)((8 - ((reader.Position - start) & 7)) & 7));
                    if (reader.Overrun)
                    {
                        throw Corrupt("The block is cut off.");
                    }

                    SbrSeen |= sbr;
                    if (sbr && !HighEfficiency && AacConfig.Rates[_rateIndex] <= 24000)
                    {
                        // Implicitly signalled SBR (4.4.2.8 FIL with EXT_SBR_DATA): decode at twice the rate from here on.
                        HighEfficiency = true;
                        _filterbank = Filterbank.For(2 * CoreFrameLength);
                        _scale = 2f / 32768;
                        Reset();
                        RescaleSpectra(2);
                    }

                    for (int c = 0; c < _channels.Length; c++)
                    {
                        Synthesize(_channels[c], output[c]);
                    }

                    return;
            }

            if (reader.Overrun)
            {
                throw Corrupt("The block is cut off.");
            }
        }
    }

    private (int Type, int First, int Second) Expect(int index, int type)
    {
        if (index >= _elements.Length || _elements[index].Type != type)
        {
            throw Corrupt($"Channel element {index} does not match the channel configuration.");
        }

        return _elements[index];
    }

    private void RescaleSpectra(float factor)
    {
        foreach (Channel channel in _channels)
        {
            foreach (ref float v in channel.Spectrum.AsSpan())
            {
                v *= factor;
            }
        }
    }

    private void ReadChannelPair(ref AacBitReader reader, Channel left, Channel right)
    {
        bool common = reader.ReadBit();
        _msMaskPresent = 0;
        if (common)
        {
            ReadIcsInfo(ref reader, left);
            right.CopyInfo(left);
            _msMaskPresent = (int)reader.Read(2);
            if (_msMaskPresent == 3)
            {
                throw Corrupt("ms_mask_present is the reserved value 3.");
            }

            if (_msMaskPresent == 1)
            {
                for (int g = 0; g < left.Groups; g++)
                {
                    for (int sfb = 0; sfb < left.MaxSfb; sfb++)
                    {
                        _msUsed[(g * MaxBands) + sfb] = reader.ReadBit();
                    }
                }
            }
        }

        ReadChannelStream(ref reader, left, null, common);
        ReadChannelStream(ref reader, right, common ? left : null, common);
        if (common)
        {
            ApplyStereo(left, right);
        }
        else if (right.HasIntensity)
        {
            throw Corrupt("Intensity stereo needs a common window.");
        }
    }

    /// <summary>individual_channel_stream (4.4.2.7, Table 4.50): side info, then the spectrum, dequantized into the channel.</summary>
    private void ReadChannelStream(ref AacBitReader reader, Channel channel, Channel? partner, bool commonWindow)
    {
        int globalGain = (int)reader.Read(8);
        if (!commonWindow)
        {
            ReadIcsInfo(ref reader, channel);
        }

        ReadSections(ref reader, channel);
        ReadScaleFactors(ref reader, channel, globalGain);

        // pulse_data (Table 4.7)
        int pulses = 0;
        int pulseStart = 0;
        Span<int> pulseOffset = stackalloc int[4];
        Span<int> pulseAmp = stackalloc int[4];
        if (reader.ReadBit())
        {
            if (channel.WindowSequence == EightShort)
            {
                throw Corrupt("Pulse data in a block of short windows.");
            }

            pulses = (int)reader.Read(2) + 1;
            pulseStart = (int)reader.Read(6);
            for (int i = 0; i < pulses; i++)
            {
                pulseOffset[i] = (int)reader.Read(5);
                pulseAmp[i] = (int)reader.Read(4);
            }
        }

        channel.TnsPresent = reader.ReadBit();
        if (channel.TnsPresent)
        {
            ReadTns(ref reader, channel);
        }

        if (reader.ReadBit())
        {
            throw Corrupt("Gain control data (AAC SSR) is not supported.");
        }

        ReadSpectrum(ref reader, channel);
        if (pulses > 0)
        {
            ReadOnlySpan<short> offsets = Offsets(shortWindows: false);
            if (pulseStart >= offsets.Length - 1)
            {
                throw Corrupt("pulse_start_sfb is past the last band.");
            }

            // 4.6.3.3: each pulse moves its line's magnitude away from zero by its amplitude.
            int k = offsets[pulseStart];
            for (int i = 0; i < pulses; i++)
            {
                k += pulseOffset[i];
                if (k >= CoreFrameLength)
                {
                    throw Corrupt("A pulse falls past the spectrum.");
                }

                _quant[k] += _quant[k] > 0 ? pulseAmp[i] : -pulseAmp[i];
            }
        }

        Dequantize(channel, partner);
    }

    /// <summary>ics_info (Table 4.6) and the window grouping it implies (4.5.2.3.4).</summary>
    private void ReadIcsInfo(ref AacBitReader reader, Channel channel)
    {
        reader.Read(1);
        channel.WindowSequence = (int)reader.Read(2);
        channel.WindowShape = (int)reader.Read(1);
        if (channel.WindowSequence == EightShort)
        {
            channel.MaxSfb = (int)reader.Read(4);
            int grouping = (int)reader.Read(7);
            channel.Groups = 1;
            channel.GroupLength[0] = 1;
            for (int w = 0; w < 7; w++)
            {
                if ((grouping & (0x40 >> w)) != 0)
                {
                    channel.GroupLength[channel.Groups - 1]++;
                }
                else
                {
                    channel.GroupLength[channel.Groups++] = 1;
                }
            }
        }
        else
        {
            channel.MaxSfb = (int)reader.Read(6);
            channel.Groups = 1;
            channel.GroupLength[0] = 1;
            if (reader.ReadBit())
            {
                throw Corrupt("Prediction (AAC Main or LTP) is not supported in AAC LC.");
            }
        }

        if (channel.MaxSfb > Offsets(channel.WindowSequence == EightShort).Length - 1)
        {
            throw Corrupt($"max_sfb {channel.MaxSfb} is past the last band.");
        }
    }

    /// <summary>section_data (Table 4.52): the codebook of every band.</summary>
    private static void ReadSections(ref AacBitReader reader, Channel channel)
    {
        int bits = channel.WindowSequence == EightShort ? 3 : 5;
        int escape = (1 << bits) - 1;
        for (int g = 0; g < channel.Groups; g++)
        {
            int k = 0;
            while (k < channel.MaxSfb)
            {
                int cb = (int)reader.Read(4);
                if (cb == 12)
                {
                    throw Corrupt("Section codebook 12 is reserved.");
                }

                int length = 0;
                int increment;
                do
                {
                    increment = (int)reader.Read(bits);
                    length += increment;
                    if (reader.Overrun)
                    {
                        throw Corrupt("The section data is cut off.");
                    }
                }
                while (increment == escape);

                if (k + length > channel.MaxSfb)
                {
                    throw Corrupt("A section runs past max_sfb.");
                }

                channel.Codebooks.AsSpan((g * MaxBands) + k, length).Fill((byte)cb);
                k += length;
            }
        }
    }

    /// <summary>scale_factor_data (Table 4.53) accumulated into absolute values (4.6.2.3.2): scalefactors, intensity positions and noise energies.</summary>
    private void ReadScaleFactors(ref AacBitReader reader, Channel channel, int globalGain)
    {
        int factor = globalGain;
        int position = 0;
        int noise = globalGain - 90 - 256;
        bool firstNoise = true;
        channel.HasIntensity = false;
        for (int g = 0; g < channel.Groups; g++)
        {
            for (int sfb = 0; sfb < channel.MaxSfb; sfb++)
            {
                int i = (g * MaxBands) + sfb;
                int cb = channel.Codebooks[i];
                if (cb == 0)
                {
                    channel.Factors[i] = 0;
                }
                else if (IsIntensity(cb))
                {
                    position += ReadDelta(ref reader);
                    channel.Factors[i] = position;
                    channel.HasIntensity = true;
                }
                else if (cb == NoiseCodebook)
                {
                    if (firstNoise)
                    {
                        firstNoise = false;
                        noise += (int)reader.Read(9);
                    }
                    else
                    {
                        noise += ReadDelta(ref reader);
                    }

                    channel.Factors[i] = noise;
                    UsedNoise = true;
                }
                else
                {
                    factor += ReadDelta(ref reader);
                    if ((uint)factor > 255)
                    {
                        throw Corrupt($"Scalefactor {factor} is out of range.");
                    }

                    channel.Factors[i] = factor;
                }
            }
        }
    }

    private static int ReadDelta(ref AacBitReader reader)
    {
        int index = ScaleFactorCodebook.Decode(ref reader);
        if (index < 0)
        {
            throw Corrupt("A scalefactor code is invalid.");
        }

        return index - 60;
    }

    /// <summary>tns_data (Table 4.48), kept as each filter's band span, direction and LPC coefficients (4.6.9.3).</summary>
    private static void ReadTns(ref AacBitReader reader, Channel channel)
    {
        bool shortWindows = channel.WindowSequence == EightShort;
        int windows = shortWindows ? 8 : 1;
        int filterBits = shortWindows ? 1 : 2;
        int lengthBits = shortWindows ? 4 : 6;
        int orderBits = shortWindows ? 3 : 5;
        int maxOrder = shortWindows ? 7 : 12;
        Span<float> parcor = stackalloc float[32];
        Span<float> b = stackalloc float[13];
        for (int w = 0; w < windows; w++)
        {
            int filters = (int)reader.Read(filterBits);
            channel.TnsFilters[w] = (byte)filters;
            if (filters == 0)
            {
                continue;
            }

            int resolution = 3 + (int)reader.Read(1);
            for (int f = 0; f < filters; f++)
            {
                int slot = (w * 4) + f;
                channel.TnsLength[slot] = (byte)reader.Read(lengthBits);
                int order = (int)reader.Read(orderBits);
                channel.TnsOrder[slot] = (byte)Math.Min(order, maxOrder);
                if (order == 0)
                {
                    continue;
                }

                channel.TnsDirection[slot] = reader.ReadBit();
                int coefBits = resolution - (int)reader.Read(1);
                double iq = ((1 << (resolution - 1)) - 0.5) / (Math.PI / 2);
                double iqNegative = ((1 << (resolution - 1)) + 0.5) / (Math.PI / 2);
                for (int i = 0; i < order; i++)
                {
                    int c = (int)reader.Read(coefBits);
                    c = (c << (32 - coefBits)) >> (32 - coefBits);
                    parcor[i] = (float)Math.Sin(c / (c >= 0 ? iq : iqNegative));
                }

                // The step-up recursion from reflection to direct-form coefficients.
                Span<float> lpc = channel.TnsLpc.AsSpan(slot * 13, 13);
                lpc.Clear();
                lpc[0] = 1;
                int used = channel.TnsOrder[slot];
                for (int m = 1; m <= used; m++)
                {
                    float k = parcor[m - 1];
                    for (int i = 1; i < m; i++)
                    {
                        b[i] = lpc[i] + (k * lpc[m - i]);
                    }

                    b[1..m].CopyTo(lpc[1..m]);
                    lpc[m] = k;
                }
            }
        }
    }

    /// <summary>spectral_data (Table 4.56): the quantized lines of every band, in the order the groups interleave them.</summary>
    private void ReadSpectrum(ref AacBitReader reader, Channel channel)
    {
        Array.Clear(_quant);
        bool shortWindows = channel.WindowSequence == EightShort;
        ReadOnlySpan<short> offsets = Offsets(shortWindows);
        int groupBase = 0;
        for (int g = 0; g < channel.Groups; g++)
        {
            int length = channel.GroupLength[g];
            for (int sfb = 0; sfb < channel.MaxSfb; sfb++)
            {
                int cb = channel.Codebooks[(g * MaxBands) + sfb];
                if (cb is 0 or NoiseCodebook or IntensityCodebook or IntensityCodebook2)
                {
                    continue;
                }

                AacHuffman book = SpectrumCodebooks[cb];
                sbyte[] tuples = Tuples[cb];
                bool unsigned = IsUnsigned(cb);
                int dimension = cb <= 4 ? 4 : 2;
                int end = groupBase + (offsets[sfb + 1] * length);
                for (int k = groupBase + (offsets[sfb] * length); k < end; k += dimension)
                {
                    int symbol = book.Decode(ref reader);
                    if (symbol < 0)
                    {
                        throw Corrupt($"A codebook {cb} code is invalid.");
                    }

                    for (int j = 0; j < dimension; j++)
                    {
                        int v = tuples[(symbol * 4) + j];
                        if (unsigned && v != 0 && reader.ReadBit())
                        {
                            v = -v;
                        }

                        _quant[k + j] = v;
                    }

                    if (cb == EscapeCodebook)
                    {
                        // 4.6.3.3: escape sequences follow the sign bits, first line first; the sign already read applies.
                        for (int j = 0; j < 2; j++)
                        {
                            int v = _quant[k + j];
                            if (v is 16 or -16)
                            {
                                int prefix = 0;
                                while (reader.ReadBit())
                                {
                                    if (++prefix > 8)
                                    {
                                        throw Corrupt("An escape sequence is longer than 8191 allows.");
                                    }
                                }

                                int magnitude = (1 << (prefix + 4)) + (int)reader.Read(prefix + 4);
                                _quant[k + j] = v < 0 ? -magnitude : magnitude;
                            }
                        }
                    }
                }

                if (reader.Overrun)
                {
                    throw Corrupt("The spectral data is cut off.");
                }
            }

            groupBase += length * (shortWindows ? 128 : CoreFrameLength);
        }
    }

    /// <summary>
    /// Inverse quantization and rescaling (4.6.1.3, 4.6.2.3.3) from the interleaved quantized lines into the channel's spectrum,
    /// window by window (quant_to_spec, 4.6.3.3), with noise filled into noise bands (4.6.13.3). In a channel pair, a noise band
    /// that is noise in the left channel too and marked by the M/S mask takes the left channel's vector at its own energy.
    /// </summary>
    private void Dequantize(Channel channel, Channel? partner)
    {
        Span<float> spectrum = channel.Spectrum;
        spectrum.Clear();
        bool shortWindows = channel.WindowSequence == EightShort;
        int windowLength = shortWindows ? 128 : CoreFrameLength;
        ReadOnlySpan<short> offsets = Offsets(shortWindows);
        int window = 0;
        int groupBase = 0;
        for (int g = 0; g < channel.Groups; g++)
        {
            int length = channel.GroupLength[g];
            for (int sfb = 0; sfb < channel.MaxSfb; sfb++)
            {
                int i = (g * MaxBands) + sfb;
                int cb = channel.Codebooks[i];
                int bandStart = offsets[sfb];
                int width = offsets[sfb + 1] - bandStart;
                if (cb is 0 or IntensityCodebook or IntensityCodebook2)
                {
                    continue;
                }

                if (cb == NoiseCodebook)
                {
                    bool shared = partner is not null && partner.Codebooks[i] == NoiseCodebook && (_msMaskPresent == 2 || (_msMaskPresent == 1 && _msUsed[i]));
                    for (int w = 0; w < length; w++)
                    {
                        Span<float> band = spectrum.Slice(((window + w) * windowLength) + bandStart, width);
                        if (shared)
                        {
                            float ratio = MathF.Pow(2, 0.25f * (channel.Factors[i] - partner!.Factors[i]));
                            ReadOnlySpan<float> source = partner.Spectrum.AsSpan(((window + w) * windowLength) + bandStart, width);
                            for (int n = 0; n < width; n++)
                            {
                                band[n] = source[n] * ratio;
                            }
                        }
                        else
                        {
                            FillNoise(band, channel.Factors[i]);
                        }
                    }

                    continue;
                }

                float gain = MathF.Pow(2, 0.25f * (channel.Factors[i] - 100)) * _scale;
                int source0 = groupBase + (bandStart * length);
                for (int w = 0; w < length; w++)
                {
                    Span<float> band = spectrum.Slice(((window + w) * windowLength) + bandStart, width);
                    ReadOnlySpan<int> quant = _quant.AsSpan(source0 + (w * width), width);
                    for (int n = 0; n < band.Length; n++)
                    {
                        int q = quant[n];
                        if (q != 0)
                        {
                            int magnitude = Math.Min(Math.Abs(q), MaxScale);
                            float v = Pow43[magnitude] * gain;
                            band[n] = q < 0 ? -v : v;
                        }
                    }
                }
            }

            window += length;
            groupBase += length * windowLength;
        }
    }

    /// <summary>A random vector at the band's noise energy, 2^(energy / 4) in all (4.6.13.3).</summary>
    private void FillNoise(Span<float> band, int energy)
    {
        double total = 0;
        for (int n = 0; n < band.Length; n++)
        {
            _random = (_random * 1664525) + 1013904223;
            float v = (int)_random / 2147483648f;
            band[n] = v;
            total += v * v;
        }

        if (total > 0)
        {
            float scale = (float)(Math.Pow(2, 0.25 * energy) / Math.Sqrt(total)) * _scale;
            for (int n = 0; n < band.Length; n++)
            {
                band[n] *= scale;
            }
        }
    }

    /// <summary>M/S (4.6.8.1) and then intensity stereo (4.6.8.2) on a pair that shares its window.</summary>
    private void ApplyStereo(Channel left, Channel right)
    {
        bool shortWindows = left.WindowSequence == EightShort;
        int windowLength = shortWindows ? 128 : CoreFrameLength;
        ReadOnlySpan<short> offsets = Offsets(shortWindows);
        int window = 0;
        for (int g = 0; g < left.Groups; g++)
        {
            int length = left.GroupLength[g];
            for (int sfb = 0; sfb < left.MaxSfb; sfb++)
            {
                int i = (g * MaxBands) + sfb;
                int rightCb = right.Codebooks[i];
                bool used = _msMaskPresent == 2 || (_msMaskPresent == 1 && _msUsed[i]);
                int bandStart = offsets[sfb];
                int width = offsets[sfb + 1] - bandStart;
                if (IsIntensity(rightCb))
                {
                    // Sign +1 for codebook 15 and -1 for 14, flipped where the M/S mask marks the band.
                    float scale = MathF.Pow(0.5f, 0.25f * right.Factors[i]) * (rightCb == IntensityCodebook ? 1 : -1);
                    if (_msMaskPresent == 1 && _msUsed[i])
                    {
                        scale = -scale;
                    }

                    for (int w = 0; w < length; w++)
                    {
                        int at = ((window + w) * windowLength) + bandStart;
                        ReadOnlySpan<float> l = left.Spectrum.AsSpan(at, width);
                        Span<float> r = right.Spectrum.AsSpan(at, width);
                        for (int n = 0; n < width; n++)
                        {
                            r[n] = l[n] * scale;
                        }
                    }
                }
                else if (used && left.Codebooks[i] != NoiseCodebook && rightCb != NoiseCodebook)
                {
                    for (int w = 0; w < length; w++)
                    {
                        int at = ((window + w) * windowLength) + bandStart;
                        Span<float> l = left.Spectrum.AsSpan(at, width);
                        Span<float> r = right.Spectrum.AsSpan(at, width);
                        for (int n = 0; n < width; n++)
                        {
                            float m = l[n];
                            float s = r[n];
                            l[n] = m + s;
                            r[n] = m - s;
                        }
                    }
                }
            }

            window += length;
        }
    }

    /// <summary>TNS synthesis (4.6.9.3) and the filterbank (4.6.11) into one channel's output.</summary>
    private void Synthesize(Channel channel, Span<float> output)
    {
        bool shortWindows = channel.WindowSequence == EightShort;
        if (channel.TnsPresent)
        {
            ApplyTns(channel, shortWindows);
        }

        Filterbank fb = _filterbank;
        int length = fb.Length;
        int shortLength = length / 8;
        int a = (length - shortLength) / 2;
        Span<float> overlap = channel.Overlap.AsSpan(0, length);
        ReadOnlySpan<float> previousShort = fb.ShortRise(channel.PreviousShape);
        ReadOnlySpan<float> currentShort = fb.ShortRise(channel.WindowShape);
        if (!shortWindows)
        {
            Span<float> coefficients = _coefficients.AsSpan(0, length);
            channel.Spectrum.CopyTo(coefficients);
            coefficients[CoreFrameLength..].Clear();
            Span<float> block = _block.AsSpan(0, 2 * length);
            fb.Long.Inverse(coefficients, block);

            // The rising half takes the previous block's window shape (4.6.11.3.2).
            if (channel.WindowSequence == LongStop)
            {
                block[..a].Clear();
                Span<float> part = block.Slice(a, shortLength);
                for (int n = 0; n < shortLength; n++)
                {
                    part[n] *= previousShort[n];
                }
            }
            else
            {
                ReadOnlySpan<float> rise = fb.LongRise(channel.PreviousShape);
                for (int n = 0; n < length; n++)
                {
                    block[n] *= rise[n];
                }
            }

            Span<float> tail = block[length..];
            if (channel.WindowSequence == LongStart)
            {
                Span<float> part = tail.Slice(a, shortLength);
                for (int n = 0; n < shortLength; n++)
                {
                    part[n] *= currentShort[shortLength - 1 - n];
                }

                tail[(a + shortLength)..].Clear();
            }
            else
            {
                ReadOnlySpan<float> fall = fb.LongRise(channel.WindowShape);
                for (int n = 0; n < length; n++)
                {
                    tail[n] *= fall[length - 1 - n];
                }
            }

            for (int n = 0; n < length; n++)
            {
                output[n] = overlap[n] + block[n];
            }

            tail.CopyTo(overlap);
        }
        else
        {
            // Eight short blocks overlap one another from a quarter of the way in (4.6.11.3.3).
            Span<float> z = _block.AsSpan(0, 2 * length);
            z.Clear();
            Span<float> coefficients = _coefficients.AsSpan(0, shortLength);
            Span<float> block = _shortBlock.AsSpan(0, 2 * shortLength);
            for (int w = 0; w < 8; w++)
            {
                channel.Spectrum.AsSpan(w * 128, 128).CopyTo(coefficients);
                coefficients[128..].Clear();
                fb.Short.Inverse(coefficients, block);
                ReadOnlySpan<float> rise = w == 0 ? previousShort : currentShort;
                Span<float> target = z.Slice(a + (w * shortLength), 2 * shortLength);
                for (int n = 0; n < shortLength; n++)
                {
                    target[n] += block[n] * rise[n];
                    target[shortLength + n] += block[shortLength + n] * currentShort[shortLength - 1 - n];
                }
            }

            for (int n = 0; n < length; n++)
            {
                output[n] = overlap[n] + z[n];
            }

            z[length..].CopyTo(overlap);
        }

        channel.PreviousShape = channel.WindowShape;
    }

    /// <summary>The all-pole TNS filter over each filter's span of bands, upward or downward (4.6.9.3).</summary>
    private void ApplyTns(Channel channel, bool shortWindows)
    {
        ReadOnlySpan<short> offsets = Offsets(shortWindows);
        int bands = offsets.Length - 1;
        int maxBands = Math.Min(shortWindows ? AacTables.TnsMaxBandsShort[_rateIndex] : AacTables.TnsMaxBandsLong[_rateIndex], channel.MaxSfb);
        int windows = shortWindows ? 8 : 1;
        int windowLength = shortWindows ? 128 : CoreFrameLength;
        Span<float> history = stackalloc float[12];
        for (int w = 0; w < windows; w++)
        {
            Span<float> spectrum = channel.Spectrum.AsSpan(w * windowLength, windowLength);
            int bottom = bands;
            for (int f = 0; f < channel.TnsFilters[w]; f++)
            {
                int slot = (w * 4) + f;
                int top = bottom;
                bottom = Math.Max(top - channel.TnsLength[slot], 0);
                int order = channel.TnsOrder[slot];
                if (order == 0)
                {
                    continue;
                }

                int start = offsets[Math.Min(bottom, maxBands)];
                int end = offsets[Math.Min(top, maxBands)];
                if (end <= start)
                {
                    continue;
                }

                ReadOnlySpan<float> lpc = channel.TnsLpc.AsSpan(slot * 13, order + 1);
                history[..order].Clear();
                int inc = channel.TnsDirection[slot] ? -1 : 1;
                int at = channel.TnsDirection[slot] ? end - 1 : start;
                for (int n = start; n < end; n++, at += inc)
                {
                    float y = spectrum[at];
                    for (int k = 1; k <= order; k++)
                    {
                        y -= lpc[k] * history[k - 1];
                    }

                    for (int k = order - 1; k > 0; k--)
                    {
                        history[k] = history[k - 1];
                    }

                    history[0] = y;
                    spectrum[at] = y;
                }
            }
        }
    }

    private static void SkipDataStream(ref AacBitReader reader, long start)
    {
        reader.Read(4);
        bool align = reader.ReadBit();
        int count = (int)reader.Read(8);
        if (count == 255)
        {
            count += (int)reader.Read(8);
        }

        if (align)
        {
            reader.Skip((int)((8 - ((reader.Position - start) & 7)) & 7));
        }

        reader.Skip(count * 8);
    }

    /// <summary>Reads past a program_config_element (Table 4.2); its channel map is not used.</summary>
    private static void SkipProgramConfig(ref AacBitReader reader, long start)
    {
        reader.Read(4 + 2 + 4);
        int front = (int)reader.Read(4);
        int side = (int)reader.Read(4);
        int back = (int)reader.Read(4);
        int lfe = (int)reader.Read(2);
        int assoc = (int)reader.Read(3);
        int cc = (int)reader.Read(4);
        for (int i = 0; i < 2; i++)
        {
            if (reader.ReadBit())
            {
                reader.Read(4);
            }
        }

        if (reader.ReadBit())
        {
            reader.Read(3);
        }

        reader.Skip(((front + side + back) * 5) + (lfe * 4) + (assoc * 4) + (cc * 5));
        reader.Skip((int)((8 - ((reader.Position - start) & 7)) & 7));
        reader.Skip((int)reader.Read(8) * 8);
    }

    /// <summary>Reads past a fill element (Table 4.11) and returns whether it holds SBR data (extension type 13 or 14).</summary>
    private static bool SkipFill(ref AacBitReader reader)
    {
        int count = (int)reader.Read(4);
        if (count == 15)
        {
            count += (int)reader.Read(8) - 1;
        }

        if (count == 0)
        {
            return false;
        }

        int type = (int)reader.Read(4);
        reader.Skip((count * 8) - 4);
        return type is 13 or 14;
    }

    /// <summary>One channel's spectrum, side info and filterbank state.</summary>
    private sealed class Channel
    {
        public readonly float[] Spectrum = new float[CoreFrameLength];
        public readonly float[] Overlap = new float[2 * CoreFrameLength];
        public readonly byte[] Codebooks = new byte[8 * MaxBands];
        public readonly int[] Factors = new int[8 * MaxBands];
        public readonly byte[] GroupLength = new byte[8];
        public readonly byte[] TnsFilters = new byte[8];
        public readonly byte[] TnsLength = new byte[32];
        public readonly byte[] TnsOrder = new byte[32];
        public readonly bool[] TnsDirection = new bool[32];
        public readonly float[] TnsLpc = new float[32 * 13];
        public int WindowSequence;
        public int WindowShape;
        public int PreviousShape;
        public int MaxSfb;
        public int Groups;
        public bool TnsPresent;
        public bool HasIntensity;

        public void CopyInfo(Channel other)
        {
            WindowSequence = other.WindowSequence;
            WindowShape = other.WindowShape;
            MaxSfb = other.MaxSfb;
            Groups = other.Groups;
            other.GroupLength.CopyTo(GroupLength, 0);
        }
    }

    /// <summary>The transforms (with their scratch, so one per decoder) and window halves for one output frame length, 1024 or 2048.</summary>
    private sealed class Filterbank
    {
        private readonly float[][] _longRise;
        private readonly float[][] _shortRise;

        private Filterbank(int length)
        {
            Length = length;
            Long = new Mdct(2 * length);
            Short = new Mdct(length / 4);
            _longRise = [MdctWindows.Sine(2 * length), MdctWindows.KaiserBesselDerived(2 * length, 4)];
            _shortRise = [MdctWindows.Sine(length / 4), MdctWindows.KaiserBesselDerived(length / 4, 6)];
        }

        public int Length { get; }

        public Mdct Long { get; }

        public Mdct Short { get; }

        public static Filterbank For(int length) => new(length);

        public ReadOnlySpan<float> LongRise(int shape) => _longRise[shape];

        public ReadOnlySpan<float> ShortRise(int shape) => _shortRise[shape];
    }
}
