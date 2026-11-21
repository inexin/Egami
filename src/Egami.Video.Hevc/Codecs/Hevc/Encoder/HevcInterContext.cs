using Egami.Heic.Hevc;

namespace Egami.Video.Codecs.Hevc.Encoder;

/// <summary>
/// What the CTU searches of one P picture share: the slice's reference picture lists, each list 0 reference's search planes,
/// the inter profile, and the skip statistics of the CTBs decided so far, which later CTBs read (rows run on several threads,
/// but a CTB reads only those to its left and above, which the wavefront order has finished).
/// </summary>
internal sealed class HevcInterContext
{
    /// <summary>The coding quadtree depths the statistics keep.</summary>
    internal const int Depths = 4;

    internal HevcInterContext(HevcSliceRefs refs, HevcReference[] references, HevcInterProfile profile, int ctbs)
    {
        Refs = refs;
        References = references;
        Profile = profile;
        SkipCost = new double[ctbs * Depths];
        SkipCount = new int[ctbs * Depths];
    }

    /// <summary>Gets the slice's reference picture lists.</summary>
    internal HevcSliceRefs Refs { get; }

    /// <summary>Gets the search planes of each list 0 reference, by refIdxL0.</summary>
    internal HevcReference[] References { get; }

    /// <summary>Gets the inter profile.</summary>
    internal HevcInterProfile Profile { get; }

    /// <summary>Gets the summed RD cost of the skipped coding units of each CTB and depth.</summary>
    internal double[] SkipCost { get; }

    /// <summary>Gets the number of skipped coding units of each CTB and depth.</summary>
    internal int[] SkipCount { get; }
}
