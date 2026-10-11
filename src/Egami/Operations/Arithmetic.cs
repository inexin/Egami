namespace Egami.Operations;

/// <summary>An arithmetic operation with a constant, for <see cref="RasterOps.Arithmetic"/>: ImageMagick's -evaluate operators.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Arithmetic.cs" region="arithmetic"/>
/// </example>
public enum PixelMath
{
    /// <summary>v + value.</summary>
    Add = 0,

    /// <summary>v − value.</summary>
    Subtract = 1,

    /// <summary>v · value.</summary>
    Multiply = 2,

    /// <summary>v / value; dividing by 0 divides by the smallest positive number instead.</summary>
    Divide = 3,

    /// <summary>v raised to the power value.</summary>
    Pow = 4,

    /// <summary>log(value · v + 1) / log(value + 1): brightens shadows, more strongly as value grows.</summary>
    Log = 5,

    /// <summary>The smaller of v and value.</summary>
    Min = 6,

    /// <summary>The larger of v and value.</summary>
    Max = 7,

    /// <summary>value, whatever v is.</summary>
    Set = 8,
}

/// <summary>How <see cref="Raster.Stack"/> combines the same channel of the same pixel across rasters.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Arithmetic.cs" region="stack"/>
/// </example>
public enum StackMode
{
    /// <summary>The mean: averages exposures and smooths noise.</summary>
    Mean = 0,

    /// <summary>The median: removes things that appear in only a few of the rasters, such as passers-by.</summary>
    Median = 1,

    /// <summary>The smallest value.</summary>
    Min = 2,

    /// <summary>The largest value: star trails, light painting.</summary>
    Max = 3,
}
