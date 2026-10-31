namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Reads a DNG file's structure into a <see cref="RawFrame"/>, from the Adobe DNG Specification 1.7.1: the IFD tree, the raw
/// IFD (NewSubFileType 0 with CFA or LinearRaw data), the camera tags of IFD0, the raw tags of the raw IFD, and the previews.
/// </summary>
internal static class DngParser
{
    private const int MaxIfds = 64;

    /// <summary>The photometric interpretations of raw data: a colour filter array, or LinearRaw.</summary>
    public const int PhotometricCfa = 32803;
    public const int PhotometricLinearRaw = 34892;

    /// <summary>Reads the file; throws <see cref="InvalidDataException"/> when it has no raw IFD.</summary>
    public static RawFrame Parse(RawTiff tiff, uint firstIfd)
    {
        List<RawIfd> all = [];
        foreach (RawIfd ifd in tiff.Chain(firstIfd, MaxIfds))
        {
            Collect(ifd, all, 0);
        }

        if (all.Count == 0)
        {
            throw new InvalidDataException("The DNG has no IFD.");
        }

        RawIfd ifd0 = all[0];
        RawIfd? raw = all.FirstOrDefault(i => (i.Int(RawTags.NewSubFileType) & 1) == 0
            && i.Int(RawTags.Photometric) is PhotometricCfa or PhotometricLinearRaw)
            ?? throw new InvalidDataException("The DNG has no raw IFD (NewSubFileType 0 with CFA or LinearRaw data).");
        RawFrame frame = new() { Sensor = Sensor(raw) };
        Camera(frame, ifd0, raw);
        RawData(frame, raw, ifd0);
        foreach (RawIfd ifd in all)
        {
            if (ifd != raw && Preview(ifd) is RawPreviewSource preview)
            {
                frame.Previews.Add(preview);
            }
        }

        return frame;
    }

    private static void Collect(RawIfd ifd, List<RawIfd> all, int depth)
    {
        if (all.Count >= MaxIfds || all.Any(i => i.Offset == ifd.Offset))
        {
            return;
        }

        all.Add(ifd);
        if (depth < 3 && ifd.Ints(RawTags.SubIfds, MaxIfds) is long[] subs)
        {
            foreach (long at in subs)
            {
                if (ifd.File.ReadIfd(at) is RawIfd sub)
                {
                    Collect(sub, all, depth + 1);
                }
            }
        }
    }

    /// <summary>Reads how an IFD stores its image data.</summary>
    public static RawSensor Sensor(RawIfd ifd)
    {
        int width = (int)Math.Clamp(ifd.Int(RawTags.ImageWidth), 0, int.MaxValue);
        int height = (int)Math.Clamp(ifd.Int(RawTags.ImageLength), 0, int.MaxValue);
        bool tiled = ifd.Has(RawTags.TileOffsets);
        long[] offsets = ifd.Ints(tiled ? RawTags.TileOffsets : RawTags.StripOffsets) ?? [];
        long[] counts = ifd.Ints(tiled ? RawTags.TileByteCounts : RawTags.StripByteCounts) ?? [];
        int unitWidth = tiled ? (int)Math.Clamp(ifd.Int(RawTags.TileWidth), 0, 1 << 20) : width;
        int unitHeight = tiled
            ? (int)Math.Clamp(ifd.Int(RawTags.TileLength), 0, 1 << 20)
            : (int)Math.Clamp(ifd.Int(RawTags.RowsPerStrip, height), 1, Math.Max(height, 1));
        return new RawSensor
        {
            Ifd = ifd,
            Width = width,
            Height = height,
            Samples = (int)Math.Clamp(ifd.Int(RawTags.SamplesPerPixel, 1), 1, 16),
            Bits = (int)Math.Clamp(ifd.Int(RawTags.BitsPerSample, 1), 1, 64),
            Compression = (int)ifd.Int(RawTags.Compression, 1),
            Photometric = (int)ifd.Int(RawTags.Photometric),
            Float = ifd.Int(RawTags.SampleFormat, 1) == 3,
            Predictor = (int)ifd.Int(RawTags.Predictor, 1),
            Tiled = tiled,
            UnitWidth = Math.Max(unitWidth, 1),
            UnitHeight = Math.Max(unitHeight, 1),
            Offsets = offsets,
            Counts = counts,
            BigEndian = ifd.File.BigEndian,
        };
    }

    /// <summary>Reads the camera-wide tags, which DNG keeps in IFD0 (the raw IFD may repeat some).</summary>
    private static void Camera(RawFrame frame, RawIfd ifd0, RawIfd raw)
    {
        frame.MetadataIfd = ifd0;
        frame.Make = ifd0.Ascii(RawTags.Make) ?? "";
        frame.Model = ifd0.Ascii(RawTags.Model) ?? "";
        frame.UniqueCameraModel = ifd0.Ascii(RawTags.UniqueCameraModel);
        frame.DngVersion = ifd0.Bytes(RawTags.DngVersion) is { Length: 4 } version ? version.ToArray() : null;
        frame.Orientation = (int)ifd0.Int(RawTags.Orientation, 1);
        RawIfd Pick(int tag) => raw.Has(tag) ? raw : ifd0;
        frame.AsShotNeutral = Pick(RawTags.AsShotNeutral).Doubles(RawTags.AsShotNeutral, 4);
        if (Pick(RawTags.AsShotWhiteXY).Doubles(RawTags.AsShotWhiteXY, 2) is { Length: 2 } white)
        {
            frame.AsShotWhite = (white[0], white[1]);
        }

        frame.AnalogBalance = Pick(RawTags.AnalogBalance).Doubles(RawTags.AnalogBalance, 4);
        frame.BaselineExposure = Pick(RawTags.BaselineExposure).Double(RawTags.BaselineExposure);
        RawProfile profile = frame.Profile;
        profile.ColorMatrix1 = Pick(RawTags.ColorMatrix1).Doubles(RawTags.ColorMatrix1, 12);
        profile.ColorMatrix2 = Pick(RawTags.ColorMatrix2).Doubles(RawTags.ColorMatrix2, 12);
        profile.ForwardMatrix1 = Pick(RawTags.ForwardMatrix1).Doubles(RawTags.ForwardMatrix1, 12);
        profile.ForwardMatrix2 = Pick(RawTags.ForwardMatrix2).Doubles(RawTags.ForwardMatrix2, 12);
        profile.CameraCalibration1 = Pick(RawTags.CameraCalibration1).Doubles(RawTags.CameraCalibration1, 16);
        profile.CameraCalibration2 = Pick(RawTags.CameraCalibration2).Doubles(RawTags.CameraCalibration2, 16);
        profile.Illuminant1 = (int)Pick(RawTags.CalibrationIlluminant1).Int(RawTags.CalibrationIlluminant1);
        profile.Illuminant2 = (int)Pick(RawTags.CalibrationIlluminant2).Int(RawTags.CalibrationIlluminant2);
        profile.Name = ifd0.Ascii(RawTags.ProfileName);
        if (profile.ColorMatrix1 is null && RawCameraTable.Find(frame.Make, frame.Model)?.Profile is RawProfile known)
        {
            // A DNG without colour data (some converters write none): the camera table's matrices.
            profile.ColorMatrix1 = known.ColorMatrix1;
            profile.ColorMatrix2 = known.ColorMatrix2;
            profile.ForwardMatrix1 ??= known.ForwardMatrix1;
            profile.ForwardMatrix2 ??= known.ForwardMatrix2;
            profile.Illuminant1 = known.Illuminant1;
            profile.Illuminant2 = known.Illuminant2;
        }
        profile.BaselineExposureOffset = ifd0.Double(RawTags.BaselineExposureOffset);
        if (ifd0.Ints(RawTags.ProfileHueSatMapDims, 3) is { Length: 3 } hsDims && Dims(hsDims) is int[] hs)
        {
            int count = hs[0] * hs[1] * hs[2] * 3;
            profile.HueSat1 = Floats(ifd0, RawTags.ProfileHueSatMapData1, count);
            profile.HueSat2 = Floats(ifd0, RawTags.ProfileHueSatMapData2, count);
            profile.HueSatDims = profile.HueSat1 is null ? null : hs;
            profile.HueSatSrgb = ifd0.Int(RawTags.ProfileHueSatMapEncoding) == 1;
        }

        if (ifd0.Ints(RawTags.ProfileLookTableDims, 3) is { Length: 3 } lookDims && Dims(lookDims) is int[] look)
        {
            profile.Look = Floats(ifd0, RawTags.ProfileLookTableData, look[0] * look[1] * look[2] * 3);
            profile.LookDims = profile.Look is null ? null : look;
            profile.LookSrgb = ifd0.Int(RawTags.ProfileLookTableEncoding) == 1;
        }

        if (ifd0.Doubles(RawTags.ProfileToneCurve, 1 << 14) is { Length: >= 4 } curve && curve.Length % 2 == 0)
        {
            profile.ToneCurve = [.. curve.Select(v => (float)v)];
        }

        RawIfd? exif = ifd0.Sub(RawTags.ExifIfd);
        double? Exif(int tag) => exif?.Has(tag) == true ? exif.Double(tag) : ifd0.Has(tag) ? ifd0.Double(tag) : null;
        frame.Iso = Exif(RawTags.IsoSpeed);
        frame.ExposureTime = Exif(RawTags.ExposureTime);
        frame.FNumber = Exif(RawTags.FNumber);
        frame.FocalLength = Exif(RawTags.FocalLength);
        frame.LensModel = exif?.Ascii(RawTags.LensModel) ?? ifd0.Ascii(RawTags.LensModel);
        frame.MakerNote = MakerNote(ifd0, exif);
    }

    /// <summary>Returns the maker note: EXIF's MakerNote, or the one DNGPrivateData keeps as <c>Adobe\0MakN</c>.</summary>
    private static byte[]? MakerNote(RawIfd ifd0, RawIfd? exif)
    {
        if (exif is not null && exif.Bytes(RawTags.MakerNote) is { Length: > 0 } note)
        {
            return note.ToArray();
        }

        ReadOnlySpan<byte> priv = ifd0.Bytes(RawTags.DngPrivateData);
        if (priv.Length > 20 && priv.StartsWith("Adobe\0MakN"u8))
        {
            // "MakN", a big-endian count, the original byte order mark and offset, then the note.
            int count = (priv[10] << 24) | (priv[11] << 16) | (priv[12] << 8) | priv[13];
            if (count > 6 && count <= priv.Length - 14)
            {
                return priv.Slice(20, count - 6).ToArray();
            }
        }

        return null;
    }

    /// <summary>Reads the tags that describe the raw data, from the raw IFD and, for what it lacks, IFD0.</summary>
    private static void RawData(RawFrame frame, RawIfd raw, RawIfd ifd0)
    {
        RawSensor sensor = frame.Sensor;
        int samples = sensor.Samples;
        if (sensor.Photometric == PhotometricCfa)
        {
            long[]? dims = raw.Ints(RawTags.CfaRepeatPatternDim, 2);
            ReadOnlySpan<byte> pattern = raw.Bytes(RawTags.CfaPattern);
            if (dims is not { Length: 2 } || dims[0] is < 1 or > 16 || dims[1] is < 1 or > 16 || pattern.Length < dims[0] * dims[1])
            {
                throw new InvalidDataException("The DNG's CFA pattern is missing or does not match its repeat size.");
            }

            byte[] planeColors = raw.Bytes(RawTags.CfaPlaneColor).ToArray() is { Length: >= 3 } pc ? pc : [0, 1, 2];
            byte[] colors = new byte[dims[0] * dims[1]];
            for (int i = 0; i < colors.Length; i++)
            {
                // CFAPattern names colours; CFAPlaneColor says which colour each plane is, so map the colour to its plane.
                int plane = Array.IndexOf(planeColors, pattern[i]);
                if (plane < 0)
                {
                    throw new InvalidDataException($"The DNG's CFA pattern uses colour {pattern[i]}, which CFAPlaneColor does not list.");
                }

                colors[i] = (byte)plane;
            }

            frame.Planes = planeColors.Length;
            frame.Cfa = new RawCfa((int)dims[0], (int)dims[1], colors);
            if (raw.Int(RawTags.CfaLayout, 1) != 1)
            {
                throw new NotSupportedException("Egami reads DNG colour filter arrays on a rectangular grid only (CFALayout 1).");
            }
        }
        else
        {
            frame.Planes = samples;
        }

        if (raw.Ints(RawTags.LinearizationTable, 65536) is { Length: > 0 } table)
        {
            frame.Linearization = [.. table.Select(v => (ushort)Math.Clamp(v, 0, 65535))];
        }

        long[]? repeat = raw.Ints(RawTags.BlackLevelRepeatDim, 2);
        int blackRows = repeat is { Length: 2 } ? (int)Math.Clamp(repeat[0], 1, 64) : 1;
        int blackColumns = repeat is { Length: 2 } ? (int)Math.Clamp(repeat[1], 1, 64) : 1;
        double[] black = new double[blackRows * blackColumns * samples];
        if (raw.Doubles(RawTags.BlackLevel, black.Length * 4) is { Length: > 0 } levels)
        {
            for (int i = 0; i < black.Length; i++)
            {
                black[i] = levels.Length == black.Length ? levels[i] : levels[i % levels.Length];
            }
        }

        frame.BlackRows = blackRows;
        frame.BlackColumns = blackColumns;
        frame.Black = black;
        double defaultWhite = sensor.Float ? 1.0 : Math.Pow(2, Math.Min(sensor.Bits, 16)) - 1;
        double[] white = new double[samples];
        double[]? whites = raw.Doubles(RawTags.WhiteLevel, 16);
        for (int s = 0; s < samples; s++)
        {
            white[s] = whites is { Length: > 0 } ? whites[Math.Min(s, whites.Length - 1)] : defaultWhite;
        }

        frame.White = white;
        Rect active = new(0, 0, sensor.Width, sensor.Height);
        if (raw.Ints(RawTags.ActiveArea, 4) is { Length: 4 } area)
        {
            int top = (int)area[0], left = (int)area[1], bottom = (int)area[2], right = (int)area[3];
            if (top >= 0 && left >= 0 && bottom > top && right > left && bottom <= sensor.Height && right <= sensor.Width)
            {
                active = new Rect(left, top, right - left, bottom - top);
            }
            else
            {
                frame.Repairs.Add(("ActiveAreaIgnored", "The DNG's ActiveArea lies outside the image; the whole image is used."));
            }
        }

        frame.Active = active;

        // The specification counts the deltas over the active area; a file that counts them over the whole image is read too.
        frame.BlackDeltaH = raw.Doubles(RawTags.BlackLevelDeltaH, sensor.Width) is { } dh && (dh.Length == active.Width || dh.Length == sensor.Width) ? dh : null;
        frame.BlackDeltaV = raw.Doubles(RawTags.BlackLevelDeltaV, sensor.Height) is { } dv && (dv.Length == active.Height || dv.Length == sensor.Height) ? dv : null;
        (double X, double Y, double Width, double Height) crop = (0, 0, active.Width, active.Height);
        if (raw.Doubles(RawTags.DefaultCropOrigin, 2) is { Length: 2 } origin && raw.Doubles(RawTags.DefaultCropSize, 2) is { Length: 2 } size)
        {
            if (origin[0] >= 0 && origin[1] >= 0 && size[0] >= 1 && size[1] >= 1 && origin[0] + size[0] <= active.Width + 0.5 && origin[1] + size[1] <= active.Height + 0.5)
            {
                crop = (origin[0], origin[1], Math.Min(size[0], active.Width - origin[0]), Math.Min(size[1], active.Height - origin[1]));
            }
            else
            {
                frame.Repairs.Add(("CropIgnored", "The DNG's default crop lies outside the active area; the whole active area is used."));
            }
        }

        frame.Crop = crop;
        frame.Opcodes1 = Opcodes(raw, ifd0, RawTags.OpcodeList1);
        frame.Opcodes2 = Opcodes(raw, ifd0, RawTags.OpcodeList2);
        frame.Opcodes3 = Opcodes(raw, ifd0, RawTags.OpcodeList3);
        frame.NoiseProfile = (raw.Has(RawTags.NoiseProfile) ? raw : ifd0).Doubles(RawTags.NoiseProfile, 32);
    }

    private static byte[]? Opcodes(RawIfd raw, RawIfd ifd0, int tag)
    {
        ReadOnlySpan<byte> bytes = raw.Bytes(tag);
        return bytes.Length >= 4 ? bytes.ToArray() : null;
    }

    /// <summary>Returns the preview an IFD holds: a baseline JPEG or 8-bit RGB, or <see langword="null"/>.</summary>
    private static RawPreviewSource? Preview(RawIfd ifd)
    {
        int photometric = (int)ifd.Int(RawTags.Photometric, -1);
        int compression = (int)ifd.Int(RawTags.Compression, 1);
        int width = (int)ifd.Int(RawTags.ImageWidth);
        int height = (int)ifd.Int(RawTags.ImageLength);
        if (width <= 0 || height <= 0 || photometric is PhotometricCfa or PhotometricLinearRaw || ifd.Int(RawTags.BitsPerSample, 8) != 8)
        {
            return null;
        }

        if (compression is 6 or 7 or 34892 && photometric is 2 or 6 or 1)
        {
            long[]? offsets = ifd.Ints(RawTags.StripOffsets, 1);
            long[]? counts = ifd.Ints(RawTags.StripByteCounts, 1);
            if (offsets is { Length: 1 } && counts is { Length: 1 } && offsets[0] > 0 && counts[0] > 0)
            {
                return new RawPreviewSource(width, height, true, ifd.File.Origin + offsets[0], counts[0], ifd);
            }

            long at = ifd.Int(RawTags.JpegInterchangeFormat);
            long length = ifd.Int(RawTags.JpegInterchangeFormatLength);
            // Where the preview's bytes lie is checked when they are read: Probe has not read that far into the file.
            if (at > 0 && length > 0)
            {
                return new RawPreviewSource(width, height, true, ifd.File.Origin + at, length, ifd);
            }

            return null;
        }

        if (compression == 1 && photometric is 2 or 1 && ifd.Int(RawTags.SamplesPerPixel, 1) == (photometric == 2 ? 3 : 1))
        {
            return new RawPreviewSource(width, height, false, 0, 0, ifd);
        }

        return null;
    }

    private static int[]? Dims(long[] dims)
    {
        if (dims[0] is < 1 or > 1024 || dims[1] is < 1 or > 1024 || dims[2] is < 1 or > 1024 || dims[0] * dims[1] * dims[2] > 1 << 20)
        {
            return null;
        }

        return [(int)dims[0], (int)dims[1], (int)dims[2]];
    }

    private static float[]? Floats(RawIfd ifd, int tag, int count)
    {
        return ifd.Doubles(tag, count) is { } values && values.Length == count ? [.. values.Select(v => (float)v)] : null;
    }
}
