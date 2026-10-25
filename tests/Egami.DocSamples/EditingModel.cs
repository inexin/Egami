using System.Numerics;

using Egami.Operations;
using Egami.Pixels;
using Egami.Video;
using Egami.Video.Editing;

namespace Egami.DocSamples;

/// <summary>
/// The V9 ONNX sample (Build Plan task 24): an <see cref="IRasterOperation"/> that runs a model on each frame as a planar float
/// tensor. The model here is a stand-in written in C#, so the adapter runs with the tests; the guide shows the ONNX Runtime
/// lines an app would put in its place, marked as untested.
/// </summary>
[Collection("Samples")]
public class EditingModel
{
    #region edit-model-adapter
    /// <summary>
    /// Runs a model on each frame: the frame goes in as a 1x3xHxW tensor of red, green and blue from 0 to 1, and the model's
    /// tensor of the same shape comes back into the frame. Alpha is kept.
    /// </summary>
    public sealed class ModelOperation(Func<float[], int, int, float[]> model) : IRasterOperation
    {
        public Size OutputSize(Size input) => input;

        public void Run<TPixel>(OperationInput<TPixel> input)
            where TPixel : unmanaged, IPixelType<TPixel>
        {
            int width = input.Source.Width, height = input.Source.Height, plane = width * height;
            float[] tensor = new float[3 * plane];
            float[] alpha = new float[plane];
            for (int y = 0; y < height; y++)
            {
                Span<TPixel> row = input.Source.Row(y);
                for (int x = 0; x < width; x++)
                {
                    Vector4 v = row[x].ToScaled();
                    int i = (y * width) + x;
                    tensor[i] = v.X;
                    tensor[plane + i] = v.Y;
                    tensor[(2 * plane) + i] = v.Z;
                    alpha[i] = v.W;
                }
            }

            float[] result = model(tensor, width, height);
            if (result.Length != tensor.Length)
            {
                throw new InvalidOperationException($"The model returned {result.Length} values, not {tensor.Length}.");
            }

            for (int y = 0; y < height; y++)
            {
                Span<TPixel> row = input.Destination.Row(y);
                for (int x = 0; x < width; x++)
                {
                    int i = (y * width) + x;
                    row[x] = TPixel.FromScaled(Vector4.Clamp(new Vector4(result[i], result[plane + i], result[(2 * plane) + i], alpha[i]), Vector4.Zero, Vector4.One));
                }
            }
        }
    }
    #endregion

    #region edit-model-standin
    /// <summary>A stand-in for a style model: a sepia tone, as a tensor in and a tensor out.</summary>
    public static float[] Sepia(float[] tensor, int width, int height)
    {
        int plane = width * height;
        float[] output = new float[tensor.Length];
        for (int i = 0; i < plane; i++)
        {
            float r = tensor[i], g = tensor[plane + i], b = tensor[(2 * plane) + i];
            output[i] = (0.393f * r) + (0.769f * g) + (0.189f * b);
            output[plane + i] = (0.349f * r) + (0.686f * g) + (0.168f * b);
            output[(2 * plane) + i] = (0.272f * r) + (0.534f * g) + (0.131f * b);
        }

        return output;
    }
    #endregion

    [Fact]
    public void ModelOnEveryFrame()
    {
        using SampleFiles files = new();
        using (VideoWriter writer = VideoWriter.Create("clip.mkv", new VideoWriterOptions { FrameRate = new Rational(10, 1) }))
        {
            for (int i = 0; i < 10; i++)
            {
                using Raster<Rgba8> frame = Raster.Create(64, 48, new Rgba8(40, 120, (byte)(20 * i), 255));
                writer.WriteFrame(frame);
            }
        }

        #region edit-model
        // A model as an effect: every frame of the edit from 0.2 s goes through it.
        VideoEdit edit = new();
        edit.Track.Add(Clip.Open("clip.mkv"));
        edit.Effects.Add(EditEffect.Apply(new ModelOperation(Sepia), "sepia model").Between(MediaTime.FromSeconds(0.2)));
        edit.Render("styled.mkv");
        #endregion

        using VideoReader reader = VideoReader.Open("styled.mkv");
        List<Rgba8> middles = [];
        foreach (VideoFrame<Rgba8> frame in reader.ReadFrames<Rgba8>())
        {
            using (frame)
            {
                middles.Add(frame.Raster.Row(24)[32]);
            }
        }

        Assert.Equal(10, middles.Count);
        Assert.True(middles[0].R < middles[0].G, "before the window the frame is as it was");
        Assert.True(middles[5].R > middles[5].B && middles[5].G > middles[5].B, "inside it the model's sepia");
    }
}
