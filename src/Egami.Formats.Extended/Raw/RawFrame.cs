namespace Egami.Formats.Extended.Raw;

/// <summary>Where a RAW file keeps its sensor data: the IFD and how its strips or tiles are stored.</summary>
internal sealed class RawSensor
{
    public required RawIfd Ifd { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    /// <summary>Gets the samples per pixel: 1 for a colour filter array, 3 (or more) for LinearRaw.</summary>
    public int Samples { get; init; } = 1;

    public int Bits { get; init; } = 16;

    public int Compression { get; init; } = 1;

    public int Photometric { get; init; }

    /// <summary>Gets whether samples are floating point (SampleFormat 3).</summary>
    public bool Float { get; init; }

    public int Predictor { get; init; } = 1;

    public bool Tiled { get; init; }

    /// <summary>Gets the width of a tile, or the image width for strips.</summary>
    public int UnitWidth { get; init; }

    /// <summary>Gets the height of a tile or strip.</summary>
    public int UnitHeight { get; init; }

    public long[] Offsets { get; init; } = [];

    public long[] Counts { get; init; } = [];

    /// <summary>Gets the byte order of uncompressed samples wider than 8 bits.</summary>
    public bool BigEndian { get; init; }

    /// <summary>Gets the codec of maker sensor data, or <see cref="RawCodec.Tiff"/> for TIFF's own compressions.</summary>
    public RawCodec Codec { get; init; }

    /// <summary>Gets what the codec needs from the file's tags (its tables, curve or parameters), or <see langword="null"/>.</summary>
    public object? CodecData { get; init; }

    /// <summary>Gets the number of units across.</summary>
    public int UnitsAcross => (Width + UnitWidth - 1) / UnitWidth;

    /// <summary>Gets the number of units down.</summary>
    public int UnitsDown => (Height + UnitHeight - 1) / UnitHeight;
}

/// <summary>The colour filter array: which colour plane each position of the repeating pattern samples.</summary>
internal sealed class RawCfa
{
    public RawCfa(int rows, int columns, byte[] colors)
    {
        Rows = rows;
        Columns = columns;
        Colors = colors;
    }

    public int Rows { get; }

    public int Columns { get; }

    /// <summary>Gets the colour plane (0 red, 1 green, 2 blue) at each position, row by row.</summary>
    public byte[] Colors { get; }

    public int ColorAt(int x, int y) => Colors[((y % Rows) * Columns) + (x % Columns)];

    /// <summary>
    /// Gets the Bayer phase when the pattern is a 2 × 2 Bayer one: the position of red as (row &lt;&lt; 1) | column, or −1.
    /// </summary>
    public int BayerRed
    {
        get
        {
            if (Rows != 2 || Columns != 2)
            {
                return -1;
            }

            int red = Array.IndexOf(Colors, (byte)0);
            int blue = Array.IndexOf(Colors, (byte)2);
            if (red < 0 || blue < 0 || (red ^ blue) != 3 || Colors[red ^ 1] != 1 || Colors[red ^ 2] != 1)
            {
                return -1;
            }

            return red;
        }
    }

    /// <summary>Returns the pattern as letters, row by row, such as <c>RGGB</c>.</summary>
    public string Name => string.Concat(Colors.Select(c => c switch
    {
        0 => 'R',
        1 => 'G',
        2 => 'B',
        3 => 'C',
        4 => 'M',
        5 => 'Y',
        _ => 'W',
    }));

    /// <summary>Returns the pattern for the sensor area that starts at (<paramref name="left"/>, <paramref name="top"/>).</summary>
    public RawCfa Shift(int left, int top)
    {
        byte[] colors = new byte[Colors.Length];
        for (int y = 0; y < Rows; y++)
        {
            for (int x = 0; x < Columns; x++)
            {
                colors[(y * Columns) + x] = (byte)ColorAt(x + left, y + top);
            }
        }

        return new RawCfa(Rows, Columns, colors);
    }
}

/// <summary>A DNG camera profile: the colour matrices and the HueSatMap, LookTable and tone curve that shape the colours.</summary>
internal sealed class RawProfile
{
    public double[]? ColorMatrix1 { get; set; }

    public double[]? ColorMatrix2 { get; set; }

    public double[]? ForwardMatrix1 { get; set; }

    public double[]? ForwardMatrix2 { get; set; }

    public double[]? CameraCalibration1 { get; set; }

    public double[]? CameraCalibration2 { get; set; }

    public int Illuminant1 { get; set; }

    public int Illuminant2 { get; set; }

    public int[]? HueSatDims { get; set; }

    public float[]? HueSat1 { get; set; }

    public float[]? HueSat2 { get; set; }

    public bool HueSatSrgb { get; set; }

    public int[]? LookDims { get; set; }

    public float[]? Look { get; set; }

    public bool LookSrgb { get; set; }

    public float[]? ToneCurve { get; set; }

    public double BaselineExposureOffset { get; set; }

    /// <summary>Gets or sets the camera's BaselineExposure, for a profile from the camera table; a file's own value wins.</summary>
    public double BaselineExposure { get; set; }

    public string? Name { get; set; }
}

/// <summary>An embedded preview image a RAW file carries.</summary>
internal sealed record RawPreviewSource(int Width, int Height, bool Jpeg, long Offset, long Length, RawIfd? Ifd);

/// <summary>Everything a RAW file says about its sensor data and how to develop it, whatever the format it came from.</summary>
internal sealed class RawFrame
{
    public required RawSensor Sensor { get; init; }

    public string Make { get; set; } = "";

    public string Model { get; set; } = "";

    public string? UniqueCameraModel { get; set; }

    public byte[]? DngVersion { get; set; }

    /// <summary>Gets or sets the colour filter array, or <see langword="null"/> for LinearRaw data.</summary>
    public RawCfa? Cfa { get; set; }

    /// <summary>Gets or sets the number of colour planes the matrices map (3 for RGB).</summary>
    public int Planes { get; set; } = 3;

    public ushort[]? Linearization { get; set; }

    public int BlackRows { get; set; } = 1;

    public int BlackColumns { get; set; } = 1;

    /// <summary>Gets or sets the black levels, BlackRows × BlackColumns × samples, in the order DNG's BlackLevel gives them.</summary>
    public double[] Black { get; set; } = [0];

    public double[]? BlackDeltaH { get; set; }

    public double[]? BlackDeltaV { get; set; }

    /// <summary>Gets or sets the white level of each sample.</summary>
    public double[] White { get; set; } = [65535];

    /// <summary>Gets or sets the active area, in sensor coordinates.</summary>
    public Rect Active { get; set; }

    /// <summary>Gets or sets the default crop, relative to the active area.</summary>
    public (double X, double Y, double Width, double Height) Crop { get; set; }

    public double[]? AsShotNeutral { get; set; }

    public (double X, double Y)? AsShotWhite { get; set; }

    public double[]? AnalogBalance { get; set; }

    public double BaselineExposure { get; set; }

    /// <summary>Gets or sets a RAF's RawExposureBias in EV (its dynamic-range setting moves it), or <see langword="null"/>.</summary>
    public double? ExposureBias { get; set; }

    public RawProfile Profile { get; set; } = new();

    public byte[]? Opcodes1 { get; set; }

    public byte[]? Opcodes2 { get; set; }

    public byte[]? Opcodes3 { get; set; }

    public double[]? NoiseProfile { get; set; }

    public int Orientation { get; set; } = 1;

    public List<RawPreviewSource> Previews { get; } = [];

    public double? Iso { get; set; }

    public double? ExposureTime { get; set; }

    public double? FNumber { get; set; }

    public double? FocalLength { get; set; }

    /// <summary>Gets or sets EXIF's FocalLengthIn35mmFilm, from which the sensor's crop factor follows, or <see langword="null"/>.</summary>
    public double? FocalLength35 { get; set; }

    /// <summary>Gets or sets the focus distance in metres (EXIF SubjectDistance), or <see langword="null"/>.</summary>
    public double? FocusDistance { get; set; }

    public string? LensModel { get; set; }

    /// <summary>Gets or sets the IFD whose tags hold the EXIF, XMP and ICC metadata (IFD0).</summary>
    public RawIfd? MetadataIfd { get; set; }

    /// <summary>Gets or sets the Exif IFD when it is not one IFD0 points at (a CR3's CMT2 block), or <see langword="null"/>.</summary>
    public RawIfd? ExifIfd { get; set; }

    /// <summary>Gets or sets the GPS IFD when it is not one IFD0 points at (a CR3's CMT4 block), or <see langword="null"/>.</summary>
    public RawIfd? GpsIfd { get; set; }

    public byte[]? MakerNote { get; set; }

    public List<(string Id, string Message)> Repairs { get; } = [];

    /// <summary>Gets or sets whether the file gave its own active area and crop, which the camera table must not replace.</summary>
    public bool HasOwnCrop { get; set; }

    /// <summary>Gets or sets whether the file gave its own black levels, which the camera table must not replace.</summary>
    public bool HasOwnLevels { get; set; }

    /// <summary>Gets or sets masked sensor pixels to measure black from when no black level is known, or <see langword="null"/>.</summary>
    public Rect? MaskedArea { get; set; }
}
