using System.Numerics;

using Egami.Drawing;

namespace Egami.Metafiles;

public sealed partial class EmfCanvas
{
    /// <summary>
    /// Every call written to the file so far (states, clips and drawing), to replay as the backdrop of a call that blends with what
    /// is beneath it: each takes the canvas and the transform from device units to its pixels.
    /// </summary>
    private readonly List<Action<Canvas, Matrix3x2>> _history = [];

    /// <summary>Remembers a call made under the current transform.</summary>
    private void Remember(Action<Canvas> draw)
    {
        Matrix3x2 m = Matrix;
        _history.Add((c, under) =>
        {
            c.SetMatrix(m * under);
            draw(c);
        });
    }

    /// <summary>Replays the first calls remembered onto a canvas whose transform maps device units, in a saved state.</summary>
    private void PlayHistory(Canvas canvas, int count)
    {
        Matrix3x2 under = canvas.Matrix;
        int saved = canvas.Save();
        try
        {
            for (int i = 0; i < count; i++)
            {
                _history[i](canvas, under);
            }
        }
        finally
        {
            canvas.RestoreToCount(saved);
            canvas.SetMatrix(under);
        }
    }
}
