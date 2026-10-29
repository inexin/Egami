using System.Buffers.Binary;

using Egami.Drawing;
using Egami.GdiPlus;

namespace Egami.SystemDrawing.Drawing2D;

/// <summary>A graphics state <see cref="Graphics.Save"/> returned, for <see cref="Graphics.Restore(GraphicsState)"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="transforms"/>
/// </example>
public sealed class GraphicsState : MarshalByRefObject
{
    internal GraphicsState(int id)
    {
        Id = id;
    }

    internal int Id { get; }
}

/// <summary>A container <see cref="Graphics.BeginContainer()"/> returned, for <see cref="Graphics.EndContainer(GraphicsContainer)"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="transforms"/>
/// </example>
public sealed class GraphicsContainer : MarshalByRefObject
{
    internal GraphicsContainer(int id)
    {
        Id = id;
    }

    internal int Id { get; }
}

/// <summary>A region in the EMF+ region format ([MS-EMFPLUS] 2.2.1.8), after GDI+'s size and checksum, from <see cref="Region.GetRegionData"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="clipping"/>
/// </example>
public sealed class RegionData
{
    /// <summary>The EMF+ object version.</summary>
    internal const uint Version = GdiPlusData.Version;

    internal RegionData(byte[] data)
    {
        Data = data;
    }

    /// <summary>Gets or sets the bytes.</summary>
    public byte[] Data { get; set; }

    /// <summary>
    /// The bytes for a region: the size of what follows the first 8 bytes, a checksum (0 here; GDI+
    /// computes its own), the version, the node count, then one node: a rectangle, a path, empty or infinite.
    /// </summary>
    internal static byte[] Write(Path? shape)
    {
        byte[] node;
        if (shape is null || shape.IsEmpty)
        {
            node = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(node, shape is null ? 0x10000003u : 0x10000002u);
        }
        else if (IsRect(shape, out RectF r))
        {
            node = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(node, 0x10000000);
            BinaryPrimitives.WriteSingleLittleEndian(node.AsSpan(4), r.X);
            BinaryPrimitives.WriteSingleLittleEndian(node.AsSpan(8), r.Y);
            BinaryPrimitives.WriteSingleLittleEndian(node.AsSpan(12), r.Width);
            BinaryPrimitives.WriteSingleLittleEndian(node.AsSpan(16), r.Height);
        }
        else
        {
            byte[] path = GdiPlusData.WritePath(shape);
            node = new byte[8 + path.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(node, 0x10000001);
            BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(4), path.Length);
            path.CopyTo(node.AsSpan(8));
        }

        byte[] data = new byte[16 + node.Length];
        BinaryPrimitives.WriteInt32LittleEndian(data, data.Length - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), Version);
        node.CopyTo(data.AsSpan(16));
        return data;
    }

    internal static bool IsRect(Path path, out RectF rect)
    {
        rect = path.Bounds;
        ReadOnlySpan<System.Numerics.Vector2> p = path.Points;
        if (path.Verbs.Count(PathVerb.Line) != 3 || path.Verbs.Count(PathVerb.Move) != 1 || p.Length != 4)
        {
            return false;
        }

        foreach (System.Numerics.Vector2 v in p)
        {
            if ((v.X != rect.X && v.X != rect.Right) || (v.Y != rect.Y && v.Y != rect.Bottom))
            {
                return false;
            }
        }

        return true;
    }
}
