namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// A WebM VP9 track's CodecPrivate (the WebM codec mapping's VP9 feature metadata): records of an id byte, a length byte and the
/// value, with id 1 the profile, 2 the level, 3 the bit depth and 4 the chroma subsampling (0 and 1 for 4:2:0 with chroma left of
/// or on the first luma sample, 2 for 4:2:2, 3 for 4:4:4). Every record is optional; unknown ids are skipped.
/// </summary>
internal readonly record struct Vp9CodecPrivate(int? Profile, int? Level, int? BitDepth, int? ChromaSubsampling)
{
    /// <summary>Reads the records, or returns null when there are none or they are cut off.</summary>
    public static Vp9CodecPrivate? Parse(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return null;
        }

        int? profile = null, level = null, bitDepth = null, subsampling = null;
        int at = 0;
        while (at + 2 <= data.Length)
        {
            int id = data[at], length = data[at + 1];
            at += 2;
            if (at + length > data.Length)
            {
                return null;
            }

            if (length == 1)
            {
                int value = data[at];
                switch (id)
                {
                    case 1:
                        profile = value;
                        break;
                    case 2:
                        level = value;
                        break;
                    case 3:
                        bitDepth = value;
                        break;
                    case 4:
                        subsampling = value;
                        break;
                }
            }

            at += length;
        }

        return new Vp9CodecPrivate(profile, level, bitDepth, subsampling);
    }

    /// <summary>The CodecPrivate chroma subsampling class of a header's subsampling, 4:2:0 counted as 1 (both 4:2:0 values compare equal through this).</summary>
    public static int SubsamplingOf(int subsamplingX, int subsamplingY) => subsamplingX == 0 ? 3 : subsamplingY == 0 ? 2 : 1;
}
