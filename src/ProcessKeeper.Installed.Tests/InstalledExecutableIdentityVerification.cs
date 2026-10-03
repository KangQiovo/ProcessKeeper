using ProcessKeeper.Core;

internal static class InstalledExecutableIdentityVerification
{
    internal static void Run(Action<bool,string> check)
    {
        const string root=@"E:\IdentityFixture\CloudMusic";
        InstalledExecutable Exe(string file,string description="")=>new(){Path=root+"\\"+file,Name=file,Description=description};
        var main=Exe("cloudmusic.exe");var reporter=Exe("cloudmusic_reporter.exe","网易云音乐");var cssdk=Exe("cssdk.exe","网易云音乐");
        var utility=Exe("cloudmusic_util.exe");var dump=Exe("minidump.exe");var uninstall=Exe("UnInstall.exe");
        var app=new InstalledApplication{Name="网易云音乐",InstallLocation=root,Executables=new[]{reporter,cssdk,uninstall,main,utility,dump}};
        var identity=new InstalledExecutableIdentity();
        check(identity.Classify(app,main).Role==InstalledExecutableRole.PotentialMain,"a known CloudMusic executable is a possible main entry, never a guaranteed runnable executable");
        check(identity.ResolveMain(app).ExecutablePath==main.Path&&identity.ResolveMain(app).IsUncertain,"main navigation chooses CloudMusic rather than the first diagnostic executable");
        foreach(var component in new[]{reporter,dump})check(identity.Classify(app,component).Role==InstalledExecutableRole.Diagnostic,"CloudMusic diagnostic executable is distinguished from its main entry: "+component.Name);
        foreach(var component in new[]{cssdk,utility})check(identity.Classify(app,component).Role==InstalledExecutableRole.Helper,"CloudMusic supporting executable is distinguished from its main entry: "+component.Name);
        check(identity.Classify(app,uninstall).Role==InstalledExecutableRole.PotentialUninstaller&&identity.Classify(app,uninstall).IsUncertain,"an uninstall-like name alone is explicitly uncertain");
        var registered=new UninstallEntry{Name=app.Name,InstallLocation=root,Command=new(uninstall.Path.ToUpperInvariant(),"",false)};
        var withRegistration=new InstalledExecutableIdentity(new[]{registered});
        check(withRegistration.Classify(app,uninstall).Role==InstalledExecutableRole.Uninstaller&&!withRegistration.Classify(app,uninstall).IsUncertain,"an exact case-insensitive registered uninstall executable is identified by its route");
        var quiet=Exe("remove-product.exe");var quietIdentity=new InstalledExecutableIdentity(new[]{registered with{Command=null,QuietCommand=new(quiet.Path,"",false)}});
        check(quietIdentity.Classify(app with{Executables=new[]{quiet}},quiet).Role==InstalledExecutableRole.Uninstaller,"a quiet uninstall route is recognized even with a nonstandard executable name");
        var declared=app with{EntryPaths=new[]{main.Path}};
        check(identity.Classify(declared,main).Role==InstalledExecutableRole.Main&&!identity.Classify(declared,main).IsUncertain,"an explicit catalog entry is distinguished from a filename heuristic");
        check(identity.ResolveMain(declared).ExecutablePath==main.Path&&!identity.ResolveMain(declared).IsUncertain,"parent location uses its unique explicit application entry");
        check(identity.OrderExecutables(app).SequenceEqual(new[]{main,uninstall,reporter,cssdk,utility,dump}),"possible main first, uninstall second, other components retain inventory order");
        check(withRegistration.OrderExecutables(declared).SequenceEqual(new[]{main,uninstall,reporter,cssdk,utility,dump}),"explicit main and registered uninstaller take priority without changing the inventory");
        check(app.Executables.SequenceEqual(new[]{reporter,cssdk,uninstall,main,utility,dump}),"display ordering never mutates the original installed inventory");
        check(withRegistration.Classify(declared with{EntryPaths=new[]{uninstall.Path}},uninstall).Role==InstalledExecutableRole.Uninstaller,"a registered uninstaller cannot become main just because a shortcut points at it");
        check(identity.Classify(app with{EntryPaths=new[]{reporter.Path}},reporter).Role==InstalledExecutableRole.Diagnostic,"known diagnostic evidence prevents a misleading main label from a helper shortcut");
        var main2=Exe("cloudmusic64.exe");var twoEntries=app with{EntryPaths=new[]{main.Path,main2.Path},Executables=new[]{main,main2}};
        check(identity.ResolveMain(twoEntries).Status==InstalledMainIdentityStatus.Ambiguous&&identity.ResolveMain(twoEntries).ExecutablePath.Length==0,"multiple explicit entry paths require a choice rather than list-order guessing");
        var generic=Exe("client.exe","Unrelated Product");var unknown=app with{Name="Example App",Executables=new[]{generic,reporter}};
        check(identity.Classify(unknown,generic).Role==InstalledExecutableRole.Unknown&&identity.ResolveMain(unknown).ExecutablePath.Length==0,"unknown applications have no invented main executable");
        var matching=Exe("ExampleApp.exe");
        check(identity.Classify(unknown with{Executables=new[]{matching}},matching).Role==InstalledExecutableRole.PotentialMain,"a matching application filename remains a possible main entry");
        var english=Exe("ExampleApp.EXE");
        check(identity.Classify(unknown with{Name="示例工具 Example App (x64) 2.0",Executables=new[]{english}},english).Role==InstalledExecutableRole.PotentialMain,"English product name matches its executable even beside localized name, architecture and version suffix");
        var englishDiagnostic=Exe("ExampleApp_reporter.exe");
        check(identity.Classify(unknown with{Name="示例工具 Example App",Executables=new[]{englishDiagnostic}},englishDiagnostic).Role==InstalledExecutableRole.Diagnostic,"English-name inference never promotes a diagnostic suffix to main");
        var work=Exe("DoubaoWork.exe");
        check(identity.Classify(unknown with{Name="豆包工作",Executables=new[]{work}},work).Role==InstalledExecutableRole.PotentialMain,"the exact Chinese DoubaoWork product name maps to its possible launcher entry");
        var described=Exe("application.exe","Example App");
        check(identity.Classify(unknown with{Executables=new[]{described}},described).Evidence==InstalledExecutableEvidence.ProductDescription,"matching product description is recorded as inference evidence");
        var duplicates=unknown with{EntryPaths=new[]{matching.Path,matching.Path.ToUpperInvariant()},Executables=new[]{matching,matching with{Path=matching.Path.ToUpperInvariant()}}};
        check(identity.ResolveMain(duplicates).ExecutablePath.Equals(matching.Path,StringComparison.OrdinalIgnoreCase),"case-only duplicate inventory paths do not create a false ambiguous main entry");
        var host=Exe("rundll32.exe","Example App");
        check(identity.Classify(unknown with{EntryPaths=new[]{host.Path},Executables=new[]{host}},host).Role==InstalledExecutableRole.Helper,"shared executable hosts are never identified as an application's main executable");
        var associated=registered with{Command=null,ApplicationPaths=new[]{generic.Path},ApplicationIdentity=new(){ExecutablePath=generic.Path,Status=UninstallApplicationIdentityStatus.Resolved}};
        check(new InstalledExecutableIdentity(new[]{associated}).Classify(unknown,generic).Role==InstalledExecutableRole.PotentialMain,"a bounded associated application path remains inferred for installed inventory display");
        check(new InstalledExecutableIdentity(new[]{associated with{ApplicationPaths=Enumerable.Repeat(generic.Path,49).ToArray()}}).Classify(unknown,generic).Role==InstalledExecutableRole.Unknown,"oversized associated-path evidence cannot bypass the bounded role index");
        var noEntry=unknown with{EntryPaths=new[]{main.Path},Executables=new[]{generic}};
        check(identity.ResolveMain(noEntry).ExecutablePath.Length==0,"an entry path absent from the executable inventory does not hijack parent navigation");
        var remote=generic with{Path=@"\\server\share\client.exe"};
        check(identity.Classify(unknown with{EntryPaths=new[]{remote.Path},Executables=new[]{remote}},remote).Role==InstalledExecutableRole.Unknown,"remote executable paths are not presented as verified local entry points");
        var traversal=generic with{Path=root+@"\..\client.exe"};
        check(identity.Classify(unknown with{EntryPaths=new[]{traversal.Path},Executables=new[]{traversal}},traversal).Role==InstalledExecutableRole.Unknown,"traversal paths cannot establish an application entry");
        var revo=Exe("RevoUninstaller.exe");var revoApp=unknown with{Name="Revo Uninstaller",EntryPaths=new[]{revo.Path},Executables=new[]{revo}};
        check(identity.Classify(revoApp,revo).Role==InstalledExecutableRole.Main,"an uninstall-management application is not confused with an arbitrary vendor removal component");
        var large=Enumerable.Range(0,301).Select(i=>Exe("component"+i+".exe")).ToArray();
        check(identity.ResolveMain(unknown with{Executables=large}).Status==InstalledMainIdentityStatus.Truncated,"oversized executable inventories are bounded instead of silently claiming a unique main entry");
        VerifyMergedEntries(check);
    }

    private static void VerifyMergedEntries(Action<bool,string> check)
    {
        const string root=@"E:\NotCreatedIdentityFixture\DoubaoWork";var main=root+@"\DoubaoWork.exe";var uninstaller=root+@"\uninstall.exe";
        var registration=new UninstallEntry{Name="豆包工作",Publisher="Fixture Publisher",IconPath=root+@"\icon.ico",Command=new(uninstaller,"",false),
            Locator=new("HKCU",64,"DoubaoWork"),ApplicationPaths=new[]{main},ApplicationIdentity=new(){ExecutablePath=main,Status=UninstallApplicationIdentityStatus.Resolved,Source=UninstallApplicationIdentitySource.InstallationDirectory}};
        var merged=InstalledApplicationCatalog.IncludeVerifiedApplications(Array.Empty<InstalledApplication>(),new[]{registration});
        check(merged.Count==1&&merged[0].EntryPaths.Count==0&&merged[0].Executables.Single().Path==main,"an inferred DoubaoWork main path enters executable inventory without becoming an explicit launch entry");
        var mergedRole=new InstalledExecutableIdentity(new[]{registration}).Classify(merged[0],merged[0].Executables[0]);
        check(mergedRole.Role==InstalledExecutableRole.PotentialMain&&mergedRole.IsUncertain,"a merged directory-inferred DoubaoWork main executable keeps a yellow possible-main role");
        check(merged[0].InstallLocation==root,"verified application inventory derives a missing installation folder from the main executable parent");
        check(InstalledApplicationCatalog.IncludeVerifiedApplications(Array.Empty<InstalledApplication>(),new[]{registration with{InstallLocation=@"E:\"}})[0].InstallLocation==root,"a declared drive root cannot broaden a verified installed application's location");
        check(new InstalledExecutableIdentity(new[]{registration}).ResolveMain(merged[0]).ExecutablePath==main,"a merged verified entry supports actual parent file navigation");
        var duplicate=registration with{Locator=new("HKCU",32,"DoubaoWork")};
        var repeated=InstalledApplicationCatalog.IncludeVerifiedApplications(merged,new[]{registration,duplicate});
        check(repeated.Count==1&&repeated[0].Executables.Count==1&&repeated[0].EntryPaths.Count==0,"repeated 32-bit and 64-bit registrations never duplicate or upgrade an inferred main application");
        var original=merged[0] with{EntryPaths=Array.Empty<string>(),Executables=new[]{new InstalledExecutable{Path=uninstaller,Name="uninstall.exe"},new InstalledExecutable{Path=root+@"\crashpad_handler.exe",Name="crashpad_handler.exe"}}};
        var existing=InstalledApplicationCatalog.IncludeVerifiedApplications(new[]{original},new[]{registration});
        check(existing.Count==1&&existing[0].Executables.Count==3&&existing[0].EntryPaths.Count==0,"same-name same-directory inventory adds an inferred main without duplicating or upgrading its existing application");
        var explicitEntry=merged[0] with{EntryPaths=new[]{main}};
        check(InstalledApplicationCatalog.IncludeVerifiedApplications(new[]{explicitEntry},new[]{registration})[0].EntryPaths.SequenceEqual(new[]{main}),"merging uninstall candidates preserves an application's existing explicit entry evidence");
        var roles=new InstalledExecutableIdentity(new[]{registration});
        check(roles.Classify(existing[0],existing[0].Executables.First()).Role==InstalledExecutableRole.Uninstaller&&roles.Classify(existing[0],existing[0].Executables[1]).Role==InstalledExecutableRole.Diagnostic,"merging a main entry never promotes its neighboring uninstall or diagnostic components to main");
        var unverified=registration with{ApplicationIdentity=null};
        check(InstalledApplicationCatalog.IncludeVerifiedApplications(Array.Empty<InstalledApplication>(),new[]{unverified}).Count==0,"a bare application path without resolved identity evidence is not merged");
        var ambiguous=registration with{ApplicationIdentity=registration.ApplicationIdentity! with{Status=UninstallApplicationIdentityStatus.Ambiguous}};
        check(InstalledApplicationCatalog.IncludeVerifiedApplications(Array.Empty<InstalledApplication>(),new[]{ambiguous}).Count==0,"ambiguous main identity evidence is not upgraded to an installed entry");
        var mismatch=registration with{ApplicationPaths=new[]{uninstaller}};
        check(InstalledApplicationCatalog.IncludeVerifiedApplications(Array.Empty<InstalledApplication>(),new[]{mismatch}).Count==0,"the resolved identity path must occur in the validated application paths before merging");
        var removal=registration with{ApplicationPaths=new[]{uninstaller},ApplicationIdentity=new(){ExecutablePath=uninstaller,Status=UninstallApplicationIdentityStatus.Resolved}};
        check(InstalledApplicationCatalog.IncludeVerifiedApplications(Array.Empty<InstalledApplication>(),new[]{removal}).Count==0,"registered removal executables can never be merged as application entries");
        var remote=registration with{ApplicationPaths=new[]{@"\\server\share\DoubaoWork.exe"},ApplicationIdentity=new(){ExecutablePath=@"\\server\share\DoubaoWork.exe",Status=UninstallApplicationIdentityStatus.Resolved}};
        check(InstalledApplicationCatalog.IncludeVerifiedApplications(Array.Empty<InstalledApplication>(),new[]{remote}).Count==0,"remote main paths are rejected even when a supplied identity claims resolution");
        var sibling=registration with{ApplicationPaths=new[]{root+@"\OtherMain.exe"},ApplicationIdentity=new(){ExecutablePath=root+@"\OtherMain.exe",Status=UninstallApplicationIdentityStatus.Resolved}};
        var two=InstalledApplicationCatalog.IncludeVerifiedApplications(merged,new[]{sibling});
        check(two.Count==1&&two[0].EntryPaths.Count==0&&two[0].Executables.Count==2&&new InstalledExecutableIdentity(new[]{registration,sibling}).ResolveMain(two[0]).Status==InstalledMainIdentityStatus.Ambiguous,"multiple inferred candidates in one installation stay explicitly ambiguous without gaining explicit entry evidence");
        var sameMainElsewhere=merged[0] with{Name="Menu Alias",Id="menu-alias"};
        check(InstalledApplicationCatalog.IncludeVerifiedApplications(new[]{sameMainElsewhere},new[]{registration}).Count==1,"a shared actual path preserves an existing shortcut application instead of creating a name alias duplicate");
    }
}
