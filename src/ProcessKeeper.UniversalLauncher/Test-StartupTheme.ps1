#requires -Version 7.0
<#
Build the native launcher with build-native.ps1 -UiFixture first. This test uses only
fixture theme injection and reads the host preference; it never changes Windows themes
or launches the product. Supply an explicit output directory and redirect TEMP/TMP there
when running under a restricted storage policy. Windows 11 is used for the UI assertions;
older Windows routing/import checks remain in the native build tests.
#>
param(
 [Parameter(Mandatory=$true)][string]$FixturePath,
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [switch]$KeepBehindOtherWindows
)
$ErrorActionPreference='Stop'
$FixturePath=[IO.Path]::GetFullPath($FixturePath)
$root=[IO.Path]::GetFullPath($OutputDirectory)
if(!(Test-Path -LiteralPath $FixturePath -PathType Leaf)){throw 'Build the isolated native UI fixture first.'}
# Inspect the PE export directory without loading/executing the candidate. This marker is
# compiled only under PK_UI_FIXTURE. It prevents accidentally launching the product here;
# it is not authentication for an executable from an untrusted source.
function Assert-UiFixture([string]$Path) {
 if((Get-Item -LiteralPath $Path).Length -gt 8MB){throw 'Refusing to execute: expected a small isolated UI fixture, not a packaged product.'}
 $bytes=[IO.File]::ReadAllBytes($Path)
 function U16([int]$offset) {if($offset-lt 0 -or $offset-gt $bytes.Length-2){throw 'Invalid PE offset.'};[BitConverter]::ToUInt16($bytes,$offset)}
 function U32([int]$offset) {if($offset-lt 0 -or $offset-gt $bytes.Length-4){throw 'Invalid PE offset.'};[BitConverter]::ToUInt32($bytes,$offset)}
 if((U16 0)-ne 0x5A4D){throw 'Not an isolated native UI fixture (missing PE header).'}
 $pe=[int](U32 0x3C)
 if((U32 $pe)-ne 0x4550 -or (U16 ($pe+4))-ne 0x14C){throw 'Expected the isolated x86 native UI fixture.'}
 $count=U16 ($pe+6);$optional=$pe+24;$optionalSize=U16 ($pe+20)
 if($count -lt 1 -or $count -gt 96 -or $optionalSize -lt 104 -or (U16 $optional)-ne 0x10B){throw 'Unsupported fixture PE layout.'}
 $sections=$optional+$optionalSize
 function Offset([uint32]$rva) {
  for($i=0;$i-lt $count;$i++) {
   $section=$sections+$i*40;$size=U32 ($section+16);$start=U32 ($section+12);$raw=U32 ($section+20)
   if([uint64]$rva-ge $start -and [uint64]$rva-lt ([uint64]$start+$size)) {
    $offset=[uint64]$raw+$rva-$start
    if($offset-ge $bytes.Length){throw 'PE export extends beyond the file.'}
    return [int]$offset
   }
  }
  throw 'Fixture marker RVA is missing.'
 }
 $exportRva=U32 ($optional+96)
 if(!$exportRva){throw 'Refusing to execute: this binary has no UI-fixture export marker.'}
 $exports=Offset $exportRva;$names=U32 ($exports+24)
 if(!$names -or $names-gt 4096){throw 'Invalid fixture export directory.'}
 $table=Offset (U32 ($exports+32));$found=$false
 for($i=0;$i-lt $names;$i++) {
  $at=Offset (U32 ($table+$i*4));$end=$at
  while($end-lt $bytes.Length -and $bytes[$end] -and $end-$at-lt 128){$end++}
  if($end-ge $bytes.Length -or $end-$at-ge 128){throw 'Invalid fixture export name.'}
  $name=[Text.Encoding]::ASCII.GetString($bytes,$at,$end-$at)
  if($name -in @('ProcessKeeperStartupThemeFixtureV1','_ProcessKeeperStartupThemeFixtureV1')){$found=$true;break}
 }
 if(!$found){throw 'Refusing to execute: production or unrelated executable; PK_UI_FIXTURE marker is required.'}
}
Assert-UiFixture $FixturePath
[void][IO.Directory]::CreateDirectory($root)
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class StartupWindowTest {
 [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
 public delegate bool EnumProc(IntPtr h,IntPtr p);
 [StructLayout(LayoutKind.Sequential)] public struct Rect { public int L,T,R,B; }
 [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr w,EnumProc p,IntPtr data);
 [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p,IntPtr data);
 [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr w,StringBuilder s,int n);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr w,StringBuilder s,int n);
 [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr w);
 [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr w);
 [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr w);
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr w,out Rect r);
 [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr w,out Rect r);
 [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr w,int x,int y,int width,int height,bool redraw);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr w,int cmd);
 [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr w,IntPtr after,int x,int y,int width,int height,uint flags);
 [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr w,uint m,IntPtr wp,IntPtr lp);
 [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr w,uint m,IntPtr wp,IntPtr lp);
 [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr w,uint m,IntPtr wp,IntPtr lp,uint flags,uint timeout,out IntPtr result);
 [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr w,IntPtr dc,uint flags);
 [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr p,uint flags);
 public static string Text(IntPtr h) {var b=new StringBuilder(512);GetWindowText(h,b,512);return b.ToString();}
 public static string Class(IntPtr h) {var b=new StringBuilder(128);GetClassName(h,b,128);return b.ToString();}
 public static IntPtr Find(uint pid) { IntPtr result=IntPtr.Zero;EnumWindows((h,p)=>{uint owner;GetWindowThreadProcessId(h,out owner);if(owner==pid&&Class(h)=="ProcessKeeper.Universal.Startup"){result=h;return false;}return true;},IntPtr.Zero);return result; }
}
'@
[void][StartupWindowTest]::SetThreadDpiAwarenessContext([IntPtr](-4))
$checks=[Collections.Generic.List[string]]::new()
$metrics=[Collections.Generic.List[object]]::new()
function Check([bool]$pass,[string]$name) { if(!$pass){throw $name};$checks.Add($name) }
function ShowFixture([IntPtr]$window) {
 [void][StartupWindowTest]::ShowWindow($window,4)
 if($KeepBehindOtherWindows){Check ([StartupWindowTest]::SetWindowPos($window,[IntPtr]1,0,0,0,0,0x13)) 'Owned fixture remains behind other windows without activation'}
}
function Snapshot([IntPtr]$window,[string]$name) {
 $rect=[StartupWindowTest+Rect]::new();[void][StartupWindowTest]::GetClientRect($window,[ref]$rect)
 $bitmap=[Drawing.Bitmap]::new($rect.R-$rect.L,$rect.B-$rect.T)
 $graphics=[Drawing.Graphics]::FromImage($bitmap);$dc=$graphics.GetHdc()
 try {Check ([StartupWindowTest]::PrintWindow($window,$dc,3)) "Screenshot $name"}finally{$graphics.ReleaseHdc($dc);$graphics.Dispose()}
 try {$bitmap.Save((Join-Path $root ($name+'.png')),[Drawing.Imaging.ImageFormat]::Png)}finally{$bitmap.Dispose()}
}
function Children([IntPtr]$window) {
 $rows=[Collections.Generic.List[object]]::new()
 $callback=[StartupWindowTest+EnumProc]{param($child,$ignored)
  $rect=[StartupWindowTest+Rect]::new();[void][StartupWindowTest]::GetWindowRect($child,[ref]$rect)
  $rows.Add([pscustomobject]@{Hwnd=$child;Text=[StartupWindowTest]::Text($child);Class=[StartupWindowTest]::Class($child);Visible=[StartupWindowTest]::IsWindowVisible($child);Id=[StartupWindowTest]::GetDlgCtrlID($child);Rect=$rect});return $true
 }
 [void][StartupWindowTest]::EnumChildWindows($window,$callback,[IntPtr]::Zero)
 return $rows
}


Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class ThemeProbe {
 [StructLayout(LayoutKind.Sequential)] public struct Point {public int X,Y;}
 [StructLayout(LayoutKind.Sequential)] public struct Contrast {public uint Size, Flags;public IntPtr Name;}
 [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action,uint size,ref Contrast contrast,uint flags);
 [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr w,ref Point p);
 [DllImport("user32.dll")] public static extern uint GetSysColor(int i);
 [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr w);
 [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr w,IntPtr dc);
 [DllImport("gdi32.dll")] public static extern uint GetTextColor(IntPtr dc);
 [DllImport("gdi32.dll")] public static extern uint GetBkColor(IntPtr dc);
}
"@
function Luminance($c) {
 $v=@($c.R,$c.G,$c.B)|ForEach-Object {$n=$_/255.0;if($n -le 0.04045){$n/12.92}else{[Math]::Pow(($n+0.055)/1.055,2.4)}}
 return 0.2126*$v[0]+0.7152*$v[1]+0.0722*$v[2]
}
function Ratio($a,$b) { $la=Luminance $a;$lb=Luminance $b;return ([Math]::Max($la,$lb)+0.05)/([Math]::Min($la,$lb)+0.05) }
function RefColor([uint32]$v) { return [Drawing.Color]::FromArgb([int]($v-band 255),[int](($v-shr 8)-band 255),[int](($v-shr 16)-band 255)) }
$results=[Collections.Generic.List[object]]::new()
foreach($language in @('en','zh-CN','zh-TW')) {
 foreach($mode in @('theme-light','theme-dark','contrast-dark','contrast-light')) {
  foreach($page in @('preparing','permission','failed','framework','unsupported')) {
   $p=Start-Process $FixturePath -ArgumentList @($page,$language,$mode) -WorkingDirectory $root -WindowStyle Hidden -PassThru
   try {
    $deadline=[DateTime]::UtcNow.AddSeconds(12)
    do {Start-Sleep -Milliseconds 20;$w=[StartupWindowTest]::Find($p.Id)}while($w -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
    if ($w -eq [IntPtr]::Zero) {
     $owned=[Collections.Generic.List[object]]::new()
     $collect=[StartupWindowTest+EnumProc]{param($handle,$ignored)
      $owner=0u;[void][StartupWindowTest]::GetWindowThreadProcessId($handle,[ref]$owner)
      if($owner -eq $p.Id){$owned.Add([pscustomobject]@{Handle=$handle.ToInt64();Class=[StartupWindowTest]::Class($handle);Text=[StartupWindowTest]::Text($handle);Visible=[StartupWindowTest]::IsWindowVisible($handle)})}
      return $true
     }
     [void][StartupWindowTest]::EnumWindows($collect,[IntPtr]::Zero)
     [ordered]@{Case="$language $mode $page";ProcessId=$p.Id;HasExited=$p.HasExited;OwnedWindows=$owned;RecordedAtUtc=[DateTime]::UtcNow.ToString('o')}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $root 'creation-failure.json')
    }
    Check ($w -ne [IntPtr]::Zero) "$language $mode $page created | pid $($p.Id) | exit $($p.HasExited) code $($p.ExitCode)"
    ShowFixture $w;Start-Sleep -Milliseconds 150
    $name="$language-$mode-$page"
    Snapshot $w $name
    $b=[Drawing.Bitmap]::new((Join-Path $root ($name+'.png')))
    try {
     $back=$b.GetPixel(10,10);$isDark=$mode -in @('theme-dark','contrast-dark')
     Check (($back.R -lt 64) -eq $isDark) "$name actual background follows selected palette"
     $rows=@(Children $w)
     $textRows=@($rows|Where-Object {$_.Visible -and $_.Text -and $_.Class -in @('Static','Edit') -and $_.Id -ne 200})
     Check ($textRows.Count -ge 1) "$name has visible text"
     $origin=[ThemeProbe+Point]::new();[void][ThemeProbe]::ClientToScreen($w,[ref]$origin)
     foreach($row in $textRows) {
      $x0=[Math]::Max(0,$row.Rect.L-$origin.X);$y0=[Math]::Max(0,$row.Rect.T-$origin.Y)
      $x1=[Math]::Min($b.Width,$row.Rect.R-$origin.X);$y1=[Math]::Min($b.Height,$row.Rect.B-$origin.Y)
      $fg=if($mode -eq 'theme-dark'){[Drawing.Color]::FromArgb(245,245,245)}elseif($mode -eq 'contrast-dark'){[Drawing.Color]::White}else{RefColor ([ThemeProbe]::GetSysColor(8))}
      $ratio=Ratio $fg $back;Check ($ratio -ge 4.5) "$name native $($row.Class) text contrast >= 4.5"
      $matching=0
      for($y=$y0;$y -lt $y1;$y+=2) {for($x=$x0;$x -lt $x1;$x+=2){$pixel=$b.GetPixel($x,$y);if([Math]::Abs([int]$pixel.R-$fg.R)-le 4 -and [Math]::Abs([int]$pixel.G-$fg.G)-le 4 -and [Math]::Abs([int]$pixel.B-$fg.B)-le 4){$matching++}}}
      Check ($matching -ge 15) "$name actual text pixels match foreground ($matching samples)"
      $results.Add([pscustomobject]@{Case=$name;Control=$row.Class;Foreground=$fg.Name;Background=$back.Name;Contrast=[Math]::Round($ratio,2);TextPixels=$matching})
     }
     if($page -eq 'preparing') {
      Check (@($rows|Where-Object {$_.Text -match 'Checking and preparing|Starting Process Keeper|正在启动|正在校验|正在啟動|正在校驗'}).Count -eq 0) "$name old prompt absent"
      $f1=[StartupWindowTest]::SendMessage($w,0x8014,0,0).ToInt64();Start-Sleep -Milliseconds 120;$f2=[StartupWindowTest]::SendMessage($w,0x8014,0,0).ToInt64()
      Check (($f2 -gt $f1) -eq (!$mode.StartsWith('contrast-'))) "$name motion respects contrast mode"
     } else {
      Check (@($rows|Where-Object {$_.Visible -and $_.Class -eq 'Button'}).Count -ge 1) "$name native recovery buttons visible"
      Check (@($rows|Where-Object {$_.Id -eq 200 -and $_.Visible}).Count -eq 0) "$name recovery hides spinner"
     }
    } finally {$b.Dispose()}
   } finally {if(!$p.HasExited){[void][StartupWindowTest]::PostMessage([StartupWindowTest]::Find($p.Id),0x10,0,0);[void]$p.WaitForExit(2500)};$p.Dispose()}
  }
 }
}
$p=Start-Process $FixturePath -ArgumentList @('preparing','en') -WorkingDirectory $root -WindowStyle Hidden -PassThru
try {
 $deadline=[DateTime]::UtcNow.AddSeconds(12)
 do {Start-Sleep -Milliseconds 25;$w=[StartupWindowTest]::Find($p.Id)}while($w -eq [IntPtr]::Zero -and [DateTime]::UtcNow -lt $deadline)
 Check ($w -ne [IntPtr]::Zero) 'Automatic theme fixture created'
 $hc=[ThemeProbe+Contrast]::new();$hc.Size=[Runtime.InteropServices.Marshal]::SizeOf($hc)
 [void][ThemeProbe]::SystemParametersInfo(0x42,$hc.Size,[ref]$hc,0)
 $preference=Get-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name AppsUseLightTheme -ErrorAction SilentlyContinue
 $expected=if($hc.Flags-band 1){2}elseif($null -ne $preference -and $preference.AppsUseLightTheme -eq 0){1}else{0}
 Check ([StartupWindowTest]::SendMessage($w,0x8018,0,0).ToInt32() -eq $expected) 'Uninjected startup follows actual system preference and contrast setting'
 ShowFixture $w;Start-Sleep -Milliseconds 150
 Snapshot $w 'actual-system-preference'
 $b=[Drawing.Bitmap]::new((Join-Path $root 'actual-system-preference.png'))
 try {
  $expectedBack=if($expected -eq 1){[Drawing.Color]::FromArgb(32,32,32)}else{RefColor ([ThemeProbe]::GetSysColor(5))}
  $expectedText=if($expected -eq 1){[Drawing.Color]::FromArgb(245,245,245)}else{RefColor ([ThemeProbe]::GetSysColor(8))}
  $actualBack=$b.GetPixel(10,10)
  Check ($actualBack.ToArgb() -eq $expectedBack.ToArgb()) 'Uninjected startup actually paints the system-selected background'
  Check ((Ratio $expectedText $actualBack) -ge 4.5) 'Uninjected startup text palette remains readable'
  $brand=@(Children $w|Where-Object {$_.Visible -and $_.Text -eq 'Process Keeper'})
  Check ($brand.Count -eq 1) 'Uninjected startup displays the English product name'
  $origin=[ThemeProbe+Point]::new();[void][ThemeProbe]::ClientToScreen($w,[ref]$origin)
  $matching=0
  for($y=[Math]::Max(0,$brand[0].Rect.T-$origin.Y);$y -lt [Math]::Min($b.Height,$brand[0].Rect.B-$origin.Y);$y+=2) {
   for($x=[Math]::Max(0,$brand[0].Rect.L-$origin.X);$x -lt [Math]::Min($b.Width,$brand[0].Rect.R-$origin.X);$x+=2) {
    $pixel=$b.GetPixel($x,$y)
    if([Math]::Abs([int]$pixel.R-$expectedText.R)-le 4 -and [Math]::Abs([int]$pixel.G-$expectedText.G)-le 4 -and [Math]::Abs([int]$pixel.B-$expectedText.B)-le 4){$matching++}
   }
  }
  Check ($matching -ge 15) 'Uninjected startup actual text pixels match the system-selected foreground'
  $actualPreference=[ordered]@{Palette=$expected;HighContrast=[bool]($hc.Flags-band 1);Background=$actualBack.Name;Foreground=$expectedText.Name;TextPixels=$matching;Contrast=[Math]::Round((Ratio $expectedText $actualBack),2)}
 } finally {$b.Dispose()}
 foreach($message in @(0x31A,0x15)) {[void][StartupWindowTest]::SendMessage($w,$message,0,0);Check ([StartupWindowTest]::SendMessage($w,0x8018,0,0).ToInt32() -eq $expected) "System theme/color notification $message preserves correct auto palette"}
} finally {if(!$p.HasExited){[void][StartupWindowTest]::PostMessage([StartupWindowTest]::Find($p.Id),0x10,0,0);[void]$p.WaitForExit(2500)};$p.Dispose()}
$p=Start-Process $FixturePath -ArgumentList @('preparing','en','theme-light') -WorkingDirectory $root -WindowStyle Hidden -PassThru
try {
 Start-Sleep -Milliseconds 250;$w=[StartupWindowTest]::Find($p.Id);ShowFixture $w;Start-Sleep -Milliseconds 120
 $gdiBefore=[StartupWindowTest]::GetGuiResources($p.Handle,0)
 for($i=0;$i -lt 100;$i++) {
  $mode=$i%4;[void][StartupWindowTest]::SendMessage($w,0x8017,$mode,0)
  $actual=[StartupWindowTest]::SendMessage($w,0x8018,0,0).ToInt32();$expected=if($mode -ge 2){2}else{$mode};Check ($actual -eq $expected) "Runtime setting message updates palette $i"
 }
 [void][StartupWindowTest]::SendMessage($w,0x8017,1,0);Start-Sleep -Milliseconds 200;Snapshot $w 'runtime-light-to-dark'
 $b=[Drawing.Bitmap]::new((Join-Path $root 'runtime-light-to-dark.png'));try{Check ($b.GetPixel(10,10).R -lt 64) 'Runtime switch actually repaints client dark'}finally{$b.Dispose()}
 $gdiAfter=[StartupWindowTest]::GetGuiResources($p.Handle,0);Check ($gdiAfter -le $gdiBefore+1) '100 theme changes do not leak GDI brushes'
 [void][StartupWindowTest]::SendMessage($w,0x8015,0,0);Check ($p.WaitForExit(2000)) 'Themed handoff closes';Check ($p.ExitCode -eq 0) 'Themed handoff exit succeeds'
} finally {if(!$p.HasExited){[void][StartupWindowTest]::PostMessage([StartupWindowTest]::Find($p.Id),0x10,0,0);[void]$p.WaitForExit(2500)};$p.Dispose()}
[ordered]@{Passed=$true;Checks=$checks.Count;Details=$checks;Colors=$results;GdiDelta=[int]$gdiAfter-[int]$gdiBefore;ActualSystemPreference=$actualPreference;KeptBehindOtherWindows=[bool]$KeepBehindOtherWindows;VerifiedAtUtc=[DateTime]::UtcNow.ToString('o');FixtureSha256=(Get-FileHash -LiteralPath $FixturePath -Algorithm SHA256).Hash;ScriptSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash;MainSourceSha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Main.cpp') -Algorithm SHA256).Hash;ActualProductLaunch=$false;SystemThemeModified=$false;Caveat='Native windows on current Windows 11; preference/high-contrast inputs injected only in fixture, with a separate uninjected system-preference pixel check; no production launch or Win7 hardware execution'}|ConvertTo-Json -Depth 6|Set-Content (Join-Path $root 'theme-verification.json')
"PASS $($checks.Count) native theme checks"
