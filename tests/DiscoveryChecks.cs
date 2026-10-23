using System.Net;
using System.Net.Sockets;
using System.Text;
using StartupHub.Core;

internal static class DiscoveryChecks
{
    internal static async Task ServeFixture(string root)
    {
        string Page(string body,string type="text/html",int status=200)=>$"HTTP/1.1 {status} Test\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        using var html=new PageServer(_=>Page("<title>验收网页</title><body>OK</body>"));
        using var xhtml=new PageServer(_=>Page("<html><body>验收正文</body></html>","application/xhtml+xml"));
        using var json=new PageServer(_=>Page("{\"ok\":true}","application/json"));
        using var error=new PageServer(_=>Page("<title>错误</title>",status:404));
        using var empty=new PageServer(_=>Page(""));
        using var redirect=new PageServer(_=>$"HTTP/1.1 302 Found\r\nLocation: http://localhost:{html.Port}/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        File.WriteAllText(Path.Combine(root,"fixture.json"),System.Text.Json.JsonSerializer.Serialize(new { Html=html.Port,Xhtml=xhtml.Port,Json=json.Port,Error=error.Port,Empty=empty.Port,Redirect=redirect.Port }));
        await Task.Delay(TimeSpan.FromMinutes(5));
    }
    internal static async Task Run(Action<bool,string> check)
    {
        var servers = new List<PageServer>();
        var snapshot = new MachineSnapshot(new(),new());
        PageServer Serve(Func<string,string> response, string program="网页测试")
        {
            var server = new PageServer(response); servers.Add(server);
            int pid=10000+servers.Count;
            snapshot.Listeners[server.Port]=[pid]; snapshot.Processes[pid]=new(pid,0,program+".exe",program);
            return server;
        }
        string Page(string body, string type="text/html", int status=200) => $"HTTP/1.1 {status} Test\r\nContent-Type: {type}; charset=utf-8\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
        string Redirect(string url) => $"HTTP/1.1 302 Found\r\nLocation: {url}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        try
        {
            var html=Serve(_=>Page("<html><title> 测试 &amp;\n 页面 </title><body>OK</body></html>"));
            var xhtml=Serve(_=>Page("<html><body>正文</body></html>","application/xhtml+xml"),"无标题程序");
            int xhtmlPid=snapshot.Listeners[xhtml.Port][0];
            snapshot.Processes[xhtmlPid]=new(xhtmlPid,0,"","无标题程序","无标题程序.exe");
            var json=Serve(_=>Page("{\"ok\":true}","application/json"));
            var error=Serve(_=>Page("<title>错误</title>",status:500));
            var empty=Serve(_=>Page(""));
            var spaces=Serve(_=>Page(" \r\n\t"));
            var noContent=Serve(_=>Page("",status:204));
            var wrongType=Serve(_=>Page("<title>普通文本</title>","text/plain"));
            var mislabeled=Serve(_=>Page("<!doctype html><html><title>可用网页</title></html>","application/octet-stream"));
            var noType=Serve(_=>Page("<html><title>无内容类型</title></html>").Replace("Content-Type: text/html; charset=utf-8\r\n",""));
            var panel=Serve(path=>path=="/panel/"?Page("<title>控制台</title>"):Page("not found","text/plain",404));
            var desktopPage=Serve(_=>Page("<title>Desktop</title><div id='root'></div>"),"sample desktop app");
            var known=Serve(_=>Page("<title>已有网址</title>"));
            var portKnown=Serve(_=>Page("<title>已有端口</title>"));
            var excluded=Serve(_=>Page("<title>不应访问</title>"),"sample_mcp.one");
            var godot=Serve(_=>Page("<title>不应访问</title>"),"sample_mcp.two");
            var alias=Serve(_=>Redirect($"http://localhost:{html.Port}/"));
            var local=Serve(path=>path=="/"?Redirect("/home"):Page("<title>本地重定向</title>"));
            var loop=Serve(_=>Redirect("/loop"));
            var remote=Serve(_=>Redirect("https://example.com/"));
            var existingRedirect=Serve(_=>Redirect($"http://127.0.0.1:{known.Port}/tools"));
            var excludedRedirect=Serve(_=>Redirect($"http://127.0.0.1:{excluded.Port}/"));
            var config=new TaskEntry[] {
                new() {Kind=TaskKind.Url,Url=$"http://localhost:{known.Port}/tools",Port=0},
                new() {Port=portKnown.Port}
            };
            var progress=new List<DiscoveryProgress>();
            var results=await LocalPageDiscovery.ScanAsync(snapshot,config,new InlineProgress(p=>{lock(progress)progress.Add(p);}));
            check(results.Count==7,"非网页或重复项进入结果："+string.Join(",",results.Select(x=>x.Url)));
            check(results.Single(x=>x.Port==html.Port).Name=="测试 & 页面","标题实体或空白处理不正确");
            check(results.Single(x=>x.Port==xhtml.Port).Name=="无标题程序" && results.All(x=>x.ProcessName.Length>0),"程序信息或标题回退缺失");
            check(results.Single(x=>x.Port==local.Port).Url.EndsWith("/home"),"未保留重定向最终地址");
            check(known.Requests==1 && results.Any(x=>x.Port==known.Port && new Uri(x.Url).AbsolutePath=="/") && portKnown.Requests==0 && excluded.Requests==0 && godot.Requests==0,"已有子路径误排除整个端口，或排除的服务被访问");
            check(results.Any(x=>x.Port==panel.Port && x.Url.EndsWith("/panel/")) && results.Any(x=>x.Port==mislabeled.Port) && results.Any(x=>x.Port==noType.Port),"控制台子路径或错误、缺失内容类型的 HTML 被漏掉");
            check(desktopPage.Requests==0 && results.All(x=>x.Port!=desktopPage.Port),"桌面程序内部页面被当成浏览器网页");
            check(loop.Requests<=24 && remote.Requests<=4,"循环或外部跳转未停止");
            check(progress.First().Completed==0 && progress.Last().Completed==progress.Last().Total,"扫描进度未完整报告");
            var aliases=new[] {new DiscoveredPage("甲",$"http://127.0.0.1:{html.Port}/",html.Port,1)};
            check(LocalPageDiscovery.ExcludeExisting(aliases,[new() {Url=$"http://[::1]:{html.Port}/"}]).Count==0,"本地地址别名未识别");
        }
        finally { foreach(var server in servers) server.Dispose(); }
    }
    private sealed class InlineProgress(Action<DiscoveryProgress> report) : IProgress<DiscoveryProgress> { public void Report(DiscoveryProgress value)=>report(value); }
    private sealed class PageServer : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task running;
        private int requests;
        public int Requests=>Volatile.Read(ref requests);
        public int Port {get;}
        public PageServer(Func<string,string> response)
        {
            listener.Start(); Port=((IPEndPoint)listener.LocalEndpoint).Port;
            running=Task.Run(async()=>
            {
                try { while(!stop.IsCancellationRequested)
                {
                    using var client=await listener.AcceptTcpClientAsync(stop.Token);
                    using var stream=client.GetStream();
                    var prefix=new byte[1];if(await stream.ReadAsync(prefix,stop.Token)==0||prefix[0]==22)continue;
                    using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
                    string line=await reader.ReadLineAsync(stop.Token)??"";
                    Interlocked.Increment(ref requests);
                    var bytes=Encoding.UTF8.GetBytes(response(line.Split(' ').ElementAtOrDefault(1)??"/"));
                    await stream.WriteAsync(bytes,stop.Token);
                }} catch(OperationCanceledException) { } catch(SocketException) when(stop.IsCancellationRequested) { }
            });
        }
        public void Dispose() {stop.Cancel(); listener.Stop(); running.GetAwaiter().GetResult(); stop.Dispose();}
    }
}
