namespace AutoMarkerReID.Windows;

public sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
    }

    public static SingleInstanceGuard TryAcquire(string applicationId)
    {
        var mutex = new Mutex(initiallyOwned: true, $"Local\\{applicationId}", out var createdNew);
        return new SingleInstanceGuard(mutex, createdNew);
    }

    public bool IsOwner => _ownsMutex;

    public void Dispose()
    {
        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
