using ProcessKeeper.Core;

internal static class SystemPerformanceVerification
{
    internal static void Run(Action<bool, string> check)
    {
        var assembly = typeof(PerformanceSampler).Assembly;
        check(assembly.GetType("ProcessKeeper.Core.SystemPerformanceSampler") is not null,
            "computer sampling has a separate source from application UI FPS and process memory");
        check(typeof(SystemPerformanceSample).GetProperty("DesktopFramesPerSecond") is not null,
            "desktop overlay carries an independently measured nullable desktop composition FPS");
        var desktop = new SystemDesktopComposition(100, 10);
        check(SystemPerformanceSampler.CompositionFramesPerSecond(null, desktop) is null,
            "desktop FPS needs two composition counters instead of substituting monitor refresh rate");
        check(SystemPerformanceSampler.CompositionFramesPerSecond(desktop, new(130, 10.5)) == 60,
            "desktop FPS measures actual composed frames over elapsed sample time");
        check(SystemPerformanceSampler.CompositionFramesPerSecond(desktop, new(100, 11)) == 0,
            "an actual interval without desktop compositions measures zero");
        foreach(var invalid in new[]{new SystemDesktopComposition(99,11),new(130,10),new(130,9),new(130,10.1),new(130,16),new(130,double.NaN),new(130,double.PositiveInfinity)})
            check(SystemPerformanceSampler.CompositionFramesPerSecond(desktop,invalid) is null,
                "reset stale or invalid composition interval remains unknown "+invalid);
        var desktopReads=0;
        var desktopSampler=new SystemPerformanceSampler(()=>new(100,200,300),()=>new(32,20),()=>1,()=>{desktopReads++;return desktop;});
        check(desktopSampler.Sample().DesktopFramesPerSecond is null,"first composition read remains unknown");
        desktop=new(160,11);check(desktopSampler.Sample().DesktopFramesPerSecond==60&&desktopReads==2,
            "one native composition read per tick produces an independent desktop FPS");
        desktopSampler.Reset();check(desktopSampler.Sample().DesktopFramesPerSecond is null,"disable and display changes discard the composition baseline");
        var unavailable=true;
        desktopSampler=new SystemPerformanceSampler(()=>new(100,200,300),()=>new(32,20),()=>1,()=>unavailable?throw new NotSupportedException():desktop);
        check(desktopSampler.Sample().DesktopFramesPerSecond is null&&desktopSampler.Sample().TotalMemoryBytes==32,
            "unavailable DWM leaves FPS unknown and preserves CPU RAM sampling");
        unavailable=false;check(desktopSampler.Sample().DesktopFramesPerSecond is null,"DWM recovery requires a new composition baseline");
        desktop=new(190,12);check(desktopSampler.Sample().DesktopFramesPerSecond==30,"DWM recovery measures only the new interval");
        using(var enteredDesktop=new ManualResetEventSlim())using(var releaseDesktop=new ManualResetEventSlim())
        {
            var block=true;
            desktopSampler=new SystemPerformanceSampler(()=>new(100,200,300),()=>new(32,20),()=>1,()=>{if(block){enteredDesktop.Set();releaseDesktop.Wait(5000);}return desktop;});
            var pendingDesktop=Task.Run(desktopSampler.Sample);check(enteredDesktop.Wait(5000),"background composition read entered");
            var resetWatch=System.Diagnostics.Stopwatch.StartNew();desktopSampler.Reset();
            check(resetWatch.ElapsedMilliseconds<100,"DWM reset never blocks UI behind a native read");
            releaseDesktop.Set();check(pendingDesktop.GetAwaiter().GetResult().DesktopFramesPerSecond is null,"reset during composition read rejects stale FPS");
            block=false;check(desktopSampler.Sample().DesktopFramesPerSecond is null,"stale composition read cannot preserve its baseline");
        }
        var prior = new SystemCpuTimes(100, 200, 300);
        check(SystemPerformanceSampler.CpuPercent(null, prior) is null, "CPU needs two actual samples instead of a fabricated first percentage");
        check(SystemPerformanceSampler.CpuPercent(prior, new(150, 260, 340)) == 50, "CPU subtracts idle already included in kernel time");
        check(SystemPerformanceSampler.CpuPercent(prior, new(200, 260, 340)) == 0, "entire idle interval measures zero system activity");
        check(SystemPerformanceSampler.CpuPercent(prior, new(100, 260, 340)) == 100, "entire busy interval measures full system activity");
        check(SystemPerformanceSampler.CpuPercent(prior, prior) is null, "zero timing interval is unknown");
        check(SystemPerformanceSampler.CpuPercent(prior, new(99, 260, 340)) is null, "counter reset cannot underflow to a fake percentage");
        check(SystemPerformanceSampler.CpuPercent(prior, new(300, 260, 340)) is null, "invalid idle delta is unknown rather than clamped to zero");
        check(SystemPerformanceSampler.CpuPercent(new(0, 0, 0), new(0, ulong.MaxValue, 1)) is null, "timing sum overflow is rejected");
        const long gib = 1024L * 1024 * 1024;
        var reads = 0;
        var times = prior;
        var groups = (ushort)1;
        var sampler = new SystemPerformanceSampler(() => { reads++; return times; }, () => new(32 * gib, 20 * gib), () => groups);
        var sample = sampler.Sample();
        check(sample.CpuPercent is null && sample.TotalMemoryBytes == 32 * gib && sample.AvailableMemoryBytes == 20 * gib && sample.UsedMemoryBytes == 12 * gib,
            "system RAM total available and used are physical bytes independent of app private memory");
        times = new(150, 260, 340); sample = sampler.Sample();
        check(sample.CpuPercent == 50 && reads == 2, "second native interval uses only one constant-cost CPU read");
        groups = 2; sample = sampler.Sample();
        check(sample.MultipleProcessorGroups && sample.CpuPercent is null && reads == 2 && sample.TotalMemoryBytes == 32 * gib,
            "multiple CPU groups do not pass partial CPU data off as whole-computer usage");
        groups = 0; sample = sampler.Sample();
        check(!sample.MultipleProcessorGroups && sample.CpuPercent is null && reads == 2, "failed group detection leaves CPU unknown");
        groups = 1; sample = sampler.Sample();
        check(sample.CpuPercent is null, "resuming a complete CPU scope requires a fresh baseline");
        times = new(200, 320, 380); check(sampler.Sample().CpuPercent == 50, "full CPU measurement resumes after a fresh baseline");
        sampler.Reset(); check(sampler.Sample().CpuPercent is null, "disabled or changed display discards stale timing intervals");
        var denied = new SystemPerformanceSampler(() => throw new System.ComponentModel.Win32Exception(5), () => new(32 * gib, 20 * gib), () => 1);
        check(denied.Sample().CpuPercent is null && denied.Sample().UsedMemoryBytes == 12 * gib, "denied CPU reads preserve available RAM metrics");
        denied = new SystemPerformanceSampler(() => prior, () => throw new System.ComponentModel.Win32Exception(5), () => 1);
        check(denied.Sample().TotalMemoryBytes is null, "failed RAM reads remain unknown rather than zero");
        foreach (var invalid in new[] { new SystemPhysicalMemory(0, 0), new SystemPhysicalMemory(32, -1), new SystemPhysicalMemory(32, 33) })
            check(new SystemPerformanceSampler(() => prior, () => invalid, () => 1).Sample().TotalMemoryBytes is null,
                "invalid physical RAM range is rejected " + invalid);
        using (var entered = new ManualResetEventSlim()) using (var release = new ManualResetEventSlim())
        {
            var slow = new SystemPerformanceSampler(() => { entered.Set(); release.Wait(5000); return prior; }, () => new(32 * gib, 20 * gib), () => 1);
            var pending = Task.Run(slow.Sample);
            check(entered.Wait(5000), "background system read entered");
            var watch = System.Diagnostics.Stopwatch.StartNew(); slow.Reset();
            check(watch.ElapsedMilliseconds < 100, "UI reset never waits behind an in-flight system read");
            release.Set(); pending.GetAwaiter().GetResult();
            check(slow.Sample().CpuPercent is null, "reset during a background read cannot preserve its old CPU baseline");
        }
        var native = new SystemPerformanceSampler();
        sample = native.Sample();
        check(sample.TotalMemoryBytes > 0 && sample.AvailableMemoryBytes >= 0 && sample.AvailableMemoryBytes <= sample.TotalMemoryBytes,
            "actual Windows physical memory API returns a valid total and available range");
        Thread.Sleep(100); sample = native.Sample();
        check(sample.MultipleProcessorGroups ? sample.CpuPercent is null : sample.CpuPercent is >= 0 and <= 100,
            "actual Windows CPU timing has honest scope and a valid measured percentage");
        Thread.Sleep(350);sample=native.Sample();
        check(sample.DesktopFramesPerSecond is >=0 and <1000,
            "actual Windows DWM composition counter returns measured desktop FPS "+sample.DesktopFramesPerSecond);
        using(var entered=new ManualResetEventSlim())using(var release=new ManualResetEventSlim())
        {
            double seconds=10;var block=true;
            using var app=new PerformanceSampler(()=>seconds,()=>{if(block){entered.Set();release.Wait(5000);}return new(200,300);});
            app.Start();for(var i=0;i<60;i++)app.RecordFrame();seconds=11;
            var pending=Task.Run(app.Sample);check(entered.Wait(5000),"background app memory read entered");
            var watch=System.Diagnostics.Stopwatch.StartNew();app.Stop();seconds=12;app.Start();
            check(watch.ElapsedMilliseconds<100,"application sampling restart never waits on blocked process memory");
            for(var i=0;i<30;i++)app.RecordFrame();release.Set();
            check(pending.GetAwaiter().GetResult() is null,"old background app sample cannot cross a stop and restart");
            block=false;seconds=13;var current=app.Sample();
            check(current?.FramesPerSecond==30&&current.IntervalSeconds==1,"restart preserves only new frames and its fresh timing baseline");
        }
        L.Language="en";check(L.T("内存")=="RAM"&&L.T("总内存")=="Total RAM","English metric headings never fall back to Chinese");
    }
}
