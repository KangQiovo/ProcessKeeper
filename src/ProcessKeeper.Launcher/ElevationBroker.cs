using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Launcher;

internal sealed class ElevationBroker : IDisposable
{
    private readonly NamedPipeServerStream _pipe;
    private readonly OriginalLauncher _original;
    private readonly byte[] _nonce = RandomNumberGenerator.GetBytes(32);
    internal string PipeName { get; } = $"ProcessKeeper.Elevation.{Environment.ProcessId}.{Guid.NewGuid():N}";

    public ElevationBroker(OriginalLauncher original)
    {
        _original = original;
        _pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance, 256, 256);
    }

    public void AddChildArguments(ProcessStartInfo start)
    {
        start.ArgumentList.Add("--elevation-pipe"); start.ArgumentList.Add(PipeName);
        start.ArgumentList.Add("--elevation-nonce"); start.ArgumentList.Add(Convert.ToHexString(_nonce));
        start.ArgumentList.Add("--elevation-parent"); start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public async Task RunAsync(Process child)
    {
        using var lifetime = new CancellationTokenSource();
        var serving = ServeAsync(child, () => _original.Elevate(), lifetime.Token);
        try { await child.WaitForExitAsync(); }
        finally
        {
            lifetime.Cancel();
            try { await serving; }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    // The test seam substitutes only the final launch operation. Real pipe
    // credentials, OS process identity and nonce validation still execute.
    internal async Task ServeAsync(Process child, Func<BrokerReply> elevate, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !child.HasExited)
        {
            var connected = false;
            try
            {
                await _pipe.WaitForConnectionAsync(token);
                connected = true;
                if (!GetNamedPipeClientProcessId(_pipe.SafePipeHandle, out var pid) ||
                    pid != child.Id || child.HasExited) continue;
                using var exchange = CancellationTokenSource.CreateLinkedTokenSource(token);
                exchange.CancelAfter(TimeSpan.FromSeconds(5));
                await _pipe.WriteAsync(new byte[] { 0x50 }, exchange.Token);
                await _pipe.FlushAsync(exchange.Token);
                var nonce = new byte[32];
                await _pipe.ReadExactlyAsync(nonce, exchange.Token);
                if (child.HasExited) continue;
                // Human UAC consent has no artificial timeout. A cancellation
                // reply leaves this same server ready for the next button click.
                var reply = CryptographicOperations.FixedTimeEquals(nonce, _nonce)
                    ? await Task.Run(elevate, token) : BrokerReply.Unavailable;
                await _pipe.WriteAsync(new[] { (byte)reply }, token);
                await _pipe.FlushAsync(token);
                // DisconnectNamedPipe discards unread bytes. Wait for the
                // client's acknowledgement so fast replies cannot be lost.
                using var acknowledgement = CancellationTokenSource.CreateLinkedTokenSource(token);
                acknowledgement.CancelAfter(TimeSpan.FromSeconds(5));
                await _pipe.ReadExactlyAsync(new byte[1], acknowledgement.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (IOException) { }
            // IsConnected is already false after an EOF/broken read, but the
            // server instance still needs Disconnect before accepting a retry.
            finally { if (connected) _pipe.Disconnect(); }
        }
    }

    public void Dispose() { _pipe.Dispose(); CryptographicOperations.ZeroMemory(_nonce); }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);
}
