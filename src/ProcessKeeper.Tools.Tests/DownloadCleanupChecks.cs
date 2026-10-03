using System.Net.Http;
using System.Reflection;
using ProcessKeeper.Core;

internal static class DownloadCleanupChecks
{
    internal static void Run()
    {
        var root=Path.Combine(Environment.GetEnvironmentVariable("TEMP")!,"download-cleanup-"+Guid.NewGuid().ToString("N"));
        if(!Path.GetFullPath(root).StartsWith("E:\\",StringComparison.OrdinalIgnoreCase))throw new Exception("Fixture must remain on E:");
        Directory.CreateDirectory(root);
        var checks=0;
        void Check(bool value,string detail){checks++;if(!value)throw new Exception(detail);}
        try
        {
            using var job=new ResourceDownloadJob(new("https://fixture.example/not-requested",root,"untouched.bin"));
            var fields=typeof(ResourceDownloadJob);
            var http=(HttpClient)fields.GetField("_http",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(job)!;
            var stage=(string)fields.GetField("_work",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(job)!;
            Directory.CreateDirectory(stage);
            using(var locked=new FileStream(Path.Combine(stage,"locked.part"),FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            {
                var cleanupFailed=false;
                try{job.Dispose();}catch(IOException){cleanupFailed=true;}
                Check(cleanupFailed,"A locked staging file must preserve the actual cleanup exception");
                var httpDisposed=false;
                try{http.Timeout=TimeSpan.FromSeconds(1);}catch(ObjectDisposedException){httpDisposed=true;}
                Check(httpDisposed,"Owned HttpClient must be disposed even when staging cleanup fails");
                Check(File.Exists(Path.Combine(stage,"locked.part")),"Failed cleanup leaves the locked file intact instead of claiming removal");
            }
            job.Dispose();
            Check(!File.Exists(Path.Combine(root,"untouched.bin")),"Disposal never creates a target file or sends a network request");
            Console.WriteLine($"PASS downloader owned cleanup: {checks} checks; native E-only lock, no HTTP request");
        }
        finally{Directory.Delete(root,true);}
    }
}
