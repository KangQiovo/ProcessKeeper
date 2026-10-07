using ProcessKeeper.Core;

internal static class InstalledPackageCatalogExternalLocationTests
{
    internal static void Run(Action<bool, string> check, System.Reflection.Assembly? packageAssembly = null)
    {
        var assembly = packageAssembly ?? typeof(InstalledApplicationCatalog).Assembly;
        var type = assembly.GetType("ProcessKeeper.Core.InstalledPackageCatalog", throwOnError: true)!;
        var select = type.GetMethod("SelectExternalInstallLocation", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException(type.FullName, "SelectExternalInstallLocation");
        string Select(string? path, bool development, bool healthy) =>
            (string)select.Invoke(null, new object?[] { path, development, healthy })!;
        var root = Path.Combine(Path.GetTempPath(), "ProcessKeeper.PackageExternalLocationTests", Guid.NewGuid().ToString("N"));
        if (Path.GetPathRoot(root)?.Equals(@"C:\", StringComparison.OrdinalIgnoreCase) != false)
            throw new InvalidOperationException("Package external-location fixtures require non-C temporary storage.");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, "Program Files"));
        Directory.CreateDirectory(Path.Combine(root, "WindowsApps"));
        check(Select(root, false, true) == root,
            "a healthy registered package can retain its existing external application directory");
        check(Select(root + "\\", false, true) == root,
            "a registered external directory keeps the same identity with a trailing separator");
        check(Select(root, true, true).Length == 0,
            "development packages cannot associate an external installation");
        check(Select(root, false, false).Length == 0,
            "unhealthy packages cannot associate an external installation");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0)
            check(Select(profile, false, true).Length == 0,
                "a registered package cannot associate the entire existing user profile as an installation");
        foreach (var candidate in new string?[]
        {
            null, "", " ", "relative\\app", @"E:relative", @"E:\", @"\\server\share\app",
            @"\\?\E:\App", @"\\.\E:\App", root + ":stream", root + "\\..\\Escape",
            root + "\\.\\Child", root + ".", root + " ", root + "\\missing", root + "\\*",
            root + "\\Program Files", root + "\\WindowsApps"
        })
            check(Select(candidate, false, true).Length == 0,
                "unsafe or unavailable external installation is not associated: " + (candidate ?? "null"));
        var file = Path.Combine(root, "not-a-directory");
        File.WriteAllText(file, "owned fixture");
        check(Select(file, false, true).Length == 0,
            "an external registration cannot associate an ordinary file as an installation directory");
        var seedType = assembly.GetType("ProcessKeeper.Core.InstalledPackageSeed", throwOnError: true)!;
        var seed = Activator.CreateInstance(seedType, new object?[] { "Fixture", "Publisher", "family", "full", @"E:\PackageManifest", Array.Empty<string>(), "", "" })!;
        check((string)seedType.GetProperty("ExternalInstallLocation")!.GetValue(seed)! == "" &&
            ((IReadOnlyList<string>)seedType.GetProperty("ExecutablePaths")!.GetValue(seed)!).Count == 0,
            "existing package seeds retain zero components without inventing an external executable");
    }
}
