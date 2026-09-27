using ProcessKeeper.Core;

ProcessKeeper.Core.L.Language = "zh-Hans";

var suite = new SpecialWindowSuite();
await PolicyTests.Run(suite);
await ServiceTests.Run(suite);
await RecoveryHintTests.Run(suite);
Console.WriteLine($"Special window fixtures: {suite.Passed} cases passed, {suite.Assertions} assertions checked; {suite.Failed} cases failed. No real targets were launched or terminated.");
Environment.ExitCode = suite.Failed == 0 ? 0 : 1;

internal sealed class SpecialWindowSuite
{
    public int Passed { get; private set; }
    public int Failed { get; private set; }
    public int Assertions { get; private set; }

    public async Task Case(string name, Func<Task> test)
    {
        try { await test(); Passed++; Console.WriteLine("PASS: " + name); }
        catch (Exception exception) { Failed++; Console.WriteLine($"FAIL: {name}: {exception}"); }
    }

    public Task Case(string name, Action test) => Case(name, () => { test(); return Task.CompletedTask; });

    public void Check(bool condition, string message)
    {
        Assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }

    public void Reject(Action test, string message)
    {
        Assertions++;
        try { test(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException(message);
    }
}
