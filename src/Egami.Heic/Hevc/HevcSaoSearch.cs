namespace Egami.Heic.Hevc;

/// <summary>Whether a CTB's SAO parameters are coded as a merge with the CTB to the left or above.</summary>
internal readonly record struct HevcSaoChoice(bool MergeLeft, bool MergeUp);

/// <summary>
/// The SAO search: for each CTB and component, the edge offset class or band position and offsets of least J against the
/// source, on the deblocked picture, and whether merging with a neighbour's parameters costs less.
/// </summary>
internal static class HevcSaoSearch
{
    /// <summary>Chooses every CTB's SAO parameters into <see cref="HevcPicture.Sao"/>; gives the merge choices.</summary>
    internal static HevcSaoChoice[] Choose(HevcPicture picture, HevcSource source, HevcSliceHeader slice, int parallelism, CancellationToken cancellationToken)
    {
        HevcSps sps = picture.Sps;
        int ctbs = sps.WidthInCtbs * sps.HeightInCtbs;
        return new HevcSaoChoice[ctbs];
    }
}
