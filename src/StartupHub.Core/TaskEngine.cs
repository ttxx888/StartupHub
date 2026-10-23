using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace StartupHub.Core;

public sealed class TaskEngine : IDisposable
{
    private readonly DataStore store;
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(2) };
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> locks = new();
    private readonly ConcurrentDictionary<Guid, TaskStatus> statuses = new();
    private readonly ConcurrentDictionary<Guid, TaskStatus> openedThisSession = new();
    private readonly ConcurrentDictionary<Guid, byte> starting = new();
    private CancellationTokenSource? batchCancellation;
    private int batchRunning;
    private int refreshing;
    private readonly object batchGate = new();
    private Task? activeBatch;
    private readonly object discoveryGate = new();
    private Task<List<DiscoveredPage>>? activeDiscovery;
    public IReadOnlyList<DiscoveryIssue> DiscoveryIssues { get; private set; } = [];
    public string? DiscoveryCatalogPath { get; set; }
    public bool BatchRunning => Volatile.Read(ref batchRunning) != 0;
    public event Action<Guid, TaskStatus>? StatusChanged;
    public event Action<string>? Activity;
    public Func<TaskEntry, CancellationToken, Task<string>>? PortalLogin { get; set; }
    public TaskEngine(DataStore store) { this.store = store; }
    public TaskStatus Status(Guid id) => statuses.TryGetValue(id, out var status) ? status : new(RunStatus.Stopped);
    private ProcessRecord? Record(Guid id) { lock (store.SyncRoot) return store.State.Runs.GetValueOrDefault(id); }
    private void Set(TaskEntry entry, TaskStatus status, bool persist = false)
    {
        if (statuses.TryGetValue(entry.Id, out var old) && old == status) return;
        statuses[entry.Id] = status;
        bool phaseChanged = old == null || old.Status != status.Status;
        if (entry.Kind == TaskKind.Network || entry.StartDelaySeconds > 0 && phaseChanged)
        {
            try
            {
                var eventsPath = Path.Combine(store.Logs, entry.Id + ".events.log");
                NativeProcess.Rotate(eventsPath);
                File.AppendAllText(eventsPath, $"{DateTimeOffset.Now:O} [{status.Text}] {status.Detail}{Environment.NewLine}");
            }
            catch (IOException) { }
        }
        if (persist)
        {
            lock (store.SyncRoot)
            {
                if (!store.State.Runs.TryGetValue(entry.Id, out var record)) store.State.Runs[entry.Id] = record = new();
                record.Status = status.Status; record.Message = status.Detail; record.ExitCode = status.ExitCode; record.UpdatedAt = DateTimeOffset.Now;
                store.SaveState();
            }
        }
        StatusChanged?.Invoke(entry.Id, status);
    }
    public static string? Missing(TaskEntry entry)
    {
        if (entry.Kind is TaskKind.Url or TaskKind.Network || entry.Kind == TaskKind.Desktop && entry.AppUserModelId.Length > 0) return null;
        if (entry.Path.Length == 0 || (!File.Exists(entry.Path) && !Directory.Exists(entry.Path))) return "找不到程序或文件：" + entry.Path;
        if (File.Exists(entry.Path) && Path.GetExtension(entry.Path).Equals(".lnk",StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var target = WindowsShortcut.Read(entry.Path).Target;
                if (!File.Exists(target) && !Directory.Exists(target)) return "快捷方式的目标不存在：" + target;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or IOException) { return "无法读取快捷方式：" + ex.Message; }
        }
        if (entry.Script.Length > 0 && !File.Exists(entry.Script)) return "找不到脚本：" + entry.Script;
        if (entry.WorkingDir.Length > 0 && !Directory.Exists(entry.WorkingDir)) return "找不到工作目录：" + entry.WorkingDir;
        return null;
    }
    public async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref refreshing, 1) != 0) return;
        try
        {
            var entries = store.Config.Entries.ToArray();
            var snapshot = await Task.Run(ProcessInspector.Snapshot);
            await Task.WhenAll(entries.Select(async e =>
            {
                if (starting.ContainsKey(e.Id)) return;
                try { Set(e, await InspectAsync(e, snapshot)); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Set(e, new(RunStatus.Failed, "状态检查失败：" + ex.Message)); }
            }));
        }
        finally { Volatile.Write(ref refreshing, 0); }
    }
    public async Task<TaskStatus> InspectAsync(TaskEntry e, MachineSnapshot? snapshot = null, CancellationToken cancellation = default)
    {
        var record = Record(e.Id);
        if (e.Kind == TaskKind.Network) return record == null ? new(RunStatus.Stopped, "尚未检查网络") : new(record.Status, record.Message);
        if (e.Kind is TaskKind.Url or TaskKind.File)
        {
            var missing = Missing(e);
            if (missing != null) return new(RunStatus.Missing, missing);
            return openedThisSession.GetValueOrDefault(e.Id) ?? new(RunStatus.Stopped, "可直接打开");
        }
        snapshot ??= await Task.Run(ProcessInspector.Snapshot);
        if (e.Kind == TaskKind.Service && e.Port > 0 && snapshot.Listeners.TryGetValue(e.Port, out var owners))
        {
            var pid = owners.FirstOrDefault(p => ProcessInspector.Belongs(e, p, record, snapshot));
            if (pid == 0) return new(RunStatus.Occupied, $"端口 {e.Port} 正被其他进程占用（PID {string.Join(", ", owners)}）。");
            RememberService(e.Id, pid);
            if (e.OpensOwnBrowser || await Healthy(e, cancellation)) return new(RunStatus.Running, $"服务已就绪 · 端口 {e.Port}", pid);
            return record?.Status == RunStatus.Failed ? new(RunStatus.Failed, record.Message, pid) : new(RunStatus.Starting, "进程已监听，网页检查尚未通过。", pid);
        }
        if (Missing(e) is { } problem) return new(RunStatus.Missing, problem);
        var processEntry = e.Kind == TaskKind.Desktop && e.AppUserModelId.Length == 0 ? TaskImport.ProcessEntry(e) : e;
        int owned = snapshot.Processes.Keys.FirstOrDefault(pid => e.AppUserModelId.Length > 0 ? PackagedApp.Matches(pid, e.AppUserModelId) && (e.ProcessMatch.Length == 0 || snapshot.Processes[pid].Path.EndsWith(e.ProcessMatch, StringComparison.OrdinalIgnoreCase)) : ProcessInspector.Belongs(processEntry, pid, record, snapshot));
        if (owned > 0)
        {
            if (e.Kind == TaskKind.Desktop && e.WaitForReady && e.Port > 0 && !snapshot.Listeners.ContainsKey(e.Port)) return new(RunStatus.Starting, $"程序运行中，等待代理端口 {e.Port}。", owned);
            if (e.Kind == TaskKind.Desktop && e.WaitForReady && !await Healthy(e, cancellation)) return new(RunStatus.Starting, "程序运行中，等待就绪检查通过。", owned);
            if (e.Kind == TaskKind.Service && e.Port > 0) return record?.Status == RunStatus.Failed ? new(RunStatus.Failed, record.Message, owned) : new(RunStatus.Starting, "进程运行中，等待端口监听。", owned);
            if (e.Kind == TaskKind.Service && !await Healthy(e)) return record?.Status == RunStatus.Failed ? new(RunStatus.Failed, record.Message, owned) : new(RunStatus.Starting, "进程运行中，等待网页就绪。", owned);
            return new(RunStatus.Running, "进程运行中", owned);
        }
        if (record != null && record.Status is RunStatus.Failed or RunStatus.Completed or RunStatus.Skipped or RunStatus.Opened) return new(record.Status, record.Message, null, record.ExitCode);
        return new(RunStatus.Stopped, record is { Status: RunStatus.Running } && e.Kind == TaskKind.Script ? "进程已结束，退出码未记录。" : "尚未运行");
    }
    private void RememberService(Guid id, int pid)
    {
        lock (store.SyncRoot)
        {
            if (!store.State.Runs.TryGetValue(id, out var record)) store.State.Runs[id] = record = new();
            var ticks = ProcessInspector.StartTicks(pid);
            if (record.ServicePid == pid && record.ServiceStartTicks == ticks) return;
            record.ServicePid = pid; record.ServiceStartTicks = ticks;
            store.SaveState();
        }
    }
    private async Task<bool> Healthy(TaskEntry entry, CancellationToken cancellation = default)
    {
        if (entry.CheckUrl.Length == 0) return true;
        try
        {
            using var response = await http.GetAsync(entry.CheckUrl, HttpCompletionOption.ResponseHeadersRead, cancellation);
            return entry.HealthCodes.Contains((int)response.StatusCode);
        }
        catch (HttpRequestException) { return false; }
        catch (TaskCanceledException) { return false; }
    }
    public Task<bool> StartAsync(TaskEntry e, bool automatic = false, CancellationToken cancellation = default, bool dependencyOnly = false)
        => ExecuteOnce(e, automatic, cancellation, dependencyOnly, new());
    private sealed class ExecutionRound
    {
        public ConcurrentDictionary<Guid, Lazy<Task<bool>>> Runs { get; } = new();
        public ConcurrentDictionary<Guid, byte> Portals { get; } = new();
        public ConcurrentDictionary<Guid, long> NetworkStarted { get; } = new();
    }
    private Task<bool> ExecuteOnce(TaskEntry e, bool automatic, CancellationToken cancellation, bool dependencyOnly, ExecutionRound round)
        => round.Runs.GetOrAdd(e.Id, _ => new Lazy<Task<bool>>(() => StartCoreAsync(e, automatic, cancellation, dependencyOnly, round))).Value;
    private async Task<bool> StartCoreAsync(TaskEntry e, bool automatic, CancellationToken cancellation, bool dependencyOnly, ExecutionRound round)
    {
        var gate = locks.GetOrAdd(e.Id, _ => new(1, 1));
        try { await gate.WaitAsync(cancellation); }
        catch (OperationCanceledException) { return false; }
        try
        {
            cancellation.ThrowIfCancellationRequested();
            starting[e.Id] = 1;
            if (e.DependsOn is { } parentId)
            {
                var parent = store.Config.Entries.FirstOrDefault(x => x.Id == parentId);
                if (parent == null) { Set(e, new(RunStatus.Failed, "前置任务已被删除，请编辑任务并重新选择依赖。"), true); return false; }
                Set(e, new(RunStatus.Starting, "等待前置任务：" + parent.Name));
                if (!await ExecuteOnce(parent, true, cancellation, true, round))
                { Set(e, new(RunStatus.Skipped, "前置任务未就绪：" + parent.Name + " · " + Status(parent.Id).Detail), true); return false; }
            }
            cancellation.ThrowIfCancellationRequested();
            if (e.Kind == TaskKind.Network) return await WaitNetworkAsync(e, e, 0, round, cancellation);
            if (e.StartDelaySeconds > 0)
            {
                var parent = store.Config.Entries.FirstOrDefault(x => x.Id == e.DependsOn);
                if (parent?.Kind == TaskKind.Network)
                {
                    if (!await WaitNetworkAsync(parent, e, e.StartDelaySeconds, round, cancellation)) return false;
                }
                else for (int left = e.StartDelaySeconds; left > 0; left--)
                { Set(e, new(RunStatus.Starting, $"倒计时 {left} 秒")); await Task.Delay(1000, cancellation); }
            }
            if (e.Kind is TaskKind.Url or TaskKind.File)
            {
                if (Missing(e) is { } missing) { Set(e, new(RunStatus.Missing, missing), true); return false; }
                OpenPage(e);
                return true;
            }
            var before = await InspectAsync(e, cancellation: cancellation);
            if (before.Status == RunStatus.Occupied || before.Status == RunStatus.Missing) { Set(e, before, true); return false; }
            if (before.Status == RunStatus.Running)
            {
                Set(e, before);
                if (!dependencyOnly && ShouldOpen(e, automatic) && !e.OpensOwnBrowser) OpenPage(e);
                return true;
            }
            if (before.Pid == null)
            {
                cancellation.ThrowIfCancellationRequested();
                Set(e, new(RunStatus.Starting, "正在启动…"));
                Process? process;
                if (e.Kind == TaskKind.Desktop) process = e.AppUserModelId.Length > 0 ? PackagedApp.Activate(e.AppUserModelId) : NativeProcess.Open(e.Path, e.Arguments, e.WorkingDir);
                else process = await Task.Run(() => NativeProcess.Launch(e, store.LogPath(e), store.LogPath(e, true)));
                if (process == null) { Set(e, new(RunStatus.Opened, "启动请求已发送给 Windows"), true); return true; }
                lock (store.SyncRoot)
                {
                    store.State.Runs[e.Id] = new() { Pid = process.Id, StartTicks = process.StartTime.ToUniversalTime().Ticks, Status = RunStatus.Starting };
                    store.SaveState();
                }
                if (e.Kind == TaskKind.Script)
                {
                    Set(e, new(RunStatus.Running, "脚本运行中", process.Id), true);
                    _ = ObserveExit(e, process);
                    return true;
                }
                if (e.Kind == TaskKind.Desktop)
                {
                    Set(e, new(RunStatus.Running, "程序已启动", process.Id), true);
                    process.Dispose(); if (!e.WaitForReady) return true;
                }
                else process.Dispose();
            }
            var watch = Stopwatch.StartNew();
            do
            {
                var status = await InspectAsync(e, cancellation: cancellation);
                if (status.Status == RunStatus.Running)
                {
                    Set(e, status, true);
                    if (!dependencyOnly && ShouldOpen(e, automatic) && !e.OpensOwnBrowser) OpenPage(e);
                    return true;
                }
                if (status.Status == RunStatus.Occupied) { Set(e, status, true); return false; }
                await Task.Delay(600);
            } while (watch.Elapsed.TotalSeconds < e.WaitSeconds);
            Set(e, new(RunStatus.Failed, $"等待 {e.WaitSeconds} 秒后仍未就绪，请查看日志。进程可能仍在运行。"), true);
            return false;
        }
        catch (OperationCanceledException) { Set(e, new(RunStatus.Skipped, "已取消尚未开始的任务"), true); return false; }
        catch (Exception ex) { Set(e, new(RunStatus.Failed, ex.Message), true); return false; }
        finally { starting.TryRemove(e.Id, out _); gate.Release(); }
    }
    private async Task<bool> WaitNetworkAsync(TaskEntry network, TaskEntry owner, int stableSeconds, ExecutionRound round, CancellationToken cancellation)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        long began = round.NetworkStarted.GetOrAdd(network.Id, _ => Environment.TickCount64);
        var remaining = TimeSpan.FromMilliseconds(Math.Max(1, network.WaitSeconds * 1000L - (Environment.TickCount64 - began)));
        deadline.CancelAfter(remaining);
        var token = deadline.Token;
        var stable = new Stopwatch(); Task<string>? login = null;
        string reason = "检测普通网络、代理与目标连通"; string loginMessage = "";
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                Set(owner, new(RunStatus.Starting, stable.IsRunning ? (stable.Elapsed.TotalSeconds >= stableSeconds ? "倒计时已满，正在确认网络仍然可用" : $"倒计时：检查网络连续可用（还需 {Math.Max(1, stableSeconds - (int)stable.Elapsed.TotalSeconds)} 秒）") : reason));
                var check = await NetworkReadiness.CheckAsync(network, token); reason = check.Detail;
                if (check.Ready)
                {
                    if (!stable.IsRunning) stable.Start();
                    if (stable.Elapsed.TotalSeconds >= stableSeconds)
                    { Set(owner, new(owner.Kind == TaskKind.Network ? RunStatus.Completed : RunStatus.Starting, reason + (stableSeconds > 0 ? $" · 已连续等待 {stableSeconds} 秒" : "")), true); return true; }
                    reason = $"倒计时：网络连续可用，还需 {Math.Max(1, stableSeconds - (int)stable.Elapsed.TotalSeconds)} 秒";
                }
                else
                {
                    stable.Reset();
                    if (!check.DirectReady && round.Portals.TryAdd(network.Id, 1))
                    {
                        if (PortalLogin != null) login = PortalLogin(network, token);
                        else { NativeProcess.Open(network.Url)?.Dispose(); loginMessage = "请手动点击登录"; }
                    }
                }
                if (login?.IsCompleted == true) { loginMessage = await login; login = null; }
                Set(owner, new(RunStatus.Starting, reason + (loginMessage.Length > 0 ? " · " + loginMessage : "")));
                double interval = stable.IsRunning && stableSeconds > 0 ? Math.Min(3, Math.Max(.1, stableSeconds - stable.Elapsed.TotalSeconds)) : 3;
                await Task.Delay(TimeSpan.FromSeconds(interval), token);
            }
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { Set(owner, new(RunStatus.Failed, $"网络等待超时（{network.WaitSeconds} 秒）：{reason}"), true); return false; }
        finally { deadline.Cancel(); if (login != null) try { await login; } catch (Exception) { } }
    }
    private async Task ObserveExit(TaskEntry e, Process process)
    {
        try
        {
            await process.WaitForExitAsync();
            var code = process.ExitCode;
            Set(e, new(code == 0 ? RunStatus.Completed : RunStatus.Failed, $"脚本已退出 · 退出码 {code}", null, code), true);
        }
        catch (Exception ex) { Set(e, new(RunStatus.Failed, ex.Message), true); }
        finally { process.Dispose(); }
    }
    private static bool ShouldOpen(TaskEntry e, bool automatic) => e.Url.Length > 0 && (!automatic || e.OpenPageOnAutoStart);
    public void OpenPage(TaskEntry e)
    {
        var address = e.Kind == TaskKind.File ? e.Path : e.Url;
        if (address.Length == 0) throw new InvalidOperationException("此任务没有网页地址。");
        NativeProcess.Open(address)?.Dispose();
        if (e.Kind is TaskKind.Url or TaskKind.File)
        {
            var status = new TaskStatus(RunStatus.Opened, "已交给默认应用打开");
            openedThisSession[e.Id] = status;
            Set(e, status, true);
        }
    }
    public Task StartBatchAsync()
    {
        lock (batchGate)
        {
            if (activeBatch is { IsCompleted: false }) { Activity?.Invoke("自启清单正在执行。"); return activeBatch; }
            return activeBatch = RunBatchAsync();
        }
    }
    private async Task RunBatchAsync()
    {
        Volatile.Write(ref batchRunning, 1);
        batchCancellation = new();
        try
        {
            Activity?.Invoke("正在执行自启清单…");
            var tasks = store.Config.Entries.Where(x => x.AutoStart).ToArray();
            var round = new ExecutionRound();
            var results = await Task.WhenAll(tasks.Select(e => ExecuteOnce(e, true, batchCancellation.Token, false, round)));
            Activity?.Invoke($"自启清单完成：成功 {results.Count(x => x)} 项，失败或跳过 {results.Count(x => !x)} 项。");
        }
        finally { Volatile.Write(ref batchRunning, 0); batchCancellation.Dispose(); batchCancellation = null; }
    }
    public void CancelPending()
    {
        var pending = batchCancellation;
        try { pending?.Cancel(); } catch (ObjectDisposedException) { }
        Activity?.Invoke("已取消尚未开始的任务，已启动的进程继续运行。");
    }
    public async Task<bool> StopAsync(TaskEntry e)
    {
        if (e.Kind is TaskKind.Url or TaskKind.File or TaskKind.Desktop or TaskKind.Network) throw new InvalidOperationException("此任务由网页或桌面应用自行关闭；网络等待可取消队列。");
        var gate = locks.GetOrAdd(e.Id, _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
            var snapshot = await Task.Run(ProcessInspector.Snapshot);
            var record = Record(e.Id);
            var candidates = snapshot.Processes.Keys.Where(p => ProcessInspector.Belongs(e, p, record, snapshot)).ToList();
            if (candidates.Count == 0) { Set(e, new(RunStatus.Stopped, "没有可确认属于此任务的运行进程。"), true); return false; }
            foreach (var pid in candidates)
            {
                try { using var process = Process.GetProcessById(pid); process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                catch (TimeoutException) { }
            }
            ProcessInspector.Invalidate();
            Set(e, new(RunStatus.Stopped, "已停止"), true);
            return true;
        }
        finally { gate.Release(); }
    }
    public async Task RestartAsync(TaskEntry e)
    {
        await StopAsync(e);
        if (e.Port > 0)
        {
            var watch = Stopwatch.StartNew();
            while (ProcessInspector.Listeners().ContainsKey(e.Port) && watch.Elapsed.TotalSeconds < 5) await Task.Delay(200);
        }
        await StartAsync(e);
    }
    public Task<List<DiscoveredPage>> DiscoverAsync(IProgress<DiscoveryProgress>? progress = null)
    {
        lock (discoveryGate)
            return activeDiscovery is { IsCompleted: false } ? activeDiscovery : activeDiscovery = DiscoverCoreAsync(progress);
    }
    private async Task<List<DiscoveredPage>> DiscoverCoreAsync(IProgress<DiscoveryProgress>? progress)
    {
        var entries = store.Config.Entries.ToArray();
        var hints = entries;
        var issues = new List<DiscoveryIssue>();
        if (DiscoveryCatalogPath is { } path && !Path.GetFullPath(path).Equals(Path.Combine(store.Root,"config.json"),StringComparison.OrdinalIgnoreCase))
        {
            try { hints = hints.Concat(DataStore.ReadExport(path).Entries).ToArray(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { issues.Add(new(path,"无法读取参考清单："+ex.Message)); }
        }
        var report = await LocalPageDiscovery.ScanReportAsync(await Task.Run(ProcessInspector.Snapshot),entries,progress,hints);
        var files = await Task.Run(()=>LocalHtmlDiscovery.Scan(hints,entries));
        DiscoveryIssues = issues.Concat(report.Issues).Concat(files.Issues).ToArray();
        return report.Pages.Concat(files.Pages).ToList();
    }
    public string ReadLogs(TaskEntry e)
    {
        if (e.SuppressOutput) return "此任务的控制台输出可能包含临时登录链接，已关闭输出记录。";
        var paths = new[] { Path.Combine(store.Logs, e.Id + ".events.log"), store.LogPath(e), store.LogPath(e, true) }.Concat(e.ExternalLogs);
        var result = new StringBuilder();
        foreach (var path in paths.Where(File.Exists))
        {
            result.AppendLine(System.IO.Path.GetFileName(path)).AppendLine(new string('─', 24));
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                bool truncated = stream.Length > 48000;
                if (truncated) stream.Seek(-48000, SeekOrigin.End);
                using var bytes = new MemoryStream(); stream.CopyTo(bytes);
                var data = bytes.ToArray();
                if (truncated)
                {
                    int newline = Array.IndexOf(data, (byte)'\n');
                    if (newline >= 0) data = data[(newline + 1)..];
                }
                string text;
                try { text = new UTF8Encoding(false, true).GetString(data); }
                catch (DecoderFallbackException)
                {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    text = Encoding.GetEncoding((int)GetACP()).GetString(data);
                }
                result.AppendLine(text.TrimStart('\uFEFF')).AppendLine();
            }
            catch (IOException ex) { result.AppendLine(ex.Message); }
        }
        return result.Length > 0 ? result.ToString() : e.VisibleConsole ? "此任务的输出显示在它自己的控制台窗口中。" : "尚无日志。启动任务后可在这里查看输出。";
    }
    public void Dispose() { http.Dispose(); }
    [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern uint GetACP();
}
public record DiscoveredPage(string Name, string Url, int Port, int Pid, string ProcessName = "", TaskKind Kind = TaskKind.Url, string Path = "", string Source = "")
{
    public string Entrance => Kind == TaskKind.File ? Path : Url;
    public string SourceLabel => Kind == TaskKind.File ? "本地 HTML · " + Source : ProcessName;
    public TaskEntry ToEntry(string group) => new() { Name=Name, Group=group, Kind=Kind, Url=Kind==TaskKind.File?"":Url, Path=Path, Port=Port, AutoStart=false, Info=Kind==TaskKind.File?"发现的本地 HTML 文件":"发现的本地网页 · "+ProcessName };
}
