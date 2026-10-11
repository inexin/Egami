namespace Egami.Operations;

/// <summary>Which neighbours a flood fill spreads to.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Keying.cs" region="flood-fill"/>
/// </example>
public enum Connectivity
{
    /// <summary>The four side neighbours: left, right, up and down.</summary>
    Four = 0,

    /// <summary>All eight neighbours, the diagonals too.</summary>
    Eight = 1,
}
