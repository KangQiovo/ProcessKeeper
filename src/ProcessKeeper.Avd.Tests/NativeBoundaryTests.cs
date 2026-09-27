using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text;
using ProcessKeeper.Core;

internal static class NativeBoundaryTests
{
    internal static async Task Run(FixtureSuite suite)
    {
        await suite.Case("owned Win32 windows: actual visible main window versus hidden/tool/child/zero region", () =>
        {
            IReadOnlyList<nint> ownHandles;
            using (var windows = new OwnWindows())
            {
                ownHandles = windows.Handles.Values.ToArray();
                suite.Check(!AvdGuiMatcher.IsGuiWindow(windows.Snapshot("main"), FakeAvdHost.AvdName, FakeAvdHost.Port), "a real hidden window is rejected");
                windows.Show("main");
                suite.Check(Native.IsWindowVisible(windows.Handles["main"]), "fixture main HWND is truly visible");
                suite.Check(AvdGuiMatcher.IsGuiWindow(windows.Snapshot("main"), FakeAvdHost.AvdName, FakeAvdHost.Port), "real visible matching Win32 main window accepted");
                foreach (string kind in new[] { "tool", "child", "zero" })
                {
                    windows.Show(kind);
                    suite.Check(!AvdGuiMatcher.IsGuiWindow(windows.Snapshot(kind), FakeAvdHost.AvdName, FakeAvdHost.Port), "real " + kind + " HWND rejected");
                }
                windows.Hide("main");
                suite.Check(!AvdGuiMatcher.IsGuiWindow(windows.Snapshot("main"), FakeAvdHost.AvdName, FakeAvdHost.Port), "later hidden HWND is no longer success evidence");
            }
            suite.Check(ownHandles.All(handle => !Native.IsWindow(handle)), "all fixture HWNDs destroyed before returning");
            return Task.CompletedTask;
        });

        using var self = Process.GetCurrentProcess();
        using var account = WindowsIdentity.GetCurrent();
        var identity = new ProcessRecord
        {
            Id = self.Id, Name = Path.GetFileName(Environment.ProcessPath!), Path = Environment.ProcessPath!,
            StartTimeUtcTicks = self.StartTime.ToUniversalTime().Ticks, SessionId = self.SessionId,
            OwnerSid = account.User!.Value
        };
        // No auth token is used or read: all servers below identify themselves as unauthenticated.
        string fakeProfile = Path.Combine(AppContext.BaseDirectory, "unused-fixture-profile");

        await suite.Case("native identity and commandline verify the test process itself", () =>
        {
            AvdNative.ValidateIdentity(identity);
            var arguments = AvdNative.ReadArguments(identity);
            suite.Check(arguments.Count >= 1 && Path.GetFullPath(arguments[0]).Equals(identity.Path, StringComparison.OrdinalIgnoreCase), "native argv[0] identifies only this fixture executable");
            return Task.CompletedTask;
        });

        await suite.Case("native console reads the exact AVD name from an exclusively owned loopback listener", async () =>
        {
            await using var server = new OwnedConsoleServer();
            suite.Check(AvdNative.IsPortOwnedBy(server.Port, identity.Id), "kernel TCP listener table binds fixture port to fixture PID");
            string name = await AvdNative.GetAvdNameAsync(identity, server.Port, fakeProfile);
            suite.Check(name == FakeAvdHost.AvdName && server.Commands.SequenceEqual(["avd name"]), "name query cannot send a shutdown command");
        });

        await suite.Case("native console graceful shutdown checks AVD name on the same connection", async () =>
        {
            await using var server = new OwnedConsoleServer();
            await AvdNative.ShutdownAsync(identity, server.Port, FakeAvdHost.AvdName, fakeProfile);
            suite.Check(server.Commands.SequenceEqual(["avd name", "kill"]), "kill only follows the matching identity query");
            suite.Check(server.Connections == 1, "identity query and shutdown share one authenticated protocol session");
            suite.Check(!self.HasExited, "fixture protocol kill does not terminate any OS process");
        });

        await suite.Case("native console refuses a different AVD and sends no kill", async () =>
        {
            await using var server = new OwnedConsoleServer { AvdName = "Other_AVD" };
            await MustReject(() => AvdNative.ShutdownAsync(identity, server.Port, FakeAvdHost.AvdName, fakeProfile));
            suite.Check(server.Commands.SequenceEqual(["avd name"]), "different AVD never receives kill");
        });

        await suite.Case("native console refuses a non-emulator banner", async () =>
        {
            await using var server = new OwnedConsoleServer { Greeting = "Not an emulator" };
            await MustReject(() => AvdNative.ShutdownAsync(identity, server.Port, FakeAvdHost.AvdName, fakeProfile));
            suite.Check(server.Commands.IsEmpty, "wrong protocol receives neither identity query nor shutdown");
        });

        await suite.Case("native console rejects stale process identity before connecting", async () =>
        {
            await using var server = new OwnedConsoleServer();
            await MustReject(() => AvdNative.ShutdownAsync(identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 },
                server.Port, FakeAvdHost.AvdName, fakeProfile));
            suite.Check(server.Connections == 0 && server.Commands.IsEmpty, "wrong start time creates no connection or command");
        });

        await suite.Case("native console cancelled before start causes no connection or command", async () =>
        {
            await using var server = new OwnedConsoleServer();
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try
            {
                await AvdNative.ShutdownAsync(identity, server.Port, FakeAvdHost.AvdName, fakeProfile, cancelled.Token);
                throw new InvalidOperationException("Expected native console cancellation.");
            }
            catch (OperationCanceledException) { }
            suite.Check(server.Connections == 0 && server.Commands.IsEmpty, "cancelled shutdown never connects or sends kill");
        });
    }

    private static async Task MustReject(Func<Task> operation)
    {
        try { await operation(); }
        catch (InvalidOperationException) { return; }
        catch (IOException) { return; }
        throw new InvalidOperationException("Expected operation rejection.");
    }

    private sealed class OwnedConsoleServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _loop;
        internal int Port { get; }
        internal int Connections;
        internal string AvdName { get; init; } = FakeAvdHost.AvdName;
        internal string Greeting { get; init; } = "Android Console | Owned test fixture";
        internal ConcurrentQueue<string> Commands { get; } = new();

        internal OwnedConsoleServer()
        {
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _loop = Accept();
        }

        private async Task Accept()
        {
            try
            {
                while (!_stopping.IsCancellationRequested)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                    Interlocked.Increment(ref Connections);
                    using var stream = client.GetStream();
                    using var input = new StreamReader(stream, new UTF8Encoding(false), false, 1024, true);
                    using var output = new StreamWriter(stream, new UTF8Encoding(false), 1024, true) { AutoFlush = true, NewLine = "\r\n" };
                    await output.WriteLineAsync(Greeting);
                    await output.WriteLineAsync("OK");
                    while (await input.ReadLineAsync(_stopping.Token) is { } command)
                    {
                        Commands.Enqueue(command);
                        if (command == "avd name") { await output.WriteLineAsync(AvdName); await output.WriteLineAsync("OK"); }
                        else if (command == "kill") { await output.WriteLineAsync("OK"); break; }
                        else { await output.WriteLineAsync("KO: Unsupported fixture command"); break; }
                    }
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
            catch (SocketException) when (_stopping.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _stopping.Cancel();
            _listener.Stop();
            await _loop;
            _stopping.Dispose();
        }
    }
}
