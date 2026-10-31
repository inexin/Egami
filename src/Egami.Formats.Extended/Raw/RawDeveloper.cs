using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Formats.Extended.Raw;

/// <summary>
/// Develops sensor data into an image, in DNG's processing order: opcode list 1 on the stored values; linearization, black
/// subtraction and white scaling over the active area; opcode list 2; raw-domain noise reduction; white balance; demosaicing
/// (or a half-size average); opcode list 3; highlight handling; chroma noise reduction; then, row by row, the default crop,
/// camera to XYZ D50, the camera profile's HueSatMap, exposure, LookTable and tone curve, and the output colour space.
/// </summary>
internal sealed class RawDeveloper
{
    private readonly RawFrame _frame;
    private readonly RawReaderOptions _options;
    private readonly bool _half;
    private readonly RawWarnings? _warnings;

    public RawDeveloper(RawFrame frame, RawReaderOptions options, bool half, ColorProfile target, RawWarnings? warnings)
    {
        _warnings = warnings;
        _frame = frame;
        _options = options;
        _half = half && frame.Cfa is not null;
        Target = target;
        Rect active = frame.Active;
        (double cx, double cy, double cw, double ch) = frame.Crop;
        int scale = _half ? 2 : 1;
        int x = (int)Math.Round(cx) / scale;
        int y = (int)Math.Round(cy) / scale;
        int w = Math.Max(1, (int)Math.Round(cw) / scale);
        int h = Math.Max(1, (int)Math.Round(ch) / scale);
        int planeW = _half ? active.Width / 2 : active.Width;
        int planeH = _half ? active.Height / 2 : active.Height;
        planeW = Math.Max(planeW, 1);
        planeH = Math.Max(planeH, 1);
        x = Math.Clamp(x, 0, planeW - 1);
        y = Math.Clamp(y, 0, planeH - 1);
        Output = new Rect(x, y, Math.Min(w, planeW - x), Math.Min(h, planeH - y));
        PlaneWidth = planeW;
        PlaneHeight = planeH;
    }

    /// <summary>Gets the output colour space.</summary>
    public ColorProfile Target { get; }

    /// <summary>Gets the part of the developed plane that is output: the default crop.</summary>
    public Rect Output { get; }

    /// <summary>Gets the size of the developed plane: the active area, halved for a half-size develop.</summary>
    public int PlaneWidth { get; }

    public int PlaneHeight { get; }

    /// <summary>Gets the colour transform the develop used, once it has run.</summary>
    public RawCamera? Camera { get; private set; }

    /// <summary>Gets which lens profile corrected the image, once the develop has run, or <see langword="null"/>.</summary>
    public string? LensProfile { get; private set; }

    /// <summary>
    /// Runs the develop and hands each output row, as encoded RGB in <see cref="Target"/>, to <paramref name="write"/>, which may be
    /// called from several threads at once for different rows.
    /// </summary>
    public void Run(RawPixels pixels, EgamiInput input, RowWriter write)
    {
        ParallelOptions parallel = new()
        {
            MaxDegreeOfParallelism = Math.Max(1, input.Context.MaxParallelism),
            CancellationToken = input.CancellationToken,
        };
        RawFrame frame = _frame;
        Rect active = frame.Active;
        int samples = pixels.Samples;
        int aw = active.Width, ah = active.Height;
        RawCfa? cfa = frame.Cfa?.Shift(active.X, active.Y);

        long mosaicBytes = (long)aw * ah * samples * 4;
        input.CheckMemory(mosaicBytes);
        using Once mosaicOwner = new(input.RentScratch(checked((int)mosaicBytes)));
        Memory<float> mosaic = Cast(mosaicOwner.Owner, aw * ah * samples);
        Linearize(pixels, mosaic, parallel, input);
        pixels.Dispose();
        if (frame.Opcodes2 is byte[] list2)
        {
            DngOpcodes.Apply(list2, new RawPlane(mosaic, aw, ah, samples, cfa), 2, _options.LensCorrections, input, _warnings);
        }

        if (cfa is not null && _options.LuminanceNoiseReduction > 0)
        {
            RawDenoise.Mosaic(mosaic, aw, ah, cfa, frame.NoiseProfile, _options.LuminanceNoiseReduction / 100.0, parallel);
        }

        RawCamera camera = WhiteBalance(mosaic.Span, aw, ah, samples, cfa);
        Camera = camera;
        Scale(mosaic, aw, ah, samples, cfa, camera.Multipliers, parallel);

        int pw = PlaneWidth, ph = PlaneHeight;
        long rgbBytes = (long)pw * ph * 3 * 4;
        input.CheckMemory(rgbBytes);
        using IMemoryOwner<byte> rgbOwner = input.RentScratch(checked((int)rgbBytes));
        Memory<float> rgb = Cast(rgbOwner, pw * ph * 3);
        if (cfa is null)
        {
            ToRgb(mosaic.Span, rgb.Span, samples);
        }
        else if (_half)
        {
            RawDemosaic.Half(mosaic.Span, aw, ah, cfa, rgb.Span);
        }
        else
        {
            RawDemosaic.Run(mosaic, aw, ah, cfa, _options.Demosaic, camera.CameraToXyz, rgb, parallel);
        }

        mosaicOwner.Dispose();
        if (frame.Opcodes3 is byte[] list3)
        {
            DngOpcodes.Apply(list3, new RawPlane(rgb, pw, ph, 3, null) { Scale = _half ? 2 : 1 }, 3, _options.LensCorrections, input, _warnings);
        }

        if (_options.LensCorrections && !HasLensOpcodes(frame.Opcodes3))
        {
            LensProfile = CorrectLens(rgb, pw, ph, parallel);
        }

        RawHighlights.Apply(rgb, pw, ph, camera.Multipliers, _options.Highlights, parallel);
        if (_options.ColorNoiseReduction > 0)
        {
            RawDenoise.Chroma(rgb, pw, ph, _options.ColorNoiseReduction / 100.0, parallel, input);
        }

        RawLook look = new(frame, camera, Target, Exposure(), _options.ApplyProfile);
        Rect output = Output;
        Parallel.For(0, (output.Height + 15) / 16, parallel, () => new float[output.Width * 3], (band, _, row) =>
        {
            Span<float> line = row;
            for (int y = band * 16; y < Math.Min(output.Height, (band * 16) + 16); y++)
            {
                ReadOnlySpan<float> source = rgb.Span.Slice((((output.Y + y) * pw) + output.X) * 3, output.Width * 3);
                look.Apply(source, line);
                write(y, MemoryMarshal.Cast<float, RgbF32>(line));
            }

            return row;
        }, _ => { });
    }

    private static bool HasLensOpcodes(byte[]? list) => list is not null && DngOpcodes.Ids(list).Exists(id => id is DngOpcodes.WarpRectilinear or DngOpcodes.WarpFisheye or DngOpcodes.FixVignetteRadial);

    /// <summary>Corrects the lens from a runtime profile that matches the camera and lens, when one does.</summary>
    private string? CorrectLens(Memory<float> rgb, int width, int height, ParallelOptions parallel)
    {
        RawFrame frame = _frame;
        if (frame.FocalLength is not double focal || string.IsNullOrWhiteSpace(frame.LensModel))
        {
            return null;
        }

        List<RawLensProfile> profiles = RawLensProfiles.Load(_options.LensProfiles, _warnings);
        if (RawLensProfiles.Match(profiles, frame.Make, frame.Model, frame.LensModel) is not RawLensProfile profile)
        {
            return null;
        }

        double crop = frame.FocalLength35 is double f35 && focal > 0 ? f35 / focal : profile.CropFactor;
        string? applied = RawLens.Apply(rgb, width, height, profile, focal, frame.FNumber ?? 0, frame.FocusDistance ?? 1000, crop, parallel);
        return applied is null ? null : $"{(profile.Format == RawLensFormat.Lcp ? "Adobe" : "Lensfun")}: {(profile.Maker.Length > 0 ? profile.Maker + " " : "")}{profile.Lens} ({applied})";
    }

    /// <summary>Returns the exposure, in stops: the file's baseline exposure (and the profile's offset) plus the option's.</summary>
    private double Exposure()
    {
        double ev = _options.Exposure;
        if (_options.ApplyBaselineExposure)
        {
            ev += _frame.BaselineExposure + (_options.ApplyProfile ? _frame.Profile.BaselineExposureOffset : 0);
        }

        return ev;
    }

    private static Memory<float> Cast(IMemoryOwner<byte> owner, int count) => new CastMemory(owner.Memory).Memory[..count];

    /// <summary>
    /// Applies opcode list 1, the linearization table, the black levels and the white level, giving values where 0 is black
    /// and 1 is the white level, over the active area.
    /// </summary>
    private void Linearize(RawPixels pixels, Memory<float> mosaic, ParallelOptions parallel, EgamiInput input)
    {
        RawFrame frame = _frame;
        Rect active = frame.Active;
        int samples = pixels.Samples;
        int aw = active.Width;
        RawPlane? stage1 = null;
        IMemoryOwner<byte>? stage1Owner = null;
        try
        {
            if (frame.Opcodes1 is byte[] list1)
            {
                // Opcode list 1 works on the stored values over the whole image, which it may change or reshape (TrimBounds).
                long bytes = (long)pixels.Width * pixels.Height * samples * 4;
                input.CheckMemory(bytes);
                stage1Owner = input.RentScratch(checked((int)bytes));
                Memory<float> values = Cast(stage1Owner, pixels.Width * pixels.Height * samples);
                Span<float> v = values.Span;
                if (pixels.IsFloat)
                {
                    pixels.F32.CopyTo(v);
                }
                else
                {
                    ReadOnlySpan<ushort> u = pixels.U16;
                    for (int i = 0; i < v.Length; i++)
                    {
                        v[i] = u[i] / 65535f;
                    }
                }

                stage1 = new RawPlane(values, pixels.Width, pixels.Height, samples, frame.Cfa);
                DngOpcodes.Apply(list1, stage1, 1, _options.LensCorrections, input, _warnings);
            }

            ushort[]? table = frame.Linearization;
            float[] lut = Lut(frame, table, pixels.IsFloat);
            double[] black = frame.Black;
            double[] white = frame.White;
            int br = frame.BlackRows, bc = frame.BlackColumns;
            double[]? dh = frame.BlackDeltaH, dv = frame.BlackDeltaV;
            bool floatData = pixels.IsFloat;
            RawPlane? source1 = stage1;
            if (!floatData && source1 is null && dh is null)
            {
                // The usual case: stored integers, a black level per pattern position and row. Per row, each position of the
                // pattern gets its offset and scale once; the loop is a table lookup and a multiply-add per sample.
                Parallel.For(0, active.Height, parallel, () => new float[bc * samples * 2], (y, _, row) =>
                {
                    int sy = y + active.Y;
                    double rowDelta = dv is null ? 0 : dv.Length == active.Height ? dv[y] : dv[Math.Min(sy, dv.Length - 1)];
                    int period = bc * samples;
                    for (int j = 0; j < period; j++)
                    {
                        int s = j % samples;
                        double b = black[((((y % br) * bc) + (j / samples)) * samples) + s] + rowDelta;
                        double range = white[s] - b;
                        row[j * 2] = range > 0 ? (float)(1 / range) : 0;
                        row[(j * 2) + 1] = range > 0 ? (float)(-b / range) : 0;
                    }

                    ReadOnlySpan<ushort> raw = pixels.U16Row(sy).Slice(active.X * samples, aw * samples);
                    Span<float> target = mosaic.Span.Slice(y * aw * samples, aw * samples);
                    for (int i = 0, j = 0; i < target.Length; i++)
                    {
                        target[i] = (lut[raw[i]] * row[j * 2]) + row[(j * 2) + 1];
                        if (++j == period)
                        {
                            j = 0;
                        }
                    }

                    return row;
                }, _ => { });
                return;
            }

            Parallel.For(0, active.Height, parallel, y =>
            {
                int sy = y + active.Y;
                Span<float> target = mosaic.Span.Slice(y * aw * samples, aw * samples);
                double rowDelta = dv is null ? 0 : dv.Length == active.Height ? dv[y] : dv[Math.Min(sy, dv.Length - 1)];
                ReadOnlySpan<ushort> raw = floatData || source1 is not null ? default : pixels.U16Row(sy).Slice(active.X * samples, aw * samples);
                ReadOnlySpan<float> rawF = source1 is not null
                    ? source1.Row(sy).Slice(active.X * samples, aw * samples)
                    : floatData ? pixels.F32Row(sy).Slice(active.X * samples, aw * samples) : default;
                for (int x = 0; x < aw; x++)
                {
                    double colDelta = dh is null ? 0 : dh.Length == aw ? dh[x] : dh[Math.Min(x + active.X, dh.Length - 1)];
                    for (int s = 0; s < samples; s++)
                    {
                        int i = (x * samples) + s;
                        double value;
                        if (source1 is not null)
                        {
                            value = floatData ? rawF[i] : lut[Math.Clamp((int)MathF.Round(rawF[i] * 65535), 0, 65535)];
                        }
                        else
                        {
                            value = floatData ? rawF[i] : lut[raw[i]];
                        }

                        double b = black[((((y % br) * bc) + (x % bc)) * samples) + s] + rowDelta + colDelta;
                        double range = white[s] - b;
                        target[i] = range > 0 ? (float)((value - b) / range) : 0;
                    }
                }
            });
        }
        finally
        {
            stage1Owner?.Dispose();
        }
    }

    /// <summary>Returns the stored-value-to-linear table: the linearization table, extended by its last value, or the identity.</summary>
    private static float[] Lut(RawFrame frame, ushort[]? table, bool isFloat)
    {
        if (isFloat)
        {
            return [];
        }

        float[] lut = new float[65536];
        for (int i = 0; i < lut.Length; i++)
        {
            lut[i] = table is { Length: > 0 } ? table[Math.Min(i, table.Length - 1)] : i;
        }

        return lut;
    }

    /// <summary>Works out the white balance the options ask for and the colour transform that goes with it.</summary>
    private RawCamera WhiteBalance(ReadOnlySpan<float> mosaic, int width, int height, int samples, RawCfa? cfa)
    {
        RawFrame frame = _frame;
        switch (_options.WhiteBalance)
        {
            case RawWhiteBalance.Custom:
                return RawCamera.Create(frame, null, RawColor.WhiteOf(_options.Temperature, _options.Tint));
            case RawWhiteBalance.Auto:
                return RawCamera.Create(frame, GrayWorld(mosaic, width, height, samples, cfa), null);
            default:
                if (frame.AsShotNeutral is { Length: 3 } neutral)
                {
                    return RawCamera.Create(frame, neutral, null);
                }

                return RawCamera.Create(frame, null, frame.AsShotWhite ?? RawColor.D50);
        }
    }

    /// <summary>
    /// Measures the camera's response to gray by the gray-world assumption: the mean of each colour over the pixels that are
    /// neither near black nor near clipping.
    /// </summary>
    private static double[] GrayWorld(ReadOnlySpan<float> mosaic, int width, int height, int samples, RawCfa? cfa)
    {
        Span<double> sum = stackalloc double[3];
        Span<long> count = stackalloc long[3];
        int step = Math.Max(1, (int)Math.Sqrt((double)width * height / 4_000_000));
        for (int y = 0; y < height; y += cfa is null ? step : step * 2)
        {
            for (int dy = 0; dy < (cfa is null ? 1 : cfa.Rows) && y + dy < height; dy++)
            {
                int yy = y + dy;
                for (int x = 0; x < width; x++)
                {
                    if (cfa is null)
                    {
                        if (x % step != 0)
                        {
                            continue;
                        }

                        int i = ((yy * width) + x) * samples;
                        float r = mosaic[i], g = mosaic[i + 1], b = mosaic[i + 2];
                        if (Math.Max(r, Math.Max(g, b)) < 0.98f && Math.Min(r, Math.Min(g, b)) > 0.002f)
                        {
                            sum[0] += r;
                            sum[1] += g;
                            sum[2] += b;
                            count[0]++;
                            count[1]++;
                            count[2]++;
                        }

                        continue;
                    }

                    float v = mosaic[(yy * width) + x];
                    int c = cfa.ColorAt(x, yy);
                    if (c < 3 && v < 0.98f && v > 0.002f)
                    {
                        sum[c] += v;
                        count[c]++;
                    }
                }
            }
        }

        if (count[0] == 0 || count[1] == 0 || count[2] == 0)
        {
            return [1, 1, 1];
        }

        return [sum[0] / count[0], sum[1] / count[1], sum[2] / count[2]];
    }

    /// <summary>Multiplies each sample by its colour's white-balance multiplier.</summary>
    private static void Scale(Memory<float> mosaic, int width, int height, int samples, RawCfa? cfa, double[] multipliers, ParallelOptions parallel)
    {
        float[] m = [.. multipliers.Select(v => (float)v)];
        Parallel.For(0, height, parallel, y =>
        {
            Span<float> row = mosaic.Span.Slice(y * width * samples, width * samples);
            if (cfa is null)
            {
                for (int i = 0; i < row.Length; i++)
                {
                    row[i] *= m[i % samples % 3];
                }

                return;
            }

            // The pattern's row, repeated: one multiplier per position across.
            Span<float> across = stackalloc float[cfa.Columns];
            for (int k = 0; k < cfa.Columns; k++)
            {
                int c = cfa.ColorAt(k, y);
                across[k] = c < 3 ? m[c] : 1;
            }

            for (int x = 0, k = 0; x < width; x++)
            {
                row[x] *= across[k];
                if (++k == across.Length)
                {
                    k = 0;
                }
            }
        });
    }

    /// <summary>Copies LinearRaw data, already one RGB triple per pixel, to the RGB plane.</summary>
    private static void ToRgb(ReadOnlySpan<float> source, Span<float> rgb, int samples)
    {
        int pixels = source.Length / samples;
        for (int i = 0; i < pixels; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                rgb[(i * 3) + c] = source[(i * samples) + Math.Min(c, samples - 1)];
            }
        }
    }

    /// <summary>Pooled memory that can be given back early and is given back once.</summary>
    private sealed class Once(IMemoryOwner<byte> owner) : IDisposable
    {
        private IMemoryOwner<byte>? _owner = owner;

        public IMemoryOwner<byte> Owner => _owner ?? throw new ObjectDisposedException(nameof(Once));

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Dispose();
    }

    /// <summary>Receives one output row.</summary>
    public delegate void RowWriter(int y, ReadOnlySpan<RgbF32> row);

    /// <summary>Views pooled bytes as floats.</summary>
    private sealed class CastMemory(Memory<byte> bytes) : MemoryManager<float>
    {
        public override Span<float> GetSpan() => MemoryMarshal.Cast<byte, float>(bytes.Span);

        public override MemoryHandle Pin(int elementIndex = 0) => throw new NotSupportedException();

        public override void Unpin()
        {
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}

/// <summary>A plane of float samples a DNG opcode list works on: stage 1 (stored values over 65535), 2 (linear mosaic) or 3 (RGB).</summary>
internal sealed class RawPlane(Memory<float> values, int width, int height, int planes, RawCfa? cfa)
{
    public Memory<float> Values { get; } = values;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public int Planes { get; } = planes;

    public RawCfa? Cfa { get; } = cfa;

    /// <summary>Gets how many full-size pixels one pixel of this plane stands for (2 for a half-size develop).</summary>
    public int Scale { get; init; } = 1;

    public Span<float> Row(int y) => Values.Span.Slice(y * Width * Planes, Width * Planes);
}
