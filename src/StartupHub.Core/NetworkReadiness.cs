using System.Net;
using Microsoft.Win32;

namespace StartupHub.Core;

public record NetworkCheck(bool Ready, bool DirectReady, string Detail);

public static class NetworkReadiness
{
    private static readonly HttpClient DirectClient = CreateClient(null);
    private static readonly object proxyGate = new();
    private static readonly Dictionary<string, HttpClient> ProxyClients = new();
    private static HttpClient CreateClient(string? proxy)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = proxy != null };
        if (proxy != null) handler.Proxy = new WebProxy(proxy);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
    }
    private static HttpClient ProxyClient(string address)
    {
        lock (proxyGate)
        {
            if (!ProxyClients.TryGetValue(address, out var client)) ProxyClients[address] = client = CreateClient(address);
            return client;
        }
    }
    public static (string Url, string Content) WindowsProbe()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\NlaSvc\Parameters\Internet");
        string host = key?.GetValue("ActiveWebProbeHost") as string ?? "www.msftconnecttest.com";
        string path = key?.GetValue("ActiveWebProbePath") as string ?? "connecttest.txt";
        return ($"http://{host}/{path.TrimStart('/')}", key?.GetValue("ActiveWebProbeContent") as string ?? "Microsoft Connect Test");
    }
    public static async Task<NetworkCheck> CheckAsync(TaskEntry entry, CancellationToken cancellation)
    {
        var windows = WindowsProbe();
        string url = entry.DirectProbeUrl.Length > 0 ? entry.DirectProbeUrl : windows.Url;
        string expected = entry.DirectProbeUrl.Length > 0 ? entry.DirectExpectedContent : windows.Content;
        var direct = DirectClient;
        try
        {
            using var response = await direct.GetAsync(url, cancellation);
            if (response.StatusCode != HttpStatusCode.OK || await response.Content.ReadAsStringAsync(cancellation) != expected)
                return new(false, false, $"等待登录：普通网络检查未通过（HTTP {(int)response.StatusCode}）。");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellation.IsCancellationRequested)
        { return new(false, false, "等待登录：普通网络连接失败。"); }
        var proxy = ProxyClient(entry.ProxyUrl);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, entry.ProxyProbeUrl);
            using var response = await proxy.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            int code = (int)response.StatusCode;
            bool reachable = code is >= 200 and < 500 && code != 407;
            return new(reachable, true, reachable ? $"普通网络、代理和目标通路可用 · HTTP {code}" + (code >= 400 ? "（认证或访问限制响应，未验证账号登录）" : "") : $"目标连通检查未通过 · HTTP {code}");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException && !cancellation.IsCancellationRequested)
        { return new(false, true, "检测代理：无法经代理建立目标 HTTPS 连接。"); }
    }
}
