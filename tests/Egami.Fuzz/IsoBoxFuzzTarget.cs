using Egami.Formats.Iso;

namespace Egami.Fuzz;

/// <summary>
/// The isobox fuzz target (Phase 2.9.8): walks every box of the input as a tree, entering each box once as a plain container
/// and once past a full box's version and flags, and reads each header again from the stream side with
/// <see cref="IsoBoxReader.ReadHeader"/>, which must agree with the walker. Only CorruptDataException may escape the walk.
/// </summary>
internal static class IsoBoxFuzzTarget
{
    public static void Walk(byte[] data)
    {
        try
        {
            Walk(new IsoBoxReader(data, "Fuzz", offset: 100), data, 0);
        }
        catch (CorruptDataException)
        {
        }
    }

    private static void Walk(IsoBoxReader reader, byte[] data, int depth)
    {
        while (reader.Next(out IsoBox box))
        {
            if (box.Start < 100 || box.ContentStart > box.End || box.End > 100 + data.Length)
            {
                throw new InvalidOperationException($"The box {box} lies outside its data.");
            }

            if (reader.Content(box).Length != box.ContentLength)
            {
                throw new InvalidOperationException($"The contents of {box} have another length.");
            }

            ReadOnlySpan<byte> head = data.AsSpan((int)(box.Start - 100), (int)Math.Min(IsoBoxReader.MaxHeaderSize, box.Size));
            IsoBox again = IsoBoxReader.ReadHeader(head, box.Start, box.End, "Fuzz");
            if (again != box)
            {
                throw new InvalidOperationException($"ReadHeader gives {again} for {box}.");
            }

            if (depth < 8)
            {
                foreach (int skip in (ReadOnlySpan<int>)[0, 4])
                {
                    try
                    {
                        Walk(reader.Children(box, skip), data, depth + 1);
                    }
                    catch (CorruptDataException)
                    {
                    }
                }
            }
        }
    }
}
