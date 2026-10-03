using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ProcessKeeper.Core;

public enum UninstallApplicationIdentityStatus { Unknown, Resolved, Ambiguous, Truncated, Unavailable }
public enum UninstallApplicationIdentitySource { None, DisplayIcon, InstalledCatalog, InstallationDirectory }
public sealed record UninstallApplicationIdentity
{
    public string ExecutablePath { get; init; } = "";
    public UninstallApplicationIdentityStatus Status { get; init; }
    public UninstallApplicationIdentitySource Source { get; init; }
    public bool MatchedFileName { get; init; }
    public bool MatchedProductMetadata { get; init; }
    public bool IsResolved => Status == UninstallApplicationIdentityStatus.Resolved && ExecutablePath.Length > 0;
}
public sealed record UninstallApplicationFile(string ProductName, string Description, string OriginalFileName);
public sealed record UninstallApplicationDirectory(IReadOnlyList<string> Executables, bool Truncated = false, bool Unavailable = false);

/// <summary>Read-only filesystem boundary. Production implementations reject remote/reparse routes and never launch files.</summary>
public interface IUninstallApplicationIdentityFiles
{
    bool IsSafeDirectory(string path);
    UninstallApplicationDirectory ReadDirectory(string path, int maximumVisitedEntries, int maximumExecutables, CancellationToken token);
    UninstallApplicationFile? ReadExecutable(string path, CancellationToken token);
}

/// <summary>Finds an associated main executable, not a launch guarantee or a trust verdict.
/// A scan shares this instance: directory/file caches and a total I/O budget bound work.
/// The registered uninstall route and its execution identity are never changed.</summary>
public sealed class UninstallApplicationIdentityResolver
{
    public const int MaximumVisitedEntries = 256, MaximumExecutables = 48;
    private readonly IUninstallApplicationIdentityFiles _files;
    private readonly Dictionary<string,List<InstalledApplication>> _installed = new(StringComparer.Ordinal);
    private readonly Dictionary<string,UninstallApplicationDirectory> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,UninstallApplicationFile?> _executables = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _catalogTruncated;
    private long _spentTicks;
    private static readonly HashSet<string> SharedDirectories = new(StringComparer.OrdinalIgnoreCase)
    { "Windows","System32","SysWOW64","Program Files","Program Files (x86)","Common Files","ProgramData","WindowsApps","Users","AppData","Local","Roaming","Temp","Tmp","Desktop","Downloads","Documents","Apps","Applications","Programs","Packages","Installer","Cache","Microsoft","bin","x86","x64","arm64" };
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    { "cmd.exe","powershell.exe","pwsh.exe","rundll32.exe","regsvr32.exe","wscript.exe","cscript.exe","mshta.exe","explorer.exe","control.exe","reg.exe","schtasks.exe","sc.exe","wmic.exe","msiexec.exe","installutil.exe","msbuild.exe","dotnet.exe","python.exe","pythonw.exe","node.exe","bash.exe","java.exe","javaw.exe","electron.exe","nw.exe","svchost.exe","dllhost.exe","conhost.exe","taskhost.exe","taskhostw.exe","runtimebroker.exe" };

    public UninstallApplicationIdentityResolver(IUninstallApplicationIdentityFiles? files = null, IReadOnlyList<InstalledApplication>? installed = null)
    {
        _files=files??new WindowsFiles();
        if(installed is null)return;
        _catalogTruncated=installed.Count>10000;
        foreach(var app in installed.Take(10000))
        {
            var key=NameKey(app.Name,"");if(key.Length==0)continue;
            if(!_installed.TryGetValue(key,out var group))_installed.Add(key,group=new());
            if(group.Count<64)group.Add(app);else _catalogTruncated=true;
        }
    }

    public UninstallApplicationIdentity Resolve(UninstallEntry entry,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();var started=Stopwatch.GetTimestamp();
        bool Available()=>_spentTicks+Stopwatch.GetTimestamp()-started<Stopwatch.Frequency*2L&&_executables.Count<1024&&_directories.Count<256;
        try
        {
            var name=NameKey(entry.Name,entry.Version);if(name.Length<2)return new();
            if(!Available())return new(){Status=UninstallApplicationIdentityStatus.Truncated};
            var roots=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var declared=DirectoryPath(entry.InstallLocation);
            if(declared is not null&&SafeRoot(declared))roots.Add(declared);
            foreach(var path in new[]{entry.IconPath,entry.Command is{IsMsi:false}?entry.Command.Executable:"",entry.QuietCommand is{IsMsi:false}?entry.QuietCommand.Executable:""})
                if(UninstallPolicy.IsLocalExecutable(path)&&!SharedHosts.Contains(Path.GetFileName(path)))
                {var root=Path.GetDirectoryName(path)!;if(SafeRoot(root))roots.Add(root);}
            var trusted=new HashSet<string>(roots,StringComparer.OrdinalIgnoreCase);
            var matches=new Dictionary<string,(int Strength,UninstallApplicationIdentitySource Source,bool Product)>(StringComparer.OrdinalIgnoreCase);
            var incomplete=false;var unavailable=false;
            void Consider(string path,UninstallApplicationIdentitySource source)
            {
                token.ThrowIfCancellationRequested();
                if(!UninstallPolicy.IsLocalExecutable(path)||SharedHosts.Contains(Path.GetFileName(path))||RegisteredUninstaller(entry,path))return;
                var parent=Path.GetDirectoryName(path)!;
                if(!(trusted.Any(root=>Within(path,root))?_files.IsSafeDirectory(parent):SafeRoot(parent)))return;
                var stem=NameKey(Path.GetFileNameWithoutExtension(path),entry.Version);var sameName=stem==name;
                if(!sameName&&Utility(Path.GetFileNameWithoutExtension(path)))return;
                if(!Available()){incomplete=true;return;}
                if(!_executables.TryGetValue(path,out var file))
                {_executables[path]=file=_files.ReadExecutable(path,token);}
                if(file is null||SharedHosts.Contains(Path.GetFileName(file.OriginalFileName)))return;
                if(Utility(Path.GetFileNameWithoutExtension(file.OriginalFileName))&&NameKey(Path.GetFileNameWithoutExtension(file.OriginalFileName),entry.Version)!=name)return;
                var product=NameKey(file.ProductName,entry.Version)==name||NameKey(file.Description,entry.Version)==name;
                if(!sameName&&!product)return;
                if(!sameName&&Utility(file.Description))return;
                var strength=sameName?2:1;
                if(!matches.TryGetValue(path,out var previous)||strength>previous.Strength||source<previous.Source)
                    matches[path]=(strength,source,product);
            }
            Consider(entry.IconPath,UninstallApplicationIdentitySource.DisplayIcon);
            if(_installed.TryGetValue(name,out var applications))
            {
                if(_catalogTruncated)incomplete=true;
                foreach(var app in applications)
                {
                    token.ThrowIfCancellationRequested();var appRoot=DirectoryPath(app.InstallLocation);
                    if(appRoot is null||!SafeRoot(appRoot)||roots.Count>0&&!roots.Any(root=>Within(root,appRoot)||Within(appRoot,root)))continue;
                    if(entry.Publisher.Length>0&&app.Publisher.Length>0&&!entry.Publisher.Equals(app.Publisher,StringComparison.OrdinalIgnoreCase))continue;
                    if(app.Executables.Count>MaximumExecutables){incomplete=true;continue;}
                    trusted.Add(appRoot);
                    foreach(var executable in app.Executables)
                        if(Within(executable.Path,appRoot))Consider(executable.Path,UninstallApplicationIdentitySource.InstalledCatalog);
                }
            }
            foreach(var root in roots)
            {
                token.ThrowIfCancellationRequested();if(!Available()){incomplete=true;break;}
                if(!_directories.TryGetValue(root,out var directory))
                    _directories[root]=directory=_files.ReadDirectory(root,MaximumVisitedEntries,MaximumExecutables,token);
                incomplete|=directory.Truncated;unavailable|=directory.Unavailable;
                if(directory.Executables.Count>MaximumExecutables){incomplete=true;continue;}
                foreach(var path in directory.Executables)
                    if(Path.GetDirectoryName(path)?.Equals(root,StringComparison.OrdinalIgnoreCase)==true)Consider(path,UninstallApplicationIdentitySource.InstallationDirectory);
            }
            if(incomplete)return new(){Status=UninstallApplicationIdentityStatus.Truncated};
            if(unavailable)return new(){Status=UninstallApplicationIdentityStatus.Unavailable};
            if(matches.Count==0)return new();
            var strongest=matches.Max(match=>match.Value.Strength);var choices=matches.Where(match=>match.Value.Strength==strongest).ToArray();
            if(choices.Length!=1)return new(){Status=UninstallApplicationIdentityStatus.Ambiguous};
            var chosen=choices[0];return new(){ExecutablePath=chosen.Key,Status=UninstallApplicationIdentityStatus.Resolved,Source=chosen.Value.Source,MatchedFileName=chosen.Value.Strength==2,MatchedProductMetadata=chosen.Value.Product};
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error)when(ReadFailure(error)){return new(){Status=UninstallApplicationIdentityStatus.Unavailable};}
        finally{_spentTicks+=Stopwatch.GetTimestamp()-started;}
    }

    private bool SafeRoot(string path)=>path.Length>3&&!SharedDirectories.Contains(Path.GetFileName(path.TrimEnd('\\')))&&_files.IsSafeDirectory(path);
    private static bool RegisteredUninstaller(UninstallEntry entry,string path)=>
        entry.Command is{IsMsi:false}&&path.Equals(entry.Command.Executable,StringComparison.OrdinalIgnoreCase)
        ||entry.QuietCommand is{IsMsi:false}&&path.Equals(entry.QuietCommand.Executable,StringComparison.OrdinalIgnoreCase);
    private static bool Within(string path,string root)=>path.Equals(root,StringComparison.OrdinalIgnoreCase)||path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
    private static bool Utility(string value)
    {
        var key=value.ToLowerInvariant();return key.StartsWith("uninst",StringComparison.Ordinal)||key.StartsWith("unins0",StringComparison.Ordinal)
            ||new[]{"uninstall","uninstaller","updater","crashpad","crashhandler","crashreporter","qtwebengineprocess","cefsubprocess","webview2","maintenance","bootstrap","helper","卸载","卸載","解除安裝"}.Any(marker=>key.Contains(marker))
            ||key is "setup" or "install" or "update" or "service" or "broker" or "host" or "repair";
    }
    private static string NameKey(string value,string version)
    {
        value=value.Trim();if(version.Length>0&&value.EndsWith(version,StringComparison.OrdinalIgnoreCase))value=value.Substring(0,value.Length-version.Length);
        value=value.Replace("(x64)","").Replace("(x86)","").Replace("(arm64)","");
        var text=new StringBuilder();foreach(var character in value)if(char.IsLetterOrDigit(character))text.Append(char.ToLowerInvariant(character));
        return text.ToString() switch{"豆包"=>"doubao","豆包工作"=>"doubaowork",var key=>key};
    }
    private static string? DirectoryPath(string value)
    {
        try
        {
            value=Environment.ExpandEnvironmentVariables(value.Trim().Trim('"')).TrimEnd('\\');
            if(value.Length<3||value.Length>=32700||!char.IsLetter(value[0])||value[1]!=':'||value[2]!='\\'||value.IndexOf(':',2)>=0||value.Any(char.IsControl)||value.Split('\\','/').Any(part=>part is "." or ".."))return null;
            var full=Path.GetFullPath(value);return full.Equals(value,StringComparison.OrdinalIgnoreCase)?full:null;
        }
        catch(Exception error)when(ReadFailure(error)){return null;}
    }
    private static bool ReadFailure(Exception error)=>error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException or System.ComponentModel.Win32Exception;

    private sealed class WindowsFiles:IUninstallApplicationIdentityFiles
    {
        private readonly Dictionary<string,bool> _drives=new(StringComparer.OrdinalIgnoreCase);
        public bool IsSafeDirectory(string path)
        {
            try
            {
                if(DirectoryPath(path) is null)return false;
                var drive=Path.GetPathRoot(path)!;
                if(!_drives.TryGetValue(drive,out var local))_drives[drive]=local=new DriveInfo(drive).DriveType is DriveType.Fixed or DriveType.Removable;
                if(!local)return false;
                using var lease=new DirectoryLease(path);return true;
            }
            catch(Exception error)when(ReadFailure(error)){return false;}
        }
        public UninstallApplicationDirectory ReadDirectory(string path,int maximumVisitedEntries,int maximumExecutables,CancellationToken token)
        {
            var paths=new List<string>();
            try
            {
                using var lease=new DirectoryLease(path);var visited=0;
                foreach(var item in Directory.EnumerateFileSystemEntries(path))
                {
                    token.ThrowIfCancellationRequested();if(++visited>maximumVisitedEntries)return new(paths.ToArray(),Truncated:true);
                    var attributes=File.GetAttributes(item);if((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint))!=0)continue;
                    if(!Path.GetExtension(item).Equals(".exe",StringComparison.OrdinalIgnoreCase))continue;
                    if(paths.Count>=maximumExecutables)return new(paths.ToArray(),Truncated:true);paths.Add(item);
                }
                return new(paths.ToArray());
            }
            catch(OperationCanceledException){throw;}
            catch(Exception error)when(ReadFailure(error)){return new(paths.ToArray(),Unavailable:true);}
        }
        public UninstallApplicationFile? ReadExecutable(string path,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var lease=new DirectoryLease(Path.GetDirectoryName(path)!);
                var handle=CreateFile(path,0x80000000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
                if(handle.IsInvalid){handle.Dispose();return null;}
                using var stream=new FileStream(handle,FileAccess.Read);
                if(!GetFileInformationByHandle(handle,out var information)||(information.Attributes&0x410)!=0||stream.Length<88)return null;
                using var reader=new BinaryReader(stream,Encoding.UTF8,leaveOpen:true);
                if(reader.ReadUInt16()!=0x5A4D)return null;stream.Position=0x3C;var offset=reader.ReadInt32();
                if(offset<64||offset>1024*1024||offset+24L>stream.Length)return null;stream.Position=offset;
                if(reader.ReadUInt32()!=0x4550)return null;stream.Position=offset+20;var optional=reader.ReadUInt16();var characteristics=reader.ReadUInt16();
                if((characteristics&0x2002)!=2||optional<70||offset+24L+optional>stream.Length)return null;
                var magic=reader.ReadUInt16();if(magic is not (0x10B or 0x20B))return null;
                stream.Position=offset+24+68;var subsystem=reader.ReadUInt16();if(subsystem is not (2 or 3))return null;
                token.ThrowIfCancellationRequested();var version=FileVersionInfo.GetVersionInfo(path);
                return new(version.ProductName??"",version.FileDescription??"",version.OriginalFilename??"");
            }
            catch(OperationCanceledException){throw;}
            catch(Exception error)when(ReadFailure(error)){return null;}
        }
    }

    private sealed class DirectoryLease:IDisposable
    {
        private readonly List<SafeFileHandle> _handles=new();
        internal DirectoryLease(string path)
        {
            try
            {
                var directories=new Stack<string>();for(var current=path;!string.IsNullOrEmpty(current);current=Path.GetDirectoryName(current))directories.Push(current);
                var root=Path.GetPathRoot(path)!;if(!directories.Contains(root,StringComparer.OrdinalIgnoreCase))directories.Push(root);
                foreach(var directory in directories)
                {
                    var handle=CreateFile(directory,0x80,3,IntPtr.Zero,3,0x02200000,IntPtr.Zero);_handles.Add(handle);
                    if(handle.IsInvalid||!GetFileInformationByHandle(handle,out var information)||(information.Attributes&0x410)!=0x10)throw new IOException("Directory identity is not local regular storage.");
                }
            }
            catch{Dispose();throw;}
        }
        public void Dispose(){foreach(var handle in _handles)handle.Dispose();_handles.Clear();}
    }
    [StructLayout(LayoutKind.Sequential)]private struct NativeTime{public uint Low,High;}
    [StructLayout(LayoutKind.Sequential)]private struct FileInformation{public uint Attributes;public NativeTime Creation,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32),DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation information);
}
