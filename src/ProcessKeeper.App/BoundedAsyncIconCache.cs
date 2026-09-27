namespace ProcessKeeper.App;

/// <summary>A temporary admission failure. Visible rows should retry on a later refresh.</summary>
public sealed class IconRequestDeferredException : Exception
{
    public IconRequestDeferredException() : base("Icon queue is busy; retry this visible row after another refresh.") { }
}

internal readonly record struct IconCacheDiagnostics(int Cached, int Active, int Queued, int InFlight);

/// <summary>
/// Bounded whole-operation scheduling and LRU retention. Queued requests own no worker
/// task until dispatched. Admission failure is explicit and is never cached. The
/// dispatcher controls the factory's execution context; this class creates no UI object.
/// </summary>
internal sealed class BoundedAsyncIconCache<T> : IDisposable
{
    private readonly object _gate = new();
    private readonly int _capacity, _concurrency, _maximumQueued;
    private readonly Func<string, CancellationToken, Task<T>> _factory;
    private readonly Func<Action, bool> _dispatch;
    private readonly Dictionary<string, Request> _inFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Task<T> Task, LinkedListNode<string> Node)> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<string> _lru = new();
    private readonly Queue<Request> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationToken _token;
    private readonly Task<T> _deferred = Task.FromException<T>(new IconRequestDeferredException());
    private readonly Task<T> _cancelled = Task.FromCanceled<T>(new CancellationToken(true));
    private int _active;
    private bool _disposed, _cancellationCompleted, _stopDisposed;

    internal BoundedAsyncIconCache(int capacity, int concurrency, int maximumQueued,
        Func<string, CancellationToken, Task<T>> factory, Func<Action, bool> dispatch)
    {
        if (capacity < 1 || concurrency < 1 || maximumQueued < 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity; _concurrency = concurrency; _maximumQueued = maximumQueued;
        _factory = factory; _dispatch = dispatch; _token = _stop.Token;
        // Mark the reusable rejection observed even if a caller chooses not to await it.
        _ = _deferred.Exception;
    }

    internal IconCacheDiagnostics Diagnostics
    {
        get { lock (_gate) return new(_cache.Count, _active, _queue.Count, _inFlight.Count); }
    }

    internal Task<T> GetAsync(string key)
    {
        Request? start = null;
        Task<T> result;
        lock (_gate)
        {
            if (_disposed) return _cancelled;
            if (_cache.TryGetValue(key, out var cached))
            {
                _lru.Remove(cached.Node); _lru.AddLast(cached.Node);
                return cached.Task;
            }
            if (_inFlight.TryGetValue(key, out var existing)) return existing.Completion.Task;
            var immediately = _active < _concurrency && _queue.Count == 0;
            if (!immediately && _queue.Count >= _maximumQueued) return _deferred;
            var request = new Request(key);
            _inFlight.Add(key, request);
            if (immediately) { _active++; start = request; }
            else _queue.Enqueue(request);
            result = request.Completion.Task;
        }
        if (start is not null) Start(start);
        return result;
    }

    private void Start(Request request)
    {
        try
        {
            if (!_dispatch(() => _ = ExecuteAsync(request)))
                Complete(request, default!, new OperationCanceledException("The icon owner dispatcher has stopped."));
        }
        catch (Exception ex) { Complete(request, default!, ex); }
    }

    private async Task ExecuteAsync(Request request)
    {
        T result = default!;
        Exception? failure = null;
        try
        {
            _token.ThrowIfCancellationRequested();
            result = await _factory(request.Key, _token);
            _token.ThrowIfCancellationRequested();
        }
        catch (Exception ex) { failure = ex; }
        Complete(request, result, failure);
    }

    private void Complete(Request request, T value, Exception? failure)
    {
        Request? next = null;
        bool cancelled;
        lock (_gate)
        {
            _active--;
            _inFlight.Remove(request.Key);
            cancelled = _disposed || failure is OperationCanceledException;
            if (!cancelled && failure is null)
            {
                var node = _lru.AddLast(request.Key);
                _cache.Add(request.Key, (request.Completion.Task, node));
                while (_cache.Count > _capacity)
                {
                    var oldest = _lru.First!;
                    _cache.Remove(oldest.Value); _lru.RemoveFirst();
                }
            }
            if (!_disposed && _queue.Count > 0) { next = _queue.Dequeue(); _active++; }
            TryDisposeStop();
        }
        if (cancelled) request.Completion.TrySetCanceled();
        else if (failure is not null) request.Completion.TrySetException(failure);
        else request.Completion.TrySetResult(value);
        if (next is not null) Start(next);
    }

    public void Dispose()
    {
        Request[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _inFlight.Values.ToArray();
            _inFlight.Clear(); _queue.Clear(); _cache.Clear(); _lru.Clear();
        }
        // Complete callers immediately, even if a non-cancellable native extraction
        // is still returning. Its own finally blocks remain responsible for handles.
        foreach (var request in pending) request.Completion.TrySetCanceled();
        _stop.Cancel();
        lock (_gate) { _cancellationCompleted = true; TryDisposeStop(); }
    }

    private void TryDisposeStop()
    {
        if (!_stopDisposed && _disposed && _cancellationCompleted && _active == 0)
        { _stopDisposed = true; _stop.Dispose(); }
    }

    private sealed class Request(string key)
    {
        internal string Key { get; } = key;
        internal TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
