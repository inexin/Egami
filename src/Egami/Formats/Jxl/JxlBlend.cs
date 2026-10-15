namespace Egami.Formats.Jxl;

/// <summary>A frame saved into a reference slot, with whether it was saved before the color transform and where it lay.</summary>
internal sealed record JxlReference(JxlPlanes Planes, bool BeforeColorTransform, int X0, int Y0);

/// <summary>
/// Blending of patches and frames (F.2, K.3): the extra channels first, against the alpha before blending, then the color
/// channels, whose alpha blending also sets the alpha channel. Ported from libjxl's blending and alpha code (see THIRD-PARTY-NOTICES).
/// </summary>
internal static class JxlBlend
{
    /// <summary>Gets scratch rows for <see cref="Row"/>.</summary>
    internal static float[][] Scratch(int channels, int width)
    {
        float[][] tmp = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            tmp[c] = new float[width];
        }

        return tmp;
    }

    /// <summary>Gets a frame blending mode as the patch mode libjxl blends it with.</summary>
    internal static JxlPatchBlending FromFrame(JxlBlending b) => new(
        b.Mode switch
        {
            JxlBlendMode.Replace => JxlPatchBlendMode.Replace,
            JxlBlendMode.Add => JxlPatchBlendMode.Add,
            JxlBlendMode.Blend => JxlPatchBlendMode.BlendAbove,
            JxlBlendMode.MulAdd => JxlPatchBlendMode.MulAddAbove,
            _ => JxlPatchBlendMode.Mul,
        },
        (int)Math.Min(b.AlphaChannel, int.MaxValue),
        b.Clamp);

    /// <summary>
    /// Blends <paramref name="n"/> samples of each channel of <paramref name="fg"/> (from <paramref name="fgAt"/>) onto
    /// <paramref name="bg"/> (from <paramref name="bgAt"/>), in place.
    /// </summary>
    internal static void Row(float[][] bg, int bgAt, float[][] fg, int fgAt, int n, JxlPatchBlending color, ReadOnlySpan<JxlPatchBlending> extras, JxlImageHeader image, float[][] tmp)
    {
        int count = extras.Length;
        bool hasAlpha = image.AlphaIndex >= 0;
        for (int i = 0; i < count; i++)
        {
            JxlPatchBlending b = extras[i];
            int c = 3 + i;
            int a = 3 + b.AlphaChannel;
            Span<float> o = tmp[c].AsSpan(0, n);
            switch (b.Mode)
            {
                case JxlPatchBlendMode.Add:
                    for (int x = 0; x < n; x++)
                    {
                        o[x] = bg[c][bgAt + x] + fg[c][fgAt + x];
                    }

                    break;
                case JxlPatchBlendMode.BlendAbove:
                    Alpha(bg[c], bg[a], bgAt, fg[c], fg[a], fgAt, o, c == a, Premultiplied(image, b), b.Clamp);
                    break;
                case JxlPatchBlendMode.BlendBelow:
                    Alpha(fg[c], fg[a], fgAt, bg[c], bg[a], bgAt, o, c == a, Premultiplied(image, b), b.Clamp);
                    break;
                case JxlPatchBlendMode.MulAddAbove:
                    WeightedAdd(bg[c], bgAt, fg[c], fg[a], fgAt, o, c == a, b.Clamp);
                    break;
                case JxlPatchBlendMode.MulAddBelow:
                    WeightedAdd(fg[c], fgAt, bg[c], bg[a], bgAt, o, c == a, b.Clamp);
                    break;
                case JxlPatchBlendMode.Mul:
                    Mul(bg[c], bgAt, fg[c], fgAt, o, b.Clamp);
                    break;
                case JxlPatchBlendMode.Replace:
                    fg[c].AsSpan(fgAt, n).CopyTo(o);
                    break;
                default:
                    bg[c].AsSpan(bgAt, n).CopyTo(o);
                    break;
            }
        }

        int alpha = 3 + color.AlphaChannel;
        switch (color.Mode)
        {
            case JxlPatchBlendMode.Add:
            case JxlPatchBlendMode.MulAddAbove or JxlPatchBlendMode.MulAddBelow when !hasAlpha:
                for (int c = 0; c < 3; c++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        tmp[c][x] = bg[c][bgAt + x] + fg[c][fgAt + x];
                    }
                }

                break;
            case JxlPatchBlendMode.MulAddAbove:
                for (int c = 0; c < 3; c++)
                {
                    WeightedAdd(bg[c], bgAt, fg[c], fg[alpha], fgAt, tmp[c].AsSpan(0, n), false, color.Clamp);
                }

                break;
            case JxlPatchBlendMode.MulAddBelow:
                for (int c = 0; c < 3; c++)
                {
                    WeightedAdd(fg[c], fgAt, bg[c], bg[alpha], bgAt, tmp[c].AsSpan(0, n), false, color.Clamp);
                }

                break;
            case JxlPatchBlendMode.BlendAbove when hasAlpha:
                AlphaColor(bg, bgAt, fg, fgAt, n, alpha, Premultiplied(image, color), color.Clamp, tmp);
                break;
            case JxlPatchBlendMode.BlendBelow when hasAlpha:
                AlphaColor(fg, fgAt, bg, bgAt, n, alpha, Premultiplied(image, color), color.Clamp, tmp);
                break;
            case JxlPatchBlendMode.Mul:
                for (int c = 0; c < 3; c++)
                {
                    Mul(bg[c], bgAt, fg[c], fgAt, tmp[c].AsSpan(0, n), color.Clamp);
                }

                break;
            case JxlPatchBlendMode.None:
                for (int c = 0; c < 3; c++)
                {
                    bg[c].AsSpan(bgAt, n).CopyTo(tmp[c]);
                }

                break;
            default:
                // Replace, and alpha blending without an alpha channel.
                for (int c = 0; c < 3; c++)
                {
                    fg[c].AsSpan(fgAt, n).CopyTo(tmp[c]);
                }

                break;
        }

        for (int c = 0; c < 3 + count; c++)
        {
            tmp[c].AsSpan(0, n).CopyTo(bg[c].AsSpan(bgAt, n));
        }
    }

    private static bool Premultiplied(JxlImageHeader image, JxlPatchBlending b) => b.AlphaChannel < image.ExtraChannels.Count && image.ExtraChannels[b.AlphaChannel].AlphaAssociated;

    private static float Clamp(float v, bool clamp) => clamp ? Math.Clamp(v, 0, 1) : v;

    /// <summary>Alpha-blends one channel of the top layer over the bottom one; the alpha channel itself when <paramref name="isAlpha"/>.</summary>
    private static void Alpha(float[] bottom, float[] bottomAlpha, int bAt, float[] top, float[] topAlpha, int tAt, Span<float> o, bool isAlpha, bool premultiplied, bool clamp)
    {
        for (int x = 0; x < o.Length; x++)
        {
            float fa = Clamp(topAlpha[tAt + x], clamp);
            float ba = bottomAlpha[bAt + x];
            if (isAlpha)
            {
                o[x] = 1 - ((1 - fa) * (1 - ba));
            }
            else if (premultiplied)
            {
                o[x] = top[tAt + x] + (bottom[bAt + x] * (1 - fa));
            }
            else
            {
                float na = 1 - ((1 - fa) * (1 - ba));
                float r = na > 0 ? 1 / na : 0;
                o[x] = ((top[tAt + x] * fa) + (bottom[bAt + x] * ba * (1 - fa))) * r;
            }
        }
    }

    /// <summary>Alpha-blends the color of the top layer over the bottom one, and sets the alpha channel to the blended alpha.</summary>
    private static void AlphaColor(float[][] bottom, int bAt, float[][] top, int tAt, int n, int alpha, bool premultiplied, bool clamp, float[][] tmp)
    {
        for (int c = 0; c < 3; c++)
        {
            Alpha(bottom[c], bottom[alpha], bAt, top[c], top[alpha], tAt, tmp[c].AsSpan(0, n), false, premultiplied, clamp);
        }

        Alpha(bottom[alpha], bottom[alpha], bAt, top[alpha], top[alpha], tAt, tmp[alpha].AsSpan(0, n), true, premultiplied, clamp);
    }

    /// <summary>Adds the top layer weighted by its alpha; the alpha channel itself keeps the bottom's.</summary>
    private static void WeightedAdd(float[] bottom, int bAt, float[] top, float[] topAlpha, int tAt, Span<float> o, bool isAlpha, bool clamp)
    {
        for (int x = 0; x < o.Length; x++)
        {
            o[x] = isAlpha ? bottom[bAt + x] : bottom[bAt + x] + (top[tAt + x] * Clamp(topAlpha[tAt + x], clamp));
        }
    }

    private static void Mul(float[] bottom, int bAt, float[] top, int tAt, Span<float> o, bool clamp)
    {
        for (int x = 0; x < o.Length; x++)
        {
            o[x] = bottom[bAt + x] * Clamp(top[tAt + x], clamp);
        }
    }

    /// <summary>
    /// Blends a frame (after its color transform) onto the canvas its blending sources give: the reference slots, or zeros where a
    /// slot is empty. Gets the whole image.
    /// </summary>
    internal static JxlPlanes Frame(JxlFrameHeader f, JxlPlanes frame, JxlReference?[] references, JxlImageHeader image)
    {
        int width = image.Width;
        int height = image.Height;
        int channels = frame.Planes.Length;
        float[][] canvas = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            uint slot = c < 3 ? f.Blending.Source : f.ExtraChannelBlending[c - 3].Source;
            canvas[c] = new float[(long)width * height];
            if (references[slot] is not JxlReference reference)
            {
                continue;
            }

            JxlPlanes source = reference.Planes;
            if (reference.BeforeColorTransform)
            {
                throw new CorruptDataException(JxlFormat.Info.Name, -1, "A frame blends onto a reference frame saved before the color transform.");
            }

            if (source.Width < width || source.Height < height || reference.X0 != 0 || reference.Y0 != 0)
            {
                throw new CorruptDataException(JxlFormat.Info.Name, -1, "A frame blends onto a cropped reference frame.");
            }

            for (int y = 0; y < height; y++)
            {
                source.Planes[c].AsSpan(y * source.Width, width).CopyTo(canvas[c].AsSpan(y * width));
            }
        }

        JxlPatchBlending color = FromFrame(f.Blending);
        JxlPatchBlending[] extras = [.. f.ExtraChannelBlending.Select(FromFrame)];
        for (int i = 0; i <= extras.Length; i++)
        {
            JxlPatchBlending b = i < extras.Length ? extras[i] : color;
            bool needsAlpha = b.Mode is JxlPatchBlendMode.BlendAbove or JxlPatchBlendMode.MulAddAbove && (i < extras.Length || image.AlphaIndex >= 0);
            if (needsAlpha && b.AlphaChannel >= image.ExtraChannels.Count)
            {
                throw new CorruptDataException(JxlFormat.Info.Name, -1, "A frame blends by an alpha channel the image does not have.");
            }
        }

        int left = Math.Max(0, -f.X0);
        int right = (int)Math.Min(frame.Width, (long)width - f.X0);
        if (right > left)
        {
            float[][] tmp = Scratch(channels, right - left);
            for (int y = 0; y < frame.Height; y++)
            {
                long cy = (long)f.Y0 + y;
                if (cy < 0 || cy >= height)
                {
                    continue;
                }

                Row(canvas, (int)(cy * width) + f.X0 + left, frame.Planes, (y * frame.Width) + left, right - left, color, extras, image, tmp);
            }
        }

        return new JxlPlanes(width, height, canvas);
    }
}
