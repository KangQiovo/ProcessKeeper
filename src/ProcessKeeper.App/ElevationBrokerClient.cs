using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.App;

internal enum ElevationRequestResult { Started, Cancelled, Unavailable, Failed }

internal static class ElevationBrokerClient
{
    private static readonly ElevationEndpoint? Endpoint = Parse(Environment.GetCommandLineArgs().Skip(1).ToArray());
    public static bool IsAvailable => Endpoint is not null;

    public static Task<ElevationRequestResult> RequestElevationAsync(CancellationToken cancellationToken = default) =>
        RequestAsync(Endpoint, cancellationToken);

    internal static async Task<ElevationRequestResult> RequestAsync(ElevationEndpoint? endpoint, CancellationToken token)
    {
        if (endpoint is null) return ElevationRequestResult.Unavailable;
        try
        {
            using var pipe = await ConnectReadyAsync(endpoint, token);
            if (pipe is null) return ElevationRequestResult.Unavailable;
            await pipe.WriteAsync(endpoint.Nonce, token);
            await pipe.FlushAsync(token);
            var response = new byte[1];
            await pipe.ReadExactlyAsync(response, token);
            await pipe.WriteAsync(new byte[] { 0xA5 }, token);
            await pipe.FlushAsync(token);
            return response[0] switch
            {
                1 => ElevationRequestResult.Started,
                2 => ElevationRequestResult.Cancelled,
                3 => ElevationRequestResult.Unavailable,
                _ => ElevationRequestResult.Failed
            };
        }
        catch (OperationCanceledException) { return token.IsCancellationRequested ? ElevationRequestResult.Cancelled : ElevationRequestResult.Unavailable; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return ElevationRequestResult.Unavailable; }
    }

    private static async Task<NamedPipeClientStream?> ConnectReadyAsync(ElevationEndpoint endpoint, CancellationToken token)
    {
        using var connecting = CancellationTokenSource.CreateLinkedTokenSource(token);
        connecting.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            connecting.Token.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(connecting.Token);
                if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid) || pid != endpoint.ParentId)
                { pipe.Dispose(); return null; }
                // A previous exchange may still be disconnecting. Retry only
                // before READY; the elevation nonce is sent at most once.
                var ready = new byte[1];
                await pipe.ReadExactlyAsync(ready, connecting.Token);
                if (ready[0] != 0x50) { pipe.Dispose(); return null; }
                return pipe;
            }
            catch (IOException)
            {
                pipe.Dispose();
                await Task.Delay(25, connecting.Token);
            }
            catch { pipe.Dispose(); throw; }
        }
    }

    internal static ElevationEndpoint? Parse(string[] args)
    {
        if (args.Length != 6) return null;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
            if (!values.TryAdd(args[i], args[i + 1])) return null;
        if (!values.TryGetValue("--elevation-pipe", out var pipe) ||
            !values.TryGetValue("--elevation-nonce", out var nonce) ||
            !values.TryGetValue("--elevation-parent", out var parent) ||
            !int.TryParse(parent, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            nonce.Length != 64 || !nonce.All(Uri.IsHexDigit)) return null;
        var prefix = $"ProcessKeeper.Elevation.{pid}.";
        if (!pipe.StartsWith(prefix, StringComparison.Ordinal) || pipe.Length != prefix.Length + 32 ||
            !Guid.TryParseExact(pipe[prefix.Length..], "N", out _)) return null;
        return new ElevationEndpoint(pipe, Convert.FromHexString(nonce), pid);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);
}

internal sealed record ElevationEndpoint(string PipeName, byte[] Nonce, int ParentId);
