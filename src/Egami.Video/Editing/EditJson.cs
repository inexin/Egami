using System.Globalization;
using System.Text;
using System.Text.Json;

using Egami.Operations;
using Egami.Pixels;
using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

namespace Egami.Video.Editing;

/// <summary>
/// An edit as JSON, the CLI's edit file: canvas, track, layers and effects, with times in seconds and names in camel case.
/// Written and read by hand with <see cref="Utf8JsonWriter"/> and <see cref="JsonDocument"/>, so it trims and runs ahead of time.
/// </summary>
internal static class EditJson
{
    public static string Write(VideoEdit edit)
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter json = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            if (edit.Canvas is EditCanvas canvas)
            {
                json.WriteStartObject("canvas");
                json.WriteNumber("width", canvas.Width);
                json.WriteNumber("height", canvas.Height);
                json.WriteString("frameRate", canvas.FrameRate.ToString());
                json.WriteString("fit", Name(canvas.Fit));
                json.WriteString("background", canvas.Background.ToHex());
                json.WriteNumber("sampleRate", canvas.SampleRate);
                json.WriteNumber("channels", canvas.AudioLayout.ChannelCount);
                json.WriteEndObject();
            }

            json.WriteStartArray("track");
            foreach (TrackItem item in edit.Track)
            {
                json.WriteStartObject();
                json.WritePropertyName("clip");
                WriteClip(json, item.Clip);
                if (item.Transition is Transition transition)
                {
                    json.WritePropertyName("transition");
                    WriteTransition(json, transition);
                }

                json.WriteEndObject();
            }

            json.WriteEndArray();
            if (edit.Layers.Count > 0)
            {
                json.WriteStartArray("layers");
                foreach (Layer layer in edit.Layers)
                {
                    WriteLayer(json, layer);
                }

                json.WriteEndArray();
            }

            WriteEffects(json, edit.Effects);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static VideoEdit Read(string text, Func<string, EditEffect>? effects, string? folder)
    {
        ArgumentNullException.ThrowIfNull(text);
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException e)
        {
            throw new FormatException($"The edit is not valid JSON: {e.Message}", e);
        }

        using (document)
        {
            Reader reader = new(effects, folder);
            JsonElement root = document.RootElement;
            Reader.Expect(root, JsonValueKind.Object, "the edit");
            VideoEdit edit = new();
            if (root.TryGetProperty("canvas", out JsonElement canvas))
            {
                edit.Canvas = Reader.Canvas(canvas);
            }

            if (!root.TryGetProperty("track", out JsonElement track) || track.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("The edit needs a \"track\" array of clips.");
            }

            int index = 0;
            foreach (JsonElement item in track.EnumerateArray())
            {
                string where = $"track[{index++}]";
                Reader.Expect(item, JsonValueKind.Object, where);
                if (!item.TryGetProperty("clip", out JsonElement clip))
                {
                    throw new FormatException($"{where} needs a \"clip\".");
                }

                Transition? transition = item.TryGetProperty("transition", out JsonElement t) ? Reader.Transition(t, where + ".transition") : null;
                edit.Track.Add(reader.Clip(clip, where + ".clip"), transition);
            }

            if (root.TryGetProperty("layers", out JsonElement layers))
            {
                index = 0;
                foreach (JsonElement layer in layers.EnumerateArray())
                {
                    edit.Layers.Add(reader.Layer(layer, $"layers[{index++}]"));
                }
            }

            foreach (EditEffect effect in reader.Effects(root, "the edit"))
            {
                edit.Effects.Add(effect);
            }

            return edit;
        }
    }

    private static void WriteClip(Utf8JsonWriter json, Clip clip)
    {
        json.WriteStartObject();
        switch (clip.Source)
        {
            case FileClipSource file:
                json.WriteString("open", file.Path);
                break;
            case StackClipSource stack:
                json.WriteString("stack", Name(stack.Layout));
                if (stack.Layout == StackLayout.Grid)
                {
                    json.WriteNumber("columns", stack.Columns);
                    json.WriteNumber("rows", stack.Rows);
                }

                json.WriteStartArray("clips");
                foreach (Clip child in stack.Clips)
                {
                    WriteClip(json, child);
                }

                json.WriteEndArray();
                if (stack.Options.Gap != 0)
                {
                    json.WriteNumber("gap", stack.Options.Gap);
                }

                if (stack.Options.Background != Rgba8.Black)
                {
                    json.WriteString("background", stack.Options.Background.ToHex());
                }

                if (stack.Options.Fit != ClipFit.Contain)
                {
                    json.WriteString("fit", Name(stack.Options.Fit));
                }

                if (stack.Options.Shortest)
                {
                    json.WriteBoolean("shortest", true);
                }

                if (stack.Options.MixAudio)
                {
                    json.WriteBoolean("mixAudio", true);
                }

                break;
            case StillClipSource { Path: string still } source:
                json.WriteString("still", still);
                json.WriteNumber("duration", Math.Round(source.Duration.Seconds, 9));
                break;
            case AnimationClipSource animation:
                json.WriteString("animation", animation.Path);
                break;
            case ColorClipSource color:
                json.WriteString("color", color.Color.ToHex());
                json.WriteNumber("duration", Math.Round(color.Duration.Seconds, 9));
                break;
            case StillClipSource:
                throw new NotSupportedException("A still made from an image in memory cannot be saved as JSON; make it from a file.");
            case AppClipSource:
                throw new NotSupportedException("A clip of an app's own frames cannot be saved as JSON.");
            default:
                throw new NotSupportedException("A clip that reads a stream cannot be saved as JSON; open it from a file.");
        }

        Time(json, "in", clip.In);
        Time(json, "out", clip.Out);
        Time(json, "fadeIn", clip.FadeInDuration, skipZero: true);
        Time(json, "fadeOut", clip.FadeOutDuration, skipZero: true);
        if (clip.FadeColor != Rgba8.Black)
        {
            json.WriteString("fadeColor", clip.FadeColor.ToHex());
        }

        if (clip.VolumeDecibels != 0)
        {
            json.WriteNumber("volume", clip.VolumeDecibels);
        }

        if (clip.IsMuted)
        {
            json.WriteBoolean("muted", true);
        }

        if (clip.SpeedFactor != 1)
        {
            json.WriteNumber("speed", clip.SpeedFactor);
            if (!clip.KeepsPitch)
            {
                json.WriteBoolean("keepPitch", false);
            }
        }

        if (clip.IsReversed)
        {
            json.WriteBoolean("reverse", true);
        }

        if (clip.LoopCount != 1)
        {
            json.WriteNumber("loop", clip.LoopCount);
        }

        Time(json, "hold", clip.HoldDuration, skipZero: true);
        if (clip.Freezes.Count > 0)
        {
            json.WriteStartArray("freeze");
            foreach (ClipFreeze freeze in clip.Freezes)
            {
                json.WriteStartArray();
                json.WriteNumberValue(Math.Round(freeze.At.Seconds, 9));
                json.WriteNumberValue(Math.Round(freeze.Duration.Seconds, 9));
                json.WriteEndArray();
            }

            json.WriteEndArray();
        }

        if (clip.Motion is PanZoomPath motion)
        {
            json.WriteStartObject("panZoom");
            Rectangle(json, "from", motion.From);
            Rectangle(json, "to", motion.To);
            if (motion.Easing != Easing.Linear)
            {
                json.WriteString("easing", Name(motion.Easing));
            }

            json.WriteEndObject();
        }

        WriteEffects(json, clip.Effects);
        json.WriteEndObject();
    }

    private static void WriteTransition(Utf8JsonWriter json, Transition transition)
    {
        if (transition.Kind == TransitionKind.Custom)
        {
            throw new NotSupportedException("An app's own transition cannot be saved as JSON.");
        }

        json.WriteStartObject();
        json.WriteString("kind", Name(transition.Kind));
        json.WriteNumber("duration", transition.Duration.Seconds);
        if (transition.Kind is TransitionKind.Wipe or TransitionKind.Slide)
        {
            json.WriteString("direction", Name(transition.Direction));
        }

        if (transition.Kind == TransitionKind.FadeThroughColor)
        {
            json.WriteString("color", transition.Color.ToHex());
        }

        if (transition.Softness != 0)
        {
            json.WriteNumber("softness", transition.Softness);
        }

        if (transition.Easing != Easing.Linear)
        {
            json.WriteString("easing", Name(transition.Easing));
        }

        if (transition.AudioCurve != FadeCurve.EqualPower)
        {
            json.WriteString("audioCurve", Name(transition.AudioCurve));
        }

        json.WriteEndObject();
    }

    private static void WriteLayer(Utf8JsonWriter json, Layer layer)
    {
        json.WriteStartObject();
        json.WritePropertyName("clip");
        WriteClip(json, layer.Clip);
        if (layer.Area is Rect area)
        {
            json.WriteStartArray("area");
            json.WriteNumberValue(area.X);
            json.WriteNumberValue(area.Y);
            json.WriteNumberValue(area.Width);
            json.WriteNumberValue(area.Height);
            json.WriteEndArray();
        }
        else
        {
            json.WriteString("anchor", Name(layer.Anchor));
            if (layer.Margin != 0)
            {
                json.WriteNumber("margin", layer.Margin);
            }

            if (layer.Scale != 1)
            {
                json.WriteNumber("scale", layer.Scale);
            }
        }

        if (layer.OpacityAmount != 1)
        {
            json.WriteNumber("opacity", layer.OpacityAmount);
        }

        if (layer.BlendMode != BlendMode.SourceOver)
        {
            json.WriteString("blend", Name(layer.BlendMode));
        }

        Time(json, "start", layer.Start, skipZero: true);
        Time(json, "end", layer.End);
        if (layer.HoldsLastFrame)
        {
            json.WriteBoolean("hold", true);
        }

        Time(json, "fadeIn", layer.FadeInDuration, skipZero: true);
        Time(json, "fadeOut", layer.FadeOutDuration, skipZero: true);
        if (layer.ChromaKey is ChromaKey key)
        {
            json.WriteStartObject("key");
            json.WriteString("color", key.Color.ToHex());
            json.WriteNumber("similarity", key.Similarity);
            json.WriteNumber("blend", key.Blend);
            json.WriteNumber("despill", key.Despill);
            if (key.SpillColor is DespillColor spill)
            {
                json.WriteString("spill", Name(spill));
            }

            if (key.Expand != 0)
            {
                json.WriteNumber("expand", key.Expand);
            }

            json.WriteEndObject();
        }

        if (layer.Animations.Count > 0)
        {
            json.WriteStartObject("animate");
            foreach ((LayerProperty property, IReadOnlyList<Keyframe> keys) in layer.Animations.OrderBy(a => a.Key))
            {
                json.WriteStartArray(Name(property));
                foreach (Keyframe frame in keys)
                {
                    json.WriteStartArray();
                    json.WriteNumberValue(frame.Time.Seconds);
                    json.WriteNumberValue(frame.Value);
                    if (frame.Easing != Easing.Linear)
                    {
                        json.WriteStringValue(Name(frame.Easing));
                    }

                    json.WriteEndArray();
                }

                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        WriteEffects(json, layer.Effects);
        json.WriteEndObject();
    }

    private static void WriteEffects(Utf8JsonWriter json, IEnumerable<EditEffect> effects)
    {
        List<EditEffect> list = [.. effects];
        if (list.Count == 0)
        {
            return;
        }

        json.WriteStartArray("effects");
        foreach (EditEffect effect in list)
        {
            if (effect.Name is null)
            {
                throw new NotSupportedException("An effect without a Name cannot be saved as JSON; give it one that a resolver can turn back into the effect.");
            }

            json.WriteStartObject();
            json.WriteString("name", effect.Name);
            Time(json, "start", effect.Start, skipZero: true);
            Time(json, "end", effect.End);
            json.WriteEndObject();
        }

        json.WriteEndArray();
    }

    private static void Rectangle(Utf8JsonWriter json, string name, System.Drawing.RectangleF r)
    {
        json.WriteStartArray(name);
        json.WriteNumberValue(r.X);
        json.WriteNumberValue(r.Y);
        json.WriteNumberValue(r.Width);
        json.WriteNumberValue(r.Height);
        json.WriteEndArray();
    }

    private static void Time(Utf8JsonWriter json, string name, MediaTime? time, bool skipZero = false)
    {
        if (time is MediaTime t && !(skipZero && t == MediaTime.Zero))
        {
            json.WriteNumber(name, Math.Round(t.Seconds, 9));
        }
    }

    private static string Name<T>(T value)
        where T : struct, Enum
    {
        string text = value.ToString();
        return char.ToLowerInvariant(text[0]) + text[1..];
    }

    /// <summary>Reads the parts of an edit, naming the place of each problem.</summary>
    private sealed class Reader(Func<string, EditEffect>? effects, string? folder)
    {
        public static EditCanvas Canvas(JsonElement e)
        {
            Expect(e, JsonValueKind.Object, "canvas");
            EditCanvas canvas = new(Int(e, "width", "canvas") ?? throw Missing("canvas", "width"), Int(e, "height", "canvas") ?? throw Missing("canvas", "height"),
                Text(e, "frameRate", "canvas") is string rate ? Rational.Parse(rate) : new Rational(30, 1));
            return canvas with
            {
                Fit = Enum<ClipFit>(e, "fit", "canvas") ?? ClipFit.Contain,
                Background = Color(e, "background", "canvas") ?? Rgba8.Black,
                SampleRate = Int(e, "sampleRate", "canvas") ?? 48000,
                AudioLayout = Int(e, "channels", "canvas") is int channels ? ChannelLayout.ForChannelCount(channels) : ChannelLayout.Stereo,
            };
        }

        public Clip Clip(JsonElement e, string where)
        {
            Expect(e, JsonValueKind.Object, where);
            Clip clip;
            if (Text(e, "open", where) is string path)
            {
                clip = Editing.Clip.Open(folder is null || System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(folder, path));
            }
            else if (Enum<StackLayout>(e, "stack", where) is StackLayout layout)
            {
                if (!e.TryGetProperty("clips", out JsonElement list) || list.ValueKind != JsonValueKind.Array)
                {
                    throw new FormatException($"{where} needs a \"clips\" array.");
                }

                int i = 0;
                Clip[] clips = [.. list.EnumerateArray().Select(c => Clip(c, $"{where}.clips[{i++}]"))];
                StackOptions options = new()
                {
                    Gap = Int(e, "gap", where) ?? 0,
                    Background = Color(e, "background", where) ?? Rgba8.Black,
                    Fit = Enum<ClipFit>(e, "fit", where) ?? ClipFit.Contain,
                    Shortest = Bool(e, "shortest", where) ?? false,
                    MixAudio = Bool(e, "mixAudio", where) ?? false,
                };
                clip = layout switch
                {
                    StackLayout.Horizontal => Stack.Horizontal(clips, options),
                    StackLayout.Vertical => Stack.Vertical(clips, options),
                    _ => Stack.Grid(Int(e, "columns", where) ?? throw Missing(where, "columns"), Int(e, "rows", where) ?? throw Missing(where, "rows"), clips, options),
                };
            }
            else if (Text(e, "still", where) is string still)
            {
                clip = Editing.Clip.Still(Rooted(still), Seconds(e, "duration", where) ?? throw Missing(where, "duration"));
            }
            else if (Text(e, "animation", where) is string animation)
            {
                clip = Editing.Clip.Animation(Rooted(animation));
            }
            else if (Color(e, "color", where) is Rgba8 color)
            {
                clip = Editing.Clip.Color(color, Seconds(e, "duration", where) ?? throw Missing(where, "duration"));
            }
            else
            {
                throw new FormatException($"{where} needs \"open\" (a file), \"still\", \"animation\", \"color\" or \"stack\" (horizontal, vertical or grid).");
            }

            if (Seconds(e, "in", where) is MediaTime start)
            {
                clip = clip.Trim(start, Seconds(e, "out", where));
            }
            else if (Seconds(e, "out", where) is MediaTime end)
            {
                clip = clip.Trim(MediaTime.Zero, end);
            }

            Rgba8? fade = Color(e, "fadeColor", where);
            if (Seconds(e, "fadeIn", where) is MediaTime fadeIn)
            {
                clip = clip.FadeIn(fadeIn, fade);
            }

            if (Seconds(e, "fadeOut", where) is MediaTime fadeOut)
            {
                clip = clip.FadeOut(fadeOut, fade);
            }

            if (Number(e, "volume", where) is double volume)
            {
                clip = clip.Volume(volume);
            }

            if (Bool(e, "muted", where) == true)
            {
                clip = clip.Muted();
            }

            if (Number(e, "speed", where) is double speed)
            {
                clip = clip.Speed(speed, Bool(e, "keepPitch", where) ?? true);
            }

            if (Bool(e, "reverse", where) == true)
            {
                clip = clip.Reverse();
            }

            if (Int(e, "loop", where) is int loop)
            {
                clip = clip.Loop(loop);
            }

            if (Seconds(e, "hold", where) is MediaTime hold)
            {
                clip = clip.Hold(hold);
            }

            if (e.TryGetProperty("freeze", out JsonElement freezes))
            {
                foreach (JsonElement freeze in freezes.EnumerateArray())
                {
                    double[] pair = [.. freeze.EnumerateArray().Select(v => v.GetDouble())];
                    if (pair.Length != 2)
                    {
                        throw new FormatException($"{where}.freeze: each freeze is [at, duration] in seconds.");
                    }

                    clip = clip.Freeze(MediaTime.FromSeconds(pair[0]), MediaTime.FromSeconds(pair[1]));
                }
            }

            if (e.TryGetProperty("panZoom", out JsonElement panZoom))
            {
                string at = where + ".panZoom";
                clip = clip.PanZoom(RectangleOf(panZoom, "from", at), RectangleOf(panZoom, "to", at), Enum<Easing>(panZoom, "easing", at) ?? Easing.Linear);
            }

            return clip with { Effects = [.. Effects(e, where)] };
        }

        public static Transition Transition(JsonElement e, string where)
        {
            Expect(e, JsonValueKind.Object, where);
            TransitionKind kind = Enum<TransitionKind>(e, "kind", where) ?? throw Missing(where, "kind");
            if (kind == TransitionKind.Custom)
            {
                throw new FormatException($"{where}: a custom transition cannot come from JSON.");
            }

            MediaTime duration = Seconds(e, "duration", where) ?? throw Missing(where, "duration");
            return Editing.Transition.Of(kind, duration) with
            {
                Direction = Enum<TransitionDirection>(e, "direction", where) ?? TransitionDirection.Left,
                Color = Color(e, "color", where) ?? Rgba8.Black,
                Softness = Number(e, "softness", where) ?? 0,
                Easing = Enum<Easing>(e, "easing", where) ?? Easing.Linear,
                AudioCurve = Enum<FadeCurve>(e, "audioCurve", where) ?? FadeCurve.EqualPower,
            };
        }

        public Layer Layer(JsonElement e, string where)
        {
            Expect(e, JsonValueKind.Object, where);
            if (!e.TryGetProperty("clip", out JsonElement clip))
            {
                throw Missing(where, "clip");
            }

            Layer layer = Editing.Layer.Of(Clip(clip, where + ".clip"));
            if (e.TryGetProperty("area", out JsonElement area))
            {
                int[] r = [.. area.EnumerateArray().Select(v => v.GetInt32())];
                if (r.Length != 4)
                {
                    throw new FormatException($"{where}.area must be [x, y, width, height].");
                }

                layer = layer.Place(new Rect(r[0], r[1], r[2], r[3]));
            }
            else
            {
                layer = layer.Place(Enum<Anchor>(e, "anchor", where) ?? Anchor.Center, Int(e, "margin", where) ?? 0, Number(e, "scale", where) ?? 1);
            }

            layer = layer.Opacity((float)(Number(e, "opacity", where) ?? 1)).Blend(Enum<BlendMode>(e, "blend", where) ?? BlendMode.SourceOver);
            MediaTime? start = Seconds(e, "start", where), end = Seconds(e, "end", where);
            if (start is not null || end is not null)
            {
                layer = layer.Between(start ?? MediaTime.Zero, end);
            }

            if (Bool(e, "hold", where) == true)
            {
                layer = layer.HoldLastFrame();
            }

            if (Seconds(e, "fadeIn", where) is MediaTime fadeIn)
            {
                layer = layer.FadeIn(fadeIn);
            }

            if (Seconds(e, "fadeOut", where) is MediaTime fadeOut)
            {
                layer = layer.FadeOut(fadeOut);
            }

            if (e.TryGetProperty("key", out JsonElement key))
            {
                string at = where + ".key";
                layer = layer.Key(new ChromaKey(Color(key, "color", at) ?? throw Missing(at, "color"))
                {
                    Similarity = (float)(Number(key, "similarity", at) ?? 0.05),
                    Blend = (float)(Number(key, "blend", at) ?? 0.35),
                    Despill = (float)(Number(key, "despill", at) ?? 0.5),
                    SpillColor = Enum<DespillColor>(key, "spill", at),
                    Expand = (float)(Number(key, "expand", at) ?? 0),
                });
            }

            if (e.TryGetProperty("animate", out JsonElement animate))
            {
                foreach (JsonProperty property in animate.EnumerateObject())
                {
                    string at = $"{where}.animate.{property.Name}";
                    LayerProperty which = Parse<LayerProperty>(property.Name, at);
                    Keyframe[] keys = [.. property.Value.EnumerateArray().Select(k =>
                    {
                        JsonElement[] parts = [.. k.EnumerateArray()];
                        if (parts.Length is < 2 or > 3)
                        {
                            throw new FormatException($"{at}: a keyframe is [seconds, value] or [seconds, value, easing].");
                        }

                        return new Keyframe(MediaTime.FromSeconds(parts[0].GetDouble()), parts[1].GetDouble(), parts.Length == 3 ? Parse<Easing>(parts[2].GetString()!, at) : Easing.Linear);
                    })];
                    layer = layer.Animate(which, keys);
                }
            }

            return layer with { Effects = [.. Effects(e, where)] };
        }

        public IEnumerable<EditEffect> Effects(JsonElement e, string where)
        {
            if (!e.TryGetProperty("effects", out JsonElement list))
            {
                yield break;
            }

            int i = 0;
            foreach (JsonElement item in list.EnumerateArray())
            {
                string at = $"{where}.effects[{i++}]";
                string name = Text(item, "name", at) ?? throw Missing(at, "name");
                if (effects is null)
                {
                    throw new FormatException($"{at} names the effect \"{name}\", but no effect resolver was given.");
                }

                EditEffect effect = effects(name) with { Name = name };
                MediaTime? start = Seconds(item, "start", at), end = Seconds(item, "end", at);
                yield return start is null && end is null ? effect : effect.Between(start ?? MediaTime.Zero, end);
            }
        }

        public static void Expect(JsonElement e, JsonValueKind kind, string where)
        {
            if (e.ValueKind != kind)
            {
                throw new FormatException($"{where} must be a JSON {kind.ToString().ToLowerInvariant()}.");
            }
        }

        private string Rooted(string path) => folder is null || System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(folder, path);

        private static System.Drawing.RectangleF RectangleOf(JsonElement e, string name, string where)
        {
            if (!e.TryGetProperty(name, out JsonElement value))
            {
                throw Missing(where, name);
            }

            float[] r = [.. value.EnumerateArray().Select(v => (float)v.GetDouble())];
            return r.Length == 4 ? new System.Drawing.RectangleF(r[0], r[1], r[2], r[3]) : throw new FormatException($"{where}.{name} must be [x, y, width, height] as fractions of the picture.");
        }

        private static FormatException Missing(string where, string name) => new($"{where} needs \"{name}\".");

        private static T Parse<T>(string text, string where)
            where T : struct, Enum => System.Enum.TryParse(text, ignoreCase: true, out T value) && System.Enum.IsDefined(value)
                ? value
                : throw new FormatException($"{where}: \"{text}\" is not one of {string.Join(", ", System.Enum.GetNames<T>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]))}.");

        private static JsonElement? Get(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement value) && value.ValueKind != JsonValueKind.Null ? value : null;

        private static string? Text(JsonElement e, string name, string where) => Get(e, name) is JsonElement v
            ? v.ValueKind == JsonValueKind.String ? v.GetString() : throw new FormatException($"{where}.{name} must be a string.")
            : null;

        private static double? Number(JsonElement e, string name, string where) => Get(e, name) is JsonElement v
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble() : throw new FormatException($"{where}.{name} must be a number.")
            : null;

        private static int? Int(JsonElement e, string name, string where) => Number(e, name, where) is double d
            ? d == Math.Floor(d) ? (int)d : throw new FormatException($"{where}.{name} must be a whole number.")
            : null;

        private static bool? Bool(JsonElement e, string name, string where) => Get(e, name) is JsonElement v
            ? v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : throw new FormatException($"{where}.{name} must be true or false.")
            : null;

        private static MediaTime? Seconds(JsonElement e, string name, string where) => Number(e, name, where) is double s ? MediaTime.FromSeconds(s) : null;

        private static Rgba8? Color(JsonElement e, string name, string where) => Text(e, name, where) is string hex
            ? Rgba8.TryFromHex(hex, out Rgba8 color) ? color : throw new FormatException($"{where}.{name}: \"{hex}\" is not a hex colour such as #00b140.")
            : null;

        private static T? Enum<T>(JsonElement e, string name, string where)
            where T : struct, Enum => Text(e, name, where) is string text ? Parse<T>(text, $"{where}.{name}") : null;
    }
}
