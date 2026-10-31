namespace Egami.Formats.Jxl;

/// <summary>
/// Patches for lossless frames (libjxl's FindBestPatchDictionary at effort 5 and up, outside its streaming mode): text-like
/// shapes found on the color, packed into a reference-only Modular frame coded losslessly, and added back where each occurs
/// (kAdd), so the frame codes only what differs from them. Samples stay exact: the frame's color is the image minus the
/// patches, in integers.
/// </summary>
internal static class JxlLosslessPatches
{
    private const int Reference = 3;

    /// <summary>Gets whether a lossless frame searches for patches: effort 5 and up, integer RGB of 16 bits or fewer, and a frame libjxl does not stream.</summary>
    internal static bool Apply(JxlEncodeInput input, int effort, int width, int height)
    {
        int groups = ((width + 255) / 256) * ((height + 255) / 256);
        return effort >= 5 && input.Color.Count == 3 && !input.BitDepth.IsFloat && input.BitDepth.BitsPerSample <= 16
            && (groups <= 8 || effort >= 10);
    }

    /// <summary>
    /// Finds patches on the color <paramref name="channels"/> (the first three, <paramref name="width"/> by <paramref name="height"/>)
    /// and takes them out; gives the reference frame's bytes and the dictionary, or <see langword="null"/> when there are none.
    /// </summary>
    internal static (byte[] Frame, JxlBitWriter Dictionary)? Find(List<JxlChannel> channels, JxlEncodeInput input, int effort, int width, int height, int[] extraShifts)
    {
        int bits = input.BitDepth.BitsPerSample;
        float max = (1 << bits) - 1;
        float[][] planes = new float[3][];
        for (int c = 0; c < 3; c++)
        {
            int[] data = channels[c].Data;
            planes[c] = new float[width * height];
            for (int i = 0; i < planes[c].Length; i++)
            {
                planes[c][i] = data[i] / max;
            }
        }

        ParallelOptions parallel = new() { MaxDegreeOfParallelism = Math.Max(1, input.Parallelism) };
        // Text-like patches grow from flat 4×4 blocks. A photo has few (under 10 % on the rate corpus, where none gave a patch)
        // and a screen many (over half), so a frame with fewer than one in eight skips the search, which would cost a photo
        // up to 7 % more time for nothing.
        List<JxlPatchCandidate> info = JxlPatchSearch.FindTextLike(planes, width, width, height, parallel, isXyb: false, minSeedFraction: 1f / 8);
        if (info.Count == 0)
        {
            return null;
        }

        info = [.. info.OrderByDescending(p => p.Width * p.Height)];
        (float[][] reference, int rw, int rh, (int X, int Y)[] places) = JxlPatchSearch.Pack(info);
        int[][] samples = new int[3][];
        for (int c = 0; c < 3; c++)
        {
            samples[c] = [.. reference[c].Select(v => (int)MathF.Round(v * max))];
        }

        // Each occurrence leaves the image less the reference, so the decoder's addition gives every sample back.
        JxlPatchBlending[] add = [new JxlPatchBlending(JxlPatchBlendMode.Add, 0, false), .. Enumerable.Repeat(new JxlPatchBlending(JxlPatchBlendMode.None, 0, false), extraShifts.Length)];
        JxlPatch[] patches = [.. info.Select((p, i) => new JxlPatch(Reference, places[i].X, places[i].Y, p.Width, p.Height, [.. p.Positions.Select(t => new JxlPatchTarget(t.X, t.Y, add))]))];
        foreach (JxlPatch p in patches)
        {
            foreach (JxlPatchTarget t in p.Targets)
            {
                for (int c = 0; c < 3; c++)
                {
                    int[] data = channels[c].Data;
                    for (int y = 0; y < p.Height; y++)
                    {
                        for (int x = 0; x < p.Width; x++)
                        {
                            data[((t.Y + y) * width) + t.X + x] -= samples[c][((p.Y0 + y) * rw) + p.X0 + x];
                        }
                    }
                }
            }
        }

        JxlBitWriter dictionary = new();
        JxlPatchSearch.WriteDictionary(dictionary, patches);
        return (ReferenceFrame(samples, rw, rh, bits, effort, input, extraShifts), dictionary);
    }

    /// <summary>Codes the reference-only frame that holds the patches, losslessly, saved before the color transform in slot 3.</summary>
    private static byte[] ReferenceFrame(int[][] samples, int rw, int rh, int bits, int effort, JxlEncodeInput input, int[] extraShifts)
    {
        int groupShift = JxlModularEncoder.GroupShift(rw, rh);
        List<JxlChannel> channels = [.. samples.Select(s => new JxlChannel(rw, rh, 0, 0) { Data = s })];

        // The extra channels are in every frame: zero here, as libjxl's placeholders, subsampled as the image header says.
        foreach (int s in extraShifts)
        {
            channels.Add(new JxlChannel((rw + (1 << s) - 1) >> s, (rh + (1 << s) - 1) >> s, s, s));
        }

        List<byte[]> sections = JxlModularEncoder.Encode(channels, 3, bits, effort, rw, rh, groupShift, input.Parallelism);
        JxlU32 factor = new(1, 0, 2, 0, 4, 0, 8, 0);
        JxlBitWriter w = new();
        w.WriteBool(false);
        w.Write(2, 2); // reference only
        w.Write(1, 1); // Modular
        w.WriteU64(0);
        w.WriteBool(false); // not YCbCr
        w.WriteU32(factor, 1); // upsampling
        foreach (int _ in extraShifts)
        {
            w.WriteU32(factor, 1);
        }

        w.Write((ulong)groupShift, 2);
        w.WriteBool(true); // the frame's own size
        JxlU32 dim = new(0, 8, 256, 11, 2304, 14, 18688, 30);
        w.WriteU32(dim, (uint)rw);
        w.WriteU32(dim, (uint)rh);
        w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 0), Reference);
        w.WriteBool(true); // before the color transform
        w.WriteU32(new JxlU32(0, 0, 0, 4, 16, 5, 48, 10), 0); // no name
        w.WriteBool(false); // loop filter: Gaborish off, no EPF
        w.WriteBool(false);
        w.Write(0, 2);
        w.WriteNoExtensions();
        w.WriteNoExtensions();
        w.WriteBool(false); // no TOC permutation
        w.ZeroPadToByte();
        JxlU32 size = new(0, 10, 1024, 14, 17408, 22, 4211712, 30);
        foreach (byte[] s in sections)
        {
            w.WriteU32(size, (uint)s.Length);
        }

        w.ZeroPadToByte();
        foreach (byte[] s in sections)
        {
            w.Append(s);
        }

        return w.ToArray();
    }
}
