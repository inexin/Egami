using System.Text;

using Egami.Compression;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Compression
{
    [Fact]
    public void ZstandardSample()
    {
        byte[] data = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("a row of texels, ", 200)));

        #region zstandard
        // Compress into a buffer sized for the worst case, then keep the bytes written.
        byte[] packed = new byte[Zstandard.MaxCompressedLength(data.Length)];
        int written = Zstandard.Compress(data, packed, level: 9);

        // Decompress into a buffer of the size you know (KTX 2 stores each level's): it returns the bytes written.
        byte[] unpacked = new byte[data.Length];
        int length = Zstandard.Decompress(packed.AsSpan(0, written), unpacked);
        #endregion

        Assert.True(written < data.Length / 10);
        Assert.Equal(data, unpacked.AsSpan(0, length).ToArray());
    }
}
