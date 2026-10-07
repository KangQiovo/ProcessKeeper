using System.Text;

namespace ProcessKeeper.Core;

public enum InstalledExecutableRole { Unknown, Main, PotentialMain, Uninstaller, PotentialUninstaller, Diagnostic, Helper }
public enum InstalledExecutableEvidence { None, ExplicitEntry, RegisteredUninstaller, AssociatedMain, KnownFileName, ApplicationName, ProductDescription, UtilityFileName }
public enum InstalledMainIdentityStatus { Unknown, Resolved, Ambiguous, Truncated }
public sealed record InstalledExecutableRoleIdentity
{
    public InstalledExecutableRole Role { get; init; }
    public InstalledExecutableEvidence Evidence { get; init; }
    public bool IsUncertain { get; init; } = true;
}
public sealed record InstalledMainIdentity
{
    public string ExecutablePath { get; init; } = "";
    public InstalledMainIdentityStatus Status { get; init; }
    public bool IsUncertain { get; init; } = true;
}

/// <summary>Uses existing inventory evidence only. Never reads files, launches software or proves it is safe/runnable.</summary>
public sealed class InstalledExecutableIdentity
{
    public const int MaximumExecutables = 300, MaximumRegistrations = 10000;
    private readonly HashSet<string> _uninstallers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _associatedMain = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<InstalledApplication,ApplicationRoles> _applications = new();
    private readonly bool _registrationsTruncated;
    private static readonly HashSet<string> Hosts = new(StringComparer.OrdinalIgnoreCase)
    { "cmd.exe","powershell.exe","pwsh.exe","rundll32.exe","regsvr32.exe","wscript.exe","cscript.exe","mshta.exe","explorer.exe","control.exe","reg.exe","schtasks.exe","sc.exe","wmic.exe","msiexec.exe","installutil.exe","msbuild.exe","dotnet.exe","python.exe","pythonw.exe","node.exe","bash.exe","java.exe","javaw.exe","electron.exe","nw.exe","svchost.exe","dllhost.exe","conhost.exe","taskhost.exe","taskhostw.exe","runtimebroker.exe" };

    public InstalledExecutableIdentity(IReadOnlyList<UninstallEntry>? registrations = null)
    {
        if(registrations is null)return;
        _registrationsTruncated=registrations.Count>MaximumRegistrations;
        foreach(var entry in registrations.Take(MaximumRegistrations))
        {
            foreach(var command in new[]{entry.Command,entry.QuietCommand})
                if(command is{IsMsi:false}&&UninstallPolicy.IsLocalExecutable(command.Executable))_uninstallers.Add(command.Executable);
            if(entry.ApplicationIdentity is{IsResolved:true} main&&entry.ApplicationPaths.Count<=UninstallApplicationIdentityResolver.MaximumExecutables
                &&entry.ApplicationPaths.Any(path=>path.Equals(main.ExecutablePath,StringComparison.OrdinalIgnoreCase))
                &&UninstallPolicy.IsLocalExecutable(main.ExecutablePath))_associatedMain.Add(main.ExecutablePath);
        }
    }

    public InstalledExecutableRoleIdentity Classify(InstalledApplication application,InstalledExecutable executable)
    {
        ArgumentNullException.ThrowIfNull(application);ArgumentNullException.ThrowIfNull(executable);
        return GetRoles(application).Roles.TryGetValue(executable.Path,out var role)?role:new();
    }
    public IReadOnlyList<InstalledExecutable> OrderExecutables(InstalledApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);
        var roles=GetRoles(application).Roles;
        // LINQ OrderBy is stable: components with the same priority keep their
        // original inventory order. This method never changes catalog evidence.
        return application.Executables.OrderBy(executable=>roles.TryGetValue(executable.Path,out var identity)
            ? identity.Role switch
            {
                InstalledExecutableRole.Main=>0,
                InstalledExecutableRole.PotentialMain=>1,
                InstalledExecutableRole.Uninstaller or InstalledExecutableRole.PotentialUninstaller=>2,
                _=>3
            }:3).ToArray();
    }
    public InstalledMainIdentity ResolveMain(InstalledApplication application)
    {
        ArgumentNullException.ThrowIfNull(application);var result=GetRoles(application);
        if(result.Truncated)return new(){Status=InstalledMainIdentityStatus.Truncated};
        var main=result.Roles.Where(pair=>pair.Value.Role==InstalledExecutableRole.Main).ToArray();
        if(main.Length==0)main=result.Roles.Where(pair=>pair.Value.Role==InstalledExecutableRole.PotentialMain).ToArray();
        if(main.Length==0)return new();
        if(main.Length!=1)return new(){Status=InstalledMainIdentityStatus.Ambiguous};
        return new(){ExecutablePath=main[0].Key,Status=InstalledMainIdentityStatus.Resolved,IsUncertain=main[0].Value.IsUncertain};
    }
    private ApplicationRoles GetRoles(InstalledApplication application)
    {
        if(_applications.TryGetValue(application,out var found))return found;
        var roles=new Dictionary<string,InstalledExecutableRoleIdentity>(StringComparer.OrdinalIgnoreCase);
        if(application.Installations.Count>0)
        {
            bool truncated=_registrationsTruncated;
            foreach(var member in application.Installations)
            {
                var original=GetRoles(member);truncated|=original.Truncated;
                foreach(var pair in original.Roles)
                    if(!roles.TryGetValue(pair.Key,out var previous)||RoleStrength(pair.Value)>RoleStrength(previous))roles[pair.Key]=pair.Value;
            }
            found=new(roles,truncated);
            if(_applications.Count<10000)_applications.Add(application,found);
            return found;
        }
        var entries=new HashSet<string>(application.EntryPaths.Take(MaximumExecutables),StringComparer.OrdinalIgnoreCase);
        var name=NameKey(application.Name);
        var englishName=EnglishNameKey(application.Name);
        foreach(var executable in application.Executables.Take(MaximumExecutables))
        {
            if(roles.ContainsKey(executable.Path))continue;
            var role=new InstalledExecutableRoleIdentity();
            if(UninstallPolicy.IsLocalExecutable(executable.Path))
            {
                var file=Path.GetFileName(executable.Path);var stem=Path.GetFileNameWithoutExtension(file);var key=NameKey(stem);
                var utility=UtilityRole(stem,name);
                if(_uninstallers.Contains(executable.Path))role=new(){Role=InstalledExecutableRole.Uninstaller,Evidence=InstalledExecutableEvidence.RegisteredUninstaller,IsUncertain=false};
                else if(Hosts.Contains(file))role=new(){Role=InstalledExecutableRole.Helper,Evidence=InstalledExecutableEvidence.UtilityFileName};
                else if(utility!=InstalledExecutableRole.Unknown)role=new(){Role=utility,Evidence=InstalledExecutableEvidence.UtilityFileName};
                else if(entries.Contains(executable.Path))role=new(){Role=_registrationsTruncated?InstalledExecutableRole.PotentialMain:InstalledExecutableRole.Main,Evidence=InstalledExecutableEvidence.ExplicitEntry,IsUncertain=_registrationsTruncated};
                else if(_associatedMain.Contains(executable.Path))role=new(){Role=InstalledExecutableRole.PotentialMain,Evidence=InstalledExecutableEvidence.AssociatedMain};
                else if(name=="cloudmusic"&&file.Equals("cloudmusic.exe",StringComparison.OrdinalIgnoreCase))role=new(){Role=InstalledExecutableRole.PotentialMain,Evidence=InstalledExecutableEvidence.KnownFileName};
                else if(name.Length>=2&&name==key)role=new(){Role=InstalledExecutableRole.PotentialMain,Evidence=InstalledExecutableEvidence.ApplicationName};
                else if(englishName.Length>=2&&englishName==key)role=new(){Role=InstalledExecutableRole.PotentialMain,Evidence=InstalledExecutableEvidence.ApplicationName};
                else if(name.Length>=2&&name==NameKey(executable.Description))role=new(){Role=InstalledExecutableRole.PotentialMain,Evidence=InstalledExecutableEvidence.ProductDescription};
            }
            roles.Add(executable.Path,role);
        }
        found=new(roles,application.Executables.Count>MaximumExecutables||application.EntryPaths.Count>MaximumExecutables||_registrationsTruncated);
        // A single inventory refresh creates one instance. Cap retained app objects as well.
        if(_applications.Count<10000)_applications.Add(application,found);
        return found;
    }
    private static InstalledExecutableRole UtilityRole(string value,string applicationName)
    {
        if(NameKey(value)==applicationName)return InstalledExecutableRole.Unknown;
        var stem=value.ToLowerInvariant();
        if(new[]{"crashpad","crashreport","crashhandler","minidump","diagnostic","bugreport","errorreport"}.Any(marker=>stem.Contains(marker))
            ||stem is "reporter" or "dump"||stem.EndsWith("_reporter",StringComparison.Ordinal)||stem.EndsWith("reporter",StringComparison.Ordinal))return InstalledExecutableRole.Diagnostic;
        if(new[]{"helper","cssdk","cefsubprocess","qtwebengineprocess","webview2","updater","service","maintenance","bootstrap"}.Any(marker=>stem.Contains(marker))
            ||stem.EndsWith("_util",StringComparison.Ordinal)||stem is "util" or "setup" or "install" or "update" or "service" or "broker" or "host" or "repair")return InstalledExecutableRole.Helper;
        if(UninstallExecutableNames.IsPossible(value,applicationName))return InstalledExecutableRole.PotentialUninstaller;
        return InstalledExecutableRole.Unknown;
    }
    private static string NameKey(string value)
    {
        var text=new StringBuilder();foreach(var character in value)if(char.IsLetterOrDigit(character))text.Append(char.ToLowerInvariant(character));
        return text.ToString() switch{"网易云音乐" or "網易雲音樂" or "neteasecloudmusic"=>"cloudmusic","豆包"=>"doubao","豆包工作"=>"doubaowork",var key=>key};
    }
    private static string EnglishNameKey(string value)
    {
        // This remains a filename heuristic, never proof that an EXE is safe or runnable.
        // Bound input and remove only terminal version/architecture tokens, preserving 7-Zip etc.
        var text=new StringBuilder();
        foreach(var character in value.Take(256))
            text.Append(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_' or '.' ? character : ' ');
        var tokens=text.ToString().Split(new[]{' '},StringSplitOptions.RemoveEmptyEntries).ToList();
        while(tokens.Count>0)
        {
            var terminal=tokens[tokens.Count-1].ToLowerInvariant();
            var version=terminal.TrimStart('v');
            if(terminal is "x64" or "x86" or "arm64" or "64-bit" or "32-bit" ||
                version.Length>0&&version.Any(char.IsDigit)&&version.All(c=>char.IsDigit(c)||c=='.'))tokens.RemoveAt(tokens.Count-1);
            else break;
        }
        var joined=string.Join(" ",tokens);
        return joined.Count(c=>c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')>=3?NameKey(joined):"";
    }
    private sealed record ApplicationRoles(Dictionary<string,InstalledExecutableRoleIdentity> Roles,bool Truncated);
    private static int RoleStrength(InstalledExecutableRoleIdentity role)=>role.Role switch
    {
        InstalledExecutableRole.Uninstaller when !role.IsUncertain=>100,
        InstalledExecutableRole.Main=>90,InstalledExecutableRole.PotentialUninstaller=>80,
        InstalledExecutableRole.Diagnostic or InstalledExecutableRole.Helper=>70,
        InstalledExecutableRole.PotentialMain=>60,_=>0
    };
}
