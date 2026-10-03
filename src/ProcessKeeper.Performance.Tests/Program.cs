using System.Text;
using System.Text.Json;
using ProcessKeeper.Core;
var checks=0;
void Check(bool value,string name){if(!value)throw new Exception("FAIL: "+name);checks++;Console.WriteLine("PASS: "+name);}
void Invalid(Action action,string name){try{action();}catch(InvalidDataException){Check(true,name);return;}throw new Exception("FAIL: "+name);}
SystemPerformanceVerification.Run(Check);
double seconds=10;var reads=0;
var defaults=new PerformancePreferences();Check(!defaults.Enabled&&defaults.Detailed&&defaults.UseAcrylic&&defaults.Horizontal&&defaults.Width==232&&defaults.Height==90,"fresh display defaults are compact and detailed but remain disabled");
var line=defaults with{CompactLine=true,Width=260,Height=36};using(var lineJson=JsonDocument.Parse(PerformancePreferencesStore.SerializeBytes(line)))Check(PerformancePreferencesStore.ReadJson(lineJson.RootElement)==line,"single-line mode and its short height roundtrip");
Invalid(()=>PerformancePreferencesStore.Validate(line with{CompactLine=false}),"card layouts reject an imported single-line height");
Invalid(()=>PerformancePreferencesStore.Validate(line with{Height=31}),"single-line height below its minimum is rejected");
var oldJson=Encoding.UTF8.GetString(PerformancePreferencesStore.SerializeBytes(defaults)).Replace(",\r\n  \"CompactLine\": false","").Replace(",\n  \"CompactLine\": false","");using(var oldDocument=JsonDocument.Parse(oldJson))Check(!PerformancePreferencesStore.ReadJson(oldDocument.RootElement).CompactLine,"old performance preferences retain card mode without the new field");
foreach(var language in new[]{"en","zh-Hans","zh-Hant"})
{
    L.Language=language;var sample=new PerformanceSample(60,128*1024*1024,96*1024*1024,1,new(42,32L*1024*1024*1024,20L*1024*1024*1024,DesktopFramesPerSecond:58));
    foreach(var detailed in new[]{false,true})
    {
        var compactText=CompactPerformanceText.Format(sample,detailed);
        Check(compactText.Contains("128 MiB")&&compactText.Contains("32 GiB")&&compactText.Contains("FPS"),"in-app line includes app working set and physical total "+language+" "+detailed);
        compactText=CompactPerformanceText.Format(sample,detailed,PerformanceDisplay.Overlay);
        Check(compactText.Contains("CPU 42%")&&compactText.Contains("12 / 32 GiB")&&compactText.Contains("58 FPS")&&!compactText.Contains("60 FPS")&&!compactText.Contains("128"),"desktop line includes independently measured desktop FPS CPU and physical used/total "+language+" "+detailed);
        Check(detailed==compactText.Contains("20 GiB"),"desktop available RAM follows detail selection "+language+" "+detailed);
    }
    Check(PerformanceMetricText.Explanation(sample with{System=sample.System! with{MultipleProcessorGroups=true}},PerformanceDisplay.Overlay).Contains(L.T("此电脑有多个处理器组，暂不显示不完整的 CPU 占用。")),"multiple CPU group scope explanation localized "+language);
}
L.Language="en";Check(CompactPerformanceText.Format(new PerformanceSample(60,null,null,1),true,PerformanceDisplay.Overlay).Contains("CPU —")&&CompactPerformanceText.Format(new PerformanceSample(60,null,null,1),true).Contains("Total RAM —"),"missing measurements remain honest placeholders");
using(var sampler=new PerformanceSampler(()=>seconds,()=>{reads++;return new PerformanceMemory(200,300);})){sampler.RecordFrame();Check(sampler.Sample() is null&&reads==0,"disabled sampler does not read memory or emit data");sampler.Start();for(var i=0;i<60;i++)sampler.RecordFrame();seconds++;var sample=sampler.Sample()!;Check(sample.FramesPerSecond==60&&sample.WorkingSetBytes==200&&sample.PrivateBytes==300&&reads==1,"real interval frame rate and independent working/private metrics");Check(sampler.Sample() is null&&reads==1,"repeat tick does not reread process");seconds+=2;sample=sampler.Sample()!;Check(sample.FramesPerSecond==0&&sample.IntervalSeconds==2&&reads==2,"idle rendering produces zero, not invented monitor FPS");sampler.RecordFrame();sampler.Stop();seconds++;Check(sampler.Sample() is null&&reads==2,"stop clears frame data and avoids all reads");sampler.Start();sampler.Start();seconds++;sample=sampler.Sample()!;Check(sample.FramesPerSecond==0&&reads==3,"restart does not carry old frames");sampler.Dispose();sampler.RecordFrame();Check(sampler.Sample() is null&&reads==3,"dispose removes data output");bool disposed=false;try{sampler.Start();}catch(ObjectDisposedException){disposed=true;}Check(disposed,"disposed sampler cannot restart");}
using(var sampler=new PerformanceSampler(()=>seconds,()=>throw new InvalidOperationException("fixture denied"))){sampler.Start();seconds++;Check(sampler.Sample()!.WorkingSetBytes is null,"unreadable memory is unknown rather than zero or fabricated");}
Check(PerformanceNativeWindow.Clamp(new PerformanceRectangle(9999,-999,300,100),new PerformanceRectangle(-1920,0,1920,1040))==new PerformanceRectangle(-300,0,300,100),"negative-coordinate monitor bounds clamp all edges");
Check(PerformanceNativeWindow.Clamp(new PerformanceRectangle(-200,-200,2000,1600),new PerformanceRectangle(0,0,800,560))==new PerformanceRectangle(0,0,800,560),"oversized display fits a small work area");
var value=new PerformancePreferences(true,PerformanceDisplay.Overlay,true,true,true,true,-900,200,420,150);
using(var doc=JsonDocument.Parse(PerformancePreferencesStore.SerializeBytes(value)))Check(PerformancePreferencesStore.ReadJson(doc.RootElement)==value,"all performance options roundtrip");
var text=Encoding.UTF8.GetString(PerformancePreferencesStore.SerializeBytes(value));
foreach(var invalid in new[]{text.Replace("\"Version\": 1","\"Version\": 2"),text.Replace("\"Overlay\"","\"9\""),text.Replace("\"Enabled\": true","\"Enabled\": \"true\""),text.Replace("\"Width\": 420","\"Width\": -1"),text.TrimEnd('}')+",\"Unknown\":true}",text.TrimEnd('}')+",\"Locked\":false}"})Invalid(()=>{using var doc=JsonDocument.Parse(invalid);PerformancePreferencesStore.ReadJson(doc.RootElement);},"malformed performance object rejected");
Invalid(()=>PerformancePreferencesStore.Validate(value with{X=double.NaN}),"NaN coordinate rejected");Invalid(()=>PerformancePreferencesStore.Validate(value with{Height=double.PositiveInfinity}),"infinite size rejected");Invalid(()=>PerformancePreferencesStore.Validate(value with{Width=179}),"too-small size rejected");
var directory=Path.Combine(Path.GetTempPath(),"ProcessKeeper-performance-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
try{var store=new PerformancePreferencesStore(directory);Check(!store.Load().Enabled&&!File.Exists(store.FilePath),"missing preferences remain off without writing defaults");store.Save(value);Check(store.Load()==value,"preferences atomic file roundtrip");store.Save(value with{Enabled=false});Check(!store.Load().Enabled&&File.Exists(store.FilePath+".bak"),"saving creates previous preference backup");File.WriteAllText(store.FilePath,"{}");var original=File.ReadAllBytes(store.FilePath);Invalid(()=>store.Load(),"corrupt preferences not silently accepted");Check(File.ReadAllBytes(store.FilePath).SequenceEqual(original),"corrupt preferences left untouched on read");}finally{Directory.Delete(directory,true);}
foreach(var language in new[]{"en","zh-Hans","zh-Hant"}){L.Language=language;Check(L.T("UI 回调计数，仅代表本应用；不是游戏帧率。").Length>0,"FPS scope explanation localized "+language);}
var saveThread=0;var saveCalls=0;PerformancePreferences? latest=null;var callerThread=Environment.CurrentManagedThreadId;
var writer=new PerformancePreferencesWriter(p=>{saveThread=Environment.CurrentManagedThreadId;Interlocked.Increment(ref saveCalls);latest=p;},TimeSpan.FromMilliseconds(100));
await writer.FlushAsync();
for(var i=0;i<1000;i++)writer.Schedule(defaults with{X=i});
Check(writer.HasPending,"queued persistence reports a pending write");
await writer.FlushAsync();
Check(saveCalls==1&&latest!.X==999,"a thousand pending edits retain only the final durable value");
Check(saveThread!=callerThread,"durable persistence runs outside the scheduling thread");
Check(!writer.HasPending,"flush drains the last value before reporting completion");
using(var started=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
{
    var ordered=new List<double>();var active=0;var maximum=0;
    var serial=new PerformancePreferencesWriter(p=>{maximum=Math.Max(maximum,Interlocked.Increment(ref active));lock(ordered)ordered.Add(p.X);if(p.X==1){started.Set();if(!release.Wait(5000))throw new TimeoutException();}Interlocked.Decrement(ref active);},TimeSpan.FromMilliseconds(100));
    serial.Schedule(defaults with{X=1});var draining=serial.FlushAsync();Check(started.Wait(5000),"first background write entered");
    serial.Schedule(defaults with{X=2});Check(!serial.TryDiscardFailedSave(),"discard cannot interrupt an active writer or erase newer pending edits");release.Set();await draining;
    Check(maximum==1&&ordered.SequenceEqual(new[]{1d,2d}),"new edits cannot overlap or overtake an in-flight write");
}
var deny=true;var failures=0;var failed=new PerformancePreferencesWriter(_=>{if(deny){failures++;throw new IOException("fixture denied");}},TimeSpan.Zero);failed.Schedule(defaults);
var rejected=false;try{await failed.FlushAsync();}catch(IOException){rejected=true;}Check(rejected,"flush reports a durable-write failure");
Check(failed.HasPending,"failed latest value remains pending for an explicit retry");await Task.Delay(100);Check(failures==1,"failed persistence does not retry forever in the background");
rejected=false;try{await failed.FlushAsync();}catch(IOException){rejected=true;}Check(rejected&&failures==2&&failed.HasPending,"another flush retries the unchanged failed value without discarding it");
deny=false;await failed.FlushAsync();Check(!failed.HasPending,"storage recovery saves the unchanged value on an explicit retry");
deny=true;failed.Schedule(defaults);try{await failed.FlushAsync();}catch(IOException){}Check(failed.TryDiscardFailedSave()&&!failed.HasPending,"explicit discard clears an idle failed save");var discardedFailures=failures;await failed.FlushAsync();Check(failures==discardedFailures,"flushing a discarded failure cannot write it again");
var sequenceDirectory=Path.Combine(Path.GetTempPath(),"ProcessKeeper-performance-writer-"+Guid.NewGuid().ToString("N"));
try{var store=new PerformancePreferencesStore(sequenceDirectory);var sequence=new PerformancePreferencesWriter(store.Save);sequence.Schedule(defaults with{X=100});await sequence.FlushAsync();var imported=defaults with{X=900,CompactLine=true,Height=36};store.Save(imported);await Task.Delay(450);Check(store.Load()==imported,"flushing before an imported commit prevents a late old write from overwriting it");}finally{if(Directory.Exists(sequenceDirectory))Directory.Delete(sequenceDirectory,true);}
Console.WriteLine($"PASS: {checks} performance assertions | CLR {IntPtr.Size*8}");
