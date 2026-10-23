using System.Diagnostics;
using System.Text.Json;
using StartupHub.Core;

internal static class BatchChecks
{
    internal static void Run(string root, Action<bool, string> check)
    {
        var file = Path.Combine(root, "保留文件.html"); File.WriteAllText(file, "<html>fixture</html>");
        var a = new TaskEntry { Name = "甲", Kind = TaskKind.Service, Path = Environment.ProcessPath!, AutoStart = true, Arguments = ["--sample", "中文 参数"], Environment = new() { ["SAMPLE"] = "保留" } };
        var b = new TaskEntry { Name = "乙", Kind = TaskKind.File, Path = file, Group = "开发网页", DependsOn = a.Id };
        var c = new TaskEntry { Name = "丙", Kind = TaskKind.Url, Url = "http://127.0.0.1:1/", Group = "直接打开" };
        var d = new TaskEntry { Name = "丁", Kind = TaskKind.Service, Path = Environment.ProcessPath!, DependsOn = a.Id };
        var e = new TaskEntry { Name = "戊", Kind = TaskKind.Url, Url = "http://127.0.0.1:2/", Group = "开发网页", DependsOn = d.Id };
        var source = new HubConfig { Entries = [a,b,c,d,e] };
        string original = JsonSerializer.Serialize(source, DataStore.Json);
        var moved = TaskBatch.ChangeGroup(source, [d.Id,b.Id,a.Id], "直接打开");
        check(moved.Entries.Select(x => x.Id).SequenceEqual(new[] { c.Id,a.Id,b.Id,d.Id,e.Id }), "移入分类末尾后相对顺序不正确");
        check(moved.Entries.Where(x => new[] {a.Id,b.Id,d.Id}.Contains(x.Id)).All(x => x.Group == "直接打开"), "分类未统一更改");
        check(moved.Entries.Single(x => x.Id == a.Id).Arguments.SequenceEqual(a.Arguments) && moved.Entries.Single(x => x.Id == a.Id).AutoStart && moved.Entries.Single(x => x.Id == a.Id).Environment["SAMPLE"] == "保留", "启动配置发生变化");
        var deleted = TaskBatch.Delete(source, [a.Id,b.Id]);
        check(deleted.Entries.Select(x => x.Id).SequenceEqual(new[] { c.Id,d.Id,e.Id }), "删除范围不正确");
        check(deleted.Entries.Single(x => x.Id == d.Id).DependsOn == null && deleted.Entries.Single(x => x.Id == e.Id).DependsOn == d.Id, "依赖解除范围不正确");
        var store = new DataStore(Path.Combine(root, "batch")); store.ReplaceConfig(deleted);
        check(new DataStore(store.Root).Config.Entries.Single(x => x.Id == d.Id).DependsOn == null, "依赖修改未持久保存");
        check(JsonSerializer.Serialize(source, DataStore.Json) == original, "批量操作修改了原配置");
        check(File.Exists(file) && Process.GetProcessById(Environment.ProcessId).HasExited == false, "项目文件或现有进程被删除");
        bool rejected = false;
        try { TaskBatch.ChangeGroup(source, [a.Id,Guid.NewGuid()], "直接打开"); } catch (ArgumentException) { rejected = true; }
        check(rejected && JsonSerializer.Serialize(source, DataStore.Json) == original, "无效选择产生了部分修改");
        rejected = false;
        try { TaskBatch.ChangeGroup(source, [a.Id], "全部任务"); } catch (ArgumentException) { rejected = true; }
        check(rejected, "允许移入汇总分类");
    }
}
