using ProcessKeeper.Core;

internal static class InstanceRedirectVerification
{
    public static void Run(Action<bool, string> check)
    {
        check(InstanceRedirect.MatchesDeclaredRoute(UpdatePackageTarget.Windows7Compat, 0x14c, false), "compatibility receipt authorizes only its real x86 fallback image");
        check(!InstanceRedirect.MatchesDeclaredRoute(UpdatePackageTarget.Windows7Compat, 0x8664, true), "compatibility flavor cannot impersonate native modern image");
        check(InstanceRedirect.MatchesDeclaredRoute(UpdatePackageTarget.Windows10x64, 0x8664, true) && InstanceRedirect.MatchesDeclaredRoute(UpdatePackageTarget.Windows10x64, 0x14c, false), "dual x86 x64 flavor admits exactly its eligible modern and legacy routes");
        check(!InstanceRedirect.MatchesDeclaredRoute(UpdatePackageTarget.Windows10x64, 0xaa64, true), "dual Intel AMD flavor cannot authorize ARM native image");
        check(InstanceRedirect.MatchesDeclaredRoute(UpdatePackageTarget.Windows10arm64, 0xaa64, true) && !InstanceRedirect.MatchesDeclaredRoute(UpdatePackageTarget.Windows10arm64, 0x14c, false), "native ARM flavor has no unverified legacy fallback");
        check(!InstanceRedirect.MatchesDeclaredRoute(UpdatePackageTarget.Unsupported, 0x8664, true), "unknown declared package carries no singleton authority");
        check(InstanceRedirect.MatchesVersionLabels("ProcessKeeper", "1.7.0+source", "v1.7.0") && InstanceRedirect.MatchesVersionLabels("Process Keeper", "1.7.0", "1.7.0+other"), "actual metadata accepts both shipped product names and equal SemVer build metadata");
        check(!InstanceRedirect.MatchesVersionLabels("Different App", "1.7.0", "1.7.0"), "display name in claim cannot authorize foreign product VERSIONINFO");
        check(!InstanceRedirect.MatchesVersionLabels("Process Keeper", "1.6.0", "1.7.0") && !InstanceRedirect.MatchesVersionLabels("ProcessKeeper", "99.0.0", "1.7.0"), "declared receipt version must match real executable metadata");
        check(!InstanceRedirect.MatchesVersionLabels("ProcessKeeper", "invalid", "1.7.0") && !InstanceRedirect.MatchesVersionLabels("ProcessKeeper", "1.7.0", ""), "malformed actual or declared version fails closed");
        check(!InstanceRedirect.MatchesVersionLabels("ProcessKeeper", "1.7.0-beta", "1.7.0") && !InstanceRedirect.MatchesVersionLabels("ProcessKeeper", "1.7.0", "1.7.0-beta"), "prerelease metadata never impersonates final release authority");
        using var malformed = new MemoryStream(new byte[128]);
        check(!InstanceRedirect.MatchesExecutableMetadata("invalid", malformed, 0x14c, "1.7.0") && malformed.Position == 0, "peer PE verification rejects invalid DOS image and restores stream position");
        check(!InstanceRedirect.TryGetPeer("../outside", out _) && !InstanceRedirect.TryGetPeer(new string('a', 31), out _), "invalid context locator rejected before touching any cache");
        check(!InstanceRedirect.IsPeerAlive(new("", 0, 0, -1, "invalid", "99.0.0", true)), "missing process cannot remain cached singleton winner");
    }
}
