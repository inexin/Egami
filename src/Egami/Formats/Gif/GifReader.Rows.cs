using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Gif;

/// <content>
/// Streaming: frames are composed one at a time on the reused canvas, each frame's indices decoded into one buffer (interlaced
/// rows land where they belong), and rows are read from the canvas. Held memory is the canvas, a restore copy and the index
/// buffer, whatever the frame count.
/// </content>
internal sealed partial class GifReader
{
    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        if (!Streams(options))
        {
            // Frames counted from the end need the frame count, which only reading every frame gives.
            return BufferedRowSource.Read(GifFormat.Info, input, options);
        }

        Decoding<Rgba8> state = new(input, options, probe: false);
        try
        {
            state.BeginStreaming();
            return new GifRows(state, input);
        }
        catch
        {
            state.Dispose();
            throw;
        }
    }

    public HeaderState HeaderStatus(EgamiInput input, ReaderOptions options)
    {
        return !Streams(options) ? HeaderState.WaitsForEnd : HeaderArrived(input, options) ? HeaderState.Arrived : HeaderState.NotYet;
    }

    private static bool HeaderArrived(EgamiInput input, ReaderOptions options)
    {
        if (!Streams(options))
        {
            return false;
        }

        // The screen descriptor and its global color table, which is all Open reads.
        Span<byte> screen = stackalloc byte[13];
        if (!input.TryPeekAt(0, screen))
        {
            return false;
        }

        int table = (screen[10] & 0x80) != 0 ? 3 * (2 << (screen[10] & 7)) : 0;
        return input.Available >= 13 + table;
    }

    /// <summary>Returns whether <see cref="Open"/> streams with these options, rather than reading every frame first.</summary>
    private static bool Streams(ReaderOptions options)
    {
        Range frames = options.Frames;
        return !frames.Start.IsFromEnd && !(frames.End.IsFromEnd && frames.End.Value > 0);
    }

    /// <summary>Returns what a streaming read holds: the canvas and a restore copy, and one index per pixel for the largest frame.</summary>
    private static long WorkingBytesFor(int width, int height)
    {
        return ((long)width * height * 9) + 4096;
    }

    /// <summary>A GIF read a frame at a time.</summary>
    private sealed class GifRows(Decoding<Rgba8> state, EgamiInput input) : RowSource
    {
        private PixelPlane<Rgba8>? _preview;
        private int _previewRow = int.MaxValue;
        private bool _disposed;

        public override int StoredWidth => state.Width;

        public override int StoredHeight => state.Height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => Rgba8.Layout;

        public override RasterMetadata Metadata => state.Metadata;

        public override bool MetadataComplete => state.Finished;

        public override int? FrameCount => null;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => WorkingBytesFor(state.Width, state.Height);

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => visitor.Visit<Rgba8>();

        public override FrameMetadata? NextFrame() => state.NextFrame();

        public override bool CanPreview => true;

        public override void PreviewRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            // Rows come in order from the region's top: the preview is built afresh when they start over.
            if (y <= _previewRow || _preview is null)
            {
                _preview = state.BuildPreview();
            }

            _previewRow = y;
            PixelConvert.Convert<Rgba8, TPixel>(_preview.Row(y).Slice(x, destination.Length), destination);
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            PixelConvert.Convert<Rgba8, TPixel>(state.Canvas!.Row(y).Slice(x, destination.Length), destination);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                state.Dispose();
                input.ReturnUnreadBytes();
                input.Dispose();
            }
        }
    }
}
