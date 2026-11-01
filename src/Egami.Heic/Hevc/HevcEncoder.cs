using Egami.Nal;

namespace Egami.Heic.Hevc;

/// <summary>
/// The intra-only HEVC encoder of Egami.Heic: one picture into one IDR slice, its CTB rows wavefront substreams. The CTBs are
/// decided and reconstructed first (by <see cref="HevcCtuSearch"/>, rows in parallel as the wavefronts allow), then the picture
/// is deblocked, its SAO parameters chosen and applied, and last the CABAC pass writes everything. The layout and every decision
/// depend only on the picture and the settings, so the output is the same on any number of threads.
/// </summary>
internal static class HevcEncoder
{
    /// <summary>Encodes a picture.</summary>
    /// <param name="settings">The picture's size and format and the coding settings.</param>
    /// <param name="planes">The planes, Y then Cb and Cr (Y alone for 4:0:0), of the picture's size.</param>
    /// <param name="strides">Each plane's row length in samples.</param>
    /// <param name="cancellationToken">Cancels encoding.</param>
    internal static HevcEncodedPicture Encode(HevcEncoderSettings settings, ushort[][] planes, int[] strides, CancellationToken cancellationToken = default)
    {
        Check(settings);
        HevcEncoderProfile profile = HevcEncoderProfile.For(settings.Effort);
        settings = settings with { MaxTransformDepth = profile.TransformDepth, Sao = settings.Sao && profile.Sao && !settings.Lossless };
        HevcSource source = new(settings, planes, strides);
        byte[] vps = HevcParameterWriter.Vps(settings), sps = HevcParameterWriter.Sps(settings), pps = HevcParameterWriter.Pps(settings);
        bool sao = settings.Sao;

        // The decoder's own reading of the parameter sets and slice header, so the reconstruction is made exactly as it decodes.
        HevcParameterSetStore store = new();
        store.Parse(HevcNal.Parse(vps), out _);
        store.Parse(HevcNal.Parse(sps), out _);
        store.Parse(HevcNal.Parse(pps), out _);
        HevcSliceHeader slice = store.Parse(HevcNal.Parse(HevcParameterWriter.SliceNal(settings, sao, [[0x80]])), out _)!;
        (HevcSps hevcSps, HevcPps hevcPps) = store.Active(0);
        HevcPicture picture = new(hevcSps, hevcPps);
        int ctbs = hevcSps.WidthInCtbs * hevcSps.HeightInCtbs;
        for (int i = 0; i < ctbs; i++)
        {
            picture.CtbSlice[i] = 0;
            picture.CtbSliceHeader[i] = slice;
        }

        int[] qgQp = HevcAdaptiveQp.GroupQps(settings, source, hevcPps.CuQpDeltaEnabled ? hevcSps.CtbLog2Size - hevcPps.DiffCuQpDeltaDepth : hevcSps.CtbLog2Size);
        bool wavefronts = HevcParameterWriter.Wavefronts(settings);
        List<HevcCodingUnit>[] ctus = Decide(settings, profile, picture, slice, source, qgQp, wavefronts, cancellationToken);

        // The loop filters: deblocking, then SAO chosen on the deblocked samples.
        HevcLoopFilter.Deblock(picture, settings.Parallelism, cancellationToken);
        HevcSaoChoice[] saoChoices = [];
        if (sao)
        {
            saoChoices = HevcSaoSearch.Choose(picture, source, slice, settings.Parallelism, cancellationToken);
            HevcLoopFilter.Sao(picture, settings.Parallelism, cancellationToken);
        }

        byte[] sliceNal = Write(settings, picture, slice, ctus, saoChoices, wavefronts);
        return new HevcEncodedPicture
        {
            Settings = settings,
            Vps = vps,
            Sps = sps,
            Pps = pps,
            Slice = sliceNal,
            Reconstruction = picture,
        };
    }

    private static void Check(HevcEncoderSettings s)
    {
        if (s.Width <= 0 || s.Height <= 0 || s.ChromaFormat is < 0 or > 3 || s.BitDepth is not (8 or 10 or 12))
        {
            throw new ArgumentException("The HEVC encoder's settings describe no picture it codes.");
        }

        if (s.Width % s.SubWidthC != 0 || s.Height % s.SubHeightC != 0)
        {
            throw new ArgumentException("A subsampled picture's size must be a multiple of its chroma subsampling.");
        }
    }

    /// <summary>Decides and reconstructs every CTB, the CTB rows on up to <see cref="HevcEncoderSettings.Parallelism"/> threads.</summary>
    private static List<HevcCodingUnit>[] Decide(HevcEncoderSettings settings, HevcEncoderProfile profile, HevcPicture picture, HevcSliceHeader slice,
        HevcSource source, int[] qgQp, bool wavefronts, CancellationToken cancellationToken)
    {
        HevcSps sps = picture.Sps;
        int rows = sps.HeightInCtbs, columns = sps.WidthInCtbs;
        List<HevcCodingUnit>[] ctus = new List<HevcCodingUnit>[rows * columns];
        int[] progress = new int[rows];
        byte[]?[] synced = new byte[]?[rows];
        int next = -1;
        Exception? failure = null;
        int threads = wavefronts ? Math.Clamp(settings.Parallelism, 1, rows) : 1;

        void Work()
        {
            HevcCtuSearch search = new(settings, profile, picture, slice, source, qgQp);
            try
            {
                int row;
                while ((row = Interlocked.Increment(ref next)) < rows)
                {
                    if (wavefronts || row == 0)
                    {
                        // The row above must be two CTBs ahead: its contexts after the second, its samples above-right.
                        if (row > 0 && !WaitFor(progress, row - 1, Math.Min(2, columns), ref failure, cancellationToken))
                        {
                            return;
                        }

                        search.StartRow(row > 0 && columns > 1 ? synced[row - 1] : null);
                    }

                    for (int column = 0; column < columns; column++)
                    {
                        if (row > 0 && !WaitFor(progress, row - 1, Math.Min(column + 2, columns), ref failure, cancellationToken))
                        {
                            return;
                        }

                        ctus[(row * columns) + column] = search.CodeCtu((row * columns) + column);
                        if (column == 1)
                        {
                            synced[row] = (byte[])search.Contexts.Clone();
                        }

                        Volatile.Write(ref progress[row], column + 1);
                    }
                }
            }
            catch (Exception e)
            {
                Interlocked.CompareExchange(ref failure, e, null);
            }
        }

        if (threads == 1)
        {
            Work();
        }
        else
        {
            Task[] tasks = new Task[threads - 1];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(Work, CancellationToken.None);
            }

            Work();
            Task.WaitAll(tasks, CancellationToken.None);
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        cancellationToken.ThrowIfCancellationRequested();
        return ctus;
    }

    private static bool WaitFor(int[] progress, int row, int count, ref Exception? failure, CancellationToken cancellationToken)
    {
        SpinWait spin = default;
        while (Volatile.Read(ref progress[row]) < count)
        {
            if (Volatile.Read(ref failure) is not null)
            {
                return false;
            }

            if (cancellationToken.IsCancellationRequested)
            {
                Interlocked.CompareExchange(ref failure, new OperationCanceledException(cancellationToken), null);
                return false;
            }

            spin.SpinOnce();
        }

        return true;
    }

    /// <summary>The CABAC pass: every CTB's SAO and coding tree, one substream per CTB row with wavefronts, into the slice's NAL unit.</summary>
    private static byte[] Write(HevcEncoderSettings settings, HevcPicture picture, HevcSliceHeader slice, List<HevcCodingUnit>[] ctus, HevcSaoChoice[] sao,
        bool wavefronts)
    {
        HevcSps sps = picture.Sps;
        int rows = sps.HeightInCtbs, columns = sps.WidthInCtbs, total = rows * columns;
        HevcBins bins = new();
        CabacEncoder encoder = new();
        bins.Target(encoder);
        HevcSyntaxWriter writer = new(picture, slice, bins);
        List<byte[]> substreams = [];
        byte[]? synced = null;
        encoder.Start();
        HevcContexts.Initialize(bins.Contexts, slice.SliceQp);
        for (int row = 0; row < rows; row++)
        {
            if (wavefronts && row > 0)
            {
                encoder.Start();
                if (columns > 1)
                {
                    Array.Copy(synced!, bins.Contexts, HevcContexts.Count);
                }
                else
                {
                    HevcContexts.Initialize(bins.Contexts, slice.SliceQp);
                }
            }

            for (int column = 0; column < columns; column++)
            {
                int ctb = (row * columns) + column;
                if (slice.SaoLuma || slice.SaoChroma)
                {
                    HevcSaoChoice choice = sao[ctb];
                    writer.Sao(ctb, picture.Sao[ctb], choice.MergeLeft, choice.MergeUp);
                }

                writer.CodingTree(ctus[ctb], column << sps.CtbLog2Size, row << sps.CtbLog2Size);
                if (column == 1)
                {
                    synced = (byte[])bins.Contexts.Clone();
                }

                bool last = ctb == total - 1;
                encoder.Terminate(last ? 1 : 0);
                if (!last && wavefronts && column == columns - 1)
                {
                    encoder.Terminate(1);
                    substreams.Add(encoder.Finish().ToArray());
                }
            }
        }

        substreams.Add(encoder.Finish().ToArray());
        return HevcParameterWriter.SliceNal(settings, slice.SaoLuma || slice.SaoChroma, substreams);
    }
}

/// <summary>An encoded picture: its parameter sets and slice as NAL units, and the reconstruction the decoder will make of it.</summary>
internal sealed class HevcEncodedPicture
{
    internal required HevcEncoderSettings Settings { get; init; }

    internal required byte[] Vps { get; init; }

    internal required byte[] Sps { get; init; }

    internal required byte[] Pps { get; init; }

    internal required byte[] Slice { get; init; }

    internal required HevcPicture Reconstruction { get; init; }

    /// <summary>Gets the hvcC record of the picture's parameter sets.</summary>
    internal byte[] ConfigurationRecord() => HevcParameterWriter.ConfigurationRecord(Settings, Vps, Sps, Pps);

    /// <summary>Gets the item data: the slice after its 4-byte size.</summary>
    internal byte[] ItemData() => HevcParameterWriter.LengthPrefixed(Slice);
}
