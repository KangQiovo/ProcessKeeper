using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using ProcessKeeper.Core;

internal static class DownloadRedirectChecks
{
    internal static async Task Run()
    {
        var root=Path.Combine(Environment.GetEnvironmentVariable("TEMP")!,"download-redirect-"+Guid.NewGuid().ToString("N"));
        if(!Path.GetFullPath(root).StartsWith("E:\\",StringComparison.OrdinalIgnoreCase))throw new Exception("Fixture must remain on E:");
        Directory.CreateDirectory(root);
        var checks=0;
        void Check(bool value,string detail){checks++;if(!value)throw new Exception(detail);}
        try
        {
            foreach(var mode in new[]{"parallel","sequential","dated"})
            {
                var handler=new RedirectServer("https://cdn.fixture.example/final",mode);
                using var http=new HttpClient(handler);
                using var job=new ResourceDownloadJob(new("https://fixture.example/start",root,mode+".bin",UserAgent:"ProcessKeeper.RedirectFixture/1.7",Connections:mode=="parallel"?4:1),http);
                var result=await job.RunAsync();
                Check(File.ReadAllBytes(result.Path).SequenceEqual(handler.Data),"HTTPS 302 completes exact file bytes: "+mode);
                var requests=handler.Requests.ToArray();var origin=requests.Where(x=>x.Host=="fixture.example").ToArray();var destination=requests.Where(x=>x.Host=="cdn.fixture.example").ToArray();
                Check(origin.Length==destination.Length&&destination.Length>1,"Every probe and transfer follows its permitted HTTPS redirect: "+mode);
                Check(destination.All(x=>x.Agent=="ProcessKeeper.RedirectFixture/1.7"&&x.Encoding=="identity"),"User-Agent and identity encoding survive redirects: "+mode);
                Check(origin.All(x=>destination.Any(y=>y.Range==x.Range&&y.IfRange==x.IfRange&&y.IfMatch==x.IfMatch&&y.IfUnmodified==x.IfUnmodified)),"Range and validator headers survive redirects: "+mode);
                if(mode=="parallel")Check(destination.Count(x=>x.Range.Length>0&&x.Range!="bytes=0-0")>0&&destination.Where(x=>x.Range.Length>0&&x.Range!="bytes=0-0").All(x=>x.IfRange=="\"v1\""),"Every adaptive redirected range carries its strong If-Range validator");
                if(mode=="sequential")Check(destination.Any(x=>x.IfMatch=="\"v1\""),"Sequential redirected transfer retains If-Match");
                if(mode=="dated")Check(destination.Any(x=>x.IfUnmodified.Length>0),"Date-validated redirected transfer retains If-Unmodified-Since");
                Check(handler.Intermediates.All(x=>x.Disposed),"Intermediate redirect responses close before completion: "+mode);
            }
            foreach(var target in new[]{"http://insecure.fixture.example/file","file:///not-a-download","https://fixture-user@cdn.fixture.example/file"})
            {
                var handler=new RedirectServer(target,"sequential");using var http=new HttpClient(handler);
                using var job=new ResourceDownloadJob(new("https://fixture.example/start",root,"reject-"+checks+".bin"),http);
                var rejected=false;try{await job.RunAsync();}catch(InvalidDataException){rejected=true;}
                Check(rejected,"Unsafe redirect is rejected before the next request: "+new Uri(target).Scheme);
                Check(handler.Requests.Count==1&&handler.Requests.All(x=>x.Host=="fixture.example"&&x.Scheme=="https"),"No request is sent to the unsafe redirect target");
                Check(handler.Intermediates.All(x=>x.Disposed),"Rejected redirect response is disposed");
            }
            var loop=new RedirectServer("/start","sequential");using(var http=new HttpClient(loop))using(var job=new ResourceDownloadJob(new("https://fixture.example/start",root,"loop.bin"),http))
            {
                var rejected=false;try{await job.RunAsync();}catch(InvalidDataException){rejected=true;}
                Check(rejected&&loop.Requests.Count<=11,"A redirect cycle stops at a bounded request count");
                Check(loop.Intermediates.All(x=>x.Disposed),"Every bounded loop response is disposed");
            }
            using(var owned=new ResourceDownloadJob(new("https://fixture.example/not-requested",root,"owned.bin")))
            {
                var client=(HttpClient)typeof(ResourceDownloadJob).GetField("_http",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(owned)!;
                var field=typeof(HttpMessageInvoker).GetFields(BindingFlags.Instance|BindingFlags.NonPublic).Single(x=>typeof(HttpMessageHandler).IsAssignableFrom(x.FieldType));
                var handler=(HttpClientHandler)field.GetValue(client)!;
                Check(!handler.AllowAutoRedirect,"Owned HTTP handler disables automatic redirects on all frameworks");
            }
            Console.WriteLine($"PASS downloader manual redirects: {checks} checks; fake HTTP only");
        }
        finally{Directory.Delete(root,true);}
    }
    private sealed record Request(string Host,string Scheme,string Range,string IfRange,string IfMatch,string IfUnmodified,string Agent,string Encoding);
    private sealed class RedirectServer(string target,string mode):HttpMessageHandler
    {
        internal readonly byte[] Data=Enumerable.Range(0,1200000).Select(x=>(byte)(x%239)).ToArray();
        internal readonly ConcurrentQueue<Request> Requests=new();
        internal readonly ConcurrentQueue<Intermediate> Intermediates=new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();var uri=request.RequestUri!;
            Requests.Enqueue(new(uri.Host,uri.Scheme,request.Headers.Range?.ToString()??"",request.Headers.IfRange?.ToString()??"",
                string.Join(",",request.Headers.IfMatch),request.Headers.IfUnmodifiedSince?.ToString("R")??"",request.Headers.UserAgent.ToString(),string.Join(",",request.Headers.AcceptEncoding)));
            if(uri.Host=="fixture.example")
            {
                var content=new Intermediate();Intermediates.Enqueue(content);
                var response=new HttpResponseMessage(HttpStatusCode.Found){RequestMessage=request,Content=content};
                response.Headers.Location=new Uri(target,UriKind.RelativeOrAbsolute);return Task.FromResult(response);
            }
            var range=request.Headers.Range?.Ranges.Single();var start=(int)(range?.From??0);var end=(int)(range?.To??Data.Length-1);
            var bytes=Data.Skip(start).Take(end-start+1).ToArray();
            var result=new HttpResponseMessage(range is null?HttpStatusCode.OK:HttpStatusCode.PartialContent){RequestMessage=request,Content=new ByteArrayContent(bytes)};
            if(mode=="dated")
            {
                result.Content.Headers.LastModified=new DateTimeOffset(2026,10,3,0,0,0,TimeSpan.Zero);
                result.Headers.Date=result.Content.Headers.LastModified.Value.AddMinutes(2);
            }
            else result.Headers.ETag=new EntityTagHeaderValue("\"v1\"");
            if(range is not null)result.Content.Headers.ContentRange=new ContentRangeHeaderValue(start,end,Data.Length);
            return Task.FromResult(result);
        }
    }
    private sealed class Intermediate():ByteArrayContent(Array.Empty<byte>())
    {
        internal bool Disposed;
        protected override void Dispose(bool disposing){Disposed=true;base.Dispose(disposing);}
    }
}
