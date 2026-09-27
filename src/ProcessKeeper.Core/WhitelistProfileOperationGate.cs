namespace ProcessKeeper.Core;

/// <summary>Session-only exclusion between rule mutations and awaited close operations.</summary>
public static class WhitelistProfileOperationGate
{
    private static readonly object Sync = new();
    private static int _closing;
    private static bool _changing;

    public static IDisposable EnterClose()
    {
        lock (Sync)
        {
            if (_changing) throw new InvalidOperationException(L.T("正在保存白名单配置，请稍后再关闭程序。"));
            _closing++;
            return new Lease(false);
        }
    }

    public static IDisposable EnterMutation()
    {
        lock (Sync)
        {
            if (_changing || _closing != 0) throw new InvalidOperationException(L.T("正在执行关闭或配置操作，请完成后再修改白名单。"));
            _changing = true;
            return new Lease(true);
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly bool _mutation;
        private bool _disposed;
        internal Lease(bool mutation) => _mutation = mutation;
        public void Dispose()
        {
            lock (Sync)
            {
                if (_disposed) return;
                _disposed = true;
                if (_mutation) _changing = false; else _closing--;
            }
        }
    }
}
