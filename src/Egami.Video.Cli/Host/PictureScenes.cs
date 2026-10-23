namespace Egami.Video.Cli.Host;

/// <summary>
/// Finds cuts between shots from frame differences. Each frame is summarized by a small grid of mean lumas
/// (<see cref="PictureFrames.Summarize"/>); its difference from the frame before is the mean absolute difference of the grids,
/// from 0 to 1. A fade, a dissolve or steady motion changes every frame a little, so the score takes off the mean difference of
/// the frames just before (the adaptive term) and scales what is left to 0 to 1; a cut changes one frame a lot and scores high.
/// A cut closer than the minimum gap to the scene's start is not taken.
/// </summary>
internal sealed class SceneDetector
{
    /// <summary>How many earlier differences the adaptive term averages.</summary>
    private const int Window = 8;

    /// <summary>The scale from a difference above the recent mean to a score: a jump of a quarter of the range scores 1.</summary>
    private const double Scale = 4;

    private readonly double _threshold;
    private readonly double _minGap;
    private readonly Queue<double> _recent = new();
    private double[]? _previous;
    private double _sceneStart;
    private long _frame;

    /// <summary>Creates a detector that takes scores at or above <paramref name="threshold"/>, at least <paramref name="minGap"/> seconds apart.</summary>
    public SceneDetector(double threshold, double minGap)
    {
        _threshold = threshold;
        _minGap = minGap;
    }

    /// <summary>Gets the scenes found so far, the first starting at the first frame.</summary>
    public List<Scene> Scenes { get; } = [];

    /// <summary>Takes the next frame; returns the scene it starts, or <see langword="null"/>.</summary>
    public Scene? Add(FrameSummary frame, double seconds)
    {
        long index = _frame++;
        double[] grid = frame.Grid;
        if (_previous is null)
        {
            _previous = grid;
            _sceneStart = seconds;
            Scene first = new(1, index, seconds, 0);
            Scenes.Add(first);
            return first;
        }

        double difference = 0;
        for (int i = 0; i < grid.Length; i++)
        {
            difference += Math.Abs(grid[i] - _previous[i]);
        }

        difference /= grid.Length;
        _previous = grid;
        double recent = _recent.Count > 0 ? _recent.Average() : 0;
        _recent.Enqueue(difference);
        if (_recent.Count > Window)
        {
            _recent.Dequeue();
        }

        double score = Math.Clamp((difference - recent) * Scale, 0, 1);
        if (score < _threshold || seconds - _sceneStart < _minGap - 1e-9)
        {
            return null;
        }

        _sceneStart = seconds;
        Scene scene = new(Scenes.Count + 1, index, seconds, score);
        Scenes.Add(scene);
        return scene;
    }
}

/// <summary>A scene: its number from 1, its first frame (counted from 0) and time, and the score of the cut that starts it (0 for the first).</summary>
/// <param name="Number">The scene's number, from 1.</param>
/// <param name="Frame">Its first frame, counted from 0 in presentation order.</param>
/// <param name="Seconds">Its first frame's time.</param>
/// <param name="Score">The cut's score, from 0 to 1.</param>
internal sealed record Scene(int Number, long Frame, double Seconds, double Score);
