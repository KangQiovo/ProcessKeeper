namespace ProcessKeeper.Core;

/// <summary>Coalesces edits and serializes durable writes away from the owning UI thread.</summary>
public sealed class PerformancePreferencesWriter
{
    private readonly object _gate = new();
    private readonly Action<PerformancePreferences> _save;
    private readonly TimeSpan _delay;
    private PerformancePreferences? _pending;
    private PerformancePreferences? _failedValue;
    private CancellationTokenSource? _wake;
    private Task _worker = Task.CompletedTask;
    private Exception? _error;
    private long _revision;
    private bool _running, _flush;

    public event Action<Exception>? SaveFailed;
    public bool HasPending { get { lock (_gate) return _running || _pending is not null || _failedValue is not null; } }

    public PerformancePreferencesWriter(Action<PerformancePreferences> save, TimeSpan? delay = null)
    {
        _save = save ?? throw new ArgumentNullException(nameof(save));
        _delay = delay ?? TimeSpan.FromMilliseconds(350);
        if (_delay < TimeSpan.Zero || _delay > TimeSpan.FromSeconds(10)) throw new ArgumentOutOfRangeException(nameof(delay));
    }

    public void Schedule(PerformancePreferences value)
    {
        value = PerformancePreferencesStore.Validate(value);
        lock (_gate)
        {
            _pending = value;
            _failedValue = null;
            _revision++;
            _wake?.Cancel();
            if (_running) return;
            _running = true;
            _worker = Task.Run(DrainAsync);
        }
    }

    /// <summary>Expedites the latest edit and waits for all previously queued writes.</summary>
    public Task FlushAsync()
    {
        lock (_gate)
        {
            if (!_running && _failedValue is not null)
            {
                _pending = _failedValue;
                _failedValue = null;
                _revision++;
                _running = true;
                _worker = Task.Run(DrainAsync);
            }
            _flush = _running;
            _wake?.Cancel();
            return ObserveAsync(_worker);
        }
    }

    /// <summary>Only an explicit discard decision may clear a failed, idle save.</summary>
    public bool TryDiscardFailedSave()
    {
        lock (_gate)
        {
            if (_running || _pending is not null) return false;
            _failedValue = null;
            _error = null;
            return true;
        }
    }

    private async Task ObserveAsync(Task worker)
    {
        await worker.ConfigureAwait(false);
        Exception? error;
        lock (_gate) error = _error;
        if (error is not null) throw new IOException(error.Message, error);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            CancellationTokenSource wake;
            long revision;
            lock (_gate)
            {
                if (_pending is null) { _running = false; _flush = false; return; }
                revision = _revision;
                _wake = wake = new CancellationTokenSource();
                if (_flush) wake.Cancel();
            }
            try { await Task.Delay(_delay, wake.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            PerformancePreferences? value;
            lock (_gate)
            {
                _wake = null;
                wake.Dispose();
                if (!_flush && revision != _revision) continue;
                value = _pending;
                _pending = null;
            }
            if (value is null) continue;
            try
            {
                _save(value);
                lock (_gate) { _error = null; _failedValue = null; }
            }
            catch (Exception error)
            {
                lock (_gate) { _error = error; _failedValue = value; }
                try { SaveFailed?.Invoke(error); } catch { }
            }
        }
    }
}
