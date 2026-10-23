using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Text;
using System.Text.RegularExpressions;

namespace StartupHub.Core;

public sealed record DiscoveryProgress(int Completed, int Total);
public sealed record DiscoveryIssue(string Address, string Reason);
public sealed record DiscoveryReport(List<DiscoveredPage> Pages, List<DiscoveryIssue> Issues);

public static class LocalPageDiscovery
{
    private static readonly string[] DesktopAppProcessMarkers=["sample desktop app","sample_mcp"];
    private static bool Local(Uri uri) => uri.Scheme is "http" or "https" && uri.IsLoopback;
    private static string AddressKey(Uri uri) => new UriBuilder(uri) { Host=uri.IsLoopback?"localhost":uri.Host, Fragment="" }.Uri.AbsoluteUri;
    public static List<DiscoveredPage> ExcludeExisting(IEnumerable<DiscoveredPage> pages, IEnumerable<TaskEntry> entries)
    {
        var addresses=new HashSet<string>(StringComparer.Ordinal);
        var files=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var entry in entries)
        {
            if(entry.Kind==TaskKind.File && entry.Path.Length>0) files.Add(System.IO.Path.GetFullPath(entry.Path));
            if(Uri.TryCreate(entry.Url,UriKind.Absolute,out var uri) && uri.Scheme is "http" or "https") addresses.Add(AddressKey(uri));
            else if(entry.Port>0) addresses.Add(AddressKey(new Uri($"http://localhost:{entry.Port}/")));
        }
        return pages.Where(p=>p.Kind==TaskKind.File?!files.Contains(System.IO.Path.GetFullPath(p.Path)):!addresses.Contains(AddressKey(new Uri(p.Url)))).ToList();
    }
    internal static string Title(string content,string fallback)
    {
        var match=Regex.Match(content,@"<title\b[^>]*>(.*?)</title\s*>",RegexOptions.IgnoreCase|RegexOptions.Singleline);
        string title=match.Success?Regex.Replace(WebUtility.HtmlDecode(match.Groups[1].Value),@"\s+"," ").Trim():"";
        return title.Length>0?title:fallback;
    }
    public static async Task<List<DiscoveredPage>> ScanAsync(MachineSnapshot snapshot,IEnumerable<TaskEntry> entries,IProgress<DiscoveryProgress>? progress=null)
        => (await ScanReportAsync(snapshot,entries,progress)).Pages;

    public static async Task<DiscoveryReport> ScanReportAsync(MachineSnapshot snapshot,IEnumerable<TaskEntry> entries,IProgress<DiscoveryProgress>? progress=null,IEnumerable<TaskEntry>? hints=null)
    {
        var config=entries.ToArray();var known=(hints??config).ToArray();
        var endpoints=(snapshot.Endpoints??snapshot.Listeners.SelectMany(p=>p.Value.Select(pid=>new ListenerEndpoint("127.0.0.1",p.Key,pid))).ToArray())
            .Select(e=>e with {Address=e.Address=="0.0.0.0"?"127.0.0.1":e.Address=="::"?"::1":e.Address})
            .DistinctBy(e=>(e.Address,e.Port,e.Pid)).ToArray();
        var localAddresses=NetworkInterface.GetAllNetworkInterfaces().SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address.ToString()).Concat(endpoints.Select(e=>e.Address)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool LocalTarget(Uri uri)=>Local(uri)||(uri.Scheme is "http" or "https" && IPAddress.TryParse(uri.DnsSafeHost,out var address)&&localAddresses.Contains(address.ToString()));
        bool Excluded(ListenerEndpoint e)=>snapshot.Processes.TryGetValue(e.Pid,out var p)&&DesktopAppProcessMarkers.Any(m=>p.CommandLine.Contains(m,StringComparison.OrdinalIgnoreCase)||p.Name.Contains(m,StringComparison.OrdinalIgnoreCase)||p.Path.Contains(m,StringComparison.OrdinalIgnoreCase));
        Uri EndpointUri(ListenerEndpoint e,string scheme)=>new UriBuilder(scheme,e.Address,e.Port).Uri;
        var issues=new ConcurrentBag<DiscoveryIssue>();var pages=new ConcurrentBag<DiscoveredPage>();
        var tlsFailures=new ConcurrentDictionary<string,string>();
        using var handler=new HttpClientHandler {UseProxy=false,AllowAutoRedirect=false};
        handler.ServerCertificateCustomValidationCallback=(request,cert,chain,errors)=>
        {
            if(errors!=SslPolicyErrors.None)
            {
                var reasons=new List<string>();
                if(errors.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch))reasons.Add("证书与地址不匹配");
                if(errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))reasons.Add("未提供证书");
                if(errors.HasFlag(SslPolicyErrors.RemoteCertificateChainErrors))reasons.Add(chain?.ChainStatus.Any(x=>x.Status==System.Security.Cryptography.X509Certificates.X509ChainStatusFlags.UntrustedRoot)==true?"根证书不受信任":"证书链校验失败");
                tlsFailures[request.RequestUri!.AbsoluteUri]="需要证书确认："+string.Join("；",reasons);
            }
            return errors==SslPolicyErrors.None;
        };
        using var http=new HttpClient(handler);using var limit=new SemaphoreSlim(8);
        int completed=0;progress?.Report(new(0,endpoints.Length));
        foreach(var hint in known.Where(e=>Uri.TryCreate(e.Url,UriKind.Absolute,out var uri)&&LocalTarget(uri)))
            if(!snapshot.Listeners.ContainsKey(new Uri(hint.Url).Port))issues.Add(new(hint.Url,"当前没有监听此端口的服务"));
        await Task.WhenAll(endpoints.Select(async endpoint=>
        {
            await limit.WaitAsync();
            var root=EndpointUri(endpoint,"http");
            try
            {
                if(Excluded(endpoint)){issues.Add(new(root.AbsoluteUri,"已排除：桌面内部页面或不参与发现的服务"));return;}
                var process=snapshot.Processes.GetValueOrDefault(endpoint.Pid);
                string name=Path.GetFileNameWithoutExtension(process is {Path.Length:>0}?process.Path:process?.Name??"");if(name.Length==0)name="本地网页";
                var urls=known.Select(e=>Uri.TryCreate(e.Url,UriKind.Absolute,out var uri)?uri:null)
                    .Where(u=>u!=null&&u.Port==endpoint.Port&&LocalTarget(u)&&(u.IsLoopback&&root.IsLoopback||u.Host==root.Host)).Cast<Uri>().ToArray();
                var schemes=urls.Any(u=>u.Scheme=="https")?new[]{"https","http"}:new[]{"http","https"};
                if(ExcludeExisting([new("",root.AbsoluteUri,root.Port,endpoint.Pid)],config).Count==0&&!urls.Any(u=>u.AbsolutePath!="/"))
                {issues.Add(new(root.AbsoluteUri,"已在当前清单中"));return;}
                string reason="没有识别到网页";
                foreach(string scheme in schemes)
                {
                    using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var origin=EndpointUri(endpoint,scheme);
                    var candidates=urls.Where(u=>u.Scheme==scheme).Concat(new[]{origin,new Uri(origin,"/panel/"),new Uri(origin,"/webui"),new Uri(origin,"/docs")}).DistinctBy(u=>u.AbsoluteUri);
                    var attempted=origin;
                    try
                    {
                        foreach(var candidate in candidates)
                        {
                            var uri=candidate;
                            for(int redirect=0;redirect<=5;redirect++)
                            {
                                if(ExcludeExisting([new("",uri.AbsoluteUri,uri.Port,endpoint.Pid)],config).Count==0){issues.Add(new(uri.AbsoluteUri,"已在当前清单中"));break;}
                                attempted=uri;
                                using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,cancel.Token);
                                if((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                                {
                                    if(response.Headers.Location is not {} location||!Uri.TryCreate(uri,location,out var next)||!LocalTarget(next)){reason="跳转目标不是本机网页";break;}
                                    if(endpoints.Any(e=>e.Port==next.Port&&Excluded(e))){reason="跳转至已排除的内部服务";break;}
                                    uri=next;reason="跳转次数超出限制";continue;
                                }
                                if(!response.IsSuccessStatusCode){if(reason=="没有识别到网页")reason=$"HTTP {(int)response.StatusCode}，未找到成功网页";break;}
                                await using var stream=await response.Content.ReadAsStreamAsync(cancel.Token);
                                var buffer=new byte[32768];int count=0;
                                while(count<buffer.Length){int read=await stream.ReadAsync(buffer.AsMemory(count),cancel.Token);if(read==0)break;count+=read;}
                                var encoding=Encoding.UTF8;
                                if(response.Content.Headers.ContentType?.CharSet is {Length:>0} charset)try{encoding=Encoding.GetEncoding(charset.Trim('"'));}catch(ArgumentException){}
                                string content=encoding.GetString(buffer,0,count).Trim('\uFEFF',' ','\t','\r','\n');
                                bool html=response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() is "text/html" or "application/xhtml+xml"||Regex.IsMatch(content,@"^\s*(?:<!doctype\s+html\b|<html\b|<head\b|<body\b)",RegexOptions.IgnoreCase);
                                if(content.Length==0||!html){if(reason=="没有识别到网页"||reason.StartsWith("HTTP "))reason=content.Length==0?"响应为空":"响应不是 HTML 网页（"+(response.Content.Headers.ContentType?.MediaType??"未提供类型")+"）";break;}
                                var owner=endpoints.FirstOrDefault(e=>e.Port==uri.Port&&(e.Address==uri.DnsSafeHost||uri.IsLoopback&&IPAddress.TryParse(e.Address,out var ip)&&IPAddress.IsLoopback(ip)))??endpoint;
                                var ownerProcess=snapshot.Processes.GetValueOrDefault(owner.Pid);
                                string ownerName=Path.GetFileNameWithoutExtension(ownerProcess is {Path.Length:>0}?ownerProcess.Path:ownerProcess?.Name??"");if(ownerName.Length==0)ownerName=name;
                                pages.Add(new(Title(content,ownerName),new UriBuilder(uri){Fragment=""}.Uri.AbsoluteUri,uri.Port,owner.Pid,ownerName));return;
                            }
                        }
                    }
                    catch(HttpRequestException ex)
                    {
                        if(tlsFailures.TryGetValue(attempted.AbsoluteUri,out var certificateProblem)){issues.Add(new(attempted.AbsoluteUri,certificateProblem));return;}
                        if(reason=="没有识别到网页")reason="连接或协议失败："+ex.GetBaseException().Message;
                    }
                    catch(OperationCanceledException){if(reason=="没有识别到网页")reason="探测超时";}
                    catch(IOException ex){if(reason=="没有识别到网页")reason="读取响应失败："+ex.Message;}
                }
                issues.Add(new(root.AbsoluteUri,reason));
            }
            finally{progress?.Report(new(Interlocked.Increment(ref completed),endpoints.Length));limit.Release();}
        }));
        return new(ExcludeExisting(pages.OrderBy(p=>p.Port).ThenBy(p=>p.Url,StringComparer.Ordinal).DistinctBy(p=>AddressKey(new Uri(p.Url))),config),issues.Distinct().OrderBy(i=>i.Address,StringComparer.Ordinal).ToList());
    }
}
