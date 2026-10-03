using System.Diagnostics;
using ProcessKeeper.Core;

namespace ProcessKeeper.App;

internal enum UpdateTransferPhase { Starting, Downloading, Pausing, Paused, Ready, Confirming, Installing, Canceled }
/// <summary>The verified download remains usable when another operation postpones the install handoff.</summary>
internal sealed class UpdateInstallDeferredException : Exception
{
    internal UpdateInstallDeferredException() : base(L.T("请等待当前操作完成")) { }
}
internal sealed record UpdateTransferSnapshot(UpdateTransferPhase Phase, long Received, long Total, double BytesPerSecond, string SourceId, string Message)
{
    internal double Percent => Total > 0 ? Math.Max(0, Math.Min(100, Received * 100d / Total)) : 0;
    internal string Detail => (Total > 0 ? Percent.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "% | " : "") +
        Bytes(Received) + " / " + (Total > 0 ? Bytes(Total) : "—") + " | " + Bytes((long)BytesPerSecond) + "/s";
    internal static string Bytes(long bytes) => bytes >= 1048576 ? (bytes / 1048576d).ToString("0.00") + " MiB" :
        bytes >= 1024 ? (bytes / 1024d).ToString("0.00") + " KiB" : Math.Max(0, bytes) + " B";
}

/// <summary>One current-lifetime transfer. Pause never discards its owned session; old callbacks cannot revive it.</summary>
internal sealed class UpdateTransferController
{
    private readonly Func<string> _createStage;
    private readonly Action<string> _discardStage;
    private readonly Func<UpdatePreferences, UpdateRelease, UpdateAsset, string, IProgress<UpdateProgress>, CancellationToken, Task<UpdateDownloadResult>> _download;
    private readonly Func<UpdateRelease, UpdateAsset, string, UpdateDownloadSession>? _createSession;
    private readonly Func<UpdatePreferences, UpdateDownloadSession, IProgress<UpdateProgress>, CancellationToken, Task<UpdateDownloadResult>>? _resume;
    private readonly Action<UpdateDownloadSession>? _discardSession;
    private readonly CancellationTokenSource _cancel = new();
    private CancellationTokenSource? _attempt;
    private TaskCompletionSource<bool>? _continue;
    private UpdateDownloadSession? _session;
    private string? _stage;
    private bool _pauseRequested, _disposed;
    private int _generation;
    private long _lastBytes;
    private readonly Stopwatch _clock = new();
    private long _lastMilliseconds;
    internal UpdatePreferences Preferences { get; private set; }
    internal bool CanPause => _createSession is not null && _resume is not null && _discardSession is not null;
    internal UpdateTransferSnapshot Snapshot { get; private set; } = new(UpdateTransferPhase.Starting, 0, 0, 0, "", "");
    internal event Action<UpdateTransferSnapshot>? Changed;
    internal UpdateTransferController(UpdatePreferences preferences, Func<string> createStage, Action<string> discardStage,
        Func<UpdatePreferences, UpdateRelease, UpdateAsset, string, IProgress<UpdateProgress>, CancellationToken, Task<UpdateDownloadResult>> download,
        Func<UpdateRelease, UpdateAsset, string, UpdateDownloadSession>? createSession = null,
        Func<UpdatePreferences, UpdateDownloadSession, IProgress<UpdateProgress>, CancellationToken, Task<UpdateDownloadResult>>? resume = null,
        Action<UpdateDownloadSession>? discardSession = null)
    { Preferences = preferences; _createStage = createStage; _discardStage = discardStage; _download = download; _createSession = createSession; _resume = resume; _discardSession = discardSession; }
    internal void Pause()
    {
        if (!CanPause || Snapshot.Phase != UpdateTransferPhase.Downloading || _pauseRequested) return;
        _pauseRequested = true; SetPhase(UpdateTransferPhase.Pausing, L.T("正在暂停…")); _attempt?.Cancel();
    }
    internal void Resume(UpdatePreferences preferences)
    {
        if (Snapshot.Phase != UpdateTransferPhase.Paused || _disposed) return;
        Preferences = UpdatePreferencesStore.Validate(preferences); _continue?.TrySetResult(true);
    }
    internal void Cancel() { if (!_disposed) _cancel.Cancel(); }
    internal void SetPhase(UpdateTransferPhase phase, string message) => Publish(Snapshot with { Phase = phase, Message = message, BytesPerSecond = phase == UpdateTransferPhase.Downloading ? Snapshot.BytesPerSecond : 0 });
    private void Publish(UpdateTransferSnapshot value) { if (_disposed) return; Snapshot = value; Changed?.Invoke(value); }
    internal async Task<UpdateDownloadResult> DownloadAsync(UpdateRelease release, UpdateAsset asset, CancellationToken token)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(token, _cancel.Token);
        var operationToken = operation.Token;
        _stage = await Task.Run(_createStage, operationToken);
        if (CanPause) _session = _createSession!(release, asset, _stage);
        while (true)
        {
            operationToken.ThrowIfCancellationRequested();
            _pauseRequested = false; _attempt = CancellationTokenSource.CreateLinkedTokenSource(operationToken);
            var attempt = _attempt; var generation = ++_generation;
            _lastBytes = Snapshot.Received; _lastMilliseconds = 0; _clock.Restart();
            SetPhase(UpdateTransferPhase.Downloading, L.T("正在下载更新…"));
            var progress = new Progress<UpdateProgress>(value =>
            {
                if (_disposed || generation != _generation || attempt.IsCancellationRequested || Snapshot.Phase != UpdateTransferPhase.Downloading) return;
                var elapsed = _clock.ElapsedMilliseconds; var delta = elapsed - _lastMilliseconds;
                var speed = delta >= 150 && value.BytesReceived >= _lastBytes ? (value.BytesReceived - _lastBytes) * 1000d / delta : Snapshot.BytesPerSecond;
                if (delta >= 150) { _lastBytes = value.BytesReceived; _lastMilliseconds = elapsed; }
                Publish(new(UpdateTransferPhase.Downloading, value.TotalBytes > 0 ? Math.Max(0, value.BytesReceived) : Snapshot.Received,
                    value.TotalBytes > 0 ? value.TotalBytes : Snapshot.Total, Math.Max(0, speed), value.SourceId, value.Message));
            });
            try
            {
                var result = _session is not null ? await _resume!(Preferences, _session, progress, attempt.Token) :
                    await _download(Preferences, release, asset, _stage, progress, attempt.Token);
                operationToken.ThrowIfCancellationRequested();
                ++_generation;
                Publish(new(UpdateTransferPhase.Ready, asset.Size, asset.Size, 0, result.SourceId, L.T("下载完成")));
                return result;
            }
            catch (OperationCanceledException) when (_pauseRequested && !operationToken.IsCancellationRequested)
            { ++_generation; _continue = new(TaskCreationOptions.RunContinuationsAsynchronously); SetPhase(UpdateTransferPhase.Paused, L.T("已暂停，可切换更新源后继续。")); }
            catch (Exception exception) when (CanPause && exception is not OperationCanceledException && !operationToken.IsCancellationRequested)
            { ++_generation; _continue = new(TaskCreationOptions.RunContinuationsAsynchronously); SetPhase(UpdateTransferPhase.Paused, L.T("下载失败，可切换更新源后重试。") + " | " + exception.Message); }
            finally { attempt.Dispose(); if (ReferenceEquals(_attempt, attempt)) _attempt = null; }
            var continued = _continue!;
            using var canceled = operationToken.Register(() => continued.TrySetCanceled());
            await continued.Task; _continue = null;
        }
    }
    internal void Dispose()
    {
        if (_disposed) return;
        _disposed = true; ++_generation; _cancel.Cancel(); _attempt?.Cancel();
        try { if (_session is not null) _discardSession?.Invoke(_session); }
        finally { if (_stage is not null) _discardStage(_stage); _cancel.Dispose(); }
    }
}
