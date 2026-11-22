using Egami.Heic.Hevc;

namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>
/// What the CTU searches of one P or B picture share: the slice's reference picture lists, each reference's search planes,
/// the inter profile, and the skip statistics of the CTBs decided so far, which later CTBs read (rows run on several threads,
/// but a CTB reads only those to its left and above, which the wavefront order has finished).
/// </summary>
internal sealed class HevcInterContext
{
    /// <summary>The coding quadtree depths the statistics keep.</summary>
    internal const int Depths = 4;

    internal HevcInterContext(HevcSliceRefs refs, HevcReference[] references, HevcReference[] references1, HevcInterProfile profile, int ctbs)
    {
        Refs = refs;
        References = references;
        References1 = references1;
        Profile = profile;
        SkipCost = new double[ctbs * Depths];
        SkipCount = new int[ctbs * Depths];
    }

    /// <summary>Gets the slice's reference picture lists.</summary>
    internal HevcSliceRefs Refs { get; }

    /// <summary>Gets the search planes of each list 0 reference, by refIdxL0.</summary>
    internal HevcReference[] References { get; }

    /// <summary>Gets the search planes of each list 1 reference, by refIdxL1 (none in a P picture).</summary>
    internal HevcReference[] References1 { get; }

    /// <summary>Gets whether the picture is a B picture.</summary>
    internal bool B => References1.Length > 0;

    /// <summary>
    /// Gets the last P or I picture coded, whose vectors (scaled to each list's distance) start a B picture's motion search, or
    /// null.
    /// </summary>
    internal HevcReference? Seeds { get; init; }

    /// <summary>Gets whether later pictures predict from this one.</summary>
    internal bool Referenced { get; init; } = true;

    /// <summary>Gets or sets the source luma as bytes when it has 8 bits (made by the first CTU search that needs it), else null.</summary>
    internal byte[]? Source8 { get; set; }

    /// <summary>Gets the inter profile.</summary>
    internal HevcInterProfile Profile { get; }

    /// <summary>Gets the summed RD cost of the skipped coding units of each CTB and depth.</summary>
    internal double[] SkipCost { get; }

    /// <summary>Gets the number of skipped coding units of each CTB and depth.</summary>
    internal int[] SkipCount { get; }
}
