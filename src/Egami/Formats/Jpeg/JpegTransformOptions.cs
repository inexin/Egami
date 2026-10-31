namespace Egami.Formats.Jpeg;

/// <summary>The rotation or flip a lossless JPEG transform applies, in the terms of <c>jpegtran</c>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="jpeg-transform"/>
/// </example>
public enum JpegTransformOperation
{
    /// <summary>No rotation or flip; with <see cref="JpegTransformOptions.Crop"/> or <see cref="JpegTransformOptions.Grayscale"/> only those apply.</summary>
    None,

    /// <summary>Mirrored left to right.</summary>
    FlipHorizontal,

    /// <summary>Mirrored top to bottom.</summary>
    FlipVertical,

    /// <summary>Turned 90° clockwise.</summary>
    Rotate90,

    /// <summary>Turned 180°.</summary>
    Rotate180,

    /// <summary>Turned 270° clockwise (90° counterclockwise).</summary>
    Rotate270,

    /// <summary>Mirrored along the top-left to bottom-right diagonal: rows become columns.</summary>
    Transpose,

    /// <summary>Mirrored along the top-right to bottom-left diagonal.</summary>
    Transverse,

    /// <summary>
    /// Whatever the EXIF orientation needs to show the image upright, after which the orientation is set to
    /// <see cref="Metadata.ImageOrientation.Normal"/>: the lossless version of <see cref="Operations.RasterOps.AutoOrient"/>. A
    /// file with no orientation, or an upright one, is copied.
    /// </summary>
    AutoOrient,
}

/// <summary>
/// What a lossless transform does with the blocks at the right or bottom edge when the image is not a whole number of MCUs (8 or 16
/// pixels) across or down, so those blocks cannot move to the other side intact.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="jpeg-transform"/>
/// </example>
public enum JpegTransformEdges
{
    /// <summary>
    /// Drops the partial MCU from an edge that is mirrored, so the image loses up to 15 pixels there (up to 7 for gray), as
    /// <c>jpegtran -trim</c> does. Edges that stay in place keep every pixel.
    /// </summary>
    Trim,

    /// <summary>
    /// Throws <see cref="InvalidOperationException"/> when a partial MCU would have to move, as <c>jpegtran -perfect</c> does, so
    /// the result is always exact and full size.
    /// </summary>
    Strict,

    /// <summary>
    /// Leaves partial MCUs where they are, unmirrored, as <c>jpegtran</c> does by default: every pixel is kept, but a strip up to
    /// 15 pixels wide at the edge is not turned with the rest.
    /// </summary>
    Keep,
}

/// <summary>
/// Options for <see cref="JpegTransform"/>: the rotation or flip, a crop, dropping color, how partial edge blocks are handled, how
/// the result is coded and whether metadata is copied. The defaults copy the image unchanged, with optimized Huffman tables.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="jpeg-transform"/>
/// </example>
public sealed class JpegTransformOptions
{
    /// <summary>Gets the default options: no change, edges trimmed, optimized Huffman tables, metadata copied.</summary>
    public static JpegTransformOptions Default { get; } = new();

    /// <summary>Gets the rotation or flip. Defaults to <see cref="JpegTransformOperation.None"/>.</summary>
    public JpegTransformOperation Operation { get; init; }

    /// <summary>
    /// Gets the area to keep, in the coordinates of the image after <see cref="Operation"/>, or <see langword="null"/> (the
    /// default) for all of it. Its left and top edges move left and up to the MCU grid, as with <c>jpegtran -crop</c>, so the
    /// result can be up to 15 pixels wider and taller than asked; its right and bottom edges are kept as given.
    /// </summary>
    public Rect? Crop { get; init; }

    /// <summary>
    /// Gets whether the result keeps only the luma component, a gray image with no loss, as <c>jpegtran -grayscale</c> does.
    /// Applies to YCbCr files; gray files are unchanged, and RGB, CMYK and YCCK files throw <see cref="NotSupportedException"/>.
    /// </summary>
    public bool Grayscale { get; init; }

    /// <summary>Gets what happens to partial MCUs at a mirrored edge. Defaults to <see cref="JpegTransformEdges.Trim"/>.</summary>
    public JpegTransformEdges Edges { get; init; }

    /// <summary>
    /// Gets whether the result is written progressive, with the scan script and per-scan tables of
    /// <see cref="JpegWriterOptions.Progressive"/>. Defaults to <see langword="false"/>: sequential, whatever the source was, as
    /// <c>jpegtran</c> does without <c>-progressive</c>.
    /// </summary>
    public bool Progressive { get; init; }

    /// <summary>
    /// Gets whether a sequential result gets Huffman tables made for it (<c>jpegtran -optimize</c>), which makes it a few percent
    /// smaller than the standard tables do. Defaults to <see langword="true"/>, so a transformed file is not larger than its
    /// source for the tables alone. 12-bit files always get their own; progressive ones always do too.
    /// </summary>
    public bool OptimizeHuffman { get; init; } = true;

    /// <summary>
    /// Gets whether the result is arithmetic-coded (<c>jpegtran -arithmetic</c>; SOF9, or SOF10 with <see cref="Progressive"/>), as
    /// <see cref="JpegWriterOptions.Arithmetic"/> writes: about 4 to 8% smaller than optimized Huffman tables, with the same
    /// pixels, so a Huffman file can be repacked with no loss; but no web browser reads it. <see cref="OptimizeHuffman"/> is then
    /// ignored. Defaults to <see langword="false"/>: Huffman-coded, whatever the source was, as <c>jpegtran</c> does.
    /// </summary>
    public bool Arithmetic { get; init; }

    /// <summary>
    /// Gets whether EXIF, ICC, XMP, IPTC, comments and other application segments are copied (<c>jpegtran -copy all</c>).
    /// Defaults to <see langword="true"/>. EXIF's pixel dimensions follow the result; its thumbnail gets the same rotation, flip
    /// and gray conversion, and is dropped after a crop, since it would still show the whole image. With <see langword="false"/>
    /// only the JFIF or Adobe segment that says how to read the colors is written.
    /// </summary>
    public bool CopyMetadata { get; init; } = true;
}

/// <summary>What a lossless JPEG transform produced: the size of the result and what was repaired or left out on the way.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationAndMetadata.cs" region="jpeg-transform"/>
/// </example>
public sealed class JpegTransformResult
{
    internal JpegTransformResult(int width, int height, IReadOnlyList<EgamiDiagnostic> warnings)
    {
        Width = width;
        Height = height;
        Warnings = warnings;
    }

    /// <summary>Gets the width of the result, in pixels.</summary>
    public int Width { get; }

    /// <summary>Gets the height of the result, in pixels.</summary>
    public int Height { get; }

    /// <summary>
    /// Gets the warnings: damage in the source that was recovered from, as a load would list in
    /// <see cref="Metadata.RasterMetadata.Warnings"/>, and metadata that could not be carried over.
    /// </summary>
    public IReadOnlyList<EgamiDiagnostic> Warnings { get; }
}
