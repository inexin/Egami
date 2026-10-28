using System.Buffers.Binary;
using System.IO.Compression;

using Egami.SystemDrawing.Imaging;

namespace Egami.SystemDrawing.Internal;

/// <summary>One metafile record as GDI+ enumerates it: its type, flags, and where its data lies in the file.</summary>
internal readonly record struct MetafileRecord(EmfPlusRecordType Type, int Flags, int Offset, int Size);

/// <summary>
/// A metafile's header facts and its records, read from the bytes as GDI+ reports them ([MS-WMF], [MS-EMF], [MS-EMFPLUS]): a
/// placeable WMF's records without its header and end record, an EMF's records from header to end, and for EMF+ the EMF header,
/// the EMF+ records and the EMF end record.
/// </summary>
internal sealed class MetafileScan
{
    private const uint PlaceableKey = 0x9AC6CDD7;
    private const uint EmfSignature = 0x464D4520;
    private const uint EmfPlusComment = 0x2B464D45;

    private MetafileScan(byte[] data)
    {
        Data = data;
    }

    /// <summary>Gets the file's bytes, inflated when it was gzipped.</summary>
    public byte[] Data { get; }

    public MetafileType Type { get; private set; }

    public int Version { get; private set; }

    /// <summary>Gets the frame in pixels at <see cref="Dpi"/>, unrounded, as <c>GetBounds</c> reports it.</summary>
    public RectangleF Frame { get; private set; }

    public (float X, float Y) Dpi { get; private set; }

    public (int X, int Y) LogicalDpi { get; private set; }

    public int Size { get; private set; }

    public int EmfPlusHeaderSize { get; private set; }

    public bool Display { get; private set; }

    public MetaHeader? WmfHeader { get; private set; }

    public WmfPlaceableFileHeader? Placeable { get; private set; }

    public List<MetafileRecord> Records { get; } = [];

    /// <summary>Gets the frame rounded to whole pixels, as the header's <c>Bounds</c>.</summary>
    public Rectangle Bounds => new((int)MathF.Round(Frame.X), (int)MathF.Round(Frame.Y), (int)MathF.Round(Frame.Width), (int)MathF.Round(Frame.Height));

    /// <summary>Whether the bytes start like a metafile.</summary>
    public static bool IsMetafile(ReadOnlySpan<byte> d) =>
        (d.Length >= 4 && BinaryPrimitives.ReadUInt32LittleEndian(d) == PlaceableKey)
        || (d.Length >= 44 && BinaryPrimitives.ReadUInt32LittleEndian(d) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(d[40..]) == EmfSignature)
        || (d.Length >= 18 && BinaryPrimitives.ReadUInt16LittleEndian(d) is 1 or 2 && BinaryPrimitives.ReadUInt16LittleEndian(d[2..]) == 9 && BinaryPrimitives.ReadUInt16LittleEndian(d[4..]) is 0x100 or 0x300)
        || (d.Length >= 2 && d[0] == 0x1F && d[1] == 0x8B);

    /// <summary>Reads a metafile's header and records.</summary>
    /// <exception cref="System.Runtime.InteropServices.ExternalException">Not a metafile GDI+ reads, as GDI+ reports it.</exception>
    public static MetafileScan Read(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0x1F && data[1] == 0x8B)
        {
            using MemoryStream inflated = new();
            using (GZipStream zip = new(new MemoryStream(data), CompressionMode.Decompress))
            {
                zip.CopyTo(inflated);
            }

            data = inflated.ToArray();
        }

        MetafileScan scan = new(data);
        try
        {
            if (data.Length >= 22 && BinaryPrimitives.ReadUInt32LittleEndian(data) == PlaceableKey)
            {
                scan.ReadWmf(22, placeable: true);
            }
            else if (data.Length >= 88 && BinaryPrimitives.ReadUInt32LittleEndian(data) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(40)) == EmfSignature)
            {
                scan.ReadEmf();
            }
            else if (data.Length >= 18)
            {
                scan.ReadWmf(0, placeable: false);
            }
            else
            {
                throw GdiErrors.Generic();
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            throw GdiErrors.Generic();
        }

        return scan;
    }

    private static short S16(byte[] d, int at) => BinaryPrimitives.ReadInt16LittleEndian(d.AsSpan(at));

    private static ushort U16(byte[] d, int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));

    private static int I32(byte[] d, int at) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(at));

    private void ReadWmf(int at, bool placeable)
    {
        byte[] d = Data;
        int inch = 1440;
        if (placeable)
        {
            Placeable = new WmfPlaceableFileHeader
            {
                Key = I32(d, 0),
                Hmf = S16(d, 4),
                BboxLeft = S16(d, 6),
                BboxTop = S16(d, 8),
                BboxRight = S16(d, 10),
                BboxBottom = S16(d, 12),
                Inch = S16(d, 14),
                Reserved = I32(d, 16),
                Checksum = S16(d, 20),
            };
            inch = Math.Max(1, (int)U16(d, 14));
        }

        if (U16(d, at + 2) != 9)
        {
            throw GdiErrors.Generic();
        }

        WmfHeader = new MetaHeader
        {
            Type = S16(d, at),
            HeaderSize = S16(d, at + 2),
            Version = S16(d, at + 4),
            Size = I32(d, at + 6),
            NoObjects = S16(d, at + 10),
            MaxRecord = I32(d, at + 12),
            NoParameters = S16(d, at + 16),
        };
        Type = placeable ? MetafileType.WmfPlaceable : MetafileType.Wmf;
        Version = U16(d, at + 4);
        Size = WmfHeader.Size * 2;
        Dpi = (96, 96);
        if (Placeable is { } p)
        {
            // A placeable file's box is in its own units, Inch of them to the inch, shown at 96 pixels to the inch.
            Frame = new RectangleF(p.BboxLeft * 96f / inch, p.BboxTop * 96f / inch, (p.BboxRight - p.BboxLeft) * 96f / inch, (p.BboxBottom - p.BboxTop) * 96f / inch);
        }

        int pos = at + 18;
        (short X, short Y)? extent = null;
        while (pos + 6 <= d.Length)
        {
            int words = I32(d, pos);
            int function = U16(d, pos + 4);
            if (words < 3 || function == 0)
            {
                break;
            }

            if (function == 0x20C && extent is null)
            {
                extent = (S16(d, pos + 8), S16(d, pos + 6));
            }

            Records.Add(new MetafileRecord((EmfPlusRecordType)(0x10000 | function), 0, pos + 6, (words * 2) - 6));
            pos += words * 2;
        }

        if (Placeable is null)
        {
            // A plain WMF has no size of its own; its first window extent stands in, in pixels.
            (short x, short y) = extent ?? (0, 0);
            Frame = new RectangleF(0, 0, Math.Abs((int)x), Math.Abs((int)y));
        }
    }

    private void ReadEmf()
    {
        byte[] d = Data;
        int frameLeft = I32(d, 24), frameTop = I32(d, 28), frameRight = I32(d, 32), frameBottom = I32(d, 36);
        Version = I32(d, 44);
        Size = I32(d, 48);
        int devX = I32(d, 72), devY = I32(d, 76), mmX = I32(d, 80), mmY = I32(d, 84);
        float dpiX = mmX > 0 ? devX * 25.4f / mmX : 96, dpiY = mmY > 0 ? devY * 25.4f / mmY : 96;
        Dpi = (dpiX, dpiY);
        // The frame is in hundredths of a millimeter; GDI+ counts its pixels with the last one included, so one more.
        Frame = new RectangleF(frameLeft * dpiX / 2540, frameTop * dpiY / 2540, ((frameRight - frameLeft) * dpiX / 2540) + 1, ((frameBottom - frameTop) * dpiY / 2540) + 1);
        Type = MetafileType.Emf;
        int pos = 0;
        List<MetafileRecord> plus = [];
        MetafileRecord? header = null, end = null;
        List<MetafileRecord> emf = [];
        bool dual = false;
        while (pos + 8 <= d.Length)
        {
            int type = I32(d, pos), size = I32(d, pos + 4);
            if (size < 8 || pos + size > d.Length)
            {
                break;
            }

            MetafileRecord record = new((EmfPlusRecordType)type, 0, pos + 8, size - 8);
            if (type == 1)
            {
                header = record;
            }
            else if (type == 14)
            {
                end = record;
            }

            if (type == 70 && size >= 16 && (uint)I32(d, pos + 12) == EmfPlusComment)
            {
                ReadPlus(pos + 16, pos + 12 + I32(d, pos + 8), plus, ref dual);
            }
            else
            {
                emf.Add(record);
            }

            if (type == 14)
            {
                break;
            }

            pos += size;
        }

        if (plus.Count > 0)
        {
            Type = dual ? MetafileType.EmfPlusDual : MetafileType.EmfPlusOnly;
            if (header is MetafileRecord h)
            {
                Records.Add(h);
            }

            Records.AddRange(plus);
            if (end is MetafileRecord e)
            {
                Records.Add(e);
            }
        }
        else
        {
            Records.AddRange(emf);
        }
    }

    private void ReadPlus(int at, int stop, List<MetafileRecord> records, ref bool dual)
    {
        byte[] d = Data;
        while (at + 12 <= stop && at + 12 <= d.Length)
        {
            int type = U16(d, at), flags = U16(d, at + 2), size = I32(d, at + 4), dataSize = I32(d, at + 8);
            if (size < 12 || at + size > d.Length)
            {
                break;
            }

            if (type == 0x4001 && dataSize >= 16)
            {
                Version = I32(d, at + 12);
                Display = (I32(d, at + 16) & 1) != 0;
                LogicalDpi = (I32(d, at + 20), I32(d, at + 24));
                EmfPlusHeaderSize = size;
                dual = (flags & 1) != 0;
            }

            records.Add(new MetafileRecord((EmfPlusRecordType)type, flags, at + 12, dataSize));
            at += size;
        }
    }
}
