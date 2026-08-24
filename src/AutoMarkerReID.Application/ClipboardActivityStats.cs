namespace AutoMarkerReID.Application;

public sealed class ClipboardActivityStats
{
    private long _received;
    private long _skipped;

    public long Received => Interlocked.Read(ref _received);
    public long Skipped => Interlocked.Read(ref _skipped);

    public event EventHandler? Changed;

    public void RecordReceived()
    {
        Interlocked.Increment(ref _received);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordSkipped()
    {
        Interlocked.Increment(ref _skipped);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
