using ProcessKeeper.Core;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    count++; Console.WriteLine("PASS " + name);
}
Check(ProcessArchitecturePolicy.IsModernProcessArchitecture("X64"), "x64 modern UI remains supported");
Check(ProcessArchitecturePolicy.IsModernProcessArchitecture("Arm64"), "native ARM64 modern UI is supported");
foreach (var unsupported in new[] { "X86", "Arm", "ARM32", "Arm64EC", "", "unknown" })
    Check(!ProcessArchitecturePolicy.IsModernProcessArchitecture(unsupported), "unverified modern ISA is not guessed: " + unsupported);
Check(ProcessArchitecturePolicy.CanReadX64ProcessEnvironment(8, "X64", "X64"), "verified native x64 private PEB layout remains enabled");
foreach (var process in new[] { "Arm64", "Arm", "X86", "unknown" })
    Check(!ProcessArchitecturePolicy.CanReadX64ProcessEnvironment(8, process, "Arm64"), "64-bit pointer width cannot authorize an unverified environment layout: " + process);
Check(!ProcessArchitecturePolicy.CanReadX64ProcessEnvironment(8, "X64", "Arm64"), "x64 emulation on ARM64 does not imply the verified native x64 target layout");
Check(!ProcessArchitecturePolicy.CanReadX64ProcessEnvironment(4, "X64", "X64"), "32-bit collector cannot read the x64 environment layout");
Console.WriteLine($"Architecture checks: {count} passed | pure inputs, no product launch or process memory access.");
