namespace Egami.Operations;

/// <summary>
/// Records a chain of operations for <see cref="Raster{TPixel}.Apply"/> and <see cref="Raster{TPixel}.Derive"/>.
/// Each method adds one step and returns this builder, so steps chain. Arguments are checked as steps are added, before any
/// pixel changes.
/// </summary>
/// <example>
/// <code>
/// raster.Apply(ops => ops.Add(new MyOperation()));
/// </code>
/// </example>
public sealed class RasterOps
{
    private readonly List<IRasterOperation> _operations = [];

    internal RasterOps()
    {
    }

    /// <summary>Gets the recorded steps, in order.</summary>
    internal IReadOnlyList<IRasterOperation> Operations => _operations;

    /// <summary>Adds a step, such as a custom <see cref="IRasterOperation"/>.</summary>
    /// <param name="operation">The operation.</param>
    /// <returns>This builder.</returns>
    public RasterOps Add(IRasterOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        _operations.Add(operation);
        return this;
    }
}
