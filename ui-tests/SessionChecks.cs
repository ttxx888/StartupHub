using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using StartupHub.App;
using StartupHub.Core;

internal static class SessionChecks
{
    private static void Await(Task task)
    {
        if(!task.IsCompleted) {
            var frame=new DispatcherFrame();
            task.ContinueWith(_=>Application.Current.Dispatcher.BeginInvoke(()=>frame.Continue=false),TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    public static void Run(MainWindow window, MainViewModel vm, DataStore store, TaskEngine engine, Action<bool,string> check)
    {
        string path = Path.Combine(store.Root,"打开验证.lnk");
        object shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        object? shortcut = null;
        try
        {
            shortcut = ((dynamic)shell).CreateShortcut(path);
            ((dynamic)shortcut).TargetPath = Environment.GetEnvironmentVariable("ComSpec")!;
            ((dynamic)shortcut).Arguments = "/c exit 0";
            ((dynamic)shortcut).WindowStyle = 7;
            ((dynamic)shortcut).Save();
        }
        finally { if (shortcut != null) Marshal.FinalReleaseComObject(shortcut); Marshal.FinalReleaseComObject(shell); }
        var file = new TaskEntry { Name="测试文件", Kind=TaskKind.File, Path=path };
        store.Config.Entries.Add(file); store.SaveConfig(); vm.Reload();
        vm.Selected = vm.Rows.Single(x=>x.Entry.Id==file.Id);
        void Pump() => window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
        engine.OpenPage(file); Pump();
        check(vm.Selected.Status.Status==RunStatus.Opened && vm.DetailFields.Any(x=>x.Label=="当前状态" && x.Value==engine.Status(file.Id).Detail),"打开后列表与详情立即同步");
        window.ShowFromTray(); window.Close(); Pump();
        check(!window.IsVisible && engine.Status(file.Id).Status==RunStatus.Opened,"关闭窗口收进托盘保留已打开");
        window.ShowFromTray(); Pump();
        check(window.IsVisible && vm.Selected.Status.Status==RunStatus.Opened,"托盘恢复保留已打开和选择");
        Await(engine.RefreshAsync()); Pump();
        check(vm.Selected.Status.Status==RunStatus.Opened && vm.DetailText.Contains("已交给默认应用打开"),"普通刷新保留列表与详情状态");
        var restored = new DataStore(store.Root);
        using var fresh = new TaskEngine(restored);
        Await(fresh.RefreshAsync());
        var freshVm = new MainViewModel(restored,fresh);
        freshVm.Selected = freshVm.Rows.Single(x=>x.Entry.Id==file.Id);
        check(freshVm.Selected.Status.Status==RunStatus.Stopped && freshVm.DetailFields.Any(x=>x.Value=="可直接打开"),"新会话列表和详情回到可打开状态");
        check(restored.State.Runs[file.Id].Status==RunStatus.Opened,"新会话保留历史打开记录");
    }
}
