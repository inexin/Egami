namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// FFV1's Parameters (RFC 9043 section 4.2): the version, coder, color space, depth, subsampling, slice grid, quantization tables
/// and initial states. Version 3 keeps them in the configuration record (section 4.3); versions 0 and 1 in every keyframe.
/// </summary>
internal sealed class Ffv1Parameters
{
    /// <summary>Contexts per range-coded scalar (CONTEXT_SIZE).</summary>
    public const int ContextSize = 32;

    /// <summary>Quantized differences that make up a context (MAX_CONTEXT_INPUTS).</summary>
    public const int ContextInputs = 5;

    private const int MaxQuantTableSets = 8;
    private const int MaxContexts = 32768;

    public int Version { get; private set; }

    public int MicroVersion { get; private set; }

    /// <summary>Gets the coder: 0 Golomb-Rice, 1 range coder with the default table, 2 with a custom table.</summary>
    public int CoderType { get; private set; }

    /// <summary>Gets the state transition table slices are range coded with.</summary>
    public Ffv1StateTable StateTable { get; private set; } = Ffv1StateTable.Default;

    /// <summary>Gets the color space: 0 YCbCr, 1 RGB through the reversible color transform.</summary>
    public int ColorSpace { get; private set; }

    public int BitsPerRawSample { get; private set; } = 8;

    public bool ChromaPlanes { get; private set; }

    public int Log2HChromaSubsample { get; private set; }

    public int Log2VChromaSubsample { get; private set; }

    public bool ExtraPlane { get; private set; }

    public int HorizontalSlices { get; private set; } = 1;

    public int VerticalSlices { get; private set; } = 1;

    /// <summary>Gets the quantization tables, by set, then context input, then the low 8 bits of a difference.</summary>
    public short[][][] QuantTables { get; private set; } = [];

    /// <summary>Gets the contexts of each quantization table set.</summary>
    public int[] ContextCounts { get; private set; } = [];

    /// <summary>Gets each set's initial range coder states (contexts times 32), or null where they are all 128.</summary>
    public byte[]?[] InitialStates { get; private set; } = [];

    /// <summary>Gets whether slices end in a CRC (ec).</summary>
    public bool SliceCrc { get; private set; }

    /// <summary>Gets whether every frame is a keyframe (intra).</summary>
    public bool IntraOnly { get; private set; }

    /// <summary>Gets the size of a slice footer in bytes.</summary>
    public int FooterSize => 3 + (SliceCrc ? 5 : 0);

    /// <summary>Gets the quantization table set indexes a version 3 slice header holds.</summary>
    public int QuantIndexCount => 1 + (ChromaPlanes || Version <= 3 ? 1 : 0) + (ExtraPlane ? 1 : 0);

    /// <summary>Reads a version 3 configuration record, checking its CRC.</summary>
    /// <param name="record">The record, as the container stores it.</param>
    /// <param name="crcOk">Whether the record's CRC parity matched.</param>
    /// <returns>The parameters.</returns>
    public static Ffv1Parameters ReadConfigurationRecord(ReadOnlySpan<byte> record, out bool crcOk)
    {
        if (record.Length < 5)
        {
            throw new CorruptDataException("FFV1", -1, $"The FFV1 configuration record has {record.Length} bytes, too few to hold one.");
        }

        crcOk = Ffv1Crc.Compute(record) == 0;
        byte[] data = record.ToArray();
        var decoder = new Ffv1RangeDecoder(data, 0, data.Length - 4, Ffv1StateTable.Default);
        var parameters = new Ffv1Parameters();
        parameters.Read(ref decoder);
        if (parameters.Version < 3)
        {
            throw new CorruptDataException("FFV1", -1, $"An FFV1 configuration record says version {parameters.Version}; only version 3 streams have one.");
        }

        return parameters;
    }

    /// <summary>Reads the parameters of a version 0 or 1 keyframe from the frame's range decoder.</summary>
    public static Ffv1Parameters ReadFrameHeader(ref Ffv1RangeDecoder decoder)
    {
        var parameters = new Ffv1Parameters();
        parameters.Read(ref decoder);
        if (parameters.Version > 1)
        {
            throw new CorruptDataException("FFV1", -1, $"An FFV1 frame header says version {parameters.Version}; versions 3 and later keep their parameters in the configuration record.");
        }

        return parameters;
    }

    /// <summary>Builds the parameters a version 3 encoder writes.</summary>
    public static Ffv1Parameters ForEncoding(
        int coderType, int colorSpace, int bits, bool chromaPlanes, int log2H, int log2V, bool extraPlane, int horizontalSlices,
        int verticalSlices, short[][][] quantTables, byte[]?[] initialStates, bool sliceCrc, bool intraOnly)
    {
        var p = new Ffv1Parameters
        {
            Version = 3,
            MicroVersion = 4,
            CoderType = coderType,
            StateTable = coderType == 2 ? new Ffv1StateTable(Ffv1StateTable.AlternativeOne()) : Ffv1StateTable.Default,
            ColorSpace = colorSpace,
            BitsPerRawSample = bits,
            ChromaPlanes = chromaPlanes,
            Log2HChromaSubsample = log2H,
            Log2VChromaSubsample = log2V,
            ExtraPlane = extraPlane,
            HorizontalSlices = horizontalSlices,
            VerticalSlices = verticalSlices,
            QuantTables = quantTables,
            ContextCounts = [.. quantTables.Select(ContextCount)],
            InitialStates = initialStates,
            SliceCrc = sliceCrc,
            IntraOnly = intraOnly,
        };
        return p;
    }

    /// <summary>Writes these parameters as a version 3 configuration record, its CRC parity last (RFC 9043 section 4.3).</summary>
    public byte[] WriteConfigurationRecord()
    {
        var rc = new Ffv1RangeEncoder();
        rc.Reset();
        Span<byte> state = stackalloc byte[ContextSize];
        state.Fill(128);
        rc.WriteSymbol(state, Version, false);
        rc.WriteSymbol(state, MicroVersion, false);
        rc.WriteSymbol(state, CoderType, false);
        if (CoderType == 2)
        {
            byte[] baseline = Ffv1StateTable.DefaultOne();
            for (int i = 1; i < 256; i++)
            {
                rc.WriteSymbol(state, StateTable.One[i] - baseline[i], true);
            }
        }

        rc.WriteSymbol(state, ColorSpace, false);
        rc.WriteSymbol(state, BitsPerRawSample, false);
        rc.WriteFlag(state, ChromaPlanes);
        rc.WriteSymbol(state, Log2HChromaSubsample, false);
        rc.WriteSymbol(state, Log2VChromaSubsample, false);
        rc.WriteFlag(state, ExtraPlane);
        rc.WriteSymbol(state, HorizontalSlices - 1, false);
        rc.WriteSymbol(state, VerticalSlices - 1, false);
        rc.WriteSymbol(state, QuantTables.Length, false);
        Span<byte> tableState = stackalloc byte[ContextSize];
        foreach (short[][] set in QuantTables)
        {
            foreach (short[] table in set)
            {
                // Runs of equal values over the first half; the second half mirrors it.
                tableState.Fill(128);
                for (int k = 0; k < 128;)
                {
                    int run = 1;
                    while (k + run < 128 && table[k + run] == table[k])
                    {
                        run++;
                    }

                    rc.WriteSymbol(tableState, run - 1, false);
                    k += run;
                }
            }
        }

        byte[] deltaStates = new byte[ContextSize * ContextSize];
        deltaStates.AsSpan().Fill(128);
        for (int i = 0; i < QuantTables.Length; i++)
        {
            byte[]? initial = InitialStates[i];
            rc.WriteFlag(state, initial is not null);
            if (initial is null)
            {
                continue;
            }

            for (int j = 0; j < ContextCounts[i]; j++)
            {
                for (int k = 0; k < ContextSize; k++)
                {
                    int pred = j > 0 ? initial[((j - 1) * ContextSize) + k] : 128;
                    rc.WriteSymbol(deltaStates.AsSpan(k * ContextSize, ContextSize), (sbyte)(byte)(initial[(j * ContextSize) + k] - pred), true);
                }
            }
        }

        rc.WriteSymbol(state, SliceCrc ? 1 : 0, false);
        rc.WriteSymbol(state, IntraOnly ? 1 : 0, false);
        rc.Finish();
        uint crc = Ffv1Crc.Compute(rc.Written);
        Span<byte> parity = [(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc];
        rc.Append(parity);
        return rc.Written.ToArray();
    }

    /// <summary>Returns the contexts a quantization table set gives: half the product of its tables' value counts, rounded up.</summary>
    public static int ContextCount(short[][] set)
    {
        long scale = 1;
        foreach (short[] table in set)
        {
            int values = 1;
            for (int k = 1; k < 128; k++)
            {
                values += table[k] != table[k - 1] ? 1 : 0;
            }

            scale *= (2 * values) - 1;
        }

        return (int)((scale + 1) / 2);
    }

    private void Read(ref Ffv1RangeDecoder rc)
    {
        Span<byte> state = stackalloc byte[ContextSize];
        state.Fill(128);
        Version = rc.ReadSymbol(state, false);
        if (Version == 2)
        {
            throw new UnsupportedFormatException("FFV1 version 2 was experimental and is not decoded.");
        }

        if (Version > 3)
        {
            throw new UnsupportedFormatException($"FFV1 version {Version} is not supported; versions 0, 1 and 3 are.");
        }

        if (Version >= 3)
        {
            MicroVersion = rc.ReadSymbol(state, false);
        }

        CoderType = rc.ReadField(state, 2, "coder_type");
        if (CoderType == 2)
        {
            byte[] one = Ffv1StateTable.DefaultOne();
            for (int i = 1; i < 256; i++)
            {
                int value = one[i] + rc.ReadSymbol(state, true);
                if ((uint)value > 255)
                {
                    throw new CorruptDataException("FFV1", -1, $"The FFV1 custom state transition table has {value} at {i}.");
                }

                one[i] = (byte)value;
            }

            StateTable = new Ffv1StateTable(one);
        }

        ColorSpace = rc.ReadField(state, 1, "colorspace_type");
        if (Version >= 1)
        {
            BitsPerRawSample = rc.ReadField(state, 16, "bits_per_raw_sample");
            if (BitsPerRawSample == 0)
            {
                BitsPerRawSample = 8;
            }
        }

        ChromaPlanes = rc.ReadFlag(state);
        Log2HChromaSubsample = rc.ReadField(state, 4, "log2_h_chroma_subsample");
        Log2VChromaSubsample = rc.ReadField(state, 4, "log2_v_chroma_subsample");
        ExtraPlane = rc.ReadFlag(state);
        int sets = 1;
        if (Version >= 3)
        {
            HorizontalSlices = rc.ReadField(state, 1023, "num_h_slices - 1") + 1;
            VerticalSlices = rc.ReadField(state, 1023, "num_v_slices - 1") + 1;
            sets = rc.ReadField(state, MaxQuantTableSets, "quant_table_set_count");
            if (sets == 0)
            {
                throw new CorruptDataException("FFV1", -1, "FFV1 quant_table_set_count is 0.");
            }
        }

        if (ColorSpace == 1 && (!ChromaPlanes || Log2HChromaSubsample != 0 || Log2VChromaSubsample != 0))
        {
            throw new UnsupportedFormatException("FFV1 RGB with subsampled or missing chroma planes is outside RFC 9043.");
        }

        if (BitsPerRawSample > 8 && CoderType == 0)
        {
            throw new UnsupportedFormatException($"FFV1 Golomb-Rice coding at {BitsPerRawSample} bits is not supported; only the range coder codes samples above 8 bits.");
        }

        QuantTables = new short[sets][][];
        ContextCounts = new int[sets];
        InitialStates = new byte[sets][];
        for (int i = 0; i < sets; i++)
        {
            QuantTables[i] = ReadQuantTableSet(ref rc, out ContextCounts[i]);
        }

        if (Version >= 3)
        {
            byte[] deltaStates = new byte[ContextSize * ContextSize];
            deltaStates.AsSpan().Fill(128);
            for (int i = 0; i < sets; i++)
            {
                if (!rc.ReadFlag(state))
                {
                    continue;
                }

                int contexts = ContextCounts[i];
                byte[] initial = new byte[contexts * ContextSize];
                for (int j = 0; j < contexts; j++)
                {
                    for (int k = 0; k < ContextSize; k++)
                    {
                        int pred = j > 0 ? initial[((j - 1) * ContextSize) + k] : 128;
                        initial[(j * ContextSize) + k] = (byte)(pred + rc.ReadSymbol(deltaStates.AsSpan(k * ContextSize, ContextSize), true));
                    }
                }

                InitialStates[i] = initial;
            }

            SliceCrc = rc.ReadField(state, 1, "ec") == 1;
            IntraOnly = rc.ReadField(state, 1, "intra") == 1;
        }
    }

    private static short[][] ReadQuantTableSet(ref Ffv1RangeDecoder rc, out int contexts)
    {
        short[][] tables = new short[ContextInputs][];
        long scale = 1;
        Span<byte> state = stackalloc byte[ContextSize];
        for (int j = 0; j < ContextInputs; j++)
        {
            state.Fill(128);
            short[] table = new short[256];
            int v = 0;
            for (int k = 0; k < 128; v++)
            {
                int length = rc.ReadField(state, 128 - k - 1, "quantization run length - 1") + 1;
                for (int n = 0; n < length; n++)
                {
                    table[k++] = (short)(scale * v);
                }
            }

            for (int k = 1; k < 128; k++)
            {
                table[256 - k] = (short)-table[k];
            }

            table[128] = (short)-table[127];
            tables[j] = table;
            scale *= (2 * v) - 1;
            if (scale > 2 * MaxContexts)
            {
                throw new CorruptDataException("FFV1", -1, $"FFV1 quantization tables give more than {MaxContexts} contexts.");
            }
        }

        contexts = (int)((scale + 1) / 2);
        return tables;
    }
}
