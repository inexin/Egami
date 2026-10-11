using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Scopes;

/// <summary>
/// Phase 2.5.1 task 11: random same-size chains in random nested scopes (rectangles, masks, channel sets) give exactly the composed
/// reference, over 10,000 seeds. The reference runs each step on the whole image and keeps the area (a flip runs on the area cut
/// out), mixes mask scopes by coverage on premultiplied values, and restores the channels a channel scope leaves out. The 2.5.1
/// operations add no parser, so this takes the fuzz run's place for the pipeline change.
/// </summary>
public sealed class ScopePropertyTests : IDisposable
{
    private const int W = 23;
    private const int H = 17;

    private readonly Raster<PremulRgba8> _logo = Premultiplied(OperationCoverageTests.Logo());
    private readonly Raster<Gray8> _mask = MaskScopeTests.GrayMask(15, 11);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void RandomNestedChainsEqualTheComposedReference()
    {
        int seeds = int.TryParse(Environment.GetEnvironmentVariable("EGAMI_SCOPE_SEEDS"), out int n) ? n : 10_000;
        Parallel.For(0, seeds, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = Ct }, seed =>
        {
            Random random = new(seed);
            Node chain = Scope.Root(RandomSteps(random, depth: 0));
            using Raster<PremulRgba8> source = Premultiplied(OperationCoverageTests.Noise(W, H, EgamiContext.Default with { MaxParallelism = 1 }));
            using Raster<PremulRgba8> actual = source.Derive(ops => chain.Record(ops), Ct);
            PremulRgba8[] expected = Pixels(source);
            chain.Reference(this, expected, new Rect(0, 0, W, H));
            PremulRgba8[] got = Pixels(actual);
            for (int i = 0; i < got.Length; i++)
            {
                if (got[i] != expected[i])
                {
                    Assert.Fail($"Seed {seed}: {chain} differs at ({i % W}, {i / W}): {got[i]}, reference {expected[i]}");
                }
            }
        });
    }

    public void Dispose()
    {
        _logo.Dispose();
        _mask.Dispose();
    }

    private List<Node> RandomSteps(Random random, int depth)
    {
        List<Node> steps = [];
        int count = random.Next(1, 4);
        for (int i = 0; i < count; i++)
        {
            int kind = random.Next(depth < 2 ? 14 : 11);
            float amount = 0.6f + (float)random.NextDouble();
            steps.Add(kind switch
            {
                0 => new Op($"Brightness{amount:0.00}", ops => ops.Brightness(amount), Matrix: true),
                1 => new Op("Invert", ops => ops.Invert(), Matrix: true),
                2 => new Op("Saturation", ops => ops.Saturation(1.5f), Matrix: true),
                3 => new Op("BlendColor", ops => ops.BlendColor(new Rgba8(200, 60, 30, 150), BlendMode.Multiply)),
                4 => new Op("Blur", ops => ops.Blur(0.8f)),
                5 => new Op("BoxBlur", ops => ops.BoxBlur(1)),
                6 => new Op("Sharpen", ops => ops.Sharpen(0.7f, 0.8f)),
                7 => new Op("Flip", ops => ops.FlipHorizontal(), Moves: true),
                8 => RandomFill(random),
                9 => RandomDraw(random),
                10 => new Op("Convolve", ops => ops.Convolve(new ConvolutionKernel(3, 3, [0, -1, 0, -1, 5, -1, 0, -1, 0]), EdgeMode.Mirror)),
                11 => new Scope(Scope.Kind.Rect, RandomRect(random), default, default, RandomSteps(random, depth + 1)),
                12 => new Scope(Scope.Kind.Mask, default, new Point(random.Next(-6, W - 4), random.Next(-4, H - 3)), default, RandomSteps(random, depth + 1)) { MaskRaster = _mask },
                _ => new Scope(Scope.Kind.Channels, default, default, (ColorChannels)random.Next(1, 15), RandomSteps(random, depth + 1)),
            });
        }

        return steps;
    }

    private static Op RandomFill(Random random)
    {
        Rect r = new(random.Next(0, W - 2), random.Next(0, H - 2), 2, 2);
        return new Op($"Fill{r}", ops => ops.FillColor(Rgba8.Lime, r));
    }

    private Op RandomDraw(Random random)
    {
        Point p = new(random.Next(-20, W), random.Next(-15, H));
        // Destination draws nothing and the planner drops it, which would let the steps around it fuse; the reference keeps steps apart.
        BlendMode mode = (BlendMode)random.Next(31);
        mode = mode == BlendMode.Destination ? BlendMode.SourceOver : mode;
        return new Op($"Draw{mode}{p}", ops => ops.DrawImage(_logo, p, mode, 0.8f));
    }

    private static Rect RandomRect(Random random)
    {
        int x = random.Next(-5, W - 2), y = random.Next(-4, H - 2);
        return new Rect(x, y, random.Next(1, W), random.Next(1, H));
    }

    private static Raster<PremulRgba8> Premultiplied(Raster<Rgba8> straight)
    {
        using (straight)
        {
            return straight.As<PremulRgba8>();
        }
    }

    private static PremulRgba8[] Pixels(Raster<PremulRgba8> raster)
    {
        PremulRgba8[] result = new PremulRgba8[W * H];
        for (int y = 0; y < H; y++)
        {
            raster.Row(y).CopyTo(result.AsSpan(y * W, W));
        }

        return result;
    }

    private static Raster<PremulRgba8> FromPixels(PremulRgba8[] pixels)
    {
        Raster<PremulRgba8> raster = Raster.Create<PremulRgba8>(W, H, EgamiContext.Default with { MaxParallelism = 1 });
        for (int y = 0; y < H; y++)
        {
            pixels.AsSpan(y * W, W).CopyTo(raster.Row(y));
        }

        return raster;
    }

    /// <summary>
    /// Runs steps in the reference. Consecutive color-only steps run as one whole-image chain, since the planner fuses them into one
    /// pass with one rounding; every other step runs on its own.
    /// </summary>
    private static void RunSteps(ScopePropertyTests owner, List<Node> steps, PremulRgba8[] image, Rect area)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            if (ColorOnly(steps[i]))
            {
                List<Node> run = [];
                while (i < steps.Count && ColorOnly(steps[i]))
                {
                    run.Add(steps[i]);
                    i++;
                }

                i--;
                new Op(string.Join(" ", run), ops => run.ForEach(n => n.Record(ops))).Reference(owner, image, area);
                continue;
            }

            steps[i].Reference(owner, image, area);
        }
    }

    /// <summary>Gets whether a node is color steps only (a channel scope of them included), which the planner may fuse with its neighbours.</summary>
    private static bool ColorOnly(Node node)
    {
        return node is Op { Matrix: true } || (node is Scope { Type: Scope.Kind.Channels } scope && scope.Steps.All(ColorOnly));
    }

    private abstract record Node
    {
        internal abstract void Record(RasterOps ops);

        /// <summary>Applies the node to <paramref name="image"/>, changing only <paramref name="area"/>.</summary>
        internal abstract void Reference(ScopePropertyTests owner, PremulRgba8[] image, Rect area);
    }

    private sealed record Op(string Name, Action<RasterOps> Add, bool Moves = false, bool Matrix = false) : Node
    {
        internal override void Record(RasterOps ops) => Add(ops);

        internal override void Reference(ScopePropertyTests owner, PremulRgba8[] image, Rect area)
        {
            if (area.IsEmpty)
            {
                return;
            }

            using Raster<PremulRgba8> whole = FromPixels(image);
            if (Moves)
            {
                // A flip treats the area as its image: cut it out, flip it, put it back.
                using Raster<PremulRgba8> cut = whole.Derive(ops => Add(ops.Crop(area)), Ct);
                for (int y = 0; y < area.Height; y++)
                {
                    cut.Row(y).CopyTo(image.AsSpan(((area.Y + y) * W) + area.X, area.Width));
                }

                return;
            }

            using Raster<PremulRgba8> result = whole.Derive(Add, Ct);
            PremulRgba8[] after = Pixels(result);
            for (int y = area.Y; y < area.Bottom; y++)
            {
                after.AsSpan((y * W) + area.X, area.Width).CopyTo(image.AsSpan((y * W) + area.X, area.Width));
            }
        }

        public override string ToString() => Name;
    }

    private sealed record Scope(Scope.Kind Type, Rect Area, Point MaskAt, ColorChannels Channels, List<Node> Steps) : Node
    {
        internal enum Kind
        {
            Root,
            Rect,
            Mask,
            Channels,
        }

        internal static Scope Root(List<Node> steps) => new(Kind.Root, default, default, default, steps);

        internal override void Record(RasterOps ops)
        {
            switch (Type)
            {
                case Kind.Root:
                    foreach (Node step in Steps)
                    {
                        step.Record(ops);
                    }

                    break;
                case Kind.Rect:
                    ops.Within(Area, o => Steps.ForEach(s => s.Record(o)));
                    break;
                case Kind.Mask:
                    ops.Within(new Mask(MaskRaster!, MaskAt), o => Steps.ForEach(s => s.Record(o)));
                    break;
                default:
                    ops.OnChannels(Channels, o => Steps.ForEach(s => s.Record(o)));
                    break;
            }
        }

        internal Raster<Gray8>? MaskRaster { get; init; }

        internal override void Reference(ScopePropertyTests owner, PremulRgba8[] image, Rect area)
        {
            switch (Type)
            {
                case Kind.Root:
                    RunSteps(owner, Steps, image, area);
                    return;
                case Kind.Rect:
                    Rect inner = Area.Intersect(area);
                    RunSteps(owner, Steps, image, inner);
                    return;
                case Kind.Mask:
                    {
                        Raster<Gray8> mask = MaskRaster!;
                        Rect box = MaskBox(mask, MaskAt).Intersect(area);
                        PremulRgba8[] original = (PremulRgba8[])image.Clone();
                        RunSteps(owner, Steps, image, box);
                        for (int y = box.Y; y < box.Bottom; y++)
                        {
                            for (int x = box.X; x < box.Right; x++)
                            {
                                int m = mask.GetPixel(x - MaskAt.X, y - MaskAt.Y).L;
                                PremulRgba8 o = original[(y * W) + x], r = image[(y * W) + x];
                                image[(y * W) + x] = new PremulRgba8(Mix(o.R, r.R, m), Mix(o.G, r.G, m), Mix(o.B, r.B, m), Mix(o.A, r.A, m));
                            }
                        }

                        return;
                    }

                default:
                    {
                        PremulRgba8[] original = (PremulRgba8[])image.Clone();
                        RunSteps(owner, Steps, image, area);
                        for (int y = area.Y; y < area.Bottom; y++)
                        {
                            for (int x = area.X; x < area.Right; x++)
                            {
                                image[(y * W) + x] = Merge(original[(y * W) + x], image[(y * W) + x]);
                            }
                        }

                        return;
                    }
            }
        }

        public override string ToString() => Type switch
        {
            Kind.Root => string.Join(" ", Steps),
            Kind.Rect => $"Within({Area}: {string.Join(" ", Steps)})",
            Kind.Mask => $"Mask({MaskAt}: {string.Join(" ", Steps)})",
            _ => $"OnChannels({Channels}: {string.Join(" ", Steps)})",
        };

        private PremulRgba8 Merge(PremulRgba8 original, PremulRgba8 result)
        {
            // In straight RGBA through floats, as the scope merges types that are not straight bytes.
            Span<PremulRgba8> pair = [original, result];
            Span<RgbaF32> straight = stackalloc RgbaF32[2];
            PixelConvert.Convert<PremulRgba8, RgbaF32>(pair, straight);
            RgbaF32 b = straight[0], a = straight[1];
            Span<RgbaF32> merged =
            [
                new RgbaF32(
                    (Channels & ColorChannels.Red) != 0 ? a.R : b.R,
                    (Channels & ColorChannels.Green) != 0 ? a.G : b.G,
                    (Channels & ColorChannels.Blue) != 0 ? a.B : b.B,
                    (Channels & ColorChannels.Alpha) != 0 ? a.A : b.A),
            ];
            Span<PremulRgba8> back = stackalloc PremulRgba8[1];
            PixelConvert.Convert<RgbaF32, PremulRgba8>(merged, back);
            return back[0];
        }

        private static Rect MaskBox(Raster<Gray8> mask, Point at)
        {
            int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue;
            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    if (mask.GetPixel(x, y).L != 0)
                    {
                        (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
                    }
                }
            }

            return left == int.MaxValue ? default : new Rect(at.X + left, at.Y + top, right - left + 1, bottom - top + 1);
        }

        private static byte Mix(byte o, byte r, int m) => m == 0 ? o : m == 255 ? r : (byte)(((r * m) + (o * (255 - m)) + 127) / 255);
    }
}
