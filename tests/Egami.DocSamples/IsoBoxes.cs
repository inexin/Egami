using Egami.Formats.Iso;

namespace Egami.DocSamples;

[Collection("Samples")]
public class IsoBoxes
{
    [Fact]
    public void Walk()
    {
        using SampleFiles _ = new();
        byte[] avif = File.ReadAllBytes("duck.avif");
        List<string> found = [];

        #region walk
        // List an AVIF, HEIC or MP4 file's top-level boxes and the boxes inside meta.
        IsoBoxReader top = new(avif, "AVIF");
        while (top.Next(out IsoBox box))
        {
            found.Add($"{box.Type} {box.Size}");
            if (box.Type == FourCC.Parse("meta"))
            {
                // meta is a full box: its version and flags (4 bytes) come before its children.
                IsoBoxReader children = top.Children(box, skip: 4);
                while (children.Next(out IsoBox child))
                {
                    found.Add($"  {child.Type} at {child.Start}");
                }
            }
        }
        #endregion

        Assert.StartsWith("ftyp ", found[0]);
        Assert.Contains(found, line => line.StartsWith("  iloc", StringComparison.Ordinal));
    }

    [Fact]
    public void Stream()
    {
        using SampleFiles _ = new();
        List<FourCC> types = [];

        #region stream
        // Walk a large file's top-level boxes without reading it into memory, then load only moov.
        using FileStream stream = File.OpenRead("duck.avif");
        byte[] header = new byte[IsoBoxReader.MaxHeaderSize];
        for (long at = 0; at < stream.Length;)
        {
            stream.Position = at;
            int read = stream.ReadAtLeast(header, (int)Math.Min(header.Length, stream.Length - at), throwOnEndOfStream: false);
            IsoBox box = IsoBoxReader.ReadHeader(header.AsSpan(0, read), at, stream.Length, "MP4");
            types.Add(box.Type);
            if (box.Type == FourCC.Parse("moov"))
            {
                byte[] moov = new byte[box.ContentLength];
                stream.Position = box.ContentStart;
                stream.ReadExactly(moov);
                IsoBoxReader inside = new(moov, "MP4", offset: box.ContentStart); // the boxes inside report file offsets
                while (inside.Next(out IsoBox track))
                {
                    types.Add(track.Type);
                }
            }

            at = box.End;
        }
        #endregion

        Assert.Equal(FourCC.Parse("ftyp"), types[0]);
    }
}
