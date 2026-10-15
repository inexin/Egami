namespace Egami.Fonts;

/// <summary>
/// The character collection a CID-keyed font's CIDs belong to (its CFF ROS operator), such as Adobe-Japan1-7; PDF's
/// CIDSystemInfo dictionary carries the same three values.
/// </summary>
/// <param name="Registry">The registry, such as "Adobe".</param>
/// <param name="Ordering">The ordering, such as "Japan1" or "Identity".</param>
/// <param name="Supplement">The supplement number.</param>
public sealed record CidSystemInfo(string Registry, string Ordering, int Supplement);
