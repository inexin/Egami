namespace Egami.Formats.Jxl;

/// <summary>
/// A JPEG XL file's headers: the boxes, the image header, the ICC profile and the frame headers with their TOCs, read
/// without decoding any group.
/// </summary>
internal sealed class JxlFile
{
    private JxlFile(JxlContainer container, JxlImageHeader header)
    {
        Container = container;
        Header = header;
    }

    internal JxlContainer Container { get; }

    internal JxlImageHeader Header { get; }

    /// <summary>Gets the ICC profile the codestream carries, or <see langword="null"/> when its color encoding is enumerated.</summary>
    internal byte[]? Icc { get; private set; }

    /// <summary>Gets or sets the color space decoded frames are put in; set before decoding.</summary>
    internal JxlOutput? Output { get; set; }

    /// <summary>Gets or sets how many threads decoding may use.</summary>
    internal JxlParallel Parallel { get; set; } = JxlParallel.Single;

    /// <summary>Gets or sets the part of the image the caller reads (ReaderOptions.Region), or <see langword="null"/> for all of it.</summary>
    internal Rect? Region { get; set; }

    /// <summary>
    /// Gets the part of a still frame to decode for <see cref="Region"/>: the region grown by a border that covers LF smoothing, the
    /// restoration filters and a varblock's reach (design question 10), or <see langword="null"/> to decode the whole frame. Only a
    /// frame drawn alone at the image's size and scale, with no extra channels, can be cut this way.
    /// </summary>
    internal (int X0, int Y0, int X1, int Y1)? Needed(JxlFrameHeader f)
    {
        const int Border = 48;
        if (Region is not Rect r || f.Modular || f.HaveCrop || f.Upsampling != 1 || f.Type != JxlFrameType.Regular || Header.ExtraChannels.Count > 0
            || f.Width != Header.Width || f.Height != Header.Height)
        {
            return null;
        }

        return (Math.Max(0, r.X - Border), Math.Max(0, r.Y - Border), r.X + r.Width + Border, r.Y + r.Height + Border);
    }

    /// <summary>Gets the preview frame's header, or <see langword="null"/>.</summary>
    internal JxlFrameHeader? PreviewFrame { get; private set; }

    /// <summary>Gets every frame's header in codestream order, LF and reference-only frames included.</summary>
    internal List<JxlFrameHeader> Frames { get; } = [];

    /// <summary>Gets each frame's TOC, matching <see cref="Frames"/>.</summary>
    internal List<JxlToc> Tocs { get; } = [];

    /// <summary>Gets where each frame's sections start in the codestream, matching <see cref="Frames"/>.</summary>
    internal List<long> SectionOffsets { get; } = [];

    /// <summary>Gets the preview frame's TOC and where its sections start, when there is a preview.</summary>
    internal (JxlToc Toc, long Offset)? PreviewSections { get; private set; }

    /// <summary>Gets whether <see cref="Frames"/> holds every frame up to the last.</summary>
    internal bool FramesKnown { get; private set; }

    /// <summary>Gets the number of frames shown once blended (key frames), or <see langword="null"/> when not every frame is known.</summary>
    internal int? DisplayedFrames => FramesKnown ? Math.Max(1, Frames.Count(f => f.IsKeyFrame)) : null;

    /// <summary>Reads the headers, then walks the frames and, with <paramref name="allBoxes"/>, the boxes after the codestream.</summary>
    internal static JxlFile Read(EgamiInput input, bool allBoxes)
    {
        JxlContainer container = JxlContainer.Open(input);
        JxlBitReader r = new(container);
        JxlFile file = new(container, JxlImageHeader.Read(r, input.Limits));
        file.WalkFrames(r, input);
        if (allBoxes)
        {
            container.Finish();
        }

        return file;
    }

    private void WalkFrames(JxlBitReader r, EgamiInput input)
    {
        if (Header.Color.WantIcc)
        {
            Icc = JxlIcc.Read(r, input.Limits);
        }

        r.ZeroPadToByte();
        if (Header.Preview is not null)
        {
            JxlFrameHeader preview = JxlFrameHeader.Read(r, Header, preview: true);
            JxlToc toc = JxlToc.Read(r, preview);
            PreviewFrame = preview;
            PreviewSections = (toc, r.BitPosition / 8);
            r.SkipBytes(toc.TotalSize);
        }

        while (true)
        {
            input.CancellationToken.ThrowIfCancellationRequested();
            JxlFrameHeader frame = JxlFrameHeader.Read(r, Header, preview: false);
            JxlToc toc = JxlToc.Read(r, frame);
            Frames.Add(frame);
            input.CheckFrameCount(Frames.Count);
            Tocs.Add(toc);
            SectionOffsets.Add(r.BitPosition / 8);
            r.SkipBytes(toc.TotalSize);
            if (frame.IsLast)
            {
                FramesKnown = true;
                return;
            }
        }
    }
}
