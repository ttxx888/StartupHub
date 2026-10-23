using System.Runtime.InteropServices;
using StartupHub.Core;

internal static class SessionChecks
{
    internal static async Task Run(string root, Action<bool,string> check)
    {
        var store = new DataStore(Path.Combine(root,"session"));
        string path = Path.Combine(root,"打开验证.lnk");
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? link = null;
        try
        {
            link = ((dynamic)shell).CreateShortcut(path);
            ((dynamic)link).TargetPath = Environment.GetEnvironmentVariable("ComSpec")!;
            ((dynamic)link).Arguments = "/c exit 0";
            ((dynamic)link).WindowStyle = 7;
            ((dynamic)link).Save();
        }
        finally { if (link != null) Marshal.FinalReleaseComObject(link); Marshal.FinalReleaseComObject(shell); }
        var file = new TaskEntry { Name="测试文件", Kind=TaskKind.File, Path=path };
        var url = new TaskEntry { Name="测试网页", Kind=TaskKind.Url, Url="https://example.com/" };
        var desktop = new TaskEntry { Name="桌面请求", Kind=TaskKind.Desktop, Path=Environment.ProcessPath! };
        store.Config.Entries.AddRange([file,url,desktop]); store.SaveConfig();
        foreach (var e in store.Config.Entries) store.State.Runs[e.Id] = new() { Status=RunStatus.Opened, Message="历史打开请求" };
        store.SaveState();
        using var engine = new TaskEngine(store);
        var empty = new MachineSnapshot(new(),new());
        check((await engine.InspectAsync(url,empty)).Status==RunStatus.Stopped && (await engine.InspectAsync(file,empty)).Status==RunStatus.Stopped,"历史网页或文件状态被恢复");
        check((await engine.InspectAsync(desktop,empty)).Status==RunStatus.Opened,"桌面程序状态被错误重置");
        var api = new CommandApi(store,engine);
        var response = await api.ExecuteAsync(["run","open",file.Id.ToString()]);
        check(response.Ok && response.Data is StartupHub.Core.TaskStatus { Status:RunStatus.Opened },"CLI 打开未立即更新状态");
        await engine.RefreshAsync();
        check(engine.Status(file.Id).Status==RunStatus.Opened,"刷新清除了会话打开状态");
        check(await engine.StartAsync(file) && engine.Status(file.Id).Status==RunStatus.Opened,"启动入口与直接打开状态不一致");
        var reloaded = new DataStore(store.Root);
        check(reloaded.State.Runs[file.Id].Status==RunStatus.Opened && reloaded.State.Runs[url.Id].Message=="历史打开请求","历史记录未保留");
        using var restarted = new TaskEngine(reloaded);
        await restarted.RefreshAsync();
        check(restarted.Status(file.Id).Status==RunStatus.Stopped && restarted.Status(url.Id).Status==RunStatus.Stopped,"新会话恢复了已打开状态");
        var bad = new TaskEntry { Name="无效文件", Kind=TaskKind.File, Path=Path.Combine(root,"missing-file.txt") };
        check(!await engine.StartAsync(bad) && engine.Status(bad.Id).Status==RunStatus.Missing,"无效文件被标记为已打开");
        check((await engine.InspectAsync(bad,empty)).Status==RunStatus.Missing,"入口缺失未优先显示");
        for (int attempt = 0; ; attempt++)
        {
            try { File.Delete(path); break; }
            catch (IOException) when (attempt < 10) { await Task.Delay(200); }
        }
        check((await engine.InspectAsync(file,empty)).Status==RunStatus.Missing,"本次打开过的入口缺失未显示");
    }
}
