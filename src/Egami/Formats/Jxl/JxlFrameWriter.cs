using System.Text;

namespace Egami.Formats.Jxl;

/// <summary>
/// Where and how one coded frame goes on the canvas (F.2): its crop, its blending for color and each extra channel, its
/// duration, whether it is the last frame, the reference slot it is saved in and its name. The default is a still image's
/// frame: the whole canvas, replaced, last.
/// </summary>
internal sealed record JxlFrameInfo
{
    /// <summary>Gets a still image's frame.</summary>
    internal static JxlFrameInfo Still { get; } = new();

    /// <summary>Gets whether the frame has its own place and size on the canvas (its size is the coded image's).</summary>
    internal bool Crop { get; init; }

    internal int X0 { get; init; }

    internal int Y0 { get; init; }

    /// <summary>Gets how the color channels blend into the canvas.</summary>
    internal JxlBlending Blending { get; init; }

    /// <summary>Gets each extra channel's blending, or <see langword="null"/> to blend each one as <see cref="Blending"/>.</summary>
    internal JxlBlending[]? ExtraBlending { get; init; }

    /// <summary>Gets the duration in ticks; written only in an animation.</summary>
    internal uint Duration { get; init; }

    internal bool IsLast { get; init; } = true;

    /// <summary>Gets the reference slot (0 to 3) the canvas is saved in after this frame, when it is not the last.</summary>
    internal int SaveAsReference { get; init; }

    internal string Name { get; init; } = string.Empty;
}

/// <summary>Writes the parts of headers that animations and layers add: the animation header and a frame's place, blending, duration and name.</summary>
internal static class JxlFrameWriter
{
    /// <summary>The longest name a frame header holds, in UTF-8 bytes.</summary>
    internal const int MaxNameBytes = 1071;

    private static readonly JxlU32 Mode = new(0, 0, 1, 0, 2, 0, 3, 2);
    private static readonly JxlU32 Slot = new(0, 0, 1, 0, 2, 0, 3, 0);

    /// <summary>Writes the AnimationHeader (D.3.8): ticks per second as a fraction, the loop count (0 loops forever), no timecodes.</summary>
    internal static void WriteAnimation(JxlBitWriter w, JxlAnimation animation)
    {
        w.WriteU32(new JxlU32(100, 0, 1000, 0, 1, 10, 1, 30), animation.TicksNumerator);
        w.WriteU32(new JxlU32(1, 0, 1001, 0, 1, 8, 1, 10), animation.TicksDenominator);
        w.WriteU32(new JxlU32(0, 0, 0, 3, 0, 16, 0, 32), animation.Loops);
        w.WriteBool(false);
    }

    /// <summary>
    /// Writes a regular frame's fields from have_crop to the name (F.2): the crop of a <paramref name="width"/> by
    /// <paramref name="height"/> frame on a <paramref name="canvasWidth"/> by <paramref name="canvasHeight"/> canvas, the
    /// blending of color and <paramref name="extras"/> extra channels, the duration when <paramref name="animated"/>, is_last,
    /// the reference slot, and the name.
    /// </summary>
    internal static void WriteLayout(JxlBitWriter w, JxlFrameInfo f, int width, int height, int canvasWidth, int canvasHeight, int extras, bool animated)
    {
        w.WriteBool(f.Crop);
        bool partial = false;
        if (f.Crop)
        {
            JxlU32 d = new(0, 8, 256, 11, 2304, 14, 18688, 30);
            w.WriteU32(d, JxlTreeLearner.PackSigned(f.X0));
            w.WriteU32(d, JxlTreeLearner.PackSigned(f.Y0));
            w.WriteU32(d, (uint)width);
            w.WriteU32(d, (uint)height);
            partial = f.X0 > 0 || f.Y0 > 0 || (long)width + f.X0 < canvasWidth || (long)height + f.Y0 < canvasHeight;
        }

        WriteBlending(w, f.Blending, extras, partial);
        bool replaceAll = f.Blending.Mode == JxlBlendMode.Replace;
        for (int i = 0; i < extras; i++)
        {
            JxlBlending b = f.ExtraBlending?[i] ?? f.Blending;
            WriteBlending(w, b, extras, partial);
            replaceAll &= b.Mode == JxlBlendMode.Replace;
        }

        if (animated)
        {
            w.WriteU32(new JxlU32(0, 0, 1, 0, 0, 8, 0, 32), f.Duration);
        }

        w.WriteBool(f.IsLast);
        if (!f.IsLast)
        {
            w.WriteU32(Slot, (uint)f.SaveAsReference);
            bool referenced = f.Duration == 0 || f.SaveAsReference != 0;
            if (referenced && replaceAll && !partial)
            {
                w.WriteBool(false); // saved after the color transform
            }
        }

        WriteName(w, f.Name);
    }

    /// <summary>Writes a name as its UTF-8 length and bytes.</summary>
    internal static void WriteName(JxlBitWriter w, string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name);
        if (bytes.Length > MaxNameBytes)
        {
            throw new ArgumentException($"A JPEG XL frame name holds {MaxNameBytes} UTF-8 bytes; this one has {bytes.Length}.", nameof(name));
        }

        w.WriteU32(new JxlU32(0, 0, 0, 4, 16, 5, 48, 10), (uint)bytes.Length);
        foreach (byte b in bytes)
        {
            w.Write(b, 8);
        }
    }

    /// <summary>Writes a BlendingInfo (F.2): the mode, the alpha channel and clamp when the mode uses them, and the source slot.</summary>
    private static void WriteBlending(JxlBitWriter w, JxlBlending b, int extras, bool partial)
    {
        w.WriteU32(Mode, (uint)b.Mode);
        bool usesAlpha = extras > 0 && b.Mode is JxlBlendMode.Blend or JxlBlendMode.MulAdd;
        if (usesAlpha)
        {
            w.WriteU32(new JxlU32(0, 0, 1, 0, 2, 0, 3, 3), b.AlphaChannel);
        }

        if (usesAlpha || b.Mode == JxlBlendMode.Mul)
        {
            w.WriteBool(b.Clamp);
        }

        if (b.Mode != JxlBlendMode.Replace || partial)
        {
            w.WriteU32(Slot, b.Source);
        }
    }
}
