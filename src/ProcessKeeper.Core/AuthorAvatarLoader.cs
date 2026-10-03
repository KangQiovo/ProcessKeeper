using System.Net;
using System.Net.Http;

namespace ProcessKeeper.Core;

/// <summary>Downloads only KangQiovo's public avatar into bounded memory.</summary>
public static class AuthorAvatarLoader
{
    private const int MaximumBytes = 256 * 1024;
    private static readonly TimeSpan SourceBudget = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(11);
    private const string OfficialImage = "https://avatars.githubusercontent.com/u/126958983?v=4&s=160";
    private static readonly string[] Sources =
    {
        OfficialImage,
        "https://github.com/KangQiovo.png?size=160",
        "https://gh-proxy.com/" + OfficialImage,
        "https://gh-proxy.org/" + OfficialImage
    };

    /// <summary>
    /// Preload once at startup and reuse the task. Returns null on failure or cancellation.
    /// A platform decoder callback must be supplied by production UI so a corrupt image
    /// is rejected before choosing a source. No image or application data is written to disk.
    /// </summary>
    public static Task<byte[]?> LoadAsync(CancellationToken cancellationToken = default,
        Func<byte[], CancellationToken, Task<bool>>? validateImage = null)
        => Task.Run(() => LoadCoreAsync(null, cancellationToken, validateImage, SourceBudget, TotalBudget));

    // HTTP is the test seam; source URLs and production limits cannot be supplied by settings.
    internal static Task<byte[]?> LoadAsync(HttpMessageHandler handler, CancellationToken cancellationToken = default,
        Func<byte[], CancellationToken, Task<bool>>? validateImage = null,
        TimeSpan? sourceTimeout = null, TimeSpan? totalTimeout = null)
        => Task.Run(() => LoadCoreAsync(handler, cancellationToken, validateImage,
            sourceTimeout ?? SourceBudget, totalTimeout ?? TotalBudget));

    private static async Task<byte[]?> LoadCoreAsync(HttpMessageHandler? handler, CancellationToken cancellationToken,
        Func<byte[], CancellationToken, Task<bool>>? validateImage, TimeSpan sourceBudget, TimeSpan totalBudget)
    {
        if (cancellationToken.IsCancellationRequested) { handler?.Dispose(); return null; }
        try
        {
            using var client = new HttpClient(handler ?? new HttpClientHandler
            {
                AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false,
                AutomaticDecompression = DecompressionMethods.None
#if !NETFRAMEWORK
                , MaxResponseHeadersLength = 8
#endif
            }) { Timeout = Timeout.InfiniteTimeSpan };
            using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            total.CancelAfter(totalBudget);
            foreach (var source in Sources)
            {
                if (total.IsCancellationRequested) return null;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                attempt.CancelAfter(sourceBudget);
                try
                {
                    var bytes = await DownloadAsync(client, new Uri(source), attempt.Token).ConfigureAwait(false);
                    if (bytes == null || !AuthorAvatarImage.IsSupported(bytes)) continue;
                    if (validateImage != null && !await WithinBudgetAsync(
                        Task.Run(() => validateImage(bytes, attempt.Token)), attempt.Token).ConfigureAwait(false)) continue;
                    attempt.Token.ThrowIfCancellationRequested();
                    return bytes;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    // Optional decoration: unreachable/invalid/expired candidates simply try the next fixed source.
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException) { }
        return null;
    }

    private static async Task<byte[]?> DownloadAsync(HttpClient client, Uri uri, CancellationToken token)
    {
        for (int redirects = 0; redirects <= 2; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("ProcessKeeper/1.6");
            request.Headers.Accept.ParseAdd("image/png, image/jpeg");
            using var response = await WithinBudgetAsync(client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
            {
                var location = response.Headers.Location;
                if (location == null || !Uri.TryCreate(uri, location, out var target) || !AllowedRedirect(target)) return null;
                uri = target;
                continue;
            }
            if (!response.IsSuccessStatusCode || response.Content == null) return null;
            var length = response.Content.Headers.ContentLength;
            if (length > MaximumBytes || length == 0) return null;
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType != null && !string.Equals(mediaType, "image/png", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mediaType, "image/jpeg", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(mediaType, "application/octet-stream", StringComparison.OrdinalIgnoreCase)) return null;
            using var input = await WithinBudgetAsync(response.Content.ReadAsStreamAsync(), token).ConfigureAwait(false);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                // Read one byte beyond the cap only to detect an unadvertised oversized response.
                int take = Math.Min(buffer.Length, MaximumBytes + 1 - (int)output.Length);
                int read = await WithinBudgetAsync(input.ReadAsync(buffer, 0, take, token), token).ConfigureAwait(false);
                if (read == 0) break;
                if (output.Length + read > MaximumBytes) return null;
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        return null;
    }

    private static bool AllowedRedirect(Uri target)
    {
        if (!target.IsAbsoluteUri || target.Scheme != Uri.UriSchemeHttps || !target.IsDefaultPort
            || target.UserInfo.Length != 0 || target.Fragment.Length != 0) return false;
        // GitHub's username endpoint reverses the same two query parameters.
        return Sources.Any(source => string.Equals(source, target.AbsoluteUri, StringComparison.Ordinal))
            || target.AbsoluteUri == "https://avatars.githubusercontent.com/u/126958983?s=160&v=4";
    }

    private static async Task<T> WithinBudgetAsync<T>(Task<T> operation, CancellationToken token)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        var expired = Task.Delay(Timeout.Infinite, timer.Token);
        if (await Task.WhenAny(operation, expired).ConfigureAwait(false) == operation)
        {
            timer.Cancel();
            return await operation.ConfigureAwait(false);
        }
        // Defend the deadline even if a platform decoder or test transport ignores cancellation.
        // Any late disposable result is released; faults are observed without retaining a UI object.
        _ = operation.ContinueWith(completed =>
        {
            if (completed.Status == TaskStatus.RanToCompletion && completed.Result is IDisposable disposable) disposable.Dispose();
            else if (completed.IsFaulted) _ = completed.Exception;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        throw new OperationCanceledException(token);
    }
}
