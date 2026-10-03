using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ProcessKeeper.Core;

internal static class UninstallApplicationIdentityVerification
{
    internal static void Run(Action<bool,string> check)
    {
        var basePath=Environment.GetEnvironmentVariable("PROCESSKEEPER_IDENTITY_FIXTURE_ROOT")??Path.GetTempPath();
        var root=Path.GetFullPath(Path.Combine(basePath,"identity-"+Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var ownedExecutable=Process.GetCurrentProcess().MainModule!.FileName;
            string Executable(string folder,string name)
            {var directory=Path.Combine(root,folder);Directory.CreateDirectory(directory);var path=Path.Combine(directory,name);File.Copy(ownedExecutable,path);WriteFixtureVersion(path);return path;}
            var main=Executable("Doubao","Doubao.exe");var uninstall=Executable("Doubao","uninst.exe");
            var entry=new UninstallEntry{Name="豆包",InstallLocation=Path.GetDirectoryName(main)!,IconPath=uninstall,Command=new(uninstall,"",false)};
            check(Observe(entry).Path==main,"Doubao registration whose icon is its uninstaller resolves the actual sibling Doubao executable");
            check(entry.IconPath==uninstall&&entry.Command!.Executable==uninstall,"finding the main application never rewrites the registered uninstaller");
            var workMain=Executable("DoubaoWork","DoubaoWork.exe");var workUninstall=Executable("DoubaoWork","uninstall.exe");
            var workEntry=new UninstallEntry{Name="豆包工作",IconPath=Path.Combine(Path.GetDirectoryName(workMain)!,"icon.ico"),Command=new(workUninstall,"",false)};
            check(Observe(workEntry).Path==workMain,"DoubaoWork's localized registration and ICO icon still resolve its actual sibling launcher executable");
            File.Delete(main);
            check(Observe(entry).Path.Length==0,"an installation containing only an uninstaller has no invented main executable");

            var ambiguousMain=Executable("AmbiguousDoubao","Doubao.exe");var localizedMain=Executable("AmbiguousDoubao","豆包.exe");var ambiguousUninstall=Executable("AmbiguousDoubao","uninstall.exe");
            var ambiguous=Observe(entry with{InstallLocation=Path.GetDirectoryName(ambiguousMain)!,IconPath=ambiguousMain,Command=new(ambiguousUninstall,"",false)});
            check(ambiguous.Path.Length==0&&ambiguous.Status=="Ambiguous","two actual matching executable identities stay explicitly ambiguous");

            var metadataMain=Executable("IdentityFixture","client.exe");var metadataUninstall=Executable("IdentityFixture","unins000.exe");
            var nativeVersion=FileVersionInfo.GetVersionInfo(metadataMain);
            if(nativeVersion.ProductName!="Identity Fixture"||nativeVersion.FileDescription!="Identity Fixture"||nativeVersion.OriginalFilename!="IdentityClient.exe")
                throw new InvalidDataException("The owned native fixture did not receive its actual VERSIONINFO resource: "+nativeVersion.ProductName+" | "+nativeVersion.FileDescription+" | "+nativeVersion.OriginalFilename);
            var product=Observe(new UninstallEntry{Name="Identity Fixture",InstallLocation=Path.GetDirectoryName(metadataMain)!,IconPath=metadataUninstall,Command=new(metadataUninstall,"",false)});
            check(product.Path==metadataMain&&product.Product&&!product.FileName,"actual native VERSIONINFO associates a main executable whose file name differs from the registration");

            var sharedMain=Executable("Apps","Doubao.exe");var sharedUninstall=Executable("Apps","uninstall.exe");
            check(Observe(entry with{InstallLocation=Path.GetDirectoryName(sharedMain)!,IconPath=sharedMain,Command=new(sharedUninstall,"",false)}).Path.Length==0,"shared generic installation folders do not establish a main application");
            check(Observe(entry with{InstallLocation=Path.GetPathRoot(root)!,IconPath="",Command=null}).Path.Length==0,"a drive root is never enumerated for application identity");
            check(Observe(entry with{InstallLocation=@"\\server\share\Doubao",IconPath=@"\\server\share\Doubao\Doubao.exe",Command=null}).Path.Length==0,"remote paths are rejected before any application discovery");
            check(Observe(entry with{InstallLocation=Path.Combine(root,"Doubao",".."),IconPath="",Command=null}).Path.Length==0,"traversal components cannot broaden application discovery");
            var invalid=Path.Combine(root,"InvalidDoubao");Directory.CreateDirectory(invalid);File.WriteAllText(Path.Combine(invalid,"Doubao.exe"),"not an executable");
            check(Observe(entry with{InstallLocation=invalid,IconPath=Path.Combine(invalid,"Doubao.exe"),Command=null}).Path.Length==0,"a renamed text file is not a discoverable main executable");
            var nested=Executable("NestedDoubao\\child","Doubao.exe");var nestedRoot=Path.GetDirectoryName(Path.GetDirectoryName(nested)!)!;
            check(Observe(entry with{InstallLocation=nestedRoot,IconPath="",Command=null}).Path.Length==0,"uninstall identity discovery never recursively walks child folders");
            var crowded=Path.Combine(root,"CrowdedDoubao");Directory.CreateDirectory(crowded);
            for(var i=0;i<300;i++)File.WriteAllText(Path.Combine(crowded,i.ToString("D4")+".txt"),"");
            File.Copy(ownedExecutable,Path.Combine(crowded,"Doubao.exe"));
            var partial=Observe(entry with{InstallLocation=crowded,IconPath="",Command=null});
            check(partial.Path.Length==0&&partial.Status=="Truncated","an incomplete bounded directory scan never claims unique application ownership");
            var childMain=Executable("DedicatedDoubao\\bin","Doubao.exe");
            check(Observe(entry with{InstallLocation=Path.GetDirectoryName(Path.GetDirectoryName(childMain)!)!,IconPath=childMain,Command=null}).Path==childMain,"an explicit icon inside a dedicated installation can identify a main executable in its bin child");

            var dllFolder=Path.Combine(root,"DllDoubao");Directory.CreateDirectory(dllFolder);var dll=Path.Combine(dllFolder,"Doubao.exe");
            File.Copy(typeof(UninstallPolicy).Assembly.Location,dll);
            check(Observe(entry with{InstallLocation=dllFolder,IconPath=dll,Command=null}).Path.Length==0,"a PE DLL renamed to EXE cannot be treated as a runnable main program");
            var link=Path.Combine(root,"LinkedDoubao");var target=Path.GetDirectoryName(childMain)!;
            if(CreateSymbolicLink(link,target,3)!=0||CreateSymbolicLink(link,target,1)!=0)
                check(Observe(entry with{InstallLocation=link,IconPath=Path.Combine(link,"Doubao.exe"),Command=null}).Path.Length==0,"a native reparse directory cannot establish an application main executable");
            else Console.WriteLine("SKIP: native reparse fixture cannot create an owned E-drive directory link | Win32 "+Marshal.GetLastWin32Error());

            VerifyBoundaries(check,root);
        }
        finally
        {
            var expected=Path.GetFullPath(basePath).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
            if(!root.StartsWith(expected,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Fixture cleanup target escaped its assigned root.");
            Directory.Delete(root,true);
        }
    }

    private static void VerifyBoundaries(Action<bool,string> check,string root)
    {
        var files=new FixtureFiles();var folder=Path.Combine(root,"CatalogDoubao");var nested=Path.Combine(folder,"bin");
        var main=Path.Combine(nested,"Doubao.exe");files.Safe.Add(folder);files.Safe.Add(nested);files.Files[main]=new("Doubao","Doubao","");
        var app=new InstalledApplication{Name="Doubao",InstallLocation=folder,Executables=new[]{new InstalledExecutable{Path=main}}};
        var resolver=new UninstallApplicationIdentityResolver(files,new[]{app});var entry=new UninstallEntry{Name="豆包",InstallLocation=folder};
        check(resolver.Resolve(entry).ExecutablePath==main,"catalog evidence may name a bounded executable in a trusted installation's bin child");
        var firstReads=files.FileReads;var firstDirectories=files.DirectoryReads;resolver.Resolve(entry);
        check(files.FileReads==firstReads&&files.DirectoryReads==firstDirectories,"duplicate registrations reuse per-scan directory and executable reads");
        var other=Path.Combine(root,"SecondDoubao");var otherMain=Path.Combine(other,"Doubao.exe");files.Safe.Add(other);files.Files[otherMain]=new("Doubao","Doubao","");
        var second=app with{InstallLocation=other,Executables=new[]{new InstalledExecutable{Path=otherMain}}};
        var both=new UninstallApplicationIdentityResolver(files,new[]{app,second}).Resolve(entry with{InstallLocation=""});
        check(both.Status==UninstallApplicationIdentityStatus.Ambiguous&&!both.IsResolved,"two catalog installations with the same application name remain ambiguous");
        var outside=Path.Combine(root,"UnrelatedDoubao");var outsideMain=Path.Combine(outside,"Doubao.exe");files.Safe.Add(outside);files.Files[outsideMain]=new("Doubao","Doubao","");
        var misplaced=app with{Executables=new[]{new InstalledExecutable{Path=outsideMain}}};
        check(!new UninstallApplicationIdentityResolver(files,new[]{misplaced}).Resolve(entry).IsResolved,"catalog executable paths outside their declared installation are not adopted");
        files.Files[main]=new("Doubao","Doubao","rundll32.exe");
        check(!new UninstallApplicationIdentityResolver(files,new[]{app}).Resolve(entry).IsResolved,"a renamed shared executable host cannot be treated as the application's main executable");
        files.Files[main]=new("Doubao","Doubao","crashpad_handler.exe");
        check(!new UninstallApplicationIdentityResolver(files,new[]{app}).Resolve(entry).IsResolved,"a renamed diagnostic helper cannot be treated as the application's main executable");
        files.Files[main]=new("Doubao","Doubao","");files.Directories[folder]=new(new[]{main},Truncated:true);
        var incomplete=new UninstallApplicationIdentityResolver(files,new[]{app}).Resolve(entry);
        check(incomplete.Status==UninstallApplicationIdentityStatus.Truncated&&!incomplete.IsResolved,"a matching catalog path does not override an incomplete directory scan");
        files.Directories[folder]=new(Array.Empty<string>(),Unavailable:true);
        var unavailable=new UninstallApplicationIdentityResolver(files,new[]{app}).Resolve(entry);
        check(unavailable.Status==UninstallApplicationIdentityStatus.Unavailable&&!unavailable.IsResolved,"an unreadable related directory is reported without inventing unique ownership");
        var cancelled=new CancellationTokenSource();cancelled.Cancel();var threw=false;
        try{resolver.Resolve(entry,cancelled.Token);}catch(OperationCanceledException){threw=true;}
        check(threw,"application identity discovery respects cancellation before any native reads");
        var capFiles=new FixtureFiles();capFiles.Safe.Add(folder);var registrations=new List<InstalledApplication>();
        for(var i=0;i<65;i++)registrations.Add(app);
        check(new UninstallApplicationIdentityResolver(capFiles,registrations).Resolve(entry).Status==UninstallApplicationIdentityStatus.Truncated,"a truncated same-name catalog never claims a complete main identity");
        var publisherApp=app with{Publisher="Unrelated Publisher"};
        files.Directories[folder]=new(Array.Empty<string>());
        check(!new UninstallApplicationIdentityResolver(files,new[]{publisherApp}).Resolve(entry with{Publisher="Expected Publisher"}).IsResolved,"contradictory publisher metadata prevents catalog association");
        var nonPe=Path.Combine(folder,"Doubao.exe");files.Files[nonPe]=new("Doubao","Doubao","");files.Directories[folder]=new(new[]{nonPe});
        check(!new UninstallApplicationIdentityResolver(files).Resolve(entry with{Command=new(nonPe,"",false)}).IsResolved,"an exact registered uninstall route stays excluded even when it uses the application name");
        var before=files.FileReads;files.Safe.Remove(folder);
        check(!new UninstallApplicationIdentityResolver(files,new[]{app}).Resolve(entry).IsResolved&&files.FileReads==before,"unsafe or reparse installation roots are rejected before executable reads");
    }

    private sealed class FixtureFiles:IUninstallApplicationIdentityFiles
    {
        internal readonly HashSet<string> Safe=new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string,UninstallApplicationFile> Files=new(StringComparer.OrdinalIgnoreCase);
        internal readonly Dictionary<string,UninstallApplicationDirectory> Directories=new(StringComparer.OrdinalIgnoreCase);
        internal int FileReads,DirectoryReads;
        public bool IsSafeDirectory(string path)=>Safe.Contains(path);
        public UninstallApplicationDirectory ReadDirectory(string path,int maximumVisitedEntries,int maximumExecutables,CancellationToken token)
        {token.ThrowIfCancellationRequested();DirectoryReads++;return Directories.TryGetValue(path,out var result)?result:new(Array.Empty<string>());}
        public UninstallApplicationFile? ReadExecutable(string path,CancellationToken token)
        {token.ThrowIfCancellationRequested();FileReads++;return Files.TryGetValue(path,out var file)?file:null;}
    }

    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern byte CreateSymbolicLink(string path,string target,int flags);

    // Only copies in the owned E fixture receive this standard Win32 VERSIONINFO.
    // This preserves a real native PE while making its product identity independent
    // of the test runner's name, architecture and original helper-like metadata.
    private static void WriteFixtureVersion(string path)
    {
        byte[] Text(string value)=>Encoding.Unicode.GetBytes(value+'\0');
        byte[] Block(string key,byte[] value,ushort valueLength,ushort type,params byte[][] children)
        {
            using var stream=new MemoryStream();using var writer=new BinaryWriter(stream,Encoding.Unicode,true);
            writer.Write((ushort)0);writer.Write(valueLength);writer.Write(type);writer.Write(Text(key));
            while(stream.Position%4!=0)writer.Write((byte)0);writer.Write(value);
            foreach(var child in children){while(stream.Position%4!=0)writer.Write((byte)0);writer.Write(child);}
            var result=stream.ToArray();BitConverter.GetBytes(checked((ushort)result.Length)).CopyTo(result,0);return result;
        }
        byte[] Value(string name,string value)=>Block(name,Text(value),checked((ushort)(value.Length+1)),1);
        using var fixedStream=new MemoryStream();using(var fixedWriter=new BinaryWriter(fixedStream,Encoding.Unicode,true))
            foreach(var value in new uint[]{0xFEEF04BD,0x00010000,0x00010000,0,0x00010000,0,0x3F,0,0x00040004,1,0,0,0})fixedWriter.Write(value);
        var table=Block("040904B0",Array.Empty<byte>(),0,1,
            Value("ProductName","Identity Fixture"),Value("FileDescription","Identity Fixture"),
            Value("OriginalFilename","IdentityClient.exe"),Value("InternalName","IdentityClient"),
            Value("FileVersion","1.0.0.0"),Value("ProductVersion","1.0.0.0"));
        var strings=Block("StringFileInfo",Array.Empty<byte>(),0,1,table);
        var translation=Block("Translation",BitConverter.GetBytes(0x04b00409u),4,0);
        var variables=Block("VarFileInfo",Array.Empty<byte>(),0,1,translation);
        var bytes=Block("VS_VERSION_INFO",fixedStream.ToArray(),52,0,strings,variables);
        var update=BeginUpdateResource(path,true);if(update==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            if(!UpdateResource(update,new IntPtr(16),new IntPtr(1),0x0409,bytes,checked((uint)bytes.Length)))
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var completed=update;update=IntPtr.Zero;
            if(!EndUpdateResource(completed,false))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        finally{if(update!=IntPtr.Zero)EndUpdateResource(update,true);}
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr BeginUpdateResource(string path,[MarshalAs(UnmanagedType.Bool)]bool deleteExisting);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResource(IntPtr update,IntPtr type,IntPtr name,ushort language,byte[] data,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndUpdateResource(IntPtr update,[MarshalAs(UnmanagedType.Bool)]bool discard);

    // The baseline adapter observes the old display route until the new resolver
    // exists, so RED fails on the wrong executable rather than on a compiler error.
    private static (string Path,string Status,bool Product,bool FileName) Observe(UninstallEntry entry)
    {
        var type=AppDomain.CurrentDomain.GetAssemblies().Select(assembly=>assembly.GetType("ProcessKeeper.Core.UninstallApplicationIdentityResolver")).FirstOrDefault(value=>value is not null);
        if(type is null)return(entry.IconPath,"Unknown",false,false);
        var resolver=Activator.CreateInstance(type,new object?[]{null,null})!;
        var result=type.GetMethod("Resolve")!.Invoke(resolver,new object[]{entry,CancellationToken.None})!;
        return((string)result.GetType().GetProperty("ExecutablePath")!.GetValue(result)!,result.GetType().GetProperty("Status")!.GetValue(result)!.ToString()!,
            (bool)result.GetType().GetProperty("MatchedProductMetadata")!.GetValue(result)!,
            (bool)result.GetType().GetProperty("MatchedFileName")!.GetValue(result)!);
    }
}
