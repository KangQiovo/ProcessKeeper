using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ProcessKeeper.App;
using ProcessKeeper.Launcher;

internal static class ElevationGateTests
{
    public static async Task RunAsync(string root, Action<bool, string> check, Action<Action, string> reject)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        var localDescriptor = UserCacheSecurity.NewDirectorySecurity(user);
        UserCacheSecurity.ValidateDescriptor(localDescriptor, user);
        check(true, "ordinary cache requires current-user owner and protected user/SYSTEM DACL");
        reject(() => CacheSecurity.ValidateDescriptor(localDescriptor, true), "ordinary cache ACL cannot pass the unchanged elevated-cache validator");
        reject(() => UserCacheSecurity.ValidateDescriptor(CacheSecurity.NewDirectorySecurity(), user), "ordinary access mode never accepts administrator cache root");
        localDescriptor.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow));
        reject(() => UserCacheSecurity.ValidateDescriptor(localDescriptor, user), "ordinary cache rejects an unexpected writer");
        var localRoot = Path.Combine(root, "ordinary-cache");
        UserCacheSecurity.CreateDirectory(localRoot);
        UserCacheSecurity.CreateDirectory(localRoot);
        check(Directory.Exists(localRoot), "ordinary owned cache directory can be created and safely reused");
        reject(() => UserCacheSecurity.GetVersionRoot("../escape"), "ordinary cache validates the version hash before touching any cache");

        var sourceFolder = Path.Combine(root, "source-folder");
        Directory.CreateDirectory(sourceFolder);
        var fakeExe = Path.Combine(sourceFolder, "source-original.exe");
        File.WriteAllText(fakeExe, "NONEXECUTABLE fixture; final launch is mocked");
        using var original = OriginalLauncher.OpenForTest(fakeExe);
        original.Validate();
        check(true, "source launcher is hash-verified while held read-only");
        reject(() => File.WriteAllText(fakeExe, "tamper"), "held original launcher cannot be written");
        reject(() => File.Move(fakeExe, fakeExe + ".moved"), "held original launcher cannot be renamed");
        reject(() => Directory.Move(sourceFolder, sourceFolder + ".moved"), "held original launcher blocks replacing its parent directory");
        ProcessStartInfo? captured = null;
        var result = original.Elevate(info => { captured = info; return true; });
        check(result == BrokerReply.Started && captured!.FileName == fakeExe && captured.UseShellExecute && captured.Verb == "runas", "elevation targets only the held original EXE with runas");
        check(captured!.ArgumentList.Count == 0 && captured.Arguments.Length == 0 &&
            captured.WorkingDirectory == Environment.GetFolderPath(Environment.SpecialFolder.System), "elevation cannot inherit user-cache paths or child-supplied arguments");
        check(original.Elevate(_ => throw new Win32Exception(1223)) == BrokerReply.Cancelled, "UAC cancellation returns a retryable cancellation result");
        check(original.Elevate(_ => throw new Win32Exception(5)) == BrokerReply.Failed, "non-cancellation launch error is not reported as started");
        check(original.Elevate(_ => false) == BrokerReply.Failed, "missing elevated process is not reported as started");

        var automaticRequests = 0;
        var permissionPage = false;
        StartupElevationFlow.Run(false,
            () => original.Elevate(_ => { automaticRequests++; throw new Win32Exception(1223); }),
            admin => { permissionPage = !admin; return 0; });
        check(automaticRequests == 1 && permissionPage,
            "cancelled automatic UAC enters the permission-page branch through the original launcher");
        original.Validate();
        reject(() => File.WriteAllText(fakeExe, "replace between auto request and retry"),
            "original EXE remains locked between automatic cancellation and later broker retries");

        using var broker = new ElevationBroker(original);
        var start = new ProcessStartInfo();
        broker.AddChildArguments(start);
        var args = start.ArgumentList.ToArray();
        var endpoint = ElevationBrokerClient.Parse(args) ?? throw new InvalidOperationException("Generated endpoint did not parse");
        check(endpoint.ParentId == Environment.ProcessId && endpoint.Nonce.Length == 32, "endpoint contains fixed-size random nonce and pinned launcher PID");
        check(!args.Any(value => value == fakeExe || value.Contains("ordinary-cache")), "IPC endpoint serializes no path or executable command");
        check(ElevationBrokerClient.Parse([]) is null, "direct App launch has no elevation capability");
        check(ElevationBrokerClient.Parse([..args, "--extra", "1"]) is null, "unexpected broker arguments rejected");
        var duplicate = args.ToArray(); duplicate[4] = duplicate[0];
        check(ElevationBrokerClient.Parse(duplicate) is null, "duplicate endpoint fields rejected");
        var badNonce = args.ToArray(); badNonce[3] = new string('z', 64);
        check(ElevationBrokerClient.Parse(badNonce) is null, "nonhex nonce rejected");
        var badPipe = args.ToArray(); badPipe[1] = "\\remote\\pipe\\evil";
        check(ElevationBrokerClient.Parse(badPipe) is null, "remote or arbitrary pipe path rejected");
        var badPid = args.ToArray(); badPid[5] = "0";
        check(ElevationBrokerClient.Parse(badPid) is null, "invalid parent identity rejected");
        check(await ElevationBrokerClient.RequestAsync(null, CancellationToken.None) == ElevationRequestResult.Unavailable, "unavailable broker is an explicit result");

        using var current = Process.GetCurrentProcess();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var calls = 0;
        var serving = broker.ServeAsync(current, () => ++calls == 1 ? BrokerReply.Cancelled : BrokerReply.Started, stop.Token);
        try
        {
            var wrongParent = endpoint with { ParentId = Environment.ProcessId + 1 };
            check(await ElevationBrokerClient.RequestAsync(wrongParent, stop.Token) == ElevationRequestResult.Unavailable && calls == 0,
                "real pipe rejects mismatched server PID before sending nonce");
            var wrongNonce = endpoint with { Nonce = new byte[32] };
            var badNonceReply = await ElevationBrokerClient.RequestAsync(wrongNonce, stop.Token);
            check(badNonceReply == ElevationRequestResult.Unavailable && calls == 0,
                $"real pipe rejects wrong nonce without elevation callback ({badNonceReply}; calls={calls}; server={serving.Status})");
            var cancelledReply = await ElevationBrokerClient.RequestAsync(endpoint, stop.Token);
            check(cancelledReply == ElevationRequestResult.Cancelled && calls == 1,
                $"real current-user pipe reports cancelled UAC without stopping broker ({cancelledReply}; calls={calls}; server={serving.Status})");
            check(await ElevationBrokerClient.RequestAsync(endpoint, stop.Token) == ElevationRequestResult.Started && calls == 2,
                "same live broker accepts a later retry and returns started");
        }
        finally { stop.Cancel(); try { await serving; } catch (OperationCanceledException) { } }
        check(serving.IsCompleted, "broker listening loop stops after lifetime cancellation");

        using var identityBroker = new ElevationBroker(original);
        var childStart = new ProcessStartInfo(Environment.ProcessPath!)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
        childStart.ArgumentList.Add("--gate-child-wait");
        using var heldChild = Process.Start(childStart)!;
        using var identityStop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var identityCalls = 0;
        var identityServing = identityBroker.ServeAsync(heldChild, () => { identityCalls++; return BrokerReply.Started; }, identityStop.Token);
        var identityArgs = new ProcessStartInfo();
        identityBroker.AddChildArguments(identityArgs);
        try
        {
            var identityEndpoint = ElevationBrokerClient.Parse(identityArgs.ArgumentList.ToArray());
            check(await ElevationBrokerClient.RequestAsync(identityEndpoint, identityStop.Token) == ElevationRequestResult.Unavailable && identityCalls == 0,
                "correct nonce from another real process is rejected by OS client PID");
        }
        finally
        {
            identityStop.Cancel();
            try { await identityServing; } catch (OperationCanceledException) { }
            await heldChild.StandardInput.WriteLineAsync("exit");
            await heldChild.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }

        using var lifetimeBroker = new ElevationBroker(original);
        var helper = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        helper.ArgumentList.Add("--gate-child-exit");
        using var child = Process.Start(helper)!;
        await lifetimeBroker.RunAsync(child).WaitAsync(TimeSpan.FromSeconds(10));
        check(child.HasExited, "actual owned child exit shuts down its waiting elevation broker");
    }
}
