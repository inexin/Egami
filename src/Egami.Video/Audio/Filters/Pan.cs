namespace Egami.Video.Audio.Filters;

/// <summary>
/// Places a mono or stereo input in a stereo output. <see cref="Position"/> pans a mono input with the constant-power law
/// (-3 dB in each channel at the centre, like ffmpeg's <c>pan=stereo|c0=0.7071*c0|c1=0.7071*c0</c>); on a stereo input it
/// turns one side down along the same law, scaled so the centre is unchanged. <see cref="Balance"/> turns one side down
/// in a straight line. <see cref="Swap"/> exchanges left and right, and <see cref="InvertLeft"/> and
/// <see cref="InvertRight"/> flip a channel's polarity. Changes to the position or balance between frames move over 5 ms.
/// For any other channel matrix use <see cref="ChannelMixer"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-mixer"/>
/// </example>
public sealed class Pan : AudioFilter
{
    private ParameterRamp _left = new(1);
    private ParameterRamp _right = new(1);
    private float[] _leftGains = [];
    private float[] _rightGains = [];
    private bool _mono;

    /// <summary>Creates a pan.</summary>
    /// <param name="position">Where to place the sound: -1 full left, 0 centre, 1 full right.</param>
    /// <exception cref="ArgumentOutOfRangeException">The position is outside -1 to 1.</exception>
    public Pan(double position = 0)
    {
        Position = position;
    }

    /// <summary>Gets or sets where the sound sits: -1 full left, 0 centre (the default), 1 full right.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside -1 to 1.</exception>
    public double Position
    {
        get;
        set
        {
            Check(value);
            field = value;
            Update();
        }
    }

    /// <summary>Gets or sets the balance: -1 turns the right side off, 0 leaves both alone (the default), 1 turns the left side off.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside -1 to 1.</exception>
    public double Balance
    {
        get;
        set
        {
            Check(value);
            field = value;
            Update();
        }
    }

    /// <summary>Gets or sets whether left and right change places, after the position and balance.</summary>
    public bool Swap { get; set; }

    /// <summary>Gets or sets whether the left output's polarity is flipped.</summary>
    public bool InvertLeft { get; set; }

    /// <summary>Gets or sets whether the right output's polarity is flipped.</summary>
    public bool InvertRight { get; set; }

    private protected override void OnStart()
    {
        _mono = Layout.ChannelCount == 1;
        if (Layout.ChannelCount > 2)
        {
            throw new ArgumentException($"Pan takes mono or stereo; the frame has {Layout}. Use ChannelMixer for other layouts.", "frame");
        }

        Update(jump: true);
    }

    private protected override void Process(AudioFrame frame)
    {
        int n = frame.SampleCount;
        AudioFrame output = frame;
        if (_mono)
        {
            output = new AudioFrame(SampleFormat.F32Planar, ChannelLayout.Stereo, SampleRate, n, frame.Timestamp, Context);
            frame.AsSpan<float>(0).CopyTo(output.GetSpan<float>(0));
            frame.AsSpan<float>(0).CopyTo(output.GetSpan<float>(1));
            frame.Dispose();
        }

        Span<float> left = output.GetSpan<float>(0), right = output.GetSpan<float>(1);
        float signLeft = InvertLeft ? -1 : 1, signRight = InvertRight ? -1 : 1;
        if (_left.IsSteady && _right.IsSteady)
        {
            float gl = (float)_left.Value, gr = (float)_right.Value;
            if (gl != 1)
            {
                SampleMath.Scale(left, gl);
            }

            if (gr != 1)
            {
                SampleMath.Scale(right, gr);
            }
        }
        else
        {
            if (_leftGains.Length < n)
            {
                _leftGains = new float[n];
                _rightGains = new float[n];
            }

            _left.Fill(_leftGains.AsSpan(0, n));
            _right.Fill(_rightGains.AsSpan(0, n));
            SampleMath.Multiply(left, _leftGains);
            SampleMath.Multiply(right, _rightGains);
        }

        if (Swap)
        {
            for (int i = 0; i < n; i++)
            {
                (left[i], right[i]) = (right[i], left[i]);
            }
        }

        if (signLeft < 0)
        {
            SampleMath.Scale(left, -1);
        }

        if (signRight < 0)
        {
            SampleMath.Scale(right, -1);
        }

        Emit(output);
    }

    private static void Check(double value)
    {
        if (!(value >= -1 && value <= 1))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "The value must be from -1 to 1.");
        }
    }

    private void Update(bool jump = false)
    {
        if (SampleRate == 0 && !jump)
        {
            return;
        }

        double angle = (Position + 1) * Math.PI / 4;
        double gl = Math.Cos(angle), gr = Math.Sin(angle);
        if (!_mono)
        {
            // Stereo: scale the law so the centre is unity, and never boost a side.
            gl = Math.Min(1, gl * Math.Sqrt(2));
            gr = Math.Min(1, gr * Math.Sqrt(2));
        }

        gl *= 1 - Math.Max(0, Balance);
        gr *= 1 + Math.Min(0, Balance);
        int length = jump ? 0 : ParameterRamp.Length(SampleRate);
        _left.Set(gl, length);
        _right.Set(gr, length);
    }
}
