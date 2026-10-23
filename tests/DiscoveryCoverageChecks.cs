using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using StartupHub.Core;

internal static class DiscoveryCoverageChecks
{
    internal static Task Files(string root,Action<bool,string> check)
    {
        var directory=Path.Combine(root,"html-project");Directory.CreateDirectory(Path.Combine(directory,"pages"));Directory.CreateDirectory(Path.Combine(directory,"node_modules"));
        string known=Path.Combine(directory,"known.html"),fresh=Path.Combine(directory,"pages","fresh.htm"),empty=Path.Combine(directory,"empty.html"),neighbor=Path.Combine(directory,"neighbor.html");
        File.WriteAllText(known,"<html><title>旧标题</title><img src='assets/test.png'></html>");File.WriteAllText(fresh,"<!doctype html><title>新页面 &amp; 测试</title>");File.WriteAllText(empty,"");File.WriteAllText(neighbor,"<title>相邻页面</title>");
        File.WriteAllText(Path.Combine(directory,"node_modules","ignored.html"),"<title>不扫描依赖</title>");
        var hint=new TaskEntry {Kind=TaskKind.File,Name="正式清单名称",Path=known};
        var report=LocalHtmlDiscovery.Scan([hint],[]);
        check(report.Pages.Count==2&&report.Pages.Single(p=>p.Path==known).Name==hint.Name,"参考清单文件与相邻 HTML 未发现或名称被改写");
        var selected=LocalHtmlDiscovery.Scan([],[],[directory]);
        check(selected.Pages.Count==3&&selected.Pages.Single(p=>p.Path==fresh).Name=="新页面 & 测试","目录递归、HTM 或标题解析不正确");
        check(selected.Issues.Any(i=>i.Address==empty&&i.Reason.Contains("为空"))&&selected.Pages.All(p=>!p.Path.Contains("node_modules")),"空文件提示或依赖目录排除不正确");
        var dedup=LocalHtmlDiscovery.Scan([hint],[hint],[directory]);
        check(dedup.Pages.Count==2&&dedup.Pages.All(p=>!p.Path.Equals(known,StringComparison.OrdinalIgnoreCase)),"文件重复项未排除");
        var imported=TaskImport.Append(new HubConfig(),selected.Pages.Select(p=>p.ToEntry("开发网页")));
        check(imported.Added.Count==3&&imported.Added.All(e=>e.Kind==TaskKind.File&&!e.AutoStart&&e.Url==""&&e.Path.Length>0),"HTML 被保存成网址或开启自启");
        check(File.ReadAllText(known).Contains("assets/test.png"),"HTML 文件或相对资源引用被修改");
        return Task.CompletedTask;
    }
    internal static async Task Network(Action<bool,string> check)
    {
        using var ipv6=new Server(IPAddress.IPv6Loopback);
        using var tls=new Server(IPAddress.Loopback,true);
        var endpoints=new ListenerEndpoint[]{new("::1",ipv6.Port,1001),new("127.0.0.1",tls.Port,1002)};
        var snapshot=new MachineSnapshot(new(){{1001,new(1001,0,"ipv6.exe","")},{1002,new(1002,0,"secure.exe","")}},new(){{ipv6.Port,[1001]},{tls.Port,[1002]}},endpoints);
        var report=await LocalPageDiscovery.ScanReportAsync(snapshot,[],hints:[new(){Kind=TaskKind.Url,Url=$"https://localhost:{tls.Port}/"}]);
        check(report.Pages.Any(p=>p.Pid==1001&&new Uri(p.Url).Host=="[::1]"),"IPv6 监听地址未用于探测");
        check(report.Pages.All(p=>p.Port!=tls.Port)&&report.Issues.Any(i=>i.Address.StartsWith("https:")&&i.Reason.Contains("证书")),"自签 HTTPS 未明确报告证书问题或绕过了验证："+System.Text.Json.JsonSerializer.Serialize(report.Issues));
        check(report.Issues.Any(i=>i.Address==$"https://localhost:{tls.Port}/"),"HTTPS 已知主机名被替换而影响证书验证");
        var automatic=await LocalPageDiscovery.ScanReportAsync(snapshot,[]);
        check(automatic.Issues.Any(i=>i.Address.StartsWith("https:")&&i.Reason.Contains("证书")),"未知 HTTPS 端口未在 HTTP 失败后继续识别");
        check(ProcessInspector.ListenerEndpoints().Any(e=>e.Port==ipv6.Port&&e.Address=="::1"),"原生监听枚举丢失 IPv6 绑定地址");
        check(ProcessInspector.ListenerEndpoints().Any(e=>e.Port==tls.Port&&e.Address=="127.0.0.1"),"原生监听枚举丢失 IPv4 绑定地址");
    }
    private sealed class Server : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource stop=new();
        private readonly Task running;
        private readonly X509Certificate2? certificate;
        public int Port {get;}
        public Server(IPAddress address,bool tls=false)
        {
            if(tls){using var key=RSA.Create(2048);var request=new CertificateRequest("CN=localhost",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1);using var generated=request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1),DateTimeOffset.Now.AddDays(1));certificate=X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx),null);}
            listener=new(address,0);listener.Start();Port=((IPEndPoint)listener.LocalEndpoint).Port;
            running=Task.Run(async()=>
            {
                try{while(!stop.IsCancellationRequested)
                {
                    using var client=await listener.AcceptTcpClientAsync(stop.Token);
                    try
                    {
                        Stream stream=client.GetStream();
                        if(certificate!=null){var secure=new SslStream(stream);stream=secure;await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions{ServerCertificate=certificate},stop.Token);}
                        using(stream)
                        {
                            var buffer=new byte[1024];if(await stream.ReadAsync(buffer,stop.Token)==0)continue;
                            string body="<html><title>IPv6 测试页面</title></html>";
                            await stream.WriteAsync(Encoding.UTF8.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}"),stop.Token);
                        }
                    }
                    catch(AuthenticationException){}catch(IOException){}
                }}catch(OperationCanceledException){}
            });
        }
        public void Dispose(){stop.Cancel();listener.Stop();running.GetAwaiter().GetResult();certificate?.Dispose();stop.Dispose();}
    }
}
