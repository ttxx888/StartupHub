using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartupHub.Core;

public sealed class DataStore
{
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() }, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public string Root { get; }
    public string Logs => System.IO.Path.Combine(Root, "logs");
    public HubConfig Config { get; private set; }
    public HubState State { get; private set; }
    private readonly object gate = new();
    public object SyncRoot => gate;
    public DataStore(string root)
    {
        Root = root;
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Logs);
        Config = Read<HubConfig>("config.json") ?? new();
        State = Read<HubState>("state.json") ?? new();
        Validate(Config);
    }
    private T? Read<T>(string name) => File.Exists(System.IO.Path.Combine(Root, name)) ? JsonSerializer.Deserialize<T>(File.ReadAllText(System.IO.Path.Combine(Root, name)), Json) : default;
    public void SaveConfig() { Validate(Config); Write("config.json", Config); }
    public void SaveState() => Write("state.json", State);
    private void Write<T>(string name, T value)
    {
        lock (gate)
        {
            var path = System.IO.Path.Combine(Root, name);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
            File.Move(path + ".tmp", path, true);
        }
    }
    public void ReplaceConfig(HubConfig config) { Validate(config); Config = config; SaveConfig(); }
    public string LogPath(TaskEntry entry, bool error = false) => System.IO.Path.Combine(Logs, entry.Id + (error ? ".err.log" : ".out.log"));
    public void Export(string path) => File.WriteAllText(path, JsonSerializer.Serialize(Config, Json));
    public static HubConfig ReadExport(string path)
    {
        var config = JsonSerializer.Deserialize<HubConfig>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("配置文件为空。");
        Validate(config);
        return config;
    }
    public static void Validate(HubConfig config)
    {
        if (config.Version != 1) throw new InvalidDataException("不支持此配置版本。");
        if (config.RefreshSeconds < 1 || config.DefaultWaitSeconds < 1) throw new InvalidDataException("刷新和等待时间必须大于零。");
        if (config.Entries.Select(x => x.Id).Distinct().Count() != config.Entries.Count) throw new InvalidDataException("任务 ID 重复。");
        foreach (var e in config.Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Name)) throw new InvalidDataException("任务名称不能为空。");
            if (string.IsNullOrWhiteSpace(e.Group) || e.Group == "全部任务") throw new InvalidDataException("请填写有效的任务分类。");
            if (e.Kind is not (TaskKind.Url or TaskKind.Network) && !(e.Kind == TaskKind.Desktop && e.AppUserModelId.Length > 0) && string.IsNullOrWhiteSpace(e.Path)) throw new InvalidDataException($"{e.Name} 缺少程序或文件路径。");
            if (e.Kind == TaskKind.Url && string.IsNullOrWhiteSpace(e.Url)) throw new InvalidDataException($"{e.Name} 缺少网页地址。");
            foreach (var address in new[] { e.Url, e.CheckUrl, e.DirectProbeUrl, e.ProxyUrl, e.ProxyProbeUrl }.Where(x => !string.IsNullOrEmpty(x)))
                if (!Uri.TryCreate(address, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https")) throw new InvalidDataException($"{e.Name} 的网页地址无效。");
            if (e.Port < 0 || e.Port > 65535 || e.WaitSeconds < 1) throw new InvalidDataException($"{e.Name} 的端口或等待时间无效。");
            if (e.StartDelaySeconds < 0 || e.StartDelaySeconds > 3600) throw new InvalidDataException("启动延迟应在 0 至 3600 秒之间。");
            if (e.Kind == TaskKind.Network && e.WaitSeconds > 300) throw new InvalidDataException("网络等待上限不能超过 300 秒。");
            if (e.Kind == TaskKind.Network && (e.Url.Length == 0 || e.ProxyUrl.Length == 0 || e.ProxyProbeUrl.Length == 0)) throw new InvalidDataException("网络任务需要认证页、代理和目标检查地址。");
            if (!config.Groups.Contains(e.Group)) config.Groups.Add(e.Group);
            var visited = new HashSet<Guid> { e.Id };
            var dep = e.DependsOn;
            while (dep.HasValue)
            {
                if (!visited.Add(dep.Value)) throw new InvalidDataException($"{e.Name} 存在循环依赖。");
                var parent = config.Entries.FirstOrDefault(x => x.Id == dep.Value) ?? throw new InvalidDataException($"{e.Name} 的前置任务不存在。");
                if (parent.Kind is not (TaskKind.Service or TaskKind.Network) && !(parent.Kind == TaskKind.Desktop && parent.WaitForReady)) throw new InvalidDataException("前置任务必须是服务、网络检查或启用就绪检查的桌面程序。");
                dep = parent.DependsOn;
            }
        }
    }
    public static HubConfig ImportLegacy(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var config = new HubConfig();
        if (root.TryGetProperty("Settings", out var settings))
        {
            config.RefreshSeconds = Number(settings, "RefreshSeconds", 8);
            config.DefaultWaitSeconds = Number(settings, "DefaultWaitSeconds", 20);
        }
        config.Groups = root.GetProperty("Groups").EnumerateArray().Select(x => Text(x, "Name")).ToList();
        var auto = new HashSet<string>();
        var statePath = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, "launcher.state.json");
        if (File.Exists(statePath))
        {
            using var state = JsonDocument.Parse(File.ReadAllText(statePath));
            if (state.RootElement.TryGetProperty("AutoStart", out var a)) foreach (var x in a.EnumerateArray()) auto.Add(x.GetString()!);
        }
        var dependencies = new Dictionary<Guid, string>();
        foreach (var old in root.GetProperty("Entries").EnumerateArray())
        {
            var kind = Text(old, "Kind");
            var e = new TaskEntry
            {
                Name = Text(old, "Name"), Group = Text(old, "Group"), Path = Text(old, "Path"), Script = Text(old, "Script"),
                Kind = kind switch { "file" => TaskKind.File, "url" => TaskKind.Url, "desktop" => TaskKind.Desktop, _ => TaskKind.Service },
                Arguments = old.TryGetProperty("Arguments", out var args) ? args.EnumerateArray().Select(x => x.GetString()!).ToList() : [],
                WorkingDir = Text(old, "WorkingDir"), Port = Number(old, "Port", 0), Url = Text(old, "Url"), CheckUrl = Text(old, "CheckUrl"), Info = Text(old, "Info"),
                WaitSeconds = Number(old, "WaitSeconds", config.DefaultWaitSeconds), AutoStart = auto.Contains(Text(old, "Name")), VisibleConsole = kind is "cmd" or "desktop",
                OpensOwnBrowser = kind == "dsh",
                SuppressOutput = kind == "dsh",
                HealthCodes = old.TryGetProperty("HealthCodes", out var codes) ? codes.EnumerateArray().Select(x => x.GetInt32()).ToList() : [200, 204, 301, 302, 401, 403, 404]
            };
            if (e.Name == "FreeLLMAPI")
            {
                e.ManagedPidFile = System.IO.Path.Combine(e.WorkingDir, "data", "freellmapi.pid");
                e.ProcessMatch = "server/dist/index.js";
                e.ExternalLogs = [System.IO.Path.Combine(e.WorkingDir, "logs", "server.out.log"), System.IO.Path.Combine(e.WorkingDir, "logs", "server.err.log")];
            }
            var dep = Text(old, "DependsOn");
            if (dep.Length > 0) dependencies[e.Id] = dep;
            config.Entries.Add(e);
        }
        foreach (var e in config.Entries)
            if (dependencies.TryGetValue(e.Id, out var name)) e.DependsOn = config.Entries.FirstOrDefault(x => x.Name == name)?.Id ?? throw new InvalidDataException($"前置任务不存在：{name}");
        Validate(config);
        return config;
    }
    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
    private static int Number(JsonElement e, string name, int fallback) => e.TryGetProperty(name, out var p) && p.TryGetInt32(out var n) ? n : fallback;
}
