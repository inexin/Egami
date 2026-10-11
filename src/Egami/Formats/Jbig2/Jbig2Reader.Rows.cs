using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Metadata;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Formats.Jbig2;

/// <content>
/// Streaming: a page is held packed, one bit a pixel, and rows are expanded as they are asked for. A striped page is decoded a
/// stripe at a time and only the current stripe is held, so a long fax or scan never needs its whole page in memory.
/// </content>
internal sealed partial class Jbig2Reader : IRowSourceReader
{
    public RowSource Open(EgamiInput input, ReaderOptions options)
    {
        (byte[] data, int length, bool _) = InputBytes.ReadAll(input);
        try
        {
            (Jbig2Decoder decoder, int page) = Prepare(input, data, length, options);
            (int width, int height) = decoder.PageSize(page);
            input.Limits.CheckFrameSize(Math.Max(width, 1), Math.Max(height, 1));
            Jbig2Decoder.Jbig2PageSession session = decoder.OpenPage(page, striped: true);
            return new Jbig2Rows(input, data, decoder, page, session, options);
        }
        catch (Exception e) when (e is InvalidDataException or Jbig2MissingSegmentException or EndOfStreamException)
        {
            ArrayPool<byte>.Shared.Return(data);
            input.Dispose();
            throw Map(input, e);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(data);
            input.Dispose();
            throw;
        }
    }

    /// <summary>One JBIG2 page, decoded whole or a stripe at a time, its rows expanded to <see cref="Gray8"/> on demand.</summary>
    private sealed class Jbig2Rows : RowSource
    {
        private readonly EgamiInput _input;
        private readonly Jbig2Decoder.Jbig2PageSession _session;
        private readonly Jbig2Decoder _decoder;
        private readonly int _page;
        private readonly bool _strict;
        private byte[]? _data;
        private byte[]? _row;
        private bool _started;
        private bool _ended;
        private int _warned;

        public Jbig2Rows(EgamiInput input, byte[] data, Jbig2Decoder decoder, int page, Jbig2Decoder.Jbig2PageSession session, ReaderOptions options)
        {
            _input = input;
            _data = data;
            _decoder = decoder;
            _page = page;
            _session = session;
            _strict = options.Recovery == RecoveryMode.Strict;
            _row = ArrayPool<byte>.Shared.Rent(Math.Max(1, session.Width));
            Metadata.FormatName = Jbig2Format.Info.Name;
            Fill(Metadata, decoder, page, session.Page, options);
        }

        public override int StoredWidth => _session.Width;

        public override int StoredHeight => _session.Height;

        public override int NativeScale => 1;

        public override int MaxNativeScale => 1;

        public override PixelLayoutInfo Layout => Gray8.Layout;

        public override RasterMetadata Metadata { get; } = new();

        public override bool MetadataComplete => _session.Done;

        public override int? FrameCount => 1;

        public override RasterAccess Access => RasterAccess.Streamed;

        public override long WorkingBytes => WorkingBytesOf(_session.Width, _session.Window.Height);

        public override TResult WithNaturalType<TResult>(INaturalTypeVisitor<TResult> visitor) => visitor.Visit<Gray8>();

        public override FrameMetadata? NextFrame()
        {
            if (!_started)
            {
                _started = true;
                return new FrameMetadata();
            }

            EndFrame();
            return null;
        }

        public override void ReadRow<TPixel>(int y, int x, Span<TPixel> destination)
        {
            try
            {
                while (y > _session.Final && _session.Step(_input.CancellationToken))
                {
                }
            }
            catch (Exception e) when (e is InvalidDataException or Jbig2MissingSegmentException or EndOfStreamException)
            {
                throw Map(_input, e);
            }

            Report();
            Span<byte> gray = _row.AsSpan(0, _session.Width);
            Expand(_session.Row(y), gray);
            PixelConvert.Convert<Gray8, TPixel>(MemoryMarshal.Cast<byte, Gray8>(gray).Slice(x, destination.Length), destination);
        }

        public override void EndFrame()
        {
            if (_ended)
            {
                return;
            }

            _ended = true;
            if (_strict)
            {
                // Strict loading checks the whole page, as Load does, even when fewer rows were asked for.
                try
                {
                    while (_session.Step(_input.CancellationToken))
                    {
                    }
                }
                catch (Exception e) when (e is InvalidDataException or Jbig2MissingSegmentException or EndOfStreamException)
                {
                    throw Map(_input, e);
                }
            }

            Report();
        }

        protected override void Dispose(bool disposing)
        {
            if (_data is not null)
            {
                ArrayPool<byte>.Shared.Return(_data);
                _data = null;
                ArrayPool<byte>.Shared.Return(_row!);
                _row = null;
                _input.Dispose();
            }
        }

        /// <summary>Copies the decode's new warnings and comments into the metadata.</summary>
        private void Report()
        {
            List<(string Id, string Message)> warnings = _session.Page.Warnings;
            for (; _warned < warnings.Count; _warned++)
            {
                Metadata.WarningList.Add(warnings[_warned].Id, warnings[_warned].Message);
            }

            if (_session.Done && Metadata.FormatDetails is Jbig2Details details)
            {
                details.Regions = _session.Page.Regions;
                if (details.Comments.Count == 0)
                {
                    foreach (KeyValuePair<string, string> comment in _session.Page.Comments)
                    {
                        details.Comments.Add(comment);
                    }
                }
            }

            _ = _decoder;
            _ = _page;
        }
    }

    /// <summary>Returns what a streaming read holds: the packed rows kept and one row of gray bytes.</summary>
    internal static long WorkingBytesOf(int width, int rows) => (((width + 63L) >> 6) * 8 * rows) + width;
}
