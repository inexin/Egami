namespace Egami.Video.Analysis;

/// <summary>Reads a picture of each scene, such as for a shot list or a storyboard.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-scene-frames"/>
/// </example>
public static class SceneFrames
{
    /// <summary>
    /// Reads one frame of each scene by seeking: its first frame, the one shown halfway through, or its last. Seeking costs up to a
    /// keyframe interval of decoding per scene, so for many short scenes on a file with sparse keyframes a single pass that keeps
    /// frames while <see cref="SceneDetector"/> runs is faster.
    /// </summary>
    /// <param name="reader">A reader on the file the scenes came from. It must be seekable; it is left at the last frame read.</param>
    /// <param name="scenes">The scenes.</param>
    /// <param name="position">Which frame of each scene to read.</param>
    /// <returns>The frames, in the scenes' order. Dispose each frame.</returns>
    /// <exception cref="NotSupportedException">The reader cannot seek.</exception>
    public static IEnumerable<SceneFrame> Read(VideoReader reader, IEnumerable<Scene> scenes, ScenePosition position = ScenePosition.First)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(scenes);
        if (!reader.CanSeek)
        {
            throw new NotSupportedException("Reading scene frames needs a seekable file.");
        }

        return ReadCore(reader, scenes, position);
    }

    private static IEnumerable<SceneFrame> ReadCore(VideoReader reader, IEnumerable<Scene> scenes, ScenePosition position)
    {
        foreach (Scene scene in scenes)
        {
            MediaTime at = position switch
            {
                ScenePosition.Middle => scene.Middle,
                ScenePosition.Last when scene.End > scene.Start => new MediaTime(scene.End.Ticks - 1, scene.End.TimeBase),
                _ => scene.Start,
            };
            reader.Seek(at);
            if (reader.ReadFrame() is { } frame)
            {
                yield return new SceneFrame(scene, frame);
            }
        }
    }
}

/// <summary>A scene and a frame of it, from <see cref="SceneFrames.Read"/>. Dispose the frame.</summary>
/// <param name="Scene">The scene.</param>
/// <param name="Frame">The frame.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-scene-frames"/>
/// </example>
public readonly record struct SceneFrame(Scene Scene, YuvVideoFrame Frame);

/// <summary>Which frame of a scene <see cref="SceneFrames.Read"/> reads.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-scene-frames"/>
/// </example>
public enum ScenePosition
{
    /// <summary>The first frame, where the cut lands.</summary>
    First,

    /// <summary>The frame shown halfway through, usually the most typical.</summary>
    Middle,

    /// <summary>The last frame before the next scene.</summary>
    Last,
}
