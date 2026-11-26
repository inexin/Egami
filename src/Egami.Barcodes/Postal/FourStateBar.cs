namespace Egami.Barcodes.Postal;

/// <summary>
/// The four bars of a 4-state postal symbol: every bar crosses the middle third (the tracker); an ascender also reaches up, a
/// descender down, and a full bar both ways.
/// </summary>
internal enum FourStateBar : byte
{
    /// <summary>The tracker only, the shortest bar.</summary>
    Tracker,

    /// <summary>The tracker and the top third.</summary>
    Ascender,

    /// <summary>The tracker and the bottom third.</summary>
    Descender,

    /// <summary>All three thirds.</summary>
    Full,
}
