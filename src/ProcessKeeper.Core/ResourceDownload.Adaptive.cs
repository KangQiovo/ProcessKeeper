using System.Net;
using System.Net.Http.Headers;
using System.Globalization;

namespace ProcessKeeper.Core;

public sealed partial class ResourceDownloadJob
{
    private const int MinimumSplitRemaining = 256 * 1024;
    private const int MaximumSegments = 4096;
    private readonly object _adaptiveSync = new();
    private readonly List<AdaptiveSegment> _segments = new();
    private int _activeConnections, _segmentCount;
    private string _fallbackReason = "";
    private sealed class AdaptiveSegment(long start, long end, string path)
    {
        internal long Start { get; } = start;
        internal long End { get; set; } = end;
        internal long Written { get; set; }
        internal string Path { get; } = path;
        internal bool Running { get; set; }
        internal long Remaining => End - Start + 1 - Written;
    }

    /// <summary>Independent adaptive scheduler: begin with one range, split the largest unfinished tail as slots become available.</summary>
    private async Task<IReadOnlyList<AdaptiveSegment>> DownloadAdaptive(Metadata metadata,
        IProgress<ResourceDownloadProgress>? progress, CancellationToken token)
    {
        _fallbackReason = "";
        // Without a strong validator retries cannot safely mix persisted old fragments.
        // A transient failure restarts the fresh transfer as a whole; a pause returns to RunAsync,
        // whose next probe also discards all unvalidated pieces before beginning again.
        for (var attempt = 0; ; attempt++)
        {
            try { return await RunAdaptiveOnce(metadata, progress, token).ConfigureAwait(false); }
            catch (Exception error) when (!token.IsCancellationRequested && metadata.Validator.Length == 0 &&
                error is not (RangeUnavailableException or InvalidDataException) && attempt < 2)
            {
                Cleanup(); Directory.CreateDirectory(_work);
                await Task.Delay(200 * (attempt + 1), token).ConfigureAwait(false);
            }
        }
    }

    private async Task<IReadOnlyList<AdaptiveSegment>> RunAdaptiveOnce(Metadata metadata,
        IProgress<ResourceDownloadProgress>? progress, CancellationToken token)
    {
        var length = metadata.Length!.Value;
        lock (_adaptiveSync)
        {
            if (_segments.Count == 0) _segments.Add(new(0, length - 1, Path.Combine(_work, "part-0")));
            var ordered = _segments.OrderBy(segment => segment.Start).ToArray(); long next = 0;
            _received = 0;
            foreach (var segment in ordered)
            {
                if (segment.Start != next || segment.End < segment.Start || segment.End >= length)
                    throw new InvalidDataException("Invalid adaptive range boundaries");
                segment.Written = File.Exists(segment.Path) ? new FileInfo(segment.Path).Length : 0;
                if (segment.Written > segment.End - segment.Start + 1) throw new InvalidDataException("Invalid partial download length");
                segment.Running = false; _received += segment.Written; next = segment.End + 1;
            }
            if (next != length) throw new InvalidDataException("Incomplete adaptive range boundaries");
            Volatile.Write(ref _activeConnections, 0); Volatile.Write(ref _segmentCount, _segments.Count);
        }
        StartTransferMeasurement();
        using var group = CancellationTokenSource.CreateLinkedTokenSource(token);
        var tasks = new List<Task>();
        while (!group.IsCancellationRequested)
        {
            var scheduled = new List<AdaptiveSegment>();
            lock (_adaptiveSync)
            {
                var active = _segments.Count(segment => segment.Running);
                foreach (var pending in _segments.Where(segment => !segment.Running && segment.Remaining > 0).ToArray())
                {
                    if (active >= _request.Connections) break;
                    pending.Running = true; active++; scheduled.Add(pending);
                }
                // The first request covers the entire file. Let it start before scheduling its first tail.
                if (tasks.Count > 0)
                {
                    while (active < _request.Connections && _segments.Count < MaximumSegments)
                    {
                        var largest = _segments.Where(segment => segment.Running && segment.Remaining > MinimumSplitRemaining)
                            .OrderByDescending(segment => segment.Remaining).FirstOrDefault();
                        if (largest is null) break;
                        var remaining = largest.Remaining;
                        var tail = remaining / 5 * 2 + remaining % 5 * 2 / 5;
                        var newStart = largest.End - tail + 1;
                        var added = new AdaptiveSegment(newStart, largest.End, Path.Combine(_work, "part-" + _segments.Count)) { Running = true };
                        // Writers commit under the same lock and obey the new End before every chunk.
                        largest.End = newStart - 1; _segments.Add(added); active++; scheduled.Add(added);
                    }
                }
                Volatile.Write(ref _segmentCount, _segments.Count);
            }
            foreach (var segment in scheduled)
                tasks.Add(Task.Run(() => DownloadAdaptiveSegment(metadata, segment, progress, group)));
            lock (_adaptiveSync)
                if (_segments.All(segment => segment.Remaining == 0)) break;
            if (tasks.Any(task => task.IsFaulted || task.IsCanceled)) { group.Cancel(); break; }
            try { await Task.Delay(20, group.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
        // Every dynamically appended task is retained and settled, including a sibling failing
        // while cancellation disposes another stream. Never merge, clean or fall back early.
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        catch
        {
            if (!token.IsCancellationRequested && tasks.Any(task => task.Exception?.Flatten().InnerExceptions.Any(error => error is RangeUnavailableException) == true))
                throw new RangeUnavailableException();
            if (!token.IsCancellationRequested)
            {
                var failure = tasks.Where(task => task.IsFaulted).SelectMany(task => task.Exception!.Flatten().InnerExceptions)
                    .FirstOrDefault(error => error is not OperationCanceledException);
                if (failure is not null) throw failure;
            }
            throw;
        }
        token.ThrowIfCancellationRequested();
        lock (_adaptiveSync)
        {
            if (_segments.Any(segment => segment.Remaining != 0)) throw new IOException("Adaptive transfer stopped before completion");
            return _segments.OrderBy(segment => segment.Start).ToArray();
        }
    }

    private async Task DownloadAdaptiveSegment(Metadata metadata, AdaptiveSegment segment,
        IProgress<ResourceDownloadProgress>? progress, CancellationTokenSource group)
    {
        Interlocked.Increment(ref _activeConnections);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                group.Token.ThrowIfCancellationRequested();
                long offset, end;
                lock (_adaptiveSync) { offset = segment.Written; end = segment.End; }
                if (segment.Start + offset > end) return;
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(group.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    using var request = Request(metadata.Url); request.Headers.Range = new RangeHeaderValue(segment.Start + offset, end);
                    if (metadata.ETag) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(metadata.Validator));
                    else if (metadata.Validator.Length > 0) request.Headers.IfRange = new RangeConditionHeaderValue(DateTimeOffset.Parse(metadata.Validator, CultureInfo.InvariantCulture));
                    using var response = await SendFollowingRedirects(request, timeout.Token).ConfigureAwait(false); CheckResponse(response, metadata.Url);
                    if (response.StatusCode == HttpStatusCode.OK) throw new RangeUnavailableException();
                    ValidateVersion(metadata, response);
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range is null || range.Unit != "bytes" ||
                        range.From != segment.Start + offset || range.To != end || range.Length != metadata.Length)
                        throw new InvalidDataException("Invalid range response");
                    if (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength.Value != end - segment.Start - offset + 1)
                        throw new IOException("Server returned an incomplete range length");
                    using var cancelled = timeout.Token.Register(() => response.Dispose());
                    using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    using var output = new FileStream(segment.Path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 65536, true); output.Position = offset;
                    var buffer = new byte[65536];
                    while (true)
                    {
                        timeout.Token.ThrowIfCancellationRequested();
                        bool finished, shortened;
                        lock (_adaptiveSync) { finished = segment.Remaining == 0; shortened = segment.End != end; }
                        if (finished)
                        {
                            if (shortened) return;
                            if (await input.ReadAsync(buffer, 0, 1, timeout.Token).ConfigureAwait(false) != 0)
                                throw new InvalidDataException("Server sent extra bytes");
                            return;
                        }
                        var read = await input.ReadAsync(buffer, 0, buffer.Length, timeout.Token).ConfigureAwait(false);
                        if (read == 0)
                        { lock (_adaptiveSync) if (segment.Remaining > 0) throw new IOException("Server truncated the file"); return; }
                        lock (_adaptiveSync)
                        {
                            if (read > segment.Remaining && segment.End == end) throw new InvalidDataException("Server sent extra bytes");
                            var count = (int)Math.Min(read, segment.Remaining);
                            if (count == 0) return;
                            // At most one 64-KiB committed write per lock acquisition; a tail split
                            // can never race past bytes already persisted to this segment's file.
                            output.Write(buffer, 0, count); segment.Written += count; Interlocked.Add(ref _received, count);
                        }
                        timeout.CancelAfter(TimeSpan.FromSeconds(30)); ReportTransfer(metadata, progress);
                    }
                }
                catch (RangeUnavailableException) { throw; }
                catch (InvalidDataException) { throw; }
                catch (Exception) when (!group.IsCancellationRequested && metadata.Validator.Length > 0 && attempt < 2)
                { await Task.Delay(200 * (attempt + 1), group.Token).ConfigureAwait(false); }
            }
        }
        catch { group.Cancel(); throw; }
        finally { lock (_adaptiveSync) { Interlocked.Decrement(ref _activeConnections); segment.Running = false; } }
    }

    private void ReportTransfer(Metadata metadata, IProgress<ResourceDownloadProgress>? progress)
    {
        lock (_clock)
        {
            var elapsed = _clock.ElapsedMilliseconds;
            if (elapsed - _lastReport < 100) return;
            var received = Interlocked.Read(ref _received);
            var speed = Math.Max(0, received - _lastReportedBytes) * 1000d / (elapsed - _lastReport);
            _lastReport = elapsed; _lastReportedBytes = received;
            progress?.Report(new(received, metadata.Length, speed, metadata.Source.Name, "downloading",
                Volatile.Read(ref _activeConnections), Volatile.Read(ref _segmentCount), _fallbackReason));
        }
    }
}
