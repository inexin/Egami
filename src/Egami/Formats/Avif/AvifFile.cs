using System.Buffers.Binary;

using Egami.Formats.Av1;
using Egami.Formats.Heif;
using Egami.Metadata;

namespace Egami.Formats.Avif;

/// <summary>
/// An AVIF file's still image, as the HEIF container describes it: the primary item (an av01 item or a grid of them), its alpha
/// item, the properties that set its size, color, depth and geometry, its Exif and XMP items, and the AV1 configuration and
/// sequence header. Reading it decodes nothing. The AV1 decoder and the AVIF reader build on it.
/// </summary>
internal sealed class AvifFile
{
    internal static readonly uint Av01 = FourCC.Of("av01");
    internal static readonly uint Grid = FourCC.Of("grid");
    internal static readonly uint Iovl = FourCC.Of("iovl");
    internal static readonly uint Tmap = FourCC.Of("tmap");
    internal static readonly uint Altr = FourCC.Of("altr");
    internal static readonly uint Exif = FourCC.Of("Exif");
    internal static readonly uint Auxl = FourCC.Of("auxl");
    internal static readonly uint Dimg = FourCC.Of("dimg");
    internal static readonly uint Cdsc = FourCC.Of("cdsc");
    internal static readonly uint Prem = FourCC.Of("prem");
    internal static readonly uint Avif = FourCC.Of("avif");
    internal static readonly uint Avis = FourCC.Of("avis");

    private AvifFile(HeifFile heif, HeifItem primary)
    {
        Heif = heif;
        Primary = primary;
    }

    /// <summary>Gets the container.</summary>
    internal HeifFile Heif { get; }

    /// <summary>Gets the primary item: an av01 item, a grid or an overlay.</summary>
    internal HeifItem Primary { get; }

    /// <summary>Gets the grid's cells or the overlay's layers in order; otherwise the primary item alone.</summary>
    internal IReadOnlyList<HeifItem> Cells { get; private set; } = [];

    /// <summary>Gets the grid's rows and columns, or (1, 1).</summary>
    internal (int Rows, int Columns) GridSize { get; private set; } = (1, 1);

    /// <summary>Gets the image size before clap, irot and imir: the grid's output size, or the ispe of the av01 item.</summary>
    internal int Width { get; private set; }

    /// <inheritdoc cref="Width"/>
    internal int Height { get; private set; }

    /// <summary>Gets the AV1 configuration of the (first cell of the) primary image.</summary>
    internal Av1Config Config { get; private set; } = null!;

    /// <summary>Gets the AV1 sequence header of the (first cell of the) primary image.</summary>
    internal Av1SequenceHeader Sequence { get; private set; } = null!;

    /// <summary>Gets the nclx colr property, or <see langword="null"/>.</summary>
    internal NclxColor? Nclx { get; private set; }

    /// <summary>Gets the ICC profile of a colr property, or an empty block.</summary>
    internal ReadOnlyMemory<byte> Icc { get; private set; }

    /// <summary>Gets the alpha item (an av01 item or a grid, or the first cell's when each cell has its own), or <see langword="null"/>.</summary>
    internal HeifItem? Alpha { get; private set; }

    /// <summary>Gets the alpha image's av01 items: the alpha grid's cells, one for each color cell, or the alpha item alone.</summary>
    internal IReadOnlyList<HeifItem> AlphaCells { get; private set; } = [];

    /// <summary>Gets how the alpha cells tile: the alpha grid's layout, the color grid's when each cell has its own alpha, or (1, 1).</summary>
    internal (int Rows, int Columns, int Width, int Height) AlphaLayout { get; private set; }

    /// <summary>Gets the overlay (iovl) when the primary item is one: its canvas fill, size and each layer's offset.</summary>
    internal HeifOverlay? Overlay { get; private set; }

    /// <summary>
    /// Gets the tone-mapped derived image (tmap) whose gain map applies to the primary item, found as libavif finds it: the file
    /// has the tmap brand, an altr group holds the tmap item and the primary item, and the tmap item's dimg references are the
    /// primary item and the gain map image. Otherwise <see langword="null"/>.
    /// </summary>
    internal HeifItem? ToneMap { get; private set; }

    /// <summary>Gets the gain map image's av01 items (a grid's cells, or the item alone).</summary>
    internal IReadOnlyList<HeifItem> GainMapCells { get; private set; } = [];

    /// <summary>Gets how the gain map's cells tile: rows, columns and output size.</summary>
    internal (int Rows, int Columns, int Width, int Height) GainMapLayout { get; private set; }

    /// <summary>Gets whether Load gives alpha: there is an alpha image, or an overlay's canvas is not opaque.</summary>
    internal bool HasAlpha => Alpha is not null || Overlay is { Fill: [_, _, _, < 0xFFFF] };

    /// <summary>Gets whether the color is premultiplied by the alpha (a prem reference from the color item to the alpha item).</summary>
    internal bool AlphaPremultiplied { get; private set; }

    /// <summary>Gets the clean aperture's crop of the image, or <see langword="null"/>.</summary>
    internal (int X, int Y, int Width, int Height)? Crop { get; private set; }

    /// <summary>Gets the clap property, valid or not, or <see langword="null"/>.</summary>
    internal CleanAperture? CleanAperture { get; private set; }

    /// <summary>Gets irot's quarter turns anticlockwise, or 0.</summary>
    internal int Rotation { get; private set; }

    /// <summary>Gets imir's axis (0 top to bottom, 1 left to right), or <see langword="null"/>.</summary>
    internal int? Mirror { get; private set; }

    /// <summary>Gets clli's levels, or <see langword="null"/>.</summary>
    internal (int MaxCll, int MaxFall)? ContentLightLevel { get; private set; }

    /// <summary>Gets mdcv, or <see langword="null"/>.</summary>
    internal MasteringDisplay? MasteringDisplay { get; private set; }

    /// <summary>Gets whether the image has progressive layers (a1lx on the av01 item and no lsel picking one).</summary>
    internal bool Progressive { get; private set; }

    /// <summary>Gets the Exif items that describe the primary item, in iinf order.</summary>
    internal List<HeifItem> ExifItems { get; } = [];

    /// <summary>Gets the XMP items (mime application/rdf+xml) that describe the primary item, in iinf order.</summary>
    internal List<HeifItem> XmpItems { get; } = [];

    /// <summary>Gets the color (CICP and range) the image is in: the nclx property's, else the sequence header's.</summary>
    internal Colors.ColorCodePoints CodePoints => Nclx is NclxColor n
        ? new Colors.ColorCodePoints((byte)n.Primaries, (byte)n.Transfer, (byte)n.Matrix, n.FullRange)
        : new Colors.ColorCodePoints((byte)Sequence.ColorPrimaries, (byte)Sequence.TransferCharacteristics, (byte)Sequence.MatrixCoefficients, Sequence.FullRange);

    /// <summary>Gets the display orientation irot and imir give, applied after clap.</summary>
    internal ImageOrientation Orientation => Orient(Rotation, Mirror);

    /// <summary>Reads the input into memory and finds the still image.</summary>
    internal static AvifFile Read(EgamiInput input) => Open(HeifFile.Read(input));

    /// <summary>Finds the still image of a parsed container.</summary>
    internal static AvifFile Open(HeifFile heif)
    {
        if (!heif.HasMeta)
        {
            throw new UnsupportedFormatException("The AVIF file has no meta box: it holds an image sequence only, which Egami does not read yet.");
        }

        heif.MarkUnsupportedEssential(AvifProperties.Understood.Contains);
        if (heif.PrimaryItem is not HeifItem primary)
        {
            throw Corrupt(heif, heif.PrimaryItemId == 0 ? "The AVIF file has no primary item (pitm box)." : $"The primary item, {heif.PrimaryItemId}, has no iinf entry.");
        }

        if (primary.Type != Av01 && primary.Type != Grid && primary.Type != Iovl)
        {
            throw new UnsupportedFormatException($"The AVIF file's primary item is a \"{FourCC.ToString(primary.Type)}\" item; Egami reads av01, grid and iovl items.");
        }

        AvifFile avif = new(heif, primary);
        avif.Resolve();
        return avif;
    }

    /// <summary>Gets the EXIF orientation that rotating anticlockwise by <paramref name="quarterTurns"/>, then mirroring, gives.</summary>
    internal static ImageOrientation Orient(int quarterTurns, int? mirror)
    {
        // Each orientation as the matrix that maps stored coordinates (x right, y down) to displayed ones, in EXIF order 1 to 8.
        ReadOnlySpan<int> matrices = [1, 0, 0, 1, -1, 0, 0, 1, -1, 0, 0, -1, 1, 0, 0, -1, 0, 1, 1, 0, 0, -1, 1, 0, 0, -1, -1, 0, 0, 1, -1, 0];
        (int a, int b, int c, int d) = (1, 0, 0, 1);
        for (int i = 0; i < (quarterTurns & 3); i++)
        {
            // A quarter turn anticlockwise on screen: (x, y) to (y, -x).
            (a, b, c, d) = (c, d, -a, -b);
        }

        if (mirror == 1)
        {
            (a, b) = (-a, -b);
        }
        else if (mirror == 0)
        {
            (c, d) = (-c, -d);
        }

        for (int o = 0; o < 8; o++)
        {
            if (matrices[4 * o] == a && matrices[(4 * o) + 1] == b && matrices[(4 * o) + 2] == c && matrices[(4 * o) + 3] == d)
            {
                return (ImageOrientation)(o + 1);
            }
        }

        return ImageOrientation.Normal;
    }

    private void Resolve()
    {
        HeifFile heif = Heif;
        if (Primary.UnsupportedEssential != 0)
        {
            throw new UnsupportedFormatException($"The AVIF file's primary item has an essential \"{FourCC.ToString(Primary.UnsupportedEssential)}\" property Egami does not understand.");
        }

        if (Primary.Type == Grid)
        {
            (List<HeifItem> cells, int rows, int columns, int width, int height) = ReadGrid(Primary);
            (Cells, GridSize, Width, Height) = (cells, (rows, columns), width, height);
        }
        else if (Primary.Type == Iovl)
        {
            ReadOverlay();
        }
        else
        {
            Cells = [Primary];
            (Width, Height) = Extents(Primary);
        }

        HeifItem first = Cells[0];
        if (heif.Property(first, AvifProperties.Av1C) is not HeifProperty av1C)
        {
            throw Corrupt(heif, $"{Capital(first)} has no av1C property.");
        }

        Config = AvifProperties.ReadAv1Config(heif, av1C);
        Sequence = Av1SequenceHeader.Find(Config.ConfigObus.Span, heif.FormatName)
            ?? Av1SequenceHeader.Find(heif.ItemData(first).Span, heif.FormatName)
            ?? throw Corrupt(heif, $"{Capital(first)} has no AV1 sequence header.");

        // The color, geometry and HDR properties sit on the primary item; a grid's cells may carry them too.
        foreach ((HeifProperty property, _) in heif.PropertiesOf(Primary))
        {
            if (property.Type == AvifProperties.Colr)
            {
                (NclxColor? nclx, ReadOnlyMemory<byte> icc) = AvifProperties.ReadColor(heif, property);
                Nclx ??= nclx;
                if (Icc.IsEmpty)
                {
                    Icc = icc;
                }
            }
            else if (property.Type == AvifProperties.Clap)
            {
                CleanAperture = AvifProperties.ReadCleanAperture(heif, property);
                Crop = CleanAperture.Value.CropRectangle(Width, Height);
            }
            else if (property.Type == AvifProperties.Irot)
            {
                Rotation = AvifProperties.ReadRotation(heif, property);
            }
            else if (property.Type == AvifProperties.Imir)
            {
                Mirror = AvifProperties.ReadMirror(heif, property);
            }
            else if (property.Type == AvifProperties.Clli)
            {
                ContentLightLevel = AvifProperties.ReadContentLightLevel(heif, property);
            }
            else if (property.Type == AvifProperties.Mdcv)
            {
                MasteringDisplay = AvifProperties.ReadMasteringDisplay(heif, property);
            }
        }

        if (Nclx is null && Icc.IsEmpty && Primary.Type != Av01)
        {
            foreach ((HeifProperty property, _) in heif.PropertiesOf(first))
            {
                if (property.Type == AvifProperties.Colr)
                {
                    (NclxColor? nclx, ReadOnlyMemory<byte> icc) = AvifProperties.ReadColor(heif, property);
                    Nclx ??= nclx;
                    Icc = Icc.IsEmpty ? icc : Icc;
                }
            }
        }

        // Layered without lsel picking a layer (or with lsel's 0xFFFF, any layer).
        Progressive = heif.Property(first, AvifProperties.A1lx) is not null
            && (heif.Property(first, AvifProperties.Lsel) is not HeifProperty lsel || heif.Content(lsel) is [0xFF, 0xFF, ..]);

        FindAlpha();
        FindGainMap();
        foreach (HeifItem item in heif.Items)
        {
            if (Array.IndexOf(item.ReferencesOf(Cdsc), Primary.Id) < 0 || item.UnsupportedEssential != 0)
            {
                continue;
            }

            if (item.Type == Exif)
            {
                ExifItems.Add(item);
            }
            else if (item.Type == FourCC.Mime && item.ContentType == "application/rdf+xml")
            {
                XmpItems.Add(item);
            }
        }
    }

    private void FindAlpha()
    {
        HeifFile heif = Heif;
        foreach (HeifItem item in heif.Items)
        {
            if (item.UnsupportedEssential != 0 || (item.Type != Av01 && item.Type != Grid))
            {
                continue;
            }

            uint[] owners = item.ReferencesOf(Auxl);
            if (owners.Length == 0 || owners[0] != Primary.Id)
            {
                continue;
            }

            if (!IsAlpha(item))
            {
                continue;
            }

            Alpha = item;
            AlphaPremultiplied = Array.IndexOf(Primary.ReferencesOf(Prem), item.Id) >= 0;
            if (item.Type == Grid)
            {
                (List<HeifItem> cells, int rows, int columns, int width, int height) = ReadGrid(item);
                (AlphaCells, AlphaLayout) = (cells, (rows, columns, width, height));
            }
            else
            {
                (int width, int height) = Extents(item);
                (AlphaCells, AlphaLayout) = ([item], (1, 1, width, height));
            }

            return;
        }

        // libavif's grid files may give each color cell its own alpha item instead of an alpha grid; every cell needs one.
        if (Primary.Type == Grid)
        {
            List<HeifItem> alphas = [];
            foreach (HeifItem cell in Cells)
            {
                HeifItem? found = null;
                foreach (HeifItem item in heif.Items)
                {
                    uint[] owners = item.ReferencesOf(Auxl);
                    if (item.Type == Av01 && item.UnsupportedEssential == 0 && owners.Length > 0 && owners[0] == cell.Id && IsAlpha(item))
                    {
                        found = item;
                        break;
                    }
                }

                if (found is null)
                {
                    return;
                }

                alphas.Add(found);
            }

            Alpha = alphas[0];
            (AlphaCells, AlphaLayout) = (alphas, (GridSize.Rows, GridSize.Columns, Width, Height));
            AlphaPremultiplied = Array.IndexOf(Cells[0].ReferencesOf(Prem), alphas[0].Id) >= 0;
        }
    }

    private void FindGainMap()
    {
        HeifFile heif = Heif;
        if (!heif.HasBrand(Tmap))
        {
            return;
        }

        foreach (HeifEntityGroup group in heif.Groups)
        {
            if (group.Type != Altr || Array.IndexOf(group.Entities, Primary.Id) < 0)
            {
                continue;
            }

            // The group lists its alternatives by preference; libavif takes the gain map only when the tmap item comes first.
            foreach (uint id in group.Entities.AsSpan(0, Math.Min(1, group.Entities.Length)))
            {
                if (heif.Item(id) is not HeifItem tmap || tmap.Type != Tmap || tmap.UnsupportedEssential != 0)
                {
                    continue;
                }

                uint[] inputs = tmap.ReferencesOf(Dimg);
                if (inputs.Length != 2 || inputs[0] != Primary.Id || heif.Item(inputs[1]) is not HeifItem map || map.UnsupportedEssential != 0)
                {
                    continue;
                }

                if (map.Type == Grid)
                {
                    try
                    {
                        (List<HeifItem> cells, int rows, int columns, int width, int height) = ReadGrid(map);
                        (GainMapCells, GainMapLayout) = (cells, (rows, columns, width, height));
                    }
                    catch (EgamiException e)
                    {
                        heif.Problems.Add(new HeifProblem("Avif.GainMapDropped", $"The gain map grid could not be read ({e.Message}); the image loads as SDR only.", -1));
                        return;
                    }
                }
                else if (map.Type == Av01)
                {
                    (int width, int height) = Extents(map);
                    (GainMapCells, GainMapLayout) = ([map], (1, 1, width, height));
                }
                else
                {
                    continue;
                }

                ToneMap = tmap;
                return;
            }
        }
    }

    private bool IsAlpha(HeifItem item)
    {
        foreach ((HeifProperty property, _) in Heif.PropertiesOf(item))
        {
            if (property.Type == AvifProperties.AuxC && AvifProperties.ReadAuxiliaryType(Heif, property) is AvifProperties.AlphaUrn or AvifProperties.HevcAlphaUrn)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Reads a grid item (HEIF 6.6.2.3.2): its av01 cells in order, rows, columns and output size.</summary>
    internal (List<HeifItem> Cells, int Rows, int Columns, int Width, int Height) ReadGrid(HeifItem grid)
    {
        HeifFile heif = Heif;
        ReadOnlySpan<byte> data = heif.ItemData(grid).Span;
        if (data.Length < 8 || data[0] != 0)
        {
            throw Corrupt(heif, data.Length < 8 ? "The grid item's data is cut off." : $"The grid item's version is {data[0]}, not 0.");
        }

        bool large = (data[1] & 1) != 0;
        int rows = data[2] + 1, columns = data[3] + 1;
        if (large && data.Length < 12)
        {
            throw Corrupt(heif, "The grid item's data is cut off.");
        }

        uint width = large ? BinaryPrimitives.ReadUInt32BigEndian(data[4..]) : BinaryPrimitives.ReadUInt16BigEndian(data[4..]);
        uint height = large ? BinaryPrimitives.ReadUInt32BigEndian(data[8..]) : BinaryPrimitives.ReadUInt16BigEndian(data[6..]);
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw Corrupt(heif, $"The grid's output size is {width} x {height}.");
        }

        uint[] ids = grid.ReferencesOf(Dimg);
        if (ids.Length != rows * columns)
        {
            throw Corrupt(heif, $"The {rows} x {columns} grid has {ids.Length} cells.");
        }

        return (Coded(ids, "grid cell"), rows, columns, (int)width, (int)height);
    }

    /// <summary>Reads an overlay item (HEIF 6.6.2.3.3): the canvas fill and size, and where each layer goes.</summary>
    private void ReadOverlay()
    {
        HeifFile heif = Heif;
        ReadOnlySpan<byte> data = heif.ItemData(Primary).Span;
        uint[] ids = Primary.ReferencesOf(Dimg);
        if (data.Length < 2 || data[0] != 0)
        {
            throw Corrupt(heif, data.Length < 2 ? "The overlay item's data is cut off." : $"The overlay item's version is {data[0]}, not 0.");
        }

        if (ids.Length == 0)
        {
            throw Corrupt(heif, "The overlay item has no layers (dimg references).");
        }

        // version, flags (bit 0: 32-bit fields), the RGBA canvas fill, the canvas size, then each layer's offset.
        bool large = (data[1] & 1) != 0;
        int field = large ? 4 : 2;
        if (data.Length < 10 + (2 * field) + (ids.Length * 2 * field))
        {
            throw Corrupt(heif, "The overlay item's data is cut off.");
        }

        ushort[] fill = new ushort[4];
        for (int i = 0; i < 4; i++)
        {
            fill[i] = BinaryPrimitives.ReadUInt16BigEndian(data[(2 + (2 * i))..]);
        }

        long width = Unsigned(data, 10), height = Unsigned(data, 10 + field);
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue)
        {
            throw Corrupt(heif, $"The overlay's canvas is {width} x {height}.");
        }

        (int X, int Y)[] offsets = new (int, int)[ids.Length];
        for (int i = 0, at = 10 + (2 * field); i < ids.Length; i++, at += 2 * field)
        {
            offsets[i] = large
                ? (BinaryPrimitives.ReadInt32BigEndian(data[at..]), BinaryPrimitives.ReadInt32BigEndian(data[(at + 4)..]))
                : (BinaryPrimitives.ReadInt16BigEndian(data[at..]), BinaryPrimitives.ReadInt16BigEndian(data[(at + 2)..]));
        }

        Cells = Coded(ids, "overlay layer");
        Overlay = new HeifOverlay(fill, (int)width, (int)height, offsets);
        (Width, Height) = ((int)width, (int)height);

        long Unsigned(ReadOnlySpan<byte> d, int position) => large ? BinaryPrimitives.ReadUInt32BigEndian(d[position..]) : BinaryPrimitives.ReadUInt16BigEndian(d[position..]);
    }

    /// <summary>Gets the av01 items a derived image references, which must exist and be understood.</summary>
    private List<HeifItem> Coded(uint[] ids, string role)
    {
        HeifFile heif = Heif;
        List<HeifItem> cells = [];
        foreach (uint id in ids)
        {
            HeifItem cell = heif.Item(id) ?? throw Corrupt(heif, $"A {role}, item {id}, has no iinf entry.");
            if (cell.Type != Av01)
            {
                throw new UnsupportedFormatException($"A {role} is a \"{FourCC.ToString(cell.Type)}\" item; Egami reads av01 ones.");
            }

            if (cell.UnsupportedEssential != 0)
            {
                throw new UnsupportedFormatException($"A {role} has an essential \"{FourCC.ToString(cell.UnsupportedEssential)}\" property Egami does not understand.");
            }

            cells.Add(cell);
        }

        return cells;
    }

    /// <summary>Gets an item's ispe size.</summary>
    internal (int Width, int Height) Extents(HeifItem item) =>
        Heif.Property(item, AvifProperties.Ispe) is HeifProperty ispe
            ? AvifProperties.ReadSpatialExtents(Heif, ispe)
            : throw Corrupt(Heif, $"{Capital(item)} has no ispe property.");

    private static string Capital(HeifItem item) => "I" + item.ToString()[1..];

    private static CorruptDataException Corrupt(HeifFile heif, string problem) => new(heif.FormatName, -1, problem);
}
