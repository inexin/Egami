using System.Buffers.Binary;
using System.IO.Compression;

using Egami.Colors;

namespace Egami.Formats.Tiff;

/// <summary>
/// An <see cref="InkRaster"/> as one separated TIFF (TIFF 6.0 section 16): PhotometricInterpretation 5 with one sample per ink,
/// InkSet 1 for plain CMYK and 2 with InkNames otherwise, NumberOfInks, the planes stored one after another (PlanarConfiguration
/// 2) as the raster holds them, the alpha plane (when asked for) as an associated alpha extra sample, and the output profile as
/// the ICC tag.
/// Reading takes any separated TIFF of 8 or 16-bit strips, chunky or planar, uncompressed, LZW, Deflate or PackBits.
/// </summary>
internal static class InkTiff
{
    private const int Software = 305;
    private const int StripTarget = 256 * 1024;

    public static void Write(InkRaster raster, Stream stream, InkTiffOptions options)
    {
        int planes = raster.Plates.Count + (options.IncludeAlpha ? 1 : 0);
        int bytes = raster.Depth == InkDepth.Unorm8 ? 1 : 2;
        int rowBytes = raster.Width * bytes;
        long total = (long)rowBytes * raster.Height * planes;
        int rowsPerStrip = Math.Clamp(StripTarget / rowBytes, 1, raster.Height);
        int stripsPerPlane = (raster.Height + rowsPerStrip - 1) / rowsPerStrip;
        bool big = total > 3L * 1024 * 1024 * 1024;
        using TiffOutput file = new(stream, bigEndian: false, big);
        ulong[] offsets = new ulong[stripsPerPlane * planes];
        ulong[] counts = new ulong[offsets.Length];
        byte[] strip = new byte[rowBytes * rowsPerStrip];
        byte[] packed = new byte[Math.Max(TiffLzwEncoder.MaxEncodedLength(strip.Length), PackBits.MaxEncodedLength(strip.Length)) + 64];
        bool predict = options.Compression is TiffCompression.Lzw or TiffCompression.Deflate;
        for (int p = 0; p < planes; p++)
        {
            InkPlate plate = p < raster.Plates.Count ? raster.Plates[p] : raster.Alpha;
            for (int s = 0; s < stripsPerPlane; s++)
            {
                int first = s * rowsPerStrip, rows = Math.Min(rowsPerStrip, raster.Height - first);
                Span<byte> data = strip.AsSpan(0, rows * rowBytes);
                for (int r = 0; r < rows; r++)
                {
                    Span<byte> row = data.Slice(r * rowBytes, rowBytes);
                    if (bytes == 1)
                    {
                        plate.Row8(first + r).CopyTo(row);
                        if (predict)
                        {
                            for (int x = row.Length - 1; x > 0; x--)
                            {
                                row[x] -= row[x - 1];
                            }
                        }
                    }
                    else
                    {
                        ReadOnlySpan<ushort> values = plate.Row16(first + r);
                        for (int x = values.Length - 1; x >= 0; x--)
                        {
                            ushort v = predict && x > 0 ? (ushort)(values[x] - values[x - 1]) : values[x];
                            BinaryPrimitives.WriteUInt16LittleEndian(row[(x * 2)..], v);
                        }
                    }
                }

                ReadOnlySpan<byte> written = Compress(options.Compression, data, packed);
                file.Align();
                offsets[(p * stripsPerPlane) + s] = (ulong)file.Position;
                counts[(p * stripsPerPlane) + s] = (ulong)written.Length;
                file.Write(written);
            }
        }

        InkSet inks = raster.Inks;
        bool cmyk = raster.Plates.Count == 4 && raster.Plates.All(p => p.Colorant!.IsCmyk) && raster.Plates[0].Name == "Cyan"
            && raster.Plates[1].Name == "Magenta" && raster.Plates[2].Name == "Yellow" && raster.Plates[3].Name == "Black";
        int longType = big ? 16 : 4;
        TiffIfdWriter ifd = new(file);
        ifd.Add(TiffTags.ImageWidth, 4, (ulong)raster.Width);
        ifd.Add(TiffTags.ImageLength, 4, (ulong)raster.Height);
        ifd.Add(TiffTags.BitsPerSample, 3, [.. Enumerable.Repeat((ulong)(bytes * 8), planes)]);
        ifd.Add(TiffTags.Compression, 3, (ulong)options.Compression);
        ifd.Add(TiffTags.Photometric, 3, 5);
        ifd.Add(TiffTags.StripOffsets, longType, offsets);
        ifd.Add(TiffTags.SamplesPerPixel, 3, (ulong)planes);
        ifd.Add(TiffTags.RowsPerStrip, 4, (ulong)rowsPerStrip);
        ifd.Add(TiffTags.StripByteCounts, longType, counts);
        ifd.Add(TiffTags.PlanarConfiguration, 3, 2);
        ifd.AddRaw(Software, 2, 6, "Egami\0"u8.ToArray());
        if (predict)
        {
            ifd.Add(TiffTags.Predictor, 3, 2);
        }

        ifd.Add(TiffTags.InkSet, 3, cmyk ? 1UL : 2UL);
        if (!cmyk)
        {
            byte[] names = System.Text.Encoding.Latin1.GetBytes(string.Concat(raster.Plates.Select(p => p.Name + "\0")));
            ifd.AddRaw(TiffTags.InkNames, 2, names.Length, names);
        }

        // libtiff holds NumberOfInks to SamplesPerPixel, so with alpha it is left out: readers then count the inks as the samples
        // that are not extra.
        if (options.IncludeAlpha)
        {
            ifd.Add(TiffTags.ExtraSamples, 3, 1);
        }
        else
        {
            ifd.Add(TiffTags.NumberOfInks, 3, (ulong)raster.Plates.Count);
        }

        byte[] icc = inks.Profile.ToIcc();
        ifd.AddRaw(TiffTags.IccProfile, 7, icc.Length, icc);
        ifd.Write();
        file.Finish();
    }

    private static ReadOnlySpan<byte> Compress(TiffCompression compression, ReadOnlySpan<byte> data, byte[] packed)
    {
        switch (compression)
        {
            case TiffCompression.None:
                return data;
            case TiffCompression.Lzw:
                return packed.AsSpan(0, TiffLzwEncoder.Encode(data, packed));
            case TiffCompression.PackBits:
                return packed.AsSpan(0, PackBits.Encode(data, packed));
            default:
                using (MemoryStream memory = new())
                {
                    using (ZLibStream zlib = new(memory, CompressionLevel.Optimal, leaveOpen: true))
                    {
                        zlib.Write(data);
                    }

                    return memory.ToArray();
                }
        }
    }

    public static InkRaster Read(Stream stream, ColorProfile? profile, EgamiContext context)
    {
        EgamiInput input = new(stream, context, default);
        try
        {
            input.SetFormat(TiffFormat.Info);
            using TiffSource source = TiffSource.Open(input);
            TiffFile file = TiffFile.Open(source, input);
            TiffIfd ifd = file.ReadIfd(file.FirstIfd);
            return Read(file, ifd, profile, context, input);
        }
        finally
        {
            input.Dispose();
        }
    }

    private static InkRaster Read(TiffFile file, TiffIfd ifd, ColorProfile? profile, EgamiContext context, EgamiInput input)
    {
        int width = (int)Math.Min(file.Integer(ifd, TiffTags.ImageWidth, 0), int.MaxValue);
        int height = (int)Math.Min(file.Integer(ifd, TiffTags.ImageLength, 0), int.MaxValue);
        int samples = (int)Math.Min(file.Integer(ifd, TiffTags.SamplesPerPixel, 1), 1024);
        if (file.Integer(ifd, TiffTags.Photometric, 0) != 5)
        {
            throw new UnsupportedFormatException("TIFF: an ink raster loads from a separated TIFF (PhotometricInterpretation 5) only.");
        }

        if (width < 1 || height < 1)
        {
            throw input.Corrupt(ifd.Offset, $"The image size {width}×{height} is invalid.");
        }

        ulong[] bits = ifd.TryGet(TiffTags.BitsPerSample, out TiffEntry bitsEntry) ? file.Integers(bitsEntry, 1024) : [1];
        int depth = (int)bits[0];
        if (depth is not (8 or 16) || bits.Any(b => b != bits[0]) || file.Integer(ifd, TiffTags.SampleFormat, 1) != 1)
        {
            throw new UnsupportedFormatException($"TIFF: ink rasters are 8 or 16-bit unsigned samples; this file has {string.Join(", ", bits.Distinct())} bits.");
        }

        if (ifd.Has(TiffTags.TileWidth))
        {
            throw new UnsupportedFormatException("TIFF: tiled separated files are not read as ink rasters; only strips.");
        }

        int compression = (int)file.Integer(ifd, TiffTags.Compression, 1);
        if (compression is not (1 or 5 or 8 or 32946 or 32773))
        {
            throw new UnsupportedFormatException($"TIFF: compression {compression} is not read for ink rasters (none, LZW, Deflate and PackBits are).");
        }

        int predictor = (int)file.Integer(ifd, TiffTags.Predictor, 1);
        if (predictor is not (1 or 2))
        {
            throw new UnsupportedFormatException($"TIFF: predictor {predictor} is not read for ink rasters.");
        }

        bool planar = file.Integer(ifd, TiffTags.PlanarConfiguration, 1) == 2 && samples > 1;
        ulong[] extra = ifd.TryGet(TiffTags.ExtraSamples, out TiffEntry extraEntry) ? file.Integers(extraEntry, 1024) : [];
        int inkCount = Math.Clamp((int)file.Integer(ifd, TiffTags.NumberOfInks, (ulong)(samples - extra.Length)), 1, samples);
        int alphaIndex = extra.Length > 0 && extra[0] is 1 or 2 && inkCount < samples ? inkCount : -1;
        bool premultiplied = alphaIndex >= 0 && extra[0] == 1;

        // The inks: InkSet 1 is CMYK; 2 names them. A file without names for every ink gets Ink 1, Ink 2 and so on.
        string[] names;
        if (file.Integer(ifd, TiffTags.InkSet, 1) == 1 && inkCount == 4)
        {
            names = ["Cyan", "Magenta", "Yellow", "Black"];
        }
        else
        {
            string[] given = ifd.TryGet(TiffTags.InkNames, out TiffEntry namesEntry) && namesEntry.ValueAt >= 0 && namesEntry.Count <= 64 * 1024
                ? System.Text.Encoding.Latin1.GetString(file.Bytes(namesEntry)).Split('\0', StringSplitOptions.RemoveEmptyEntries)
                : [];
            names = given.Length >= inkCount && given.Take(inkCount).Distinct(StringComparer.Ordinal).Count() == inkCount
                ? given[..inkCount]
                : [.. Enumerable.Range(1, inkCount).Select(i => $"Ink {i}")];
        }

        // The profile: the file's own when it has inks, else the one given, else FOGRA39. Inks it lacks become spots.
        if (ifd.TryGet(TiffTags.IccProfile, out TiffEntry iccEntry) && iccEntry.ValueAt >= 0)
        {
            try
            {
                ColorProfile embedded = ColorProfile.FromIcc(file.Bytes(iccEntry));
                if (embedded.Model is ColorModel.Cmyk or ColorModel.NColor)
                {
                    profile = embedded;
                }
            }
            catch (EgamiException)
            {
                // A damaged profile is ignored, as for any TIFF.
            }
        }

        InkSet inks = InkSet.FromProfile(profile ?? ColorProfile.Fogra39);
        foreach (string name in names)
        {
            if (!inks.Contains(name))
            {
                inks = name is "All" or "None" || Colorant.IsCmykName(name) || name.Length > Colorant.MaxNameLength
                    ? throw input.Corrupt(ifd.Offset, $"The ink name '{name}' cannot name a spot.")
                    : inks.WithSpot(name, null);
            }
        }

        InkRaster raster = new(inks, width, height, depth == 8 ? InkDepth.Unorm8 : InkDepth.Unorm16, context);
        try
        {
            InkPlate?[] targets = new InkPlate?[samples];
            for (int i = 0; i < inkCount; i++)
            {
                targets[i] = raster.Plate(names[i]);
            }

            if (alphaIndex >= 0)
            {
                targets[alphaIndex] = raster.Alpha;
            }

            ReadStrips(file, ifd, input, raster, targets, samples, planar, compression, predictor, depth);
            Finish(raster, alphaIndex >= 0, premultiplied);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    private static void ReadStrips(TiffFile file, TiffIfd ifd, EgamiInput input, InkRaster raster, InkPlate?[] targets, int samples, bool planar, int compression, int predictor, int depth)
    {
        int width = raster.Width, height = raster.Height, bytes = depth / 8;
        int rowsPerStrip = (int)Math.Min(Math.Max(file.Integer(ifd, TiffTags.RowsPerStrip, (ulong)height), 1UL), (ulong)height);
        int stripsPerPlane = (height + rowsPerStrip - 1) / rowsPerStrip;
        int planes = planar ? samples : 1;
        int unitSamples = planar ? 1 : samples;
        if (!ifd.TryGet(TiffTags.StripOffsets, out TiffEntry offsetsEntry) || !ifd.TryGet(TiffTags.StripByteCounts, out TiffEntry countsEntry))
        {
            throw input.Corrupt(ifd.Offset, "The page has no strip offsets or byte counts.");
        }

        ulong[] offsets = file.Integers(offsetsEntry, (long)stripsPerPlane * planes);
        ulong[] counts = file.Integers(countsEntry, (long)stripsPerPlane * planes);
        if (offsets.Length < stripsPerPlane * planes || counts.Length < offsets.Length)
        {
            throw input.Corrupt(ifd.Offset, $"The page needs {stripsPerPlane * planes} strips but lists {Math.Min(offsets.Length, counts.Length)}.");
        }

        long rowBytes = (long)width * unitSamples * bytes;
        byte[] decoded = new byte[checked(rowBytes * rowsPerStrip)];
        for (int p = 0; p < planes; p++)
        {
            for (int s = 0; s < stripsPerPlane; s++)
            {
                int strip = (p * stripsPerPlane) + s;
                int first = s * rowsPerStrip, rows = Math.Min(rowsPerStrip, height - first);
                if (offsets[strip] > (ulong)file.Source.Length || counts[strip] > (ulong)(file.Source.Length - (long)offsets[strip]) || counts[strip] > int.MaxValue)
                {
                    throw file.OutOfFile(ifd.Offset, $"Strip {strip} lies outside the file.");
                }

                byte[] packed = new byte[(int)counts[strip]];
                int got = file.Source.Read((long)offsets[strip], packed);
                Span<byte> target = decoded.AsSpan(0, (int)(rowBytes * rows));
                target.Clear();
                Decode(compression, packed.AsSpan(0, got), target);
                for (int r = 0; r < rows; r++)
                {
                    Span<byte> row = target.Slice((int)(r * rowBytes), (int)rowBytes);
                    if (bytes == 2 && file.BigEndian)
                    {
                        TiffFile.Swap(row, 2);
                    }

                    if (predictor == 2)
                    {
                        TiffDecoder.UndoHorizontal(row, unitSamples, depth);
                    }

                    for (int c = 0; c < unitSamples; c++)
                    {
                        InkPlate? plate = targets[planar ? p : c];
                        if (plate is null)
                        {
                            continue;
                        }

                        if (bytes == 1)
                        {
                            Span<byte> to = plate.Row8(first + r);
                            for (int x = 0; x < width; x++)
                            {
                                to[x] = row[(x * unitSamples) + c];
                            }
                        }
                        else
                        {
                            Span<ushort> to = plate.Row16(first + r);
                            ReadOnlySpan<ushort> from = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(row);
                            for (int x = 0; x < width; x++)
                            {
                                to[x] = from[(x * unitSamples) + c];
                            }
                        }
                    }
                }
            }
        }
    }

    private static void Decode(int compression, ReadOnlySpan<byte> packed, Span<byte> target)
    {
        switch (compression)
        {
            case 1:
                packed[..Math.Min(packed.Length, target.Length)].CopyTo(target);
                break;
            case 5:
                TiffLzw.Decode(packed, target);
                break;
            case 32773:
                PackBits.Decode(packed, target, out _);
                break;
            default:
                TiffDecoder.Inflate(packed, target);
                break;
        }
    }

    // Without alpha the page is covered; unassociated alpha is multiplied into the planes, which hold ink as printed.
    private static void Finish(InkRaster raster, bool hasAlpha, bool premultiplied)
    {
        for (int y = 0; y < raster.Height; y++)
        {
            if (raster.Depth == InkDepth.Unorm8)
            {
                Span<byte> alpha = raster.Alpha.Row8(y);
                if (!hasAlpha)
                {
                    alpha.Fill(255);
                    continue;
                }

                if (!premultiplied)
                {
                    foreach (InkPlate plate in raster.Plates)
                    {
                        Span<byte> row = plate.Row8(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            row[x] = (byte)(((row[x] * alpha[x]) + 127) / 255);
                        }
                    }
                }
            }
            else
            {
                Span<ushort> alpha = raster.Alpha.Row16(y);
                if (!hasAlpha)
                {
                    alpha.Fill(65535);
                    continue;
                }

                if (!premultiplied)
                {
                    foreach (InkPlate plate in raster.Plates)
                    {
                        Span<ushort> row = plate.Row16(y);
                        for (int x = 0; x < row.Length; x++)
                        {
                            row[x] = (ushort)((((uint)row[x] * alpha[x]) + 32767) / 65535);
                        }
                    }
                }
            }
        }
    }
}
