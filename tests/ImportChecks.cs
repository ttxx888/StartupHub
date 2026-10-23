using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text.Json;
using StartupHub.Core;

internal static class ImportChecks
{
    internal static void Run(string root,Action<bool,string> check)
    {
        string link=Path.Combine(root,"带参数程序.lnk"),doc=Path.Combine(root,"文档.txt"),docLink=Path.Combine(root,"文档快捷方式.lnk"),fresh=Path.Combine(root,"新文件.txt"),url=Path.Combine(root,"网址.url"),broken=Path.Combine(root,"失效.lnk");
        File.WriteAllText(doc,"document");File.WriteAllText(fresh,"fresh");File.WriteAllText(url,"[InternetShortcut]\nURL=https://example.com/tool\n");
        WriteLink(link,Environment.ProcessPath!,"--sample \"中文 参数\" \"\"",root);WriteLink(docLink,doc,"","");WriteLink(broken,Path.Combine(root,"gone.exe"),"","");
        var metadata=WindowsShortcut.Read(link);
        check(metadata.Target==Environment.ProcessPath && metadata.Arguments.SequenceEqual(new[] {"--sample","中文 参数",""}) && metadata.WorkingDirectory==root,"快捷方式参数或工作目录未保留");
        check(TaskEntrance.Parse(docLink).Kind==TaskKind.File,"文档快捷方式被当成桌面程序");
        var known=new TaskEntry {Name="已有程序",Kind=TaskKind.Desktop,Path=Environment.ProcessPath!,Arguments=["--sample","中文 参数",""],WorkingDir=root};
        var source=new HubConfig {Entries=[known,new() {Name="已有文档",Kind=TaskKind.File,Path=doc},new() {Name="已有网址",Kind=TaskKind.Url,Url="https://EXAMPLE.COM/tool"}]};
        string before=JsonSerializer.Serialize(source,DataStore.Json);
        var rows=TaskImport.Prepare([link,docLink,url,fresh,fresh,broken,Path.Combine(root,"missing.txt")],source,"开发网页");
        check(rows.Count==7 && rows.Count(x=>x.Issue.Length==0)==1,"重复与无效项过滤不正确");
        check(rows.Take(3).All(x=>x.Issue.StartsWith("重复入口")) && rows[4].Issue.StartsWith("重复入口"),"现有入口或本次拖入的重复项未识别");
        var result=TaskImport.Append(source,rows.Where(x=>x.Issue.Length==0).Select(x=>x.Entry!));
        check(result.Added.Count==1 && result.Added[0].Name=="新文件" && !result.Added[0].AutoStart && result.Added[0].Group=="开发网页","导入默认值或添加范围不正确");
        check(JsonSerializer.Serialize(source,DataStore.Json)==before && File.Exists(link) && File.Exists(fresh),"准备或添加修改了原配置或移动了源文件");
        var target=new DataStore(Path.Combine(root,"import"));target.ReplaceConfig(result.Config);
        check(new DataStore(target.Root).Config.Entries.Any(x=>x.Id==result.Added[0].Id),"导入结果无法重读");
        var shortcutEntry=TaskImport.Prepare([link],new HubConfig(),"常用服务")[0].Entry!;
        check(shortcutEntry.Path==link && shortcutEntry.Arguments.Count==0 && shortcutEntry.WorkingDir==root,"快捷方式未保留原入口或重复添加了参数");
        var processEntry=TaskImport.ProcessEntry(shortcutEntry);
        check(processEntry.Path==metadata.Target && processEntry.Arguments.SequenceEqual(metadata.Arguments),"进程识别未使用真实目标及参数");
        using var engine=new TaskEngine(target);
        var snapshot=new MachineSnapshot(new() {{12345,new(12345,0,metadata.Target,"app.exe --sample \"中文 参数\" \"\"")}},new());
        check(engine.InspectAsync(shortcutEntry,snapshot).GetAwaiter().GetResult().Status==RunStatus.Running,"未能识别已运行的快捷方式目标");
        var wrong=new MachineSnapshot(new() {{12345,new(12345,0,metadata.Target,"app.exe --other")}},new());
        check(engine.InspectAsync(shortcutEntry,wrong).GetAwaiter().GetResult().Status==RunStatus.Stopped,"错误参数的同名进程被复用");
        check(TaskEngine.Missing(new() {Kind=TaskKind.Desktop,Path=broken})!=null,"目标失效的快捷方式没有显示入口缺失");
        var a=new TaskEntry {Kind=TaskKind.Url,Name="甲",Url="https://example.com/Tool"};
        check(TaskImport.Append(source,[a]).Added.Count==1,"不同网页路径被错误合并");
        string launchLink=Path.Combine(root,"启动验证.lnk"),output=Path.Combine(root,"shortcut-output.json");
        WriteLink(launchLink,Environment.ProcessPath!,"--import-emit "+NativeProcess.Quote(output)+" \"中文 参数\" \"\"",root);
        var launchEntry=TaskImport.Prepare([launchLink],new HubConfig(),"常用服务")[0].Entry!;
        using var launched=Process.Start(new ProcessStartInfo(launchEntry.Path) {UseShellExecute=true,WorkingDirectory=launchEntry.WorkingDir,WindowStyle=ProcessWindowStyle.Hidden})!;
        var live=engine.InspectAsync(launchEntry).GetAwaiter().GetResult();
        check(live.Status==RunStatus.Running && live.Pid==launched.Id,"实际启动后未识别快捷方式的目标进程");
        check(launched.WaitForExit(10000) && launched.ExitCode==0 && File.Exists(output),"快捷方式启动验证程序未正常完成");
        using var emitted=JsonDocument.Parse(File.ReadAllText(output));
        check(emitted.RootElement.GetProperty("Args").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(new[] {"中文 参数",""}) && string.Equals(emitted.RootElement.GetProperty("Cwd").GetString(),root,StringComparison.OrdinalIgnoreCase),"实际启动参数被重复传递或工作目录丢失");
    }
    private static void WriteLink(string path,string target,string arguments,string directory)
    {
        object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;object? link=null;
        try {link=((dynamic)shell).CreateShortcut(path);((dynamic)link).TargetPath=target;((dynamic)link).Arguments=arguments;((dynamic)link).WorkingDirectory=directory;((dynamic)link).Save();}
        finally {if(link!=null) Marshal.FinalReleaseComObject(link);Marshal.FinalReleaseComObject(shell);}
    }
}
