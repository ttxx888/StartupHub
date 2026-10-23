using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartupHub.Core;

public sealed record CommandRequest(string[] Args, string DataRoot);
public sealed record CommandResponse([property: JsonPropertyName("ok")] bool Ok, [property: JsonPropertyName("data")] object? Data = null, [property: JsonPropertyName("error")] string? Error = null);
public static class CommandProtocol
{
    public static readonly JsonSerializerOptions Json = new(DataStore.Json) { WriteIndented = false };
    public static string PipeName(bool customData) => "StartupHub-" + System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value + (customData ? "-preview" : "");
}
public sealed class CommandApi(DataStore store, TaskEngine engine)
{
    public event Action? ConfigurationChanged;
    public static readonly string Help = """
启动台 CLI
用法：startuphub-cli [--data 数据目录] [--json] <命令>
首次连接时自动在托盘打开启动台，不执行自启清单。

tasks list                         列出任务及实时状态
tasks show <ID或名称>                查看完整配置
tasks add --file task.json           添加任务
tasks update <ID或名称> --file patch.json  合并修改字段
tasks copy <ID或名称> [--name 名称]    复制任务（默认关闭自启）
tasks delete <ID或名称>              从清单删除，保留文件和运行进程
tasks move <ID或名称> up|down         调整顺序
tasks move <ID或名称> before|after <目标ID或名称>
tasks group <ID或名称> <分类>        移入分类末尾
tasks autostart <ID或名称> on|off     设置登录自启
run start <ID或名称> [--background]   启动任务；background 不额外打开页面
run stop|restart|open <ID或名称>      停止 / 重启 / 打开页面
run auto|cancel|status               执行清单 / 取消待启动 / 刷新状态
logs show <ID或名称>                 查看日志
groups list|add <名称>
groups rename <旧名称> <新名称>
groups delete <名称>                 仅删除空分类
groups move <名称> before|after <目标分类>  调整侧栏分类顺序
config show|export <文件>
config import|import-legacy <文件>    导入配置，先备份当前配置
config settings --refresh 秒 --wait 秒
discover [--details]                发现网页及 HTML；details 显示跳过原因
startup status|enable|disable        当前用户登录启动入口
ui state|show|hide|exit               界面状态 / 显示 / 收进托盘 / 退出
ui select <ID或名称>                 选择任务
ui search <文本>                     筛选任务
ui details show|hide|toggle          展开 / 收起 / 切换详情
ui sidebar show|hide|toggle          展开 / 收起 / 切换侧栏
ui theme system|light|dark           切换外观主题并保存偏好
ui snapshot --output 文件.png [--view main|editor|settings|groups|discover]
            [--width 1240 --height 780 --scale 1.0] [--task ID] [--details show|hide] [--sidebar show|hide] [--theme system|light|dark]
                                    导出应用自身渲染，不操作鼠标键盘

--help                              查看帮助
输出：--json 返回 {ok,data,error}；退出码 0 成功，1 操作失败，2 参数错误。
""";
    private TaskEntry Find(string value)
    {
        var results = store.Config.Entries.Where(e => e.Id.ToString().Equals(value, StringComparison.OrdinalIgnoreCase) || e.Name.Equals(value, StringComparison.OrdinalIgnoreCase)).ToArray();
        return results.Length == 1 ? results[0] : throw new InvalidOperationException(results.Length == 0 ? "找不到任务：" + value : "任务名称重复，请使用 ID。");
    }
    public TaskEntry FindEntry(string value) => Find(value);
    public static string? Option(string[] args, string key) { int i = Array.IndexOf(args, key); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
    public async Task<CommandResponse> ExecuteAsync(string[] args)
    {
        try
        {
            if (args.Length == 0 || args.Contains("--help")) return new(true, Help);
            string group = args[0], verb = args.ElementAtOrDefault(1) ?? "list";
            string Required(int i) => args.ElementAtOrDefault(i) ?? throw new ArgumentException("缺少参数，使用 --help 查看用法。");
            object? data;
            if (group == "tasks" && verb == "list" || group == "run" && verb == "status")
            {
                await engine.RefreshAsync();
                data = store.Config.Entries.Select(e => new { e.Id, e.Name, e.Group, Kind = e.Kind.ToString(), e.AutoStart, Status = engine.Status(e.Id), Missing = TaskEngine.Missing(e) }).ToArray();
            }
            else if (group == "tasks" && verb == "show") data = Find(Required(2));
            else if (group == "tasks")
            {
                if (engine.BatchRunning) throw new InvalidOperationException("自启清单执行中，请完成后修改配置。");
                var config = JsonSerializer.Deserialize<HubConfig>(JsonSerializer.Serialize(store.Config, DataStore.Json), DataStore.Json)!;
                if (verb == "add")
                {
                    var file = Option(args, "--file") ?? throw new ArgumentException("请指定 --file。");
                    var entry = JsonSerializer.Deserialize<TaskEntry>(File.ReadAllText(file), DataStore.Json) ?? throw new InvalidDataException("任务配置为空。");
                    config.Entries.Add(entry); data = entry;
                }
                else
                {
                    var target = Find(Required(2)); var e = config.Entries.Single(x => x.Id == target.Id); int index = config.Entries.IndexOf(e);
                    switch (verb)
                    {
                        case "update":
                            var file = Option(args, "--file") ?? throw new ArgumentException("请指定 --file。");
                            var original = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(e, DataStore.Json))!.AsObject();
                            var patch = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!.AsObject();
                            foreach (var item in patch)
                            {
                                var key = original.Select(x => x.Key).FirstOrDefault(k => k.Equals(item.Key, StringComparison.OrdinalIgnoreCase)) ?? throw new ArgumentException("未知任务字段：" + item.Key);
                                if (key == "Id") throw new ArgumentException("任务 ID 不可修改。");
                                original[key] = item.Value?.DeepClone();
                            }
                            e = original.Deserialize<TaskEntry>(DataStore.Json)!; config.Entries[index] = e; break;
                        case "copy": e = e.Copy(); e.Id = Guid.NewGuid(); e.Name = Option(args, "--name") ?? e.Name + "（副本）"; e.AutoStart = false; config.Entries.Add(e); break;
                        case "delete": config.Entries.RemoveAt(index); break;
                        case "autostart": e.AutoStart = Required(3) switch { "on" => true, "off" => false, _ => throw new ArgumentException("请使用 on 或 off。") }; break;
                        case "move":
                            if (Required(3) is "before" or "after") { TaskPlacement.Reorder(config, e.Id, Find(Required(4)).Id, Required(3) == "after"); break; }
                            int offset = Required(3) switch { "up" => -1, "down" => 1, _ => throw new ArgumentException("请使用 up 或 down。") };
                            int destination = Math.Clamp(index + offset, 0, config.Entries.Count - 1); config.Entries.RemoveAt(index); config.Entries.Insert(destination, e); break;
                        case "group": TaskPlacement.ChangeGroup(config, e.Id, Required(3)); break;
                        default: throw new ArgumentException("未知任务命令。");
                    }
                    data = e;
                }
                store.ReplaceConfig(config); ConfigurationChanged?.Invoke();
            }
            else if (group == "run")
            {
                if (verb == "auto") { await engine.StartBatchAsync(); data = "自启清单执行完成"; }
                else if (verb == "cancel") { engine.CancelPending(); data = "已取消待启动任务"; }
                else
                {
                    var e = Find(Required(2));
                    switch (verb)
                    {
                        case "start": if (!await engine.StartAsync(e, args.Contains("--background"), default, args.Contains("--background"))) return new(false, engine.Status(e.Id), engine.Status(e.Id).Detail); break;
                        case "stop": await engine.StopAsync(e); break;
                        case "restart": await engine.RestartAsync(e); if (engine.Status(e.Id).Status != RunStatus.Running && engine.Status(e.Id).Status != RunStatus.Completed) return new(false, engine.Status(e.Id), engine.Status(e.Id).Detail); break;
                        case "open": engine.OpenPage(e); break;
                        default: throw new ArgumentException("未知运行命令。");
                    }
                    data = engine.Status(e.Id);
                }
            }
            else if (group == "logs" && verb == "show") data = engine.ReadLogs(Find(Required(2)));
            else if (group == "groups")
            {
                if (verb == "list") return new(true, store.Config.Groups);
                if (engine.BatchRunning) throw new InvalidOperationException("自启清单执行中，暂不能修改分类。");
                string name = Required(2);
                if (verb == "add") { if (name.Length == 0 || name == "全部任务" || store.Config.Groups.Contains(name)) throw new ArgumentException("分类名称无效或已存在。"); store.Config.Groups.Add(name); }
                else if (verb == "rename")
                {
                    string next = Required(3); int index = store.Config.Groups.IndexOf(name);
                    if (index < 0 || next.Length == 0 || next == "全部任务" || store.Config.Groups.Contains(next)) throw new ArgumentException("分类不存在或新名称无效。");
                    store.Config.Groups[index] = next; foreach (var e in store.Config.Entries.Where(x => x.Group == name)) e.Group = next;
                }
                else if (verb == "delete") { if (store.Config.Entries.Any(e => e.Group == name)) throw new InvalidOperationException("请先移动分类中的任务。"); store.Config.Groups.Remove(name); }
                else if (verb == "move") { string direction = Required(3); if (direction is not ("before" or "after")) throw new ArgumentException("请使用 before 或 after。"); TaskPlacement.ReorderGroup(store.Config, name, Required(4), direction == "after"); }
                else throw new ArgumentException("未知分类命令。");
                store.SaveConfig(); ConfigurationChanged?.Invoke(); data = store.Config.Groups;
            }
            else if (group == "config")
            {
                switch (verb)
                {
                    case "show": data = store.Config; break;
                    case "export": store.Export(Required(2)); data = Required(2); break;
                    case "import": case "import-legacy":
                        if (engine.BatchRunning) throw new InvalidOperationException("自启清单执行中，暂不能导入。");
                        var config = verb == "import" ? DataStore.ReadExport(Required(2)) : DataStore.ImportLegacy(Required(2));
                        store.Export(Path.Combine(store.Root, "config.before-import.json")); store.ReplaceConfig(config); ConfigurationChanged?.Invoke(); data = new { Count = config.Entries.Count }; break;
                    case "settings":
                        int refresh = int.Parse(Option(args, "--refresh") ?? store.Config.RefreshSeconds.ToString());
                        int wait = int.Parse(Option(args, "--wait") ?? store.Config.DefaultWaitSeconds.ToString());
                        if (refresh < 1 || wait < 1) throw new ArgumentException("时间必须为正整数。");
                        store.Config.RefreshSeconds = refresh; store.Config.DefaultWaitSeconds = wait; store.SaveConfig(); data = new { RefreshSeconds = refresh, DefaultWaitSeconds = wait }; break;
                    default: throw new ArgumentException("未知配置命令。");
                }
            }
            else if (group == "discover")
            {
                var pages = await engine.DiscoverAsync();
                data = args.Contains("--details") ? new { Pages=pages, Issues=engine.DiscoveryIssues } : (object)pages;
            }
            else throw new ArgumentException("未知命令，使用 --help 查看用法。");
            return new(true, data);
        }
        catch (Exception ex) { return new(false, null, ex.Message); }
    }
}
