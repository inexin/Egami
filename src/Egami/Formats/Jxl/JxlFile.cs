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

    /// <summary>Gets the preview frame's header, or <see langword="null"/>.</summary>
    internal JxlFrameHeader? PreviewFrame { get; private set; }

    /// <summary>Gets every frame's header in codestream order, LF and reference-only frames included.</summary>
    internal List<JxlFrameHeader> Frames { get; } = [];

    /// <summary>Gets each frame's TOC, matching <see cref="Frames"/>.</summary>
    internal List<JxlToc> Tocs { get; } = [];

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
            r.SkipBytes(toc.TotalSize);
            if (frame.IsLast)
            {
                FramesKnown = true;
                return;
            }
        }
    }
}
