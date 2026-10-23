using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using StartupHub.Core;

if (args.FirstOrDefault() == "--discover-serve") { await DiscoveryChecks.ServeFixture(args[1]); return 0; }

if (args.FirstOrDefault() == "--import-emit")
{
    File.WriteAllText(args[1],JsonSerializer.Serialize(new { Args=args.Skip(2).ToArray(),Cwd=Environment.CurrentDirectory }));
    await Task.Delay(3000); return 0;
}

if (args.FirstOrDefault() == "--emit")
{
    Console.WriteLine(JsonSerializer.Serialize(new { Args = args.Skip(1).ToArray(), Cwd = Environment.CurrentDirectory, Env = Environment.GetEnvironmentVariable("HUB_TEST"), Console = GetConsoleWindow().ToInt64() }));
    Console.Error.WriteLine("示例错误输出"); return 0;
}
if (args.FirstOrDefault() == "--sleep") { await Task.Delay(int.Parse(args[1])); return int.Parse(args[2]); }
if (args.FirstOrDefault() == "--serve")
{
    int port = int.Parse(args[1]);
    if (args.Length > 2) await Task.Delay(int.Parse(args[2]));
    var server = new TcpListener(IPAddress.Loopback, port); server.Start();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(120));
    _ = Task.Run(async () => { while (!stop.IsCancellationRequested) { Console.WriteLine("服务心跳 " + DateTimeOffset.Now.ToUnixTimeMilliseconds()); await Task.Delay(250); } });
    try
    {
        while (!stop.IsCancellationRequested)
        {
            using var client = await server.AcceptTcpClientAsync(stop.Token);
            using var stream = client.GetStream();
            var buffer = new byte[2048]; int bytesRead = await stream.ReadAsync(buffer, stop.Token);
            if (bytesRead == 0) continue;
            await stream.WriteAsync(Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK"), stop.Token);
        }
    }
    catch (OperationCanceledException) { }
    finally { server.Stop(); }
    return 0;
}
if (args.FirstOrDefault() == "--spawn-and-exit")
{
    var e = new TaskEntry { Path = Environment.ProcessPath!, Arguments = ["--serve", args[2]], WorkingDir = args[1] };
    using var p = NativeProcess.Launch(e, Path.Combine(args[1], "detached.out.log"), Path.Combine(args[1], "detached.err.log"));
    File.WriteAllText(Path.Combine(args[1], "detached.json"), JsonSerializer.Serialize(new { Pid = p.Id, Ticks = p.StartTime.ToUniversalTime().Ticks }));
    return 0;
}

var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "tests-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")));
Directory.CreateDirectory(root);
var reports = new List<object>(); int passed = 0, failed = 0;
async Task Check(string name, Func<Task> test)
{
    var filter = Environment.GetEnvironmentVariable("HUB_TEST_FILTER");
    if (!string.IsNullOrEmpty(filter) && !name.Contains(filter)) return;
    try { await test(); passed++; reports.Add(new { Name = name, Passed = true }); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failed++; reports.Add(new { Name = name, Passed = false, Error = ex.ToString() }); Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
int FreePort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); int port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
TaskEntry Service(int port, string name = "验收服务") => new() { Name = name, Path = Environment.ProcessPath!, WorkingDir = root, Arguments = ["--serve", port.ToString()], Port = port, CheckUrl = $"http://127.0.0.1:{port}/", WaitSeconds = 8 };
async Task Until(Func<bool> condition, int ms = 8000) { var w = Stopwatch.StartNew(); while (!condition() && w.ElapsedMilliseconds < ms) await Task.Delay(100); Assert(condition(), "等待条件超时"); }
var store = new DataStore(Path.Combine(root, "data")); using var engine = new TaskEngine(store);

await Check("打开会话：历史重置、刷新保留、CLI 一致和入口缺失", () => SessionChecks.Run(root,Assert));
await Check("网页发现：HTML 过滤、本地重定向、标题、程序、去重与进度", () => DiscoveryChecks.Run(Assert));
await Check("网页发现：本地 HTML、参考清单、目录查找及文件添加", () => DiscoveryCoverageChecks.Files(root,Assert));
await Check("网页发现：IPv6 地址与 HTTPS 证书原因", () => DiscoveryCoverageChecks.Network(Assert));

await Check("桌面导入：快捷方式参数、重复入口、混合无效项与运行识别", () =>
{
    ImportChecks.Run(root,Assert); return Task.CompletedTask;
});

await Check("批量管理：分类顺序、解除依赖、原配置和文件保留", () =>
{
    BatchChecks.Run(root, Assert); return Task.CompletedTask;
});

await Check("智能入口：网址、程序、脚本、文件和网址快捷方式", () =>
{
    Assert(TaskEntrance.Parse("https://example.com/tool?q=1").Kind == TaskKind.Url && TaskEntrance.Parse("https://example.com/tool?q=1").SuggestedName == "example.com", "网址未识别");
    Assert(TaskEntrance.Parse("  \"C:\\工具 文件\\我的程序.EXE\"  ") == new TaskEntrance(TaskKind.Desktop, "C:\\工具 文件\\我的程序.EXE", "我的程序"), "程序路径或名称不正确");
    foreach (var ext in new[] { ".ps1", ".bat", ".cmd", ".py", ".js", ".mjs", ".cjs" }) Assert(TaskEntrance.Parse("C:\\run" + ext).Kind == TaskKind.Script, "脚本类型未识别：" + ext);
    Assert(TaskEntrance.Parse("C:\\小游戏\\index.html").Kind == TaskKind.File && TaskEntrance.Parse("C:\\文档\\简历.docx").SuggestedName == "简历", "普通文件未识别");
    Assert(TaskEntrance.Parse("C:\\应用.lnk").Kind == TaskKind.Desktop, "桌面快捷方式未识别");
    var shortcut = Path.Combine(root, "示例网址.url"); File.WriteAllText(shortcut, "[InternetShortcut]\nURL=https://example.com/start\n");
    Assert(TaskEntrance.Parse(shortcut) == new TaskEntrance(TaskKind.Url,"https://example.com/start","示例网址"), "网址快捷方式未解析");
    Assert(TaskEntrance.Parse("").SuggestedName == "", "空入口生成了名称");
    return Task.CompletedTask;
});

await Check("依赖循环与缺失前置任务被拒绝", () =>
{
    var a = Service(0); var b = Service(0); a.DependsOn = b.Id; b.DependsOn = a.Id;
    bool rejected = false; try { DataStore.Validate(new() { Entries = [a, b] }); } catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "循环未拒绝"); b.DependsOn = null; rejected = false;
    try { DataStore.Validate(new() { Entries = [a] }); } catch (InvalidDataException) { rejected = true; }
    Assert(rejected, "缺失依赖未拒绝"); return Task.CompletedTask;
});
await Check("原生启动：中文、空格、引号、反斜杠、空参数、独立环境和隐藏窗口", async () =>
{
    var cwd = Path.Combine(root, "中文 空格目录"); Directory.CreateDirectory(cwd);
    string[] values = ["中文参数", "two words", "quote\"inside", "tail\\", "", @"C:\x y\", "literal&pipe|"];
    var e = new TaskEntry { Path = Environment.ProcessPath!, WorkingDir = cwd, Arguments = ["--emit", .. values], Environment = new() { ["HUB_TEST"] = "独立环境" } };
    using var p = NativeProcess.Launch(e, store.LogPath(e), store.LogPath(e, true)); await p.WaitForExitAsync();
    Assert(p.ExitCode == 0, "进程失败");
    using var output = JsonDocument.Parse(File.ReadAllText(store.LogPath(e)));
    Assert(output.RootElement.GetProperty("Args").EnumerateArray().Select(x => x.GetString()).SequenceEqual(values), "参数失真");
    Assert(output.RootElement.GetProperty("Cwd").GetString() == cwd, "工作目录错误");
    Assert(output.RootElement.GetProperty("Env").GetString() == "独立环境", "环境错误");
    Assert(output.RootElement.GetProperty("Console").GetInt64() == 0, "隐藏进程有控制台");
    Assert(engine.ReadLogs(e).Contains("示例错误输出"), "中文错误日志显示不正确");
});
await Check("禁止保存输出的任务不写入日志", async () =>
{
    var e = new TaskEntry { Path = Environment.ProcessPath!, Arguments = ["--emit", "private-output"], SuppressOutput = true };
    using var p = NativeProcess.Launch(e, store.LogPath(e), store.LogPath(e, true));
    await p.WaitForExitAsync();
    Assert(p.ExitCode == 0, "进程失败");
    Assert(!File.Exists(store.LogPath(e)) && !File.Exists(store.LogPath(e, true)), "输出被保存");
});
await Check("PowerShell 脚本路径和参数及非零退出码", async () =>
{
    var path = Path.Combine(root, "中文 脚本.ps1"); File.WriteAllText(path, "[Console]::WriteLine(($args | ConvertTo-Json -Compress)); exit 7", new UTF8Encoding(true));
    var e = new TaskEntry { Path = path, Arguments = ["中文", "two words"], WorkingDir = root };
    using var p = NativeProcess.Launch(e, store.LogPath(e), store.LogPath(e, true)); await p.WaitForExitAsync();
    Assert(p.ExitCode == 7, "退出码不符");
    Assert(File.ReadAllText(store.LogPath(e)).Contains("two words"), "参数丢失");
});
await Check("CMD 脚本含空格路径与多个参数", async () =>
{
    var path = Path.Combine(root, "批处理 测试.cmd"); File.WriteAllText(path, "@echo off\r\necho(%~1\r\necho(%~2\r\nexit /b 9\r\n", Encoding.ASCII);
    var e = new TaskEntry { Path = path, Arguments = ["two words", "second"], WorkingDir = root };
    using var p = NativeProcess.Launch(e, store.LogPath(e), store.LogPath(e, true)); await p.WaitForExitAsync();
    Assert(p.ExitCode == 9, "退出码不符"); Assert(File.ReadAllText(store.LogPath(e)).Contains("two words"), "批处理参数错误");
});
await Check("一次性脚本成功 / 失败状态及退出码", async () =>
{
    foreach (int code in new[] { 0, 6 })
    {
        var e = new TaskEntry { Kind = TaskKind.Script, Path = Environment.ProcessPath!, WorkingDir = root, Arguments = ["--sleep", "250", code.ToString()] };
        store.Config.Entries.Add(e); await engine.StartAsync(e); await Until(() => engine.Status(e.Id).ExitCode == code);
        Assert(engine.Status(e.Id).Status == (code == 0 ? RunStatus.Completed : RunStatus.Failed), "脚本状态错误");
    }
});
await Check("服务就绪、避免重复启动、重新识别、停止与重启", async () =>
{
    var e = Service(FreePort()); store.Config.Entries.Add(e);
    try
    {
        Assert(await engine.StartAsync(e), engine.Status(e.Id).Detail); var pid = engine.Status(e.Id).Pid;
        Assert(await engine.StartAsync(e), "重复请求失败"); Assert(engine.Status(e.Id).Pid == pid, "重复启动进程");
        var reloaded = new DataStore(store.Root); using var second = new TaskEngine(reloaded);
        Assert((await second.InspectAsync(e)).Status == RunStatus.Running, "重新识别失败");
        await engine.RestartAsync(e); Assert(engine.Status(e.Id).Status == RunStatus.Running && engine.Status(e.Id).Pid != pid, "重启失败");
    }
    finally { await engine.StopAsync(e); }
    Assert(!ProcessInspector.Listeners().ContainsKey(e.Port), "停止后端口仍监听");
});
await Check("端口冲突不会停止无关进程", async () =>
{
    var port = FreePort(); var foreign = Service(port);
    using var p = NativeProcess.Launch(foreign, store.LogPath(foreign), store.LogPath(foreign, true));
    try
    {
        await Until(() => ProcessInspector.Listeners().ContainsKey(port));
        var e = new TaskEntry { Path = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", Port = port, CheckUrl = foreign.CheckUrl, Arguments = ["-NoProfile", "-Command", "exit 0"] };
        Assert(!await engine.StartAsync(e), "冲突未阻止启动"); Assert(engine.Status(e.Id).Status == RunStatus.Occupied, "未显示冲突");
        Assert(!await engine.StopAsync(e) && !p.HasExited, "错误停止无关进程");
    }
    finally { if (!p.HasExited) { p.Kill(true); await p.WaitForExitAsync(); } }
});
await Check("前置服务就绪后启动依赖任务", async () =>
{
    var parent = Service(FreePort(), "前置"); var child = Service(FreePort(), "依赖"); child.DependsOn = parent.Id;
    store.Config.Entries.Add(parent); store.Config.Entries.Add(child);
    try { Assert(await engine.StartAsync(child), "依赖启动失败"); Assert(engine.Status(parent.Id).Status == RunStatus.Running, "前置未运行"); }
    finally { await engine.StopAsync(child); await engine.StopAsync(parent); }
});
await Check("超时与依赖失败不阻塞独立任务，取消只影响未启动任务", async () =>
{
    var bad = Service(FreePort(), "缓慢服务"); bad.Arguments = ["--sleep", "30000", "0"]; bad.WaitSeconds = 1;
    var child = Service(FreePort(), "应跳过"); child.DependsOn = bad.Id; child.AutoStart = true;
    var good = new TaskEntry { Name = "独立任务", Kind = TaskKind.Script, Path = Environment.ProcessPath!, Arguments = ["--sleep", "250", "0"], AutoStart = true };
    store.Config.Entries.AddRange([bad, child, good]);
    try
    {
        await engine.StartBatchAsync(); await Until(() => engine.Status(good.Id).Status == RunStatus.Completed);
        Assert(engine.Status(bad.Id).Status == RunStatus.Failed && engine.Status(child.Id).Status == RunStatus.Skipped, "失败没有正确传播");
        await engine.RefreshAsync(); Assert(engine.Status(bad.Id).Status == RunStatus.Failed, "刷新覆盖了超时原因");
        await engine.StopAsync(bad); bad.WaitSeconds = 3;
        var run = engine.StartBatchAsync(); await Task.Delay(100); engine.CancelPending(); await run;
        Assert(engine.Status(child.Id).Status == RunStatus.Skipped, "依赖任务未取消");
    }
    finally { await engine.StopAsync(bad); await engine.StopAsync(child); good.AutoStart = false; child.AutoStart = false; }
});
await Check("父程序退出后服务继续且日志持续写入", async () =>
{
    var port = FreePort(); var psi = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
    foreach (var a in new[] { "--spawn-and-exit", root, port.ToString() }) psi.ArgumentList.Add(a);
    using var parent = Process.Start(psi)!; await parent.WaitForExitAsync();
    using var record = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "detached.json"))); int pid = record.RootElement.GetProperty("Pid").GetInt32();
    using var process = Process.GetProcessById(pid);
    try
    {
        await Until(() => ProcessInspector.Listeners().ContainsKey(port));
        var path = Path.Combine(root, "detached.out.log"); long before = new FileInfo(path).Length; await Task.Delay(800);
        Assert(!process.HasExited && new FileInfo(path).Length > before, "父程序退出中断服务或日志");
    }
    finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
});
TaskEntry Network(ProbeServer direct, ProbeServer proxy, int seconds = 8) => new() { Name = "网络测试", Kind = TaskKind.Network, Url = "http://127.0.0.1/", DirectProbeUrl = direct.Url, DirectExpectedContent = "OK", ProxyUrl = proxy.Url, ProxyProbeUrl = "http://target.invalid/", WaitSeconds = seconds };
await Check("桌面就绪：进程存在仍须等待端口", async () =>
{
    var e = new TaskEntry { Name = "桌面就绪", Kind = TaskKind.Desktop, Path = Environment.ProcessPath!, WaitForReady = true, Port = FreePort() };
    var snapshot = new MachineSnapshot(new() { [Environment.ProcessId] = new(Environment.ProcessId, 0, e.Path, e.Path) }, new());
    Assert((await engine.InspectAsync(e, snapshot)).Status == RunStatus.Starting, "仅进程存在即错误就绪");
    snapshot.Listeners[e.Port] = [Environment.ProcessId];
    Assert((await engine.InspectAsync(e, snapshot)).Status == RunStatus.Running, "进程和端口未就绪");
    snapshot.Processes.Clear(); Assert((await engine.InspectAsync(e, snapshot)).Status != RunStatus.Running, "只有端口即错误就绪");
});
await Check("网络链：直连预期正文和代理响应分别判断", async () =>
{
    using var direct = new ProbeServer(); using var proxy = new ProbeServer(); var e = Network(direct, proxy);
    var check = await NetworkReadiness.CheckAsync(e, default); Assert(check.Ready && proxy.Count > 0, "代理未经过显式代理请求");
    direct.Body = "portal"; check = await NetworkReadiness.CheckAsync(e, default); Assert(!check.Ready && !check.DirectReady, "认证劫持正文被误判成功");
    direct.Body = "OK"; proxy.Code = 403; check = await NetworkReadiness.CheckAsync(e, default); Assert(check.Ready && check.Detail.Contains("限制"), "可达与账号认证未区分");
    proxy.Code = 407; Assert(!(await NetworkReadiness.CheckAsync(e, default)).Ready, "代理认证失败被判成功");
    proxy.Code = 503; Assert(!(await NetworkReadiness.CheckAsync(e, default)).Ready, "目标故障未报告");
    proxy.Dispose(); check = await NetworkReadiness.CheckAsync(e, default); Assert(!check.Ready && check.DirectReady, "代理连接失败未区分");
});
await Check("网络链：认证入口一次、按钮识别失败后继续等待并超时", async () =>
{
    using var direct = new ProbeServer { Body = "portal" }; using var proxy = new ProbeServer();
    var data = new DataStore(Path.Combine(root, "network-login")); using var runner = new TaskEngine(data); var e = Network(direct, proxy, 4);
    int clicks = 0; bool manual = false;
    runner.PortalLogin = (_, _) => { clicks++; return Task.FromResult("请手动点击登录"); };
    runner.StatusChanged += (_, s) => manual |= s.Detail.Contains("请手动点击登录");
    Assert(!await runner.StartAsync(e), "离线不应成功"); Assert(clicks == 1 && manual, "认证尝试次数或手动提示错误");
    Assert(runner.Status(e.Id).Detail.Contains("超时"), "未解释超时");
    direct.Body = "OK"; Assert(await runner.StartAsync(e) && clicks == 1, "重试已连通仍打开认证页");
});
await Check("网络链：倒计时断网重置且成功后才启动", async () =>
{
    using var direct = new ProbeServer(); using var proxy = new ProbeServer();
    var data = new DataStore(Path.Combine(root, "network-delay")); using var runner = new TaskEngine(data); var network = Network(direct, proxy, 22);
    int portals = 0; runner.PortalLogin = (_, _) => { portals++; return Task.FromResult("请手动点击登录"); };
    var child = new TaskEntry { Name = "延迟脚本", Kind = TaskKind.Script, Path = Environment.ProcessPath!, Arguments = ["--sleep", "50", "0"], DependsOn = network.Id, StartDelaySeconds = 4 };
    data.Config.Entries.AddRange([network, child]); bool interrupted = false;
    runner.StatusChanged += (id, s) =>
    {
        if (id != child.Id || interrupted || !s.Detail.StartsWith("倒计时")) return;
        interrupted = true; direct.Body = "portal";
        _ = Task.Run(async () => { await Task.Delay(4200); direct.Body = "OK"; });
    };
    var watch = Stopwatch.StartNew(); Assert(await runner.StartAsync(child), "恢复后启动失败");
    Assert(interrupted && watch.Elapsed.TotalSeconds >= 10 && portals == 1, "断网没有重新计时或认证入口重复打开");
    await Until(() => runner.Status(child.Id).Status == RunStatus.Completed);
});
await Check("网络链：同轮失败依赖只执行一次、独立任务执行", async () =>
{
    using var direct = new ProbeServer { Body = "portal" }; using var proxy = new ProbeServer();
    var data = new DataStore(Path.Combine(root, "network-round")); using var runner = new TaskEngine(data); var network = Network(direct, proxy, 1); network.AutoStart = true;
    TaskEntry Child(string name) => new() { Name = name, Kind = TaskKind.Script, Path = Environment.ProcessPath!, Arguments = ["--sleep", "50", "0"], AutoStart = true, DependsOn = network.Id };
    var a = Child("甲"); var b = Child("乙"); var independent = Child("独立"); independent.DependsOn = null;
    data.Config.Entries.AddRange([network, a, b, independent]); int attempts = 0; runner.PortalLogin = (_, _) => { attempts++; return Task.FromResult("请手动点击登录"); };
    var round = runner.StartBatchAsync(); var joined = runner.StartBatchAsync();
    Assert(ReferenceEquals(round, joined), "同时执行未等待同一轮完成");
    await joined; Assert(attempts == 1 && runner.Status(a.Id).Status == RunStatus.Skipped && runner.Status(b.Id).Status == RunStatus.Skipped, "失败依赖被重复执行");
    await Until(() => runner.Status(independent.Id).Status == RunStatus.Completed);
    await runner.StartBatchAsync(); Assert(attempts == 2, "重试没有创建新轮次");
});
await Check("网络链：取消网络等待不启动后续程序", async () =>
{
    using var direct = new ProbeServer { Body = "portal" }; using var proxy = new ProbeServer();
    var data = new DataStore(Path.Combine(root, "network-cancel")); using var runner = new TaskEngine(data); var network = Network(direct, proxy); network.AutoStart = true;
    var child = new TaskEntry { Name = "取消后续", Kind = TaskKind.Script, Path = Environment.ProcessPath!, Arguments = ["--sleep", "50", "0"], AutoStart = true, DependsOn = network.Id };
    data.Config.Entries.AddRange([network, child]); runner.PortalLogin = (_, _) => Task.FromResult("请手动点击登录");
    var run = runner.StartBatchAsync(); await Task.Delay(150); runner.CancelPending(); await run;
    Assert(runner.Status(child.Id).Status == RunStatus.Skipped && !data.State.Runs.GetValueOrDefault(child.Id)!.Pid.Equals(Environment.ProcessId), "取消后仍启动");
});
await Check("网络链：登录通知在同一 Windows 会话内跨实例去重，手动仍可执行", async () =>
{
    var data = new DataStore(Path.Combine(root, "login-session")); using var runner = new TaskEngine(data);
    int rounds = 0; runner.Activity += x => { if (x == "正在执行自启清单…") rounds++; };
    var login = new LoginExecution(data, runner);
    Assert(await login.ReceiveAsync("test", ["--autostart", "--tray"]), "首次未执行");
    Assert(!await login.ReceiveAsync("test", ["--autostart"]) && rounds == 1, "重复通知未去重");
    var reload = new DataStore(data.Root); using var another = new TaskEngine(reload);
    Assert(!await new LoginExecution(reload, another).ReceiveAsync("test-restart", ["--autostart"]), "重启后重复登录执行");
    await runner.StartBatchAsync(); Assert(rounds == 2 && LoginExecution.Session.Contains(':'), "手动执行受去重限制");
});
File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { Passed = passed, Failed = failed, Cases = reports }, DataStore.Json));
Console.WriteLine($"RESULT {passed} passed, {failed} failed\n{root}");
return failed == 0 ? 0 : 1;

[DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();

sealed class ProbeServer : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new();
    public string Body = "OK";
    public int Code = 200;
    public int Count;
    public string Url { get; }
    public ProbeServer()
    {
        listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        _ = Run();
    }
    private async Task Run()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token); using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
                Interlocked.Increment(ref Count);
                var body = Encoding.UTF8.GetBytes(Body);
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {Code} Fixture\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                await stream.WriteAsync(body, stop.Token);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException) { }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
