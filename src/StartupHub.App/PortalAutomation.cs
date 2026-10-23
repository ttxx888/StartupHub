using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using StartupHub.Core;

namespace StartupHub.App;

public static class PortalAutomation
{
    public static Task<string> OpenAndTryLoginAsync(TaskEntry entry, CancellationToken cancellation)
    {
        NativeProcess.Open(entry.Url)?.Dispose();
        return entry.AutoLogin ? TryLoginAsync(entry.Url, cancellation) : Task.FromResult("请手动点击登录");
    }
    public static async Task<string> TryLoginAsync(string portal, CancellationToken cancellation)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try { return await FindAndClickAsync(portal, stop.Token).WaitAsync(TimeSpan.FromSeconds(20), cancellation); }
        catch (TimeoutException) { return "请手动点击登录（20 秒内未识别到唯一可调用按钮）"; }
        finally { stop.Cancel(); }
    }
    private static Task<string> FindAndClickAsync(string portal, CancellationToken cancellation) => Task.Run(async () =>
    {
        string host = new Uri(portal).Host;
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed.TotalSeconds < 20)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
                foreach (AutomationElement window in windows)
                {
                    using var process = Process.GetProcessById(window.Current.ProcessId);
                    if (!process.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)) continue;
                    var edits = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                    bool portalTab = false;
                    foreach (AutomationElement edit in edits)
                    {
                        string name = edit.Current.Name;
                        if (!(name.Contains("地址") || name.Contains("Address and search", StringComparison.OrdinalIgnoreCase))) continue;
                        if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)) continue;
                        var address = ((ValuePattern)pattern).Current.Value;
                        if (!address.Contains("://")) address = "http://" + address;
                        if (Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Host == host) portalTab = true;
                    }
                    if (!portalTab) continue;
                    var documents = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
                    var buttons = documents.Cast<AutomationElement>().Where(d => !d.Current.IsOffscreen)
                        .SelectMany(d => d.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)).Cast<AutomationElement>())
                        .Where(b => !b.Current.IsOffscreen && b.Current.IsEnabled && string.Concat(b.Current.Name.Where(c => !char.IsWhiteSpace(c))) == "登录").ToArray();
                    if (buttons.Length != 1) continue;
                    if (!buttons[0].TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) return "请手动点击登录（按钮不支持自动调用）";
                    cancellation.ThrowIfCancellationRequested();
                    if (watch.Elapsed.TotalSeconds >= 20) break;
                    ((InvokePattern)invoke).Invoke();
                    return "已尝试点击登录一次，等待网络恢复";
                }
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException) { }
            await Task.Delay(500, cancellation);
        }
        return "请手动点击登录（20 秒内未识别到唯一可调用按钮）";
    }, cancellation);
}
