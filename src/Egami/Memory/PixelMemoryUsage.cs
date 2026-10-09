using System.Diagnostics;

namespace Egami;

/// <summary>
/// What a <see cref="PooledPixelMemorySource"/> has handed out and not yet had back, and opt-in tracking of blocks that were never
/// disposed. Get it from <see cref="PooledPixelMemorySource.Usage"/>; most callers want <c>PooledPixelMemorySource.Shared.Usage</c>.
/// </summary>
/// <remarks>
/// The counts are always kept, at the cost of one atomic addition per allocation and per return. Leak tracking adds a small
/// object with a finalizer to every block allocated while it is on, so turn it on only while looking for a leak.
/// </remarks>
/// <example>
/// <code>
/// PixelMemoryUsage usage = PooledPixelMemorySource.Shared.Usage;
/// usage.CaptureStackTraces = true;
/// usage.LeakDetected += (_, leak) => Console.WriteLine($"{leak.Bytes} bytes never disposed, allocated at {leak.StackTrace}");
/// usage.TrackLeaks = true;
/// RunTheCodeUnderSuspicion();
/// Console.WriteLine($"{usage.LiveBlocks} blocks, {usage.LiveBytes} bytes still out");
/// </code>
/// </example>
public sealed class PixelMemoryUsage
{
    /// <summary>Bytes in the low 40 bits (up to 1 TB) and blocks above them (up to 16 million), so one atomic add counts both.</summary>
    private const int BlockShift = 40;
    private const long ByteMask = (1L << BlockShift) - 1;

    private long _state;
    private volatile bool _trackLeaks;
    private volatile bool _captureStackTraces;
    private readonly Counted _untracked;

    internal PixelMemoryUsage()
    {
        _untracked = new Counted(this);
    }

    /// <summary>
    /// Occurs when a block allocated while <see cref="TrackLeaks"/> was on is collected by the garbage collector without having
    /// been disposed. Handlers run on the finalizer thread, so they must be quick, must not throw, and must not touch objects that
    /// may already be finalized.
    /// </summary>
    public event EventHandler<PixelMemoryLeakEventArgs>? LeakDetected;

    /// <summary>Gets the number of blocks handed out and not yet disposed.</summary>
    public long LiveBlocks => Interlocked.Read(ref _state) >> BlockShift;

    /// <summary>Gets the bytes in blocks handed out and not yet disposed.</summary>
    public long LiveBytes => Interlocked.Read(ref _state) & ByteMask;

    /// <summary>
    /// Gets or sets whether blocks allocated from now on are tracked, so that one collected without being disposed raises
    /// <see cref="LeakDetected"/> and leaves the counts. Defaults to <see langword="false"/>.
    /// </summary>
    public bool TrackLeaks
    {
        get => _trackLeaks;
        set => _trackLeaks = value;
    }

    /// <summary>
    /// Gets or sets whether tracked blocks record the stack trace of their allocation for <see cref="LeakDetected"/>. It makes
    /// every allocation much slower. Defaults to <see langword="false"/>.
    /// </summary>
    public bool CaptureStackTraces
    {
        get => _captureStackTraces;
        set => _captureStackTraces = value;
    }

    /// <summary>Counts a new block. The result, the usage itself or a leak tracker, is told when the block is returned.</summary>
    internal Counted Allocated(int bytes)
    {
        Interlocked.Add(ref _state, (1L << BlockShift) + bytes);
        return _trackLeaks ? new LeakTracker(this, bytes, _captureStackTraces ? new StackTrace(2, fNeedFileInfo: true).ToString() : null) : _untracked;
    }

    private void Leaked(int bytes, string? stackTrace)
    {
        Interlocked.Add(ref _state, -((1L << BlockShift) + bytes));
        try
        {
            LeakDetected?.Invoke(this, new PixelMemoryLeakEventArgs(bytes, stackTrace));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // An exception on the finalizer thread would end the process; a broken handler must not do that.
            Debug.Fail($"A LeakDetected handler threw: {e}");
        }
    }

    /// <summary>What a block holds to report its return: one shared instance per usage, or a leak tracker of its own.</summary>
    internal class Counted(PixelMemoryUsage usage)
    {
        protected PixelMemoryUsage Usage => usage;

        /// <summary>Counts the block's return.</summary>
        public virtual void Released(int bytes)
        {
            Interlocked.Add(ref usage._state, -((1L << BlockShift) + bytes));
        }
    }

    /// <summary>Reports its block as leaked when collected; returning the block cancels it.</summary>
    internal sealed class LeakTracker(PixelMemoryUsage usage, int bytes, string? stackTrace) : Counted(usage)
    {
        ~LeakTracker()
        {
            Usage.Leaked(bytes, stackTrace);
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA1816", Justification = "The tracker has no resources; cancelling it is exactly not running its finalizer.")]
        public override void Released(int bytes)
        {
            base.Released(bytes);
            GC.SuppressFinalize(this);
        }
    }
}

/// <summary>Describes a pixel memory block that was collected without being disposed.</summary>
public sealed class PixelMemoryLeakEventArgs : EventArgs
{
    internal PixelMemoryLeakEventArgs(long bytes, string? stackTrace)
    {
        Bytes = bytes;
        StackTrace = stackTrace;
    }

    /// <summary>Gets the size of the block in bytes.</summary>
    public long Bytes { get; }

    /// <summary>Gets the stack trace of the allocation, or <see langword="null"/> if stack traces were not captured.</summary>
    public string? StackTrace { get; }
}
