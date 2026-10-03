namespace ProcessKeeper.Core;

public enum MemoryOptimizationMode { Standard, Deep }
public enum MemoryOptimizationStage { WorkingSets, FileCache, ModifiedPages, StandbyPages, LowPriorityStandby, RegistryCache, CombinePages }
public enum MemoryOperationStatus { Succeeded, Unsupported, Failed }
public sealed record MemoryReading(ulong TotalBytes, ulong AvailableBytes);
public sealed record MemoryOperation(MemoryOptimizationStage Stage, MemoryOperationStatus Status, string Detail);
public sealed record MemoryOptimizationResult(bool Confirmed, bool Busy, bool Cancelled, MemoryReading? Before,
    MemoryReading? After, IReadOnlyList<MemoryOperation> Operations)
{
    // A sample delta includes other programs' activity; it is not a guarantee of freed or decommitted memory.
    public long? AvailableChange => Before is null || After is null ? null : checked((long)After.AvailableBytes - (long)Before.AvailableBytes);
}
public interface IMemoryOptimizationSession : IDisposable
{
    MemoryReading? Capture();
    MemoryOperation Execute(MemoryOptimizationStage stage);
}
public interface IMemoryOptimizationBackend { IMemoryOptimizationSession Open(); }

public sealed class MemoryOptimizationService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private readonly IMemoryOptimizationBackend _backend;
    public MemoryOptimizationService(IMemoryOptimizationBackend backend) => _backend = backend;
    public async Task<MemoryOptimizationResult> RunAsync(MemoryOptimizationMode mode, Func<Task<bool>> confirm,
        IProgress<MemoryOperation>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(typeof(MemoryOptimizationMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        if (confirm is null) throw new ArgumentNullException(nameof(confirm));
        if (!Gate.Wait(0)) return new(false, true, false, null, null, Array.Empty<MemoryOperation>());
        var confirmed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            confirmed = await confirm().ConfigureAwait(false);
            if (!confirmed) return new(false, false, false, null, null, Array.Empty<MemoryOperation>());
            // A dedicated worker keeps impersonation off the pool even if token restoration fails.
            var completion = new TaskCompletionSource<MemoryOptimizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            var worker = new Thread(() =>
            {
                try { completion.SetResult(Execute(mode, progress, cancellationToken)); }
                catch (Exception error) { completion.SetException(error); }
            }) { IsBackground = true, Name = "ProcessKeeper.MemoryOptimization" };
            worker.Start();
            return await completion.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { return new(confirmed, false, true, null, null, Array.Empty<MemoryOperation>()); }
        finally { Gate.Release(); }
    }
    private MemoryOptimizationResult Execute(MemoryOptimizationMode mode, IProgress<MemoryOperation>? progress, CancellationToken token)
    {
        var operations = new List<MemoryOperation>();
        token.ThrowIfCancellationRequested();
        using var session = _backend.Open();
        MemoryReading? Capture() { try { return session.Capture(); } catch { return null; } }
        var before = Capture();
        var stages = mode == MemoryOptimizationMode.Deep ? new[]
        {
            MemoryOptimizationStage.WorkingSets, MemoryOptimizationStage.FileCache, MemoryOptimizationStage.ModifiedPages,
            MemoryOptimizationStage.StandbyPages, MemoryOptimizationStage.LowPriorityStandby,
            MemoryOptimizationStage.RegistryCache, MemoryOptimizationStage.CombinePages
        } : new[] { MemoryOptimizationStage.WorkingSets, MemoryOptimizationStage.ModifiedPages, MemoryOptimizationStage.StandbyPages };
        foreach (var stage in stages)
        {
            if (token.IsCancellationRequested) break;
            MemoryOperation result;
            try { result = session.Execute(stage); }
            catch (Exception error) { result = new(stage, MemoryOperationStatus.Failed, error.Message); }
            operations.Add(result);
            progress?.Report(result);
        }
        return new(true, false, token.IsCancellationRequested, before, Capture(), operations.ToArray());
    }
}
