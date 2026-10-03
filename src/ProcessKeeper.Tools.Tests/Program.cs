using ProcessKeeper.Core;

var checks = 0;
void Check(bool value, string description) { checks++; if (!value) throw new Exception(description); }
var backend = new MemoryBackend();
var service = new MemoryOptimizationService(backend);
var denied = await service.RunAsync(MemoryOptimizationMode.Deep, () => Task.FromResult(false));
Check(!denied.Confirmed && backend.OpenCount == 0, "Declined confirmation must never touch native memory");
var order = new List<string>();
backend.OnOpen = () => order.Add("native");
var result = await service.RunAsync(MemoryOptimizationMode.Deep, () => { order.Add("confirmed"); return Task.FromResult(true); });
Check(order.SequenceEqual(new[] { "confirmed", "native" }), "Native memory operation must start after explicit confirmation");
Check(result.Operations.Count == 7 && backend.Session.Stages.Distinct().Count() == 7, "Deep mode must execute all distinct memory scopes");
Check(backend.Session.Disposed, "Native session must dispose on completion");
Check(result.AvailableChange == -50, "Decreasing availability must remain negative, not pretend to free memory");
backend = new MemoryBackend(); service = new MemoryOptimizationService(backend);
var standard = await service.RunAsync(MemoryOptimizationMode.Standard, () => Task.FromResult(true));
Check(standard.Operations.Select(x => x.Stage).SequenceEqual(new[] { MemoryOptimizationStage.WorkingSets, MemoryOptimizationStage.ModifiedPages, MemoryOptimizationStage.StandbyPages }), "Standard mode must match the locally verified PCL memory-list commands 2, 3 and 4");
var pending = new TaskCompletionSource<bool>();
var first = service.RunAsync(MemoryOptimizationMode.Standard, () => pending.Task);
var other = await new MemoryOptimizationService(new MemoryBackend()).RunAsync(MemoryOptimizationMode.Standard, () => throw new Exception("Busy operation must not request another confirmation"));
Check(other.Busy, "Concurrent requests must be rejected process wide"); pending.SetResult(false); await first;
backend = new MemoryBackend(); service = new MemoryOptimizationService(backend);
using var cancel = new CancellationTokenSource();
backend.Session.OnExecute = stage => { if (stage == MemoryOptimizationStage.WorkingSets) cancel.Cancel(); };
var cancelled = await service.RunAsync(MemoryOptimizationMode.Deep, () => Task.FromResult(true), cancellationToken: cancel.Token);
Check(cancelled.Cancelled && cancelled.Operations.Count == 1, "Cancellation must stop subsequent native steps");
Check(backend.Session.Disposed, "Cancellation must restore native session resources");
backend = new MemoryBackend(); service = new MemoryOptimizationService(backend);
backend.Session.OnExecute = stage => { if (stage == MemoryOptimizationStage.ModifiedPages) throw new InvalidOperationException("test denial"); };
var partial = await service.RunAsync(MemoryOptimizationMode.Standard, () => Task.FromResult(true));
Check(partial.Operations.Count == 3 && partial.Operations[1].Status == MemoryOperationStatus.Failed, "One step failure must remain visible while later steps continue");
Check(partial.Operations[1].Detail.Contains("test denial"), "Actual failure detail must be retained");
Check(backend.Session.Disposed, "Failure must dispose native resources");
Console.WriteLine($"PASS tools memory behavior: {checks} checks; no global operation executed");
await DownloadAdaptiveChecks.Run();
await DownloadChecks.Run();
await DownloadAdvancedChecks.Run();
DownloadCleanupChecks.Run();
await DownloadRedirectChecks.Run();

sealed class MemoryBackend : IMemoryOptimizationBackend
{
    public int OpenCount; public Action? OnOpen; public MemorySession Session = new();
    public IMemoryOptimizationSession Open() { OpenCount++; OnOpen?.Invoke(); return Session; }
}
sealed class MemorySession : IMemoryOptimizationSession
{
    public List<MemoryOptimizationStage> Stages = new(); public bool Disposed; public Action<MemoryOptimizationStage>? OnExecute; private int _samples;
    public MemoryReading Capture() => new(1000, ++_samples == 1 ? 500ul : 450ul);
    public MemoryOperation Execute(MemoryOptimizationStage stage) { Stages.Add(stage); OnExecute?.Invoke(stage); return new(stage, MemoryOperationStatus.Succeeded, ""); }
    public void Dispose() => Disposed = true;
}
