using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartupHub.Core;

public enum TaskKind { Service, Script, Desktop, Url, File, Network }
public enum RunStatus { Stopped, Starting, Running, Opened, Completed, Failed, Missing, Occupied, Skipped }

public sealed class TaskEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "新任务";
    public string Group { get; set; } = "未分类";
    public TaskKind Kind { get; set; } = TaskKind.Service;
    public string Path { get; set; } = "";
    public string Script { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public string WorkingDir { get; set; } = "";
    public string Url { get; set; } = "";
    public string CheckUrl { get; set; } = "";
    public int Port { get; set; }
    public int WaitSeconds { get; set; } = 20;
    public List<int> HealthCodes { get; set; } = [200, 204, 301, 302];
    public Guid? DependsOn { get; set; }
    public bool WaitForReady { get; set; }
    public string AppUserModelId { get; set; } = "";
    public int StartDelaySeconds { get; set; }
    public string DirectProbeUrl { get; set; } = "";
    public string DirectExpectedContent { get; set; } = "";
    public string ProxyUrl { get; set; } = "http://127.0.0.1:7897";
    public string ProxyProbeUrl { get; set; } = "https://chatgpt.com/";
    public bool AutoLogin { get; set; } = true;
    public bool AutoStart { get; set; }
    public bool OpenPageOnAutoStart { get; set; }
    public bool VisibleConsole { get; set; }
    public bool OpensOwnBrowser { get; set; }
    public bool SuppressOutput { get; set; }
    public string Info { get; set; } = "";
    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string ManagedPidFile { get; set; } = "";
    public string ProcessMatch { get; set; } = "";
    public List<string> ExternalLogs { get; set; } = [];
    public TaskEntry Copy() => JsonSerializer.Deserialize<TaskEntry>(JsonSerializer.Serialize(this, DataStore.Json), DataStore.Json)!;
    [JsonIgnore] public string KindText => Kind switch { TaskKind.Network => "网络检查", TaskKind.Service => "本地服务", TaskKind.Script => "一次性脚本", TaskKind.Desktop => "桌面程序", TaskKind.Url => "网址", _ => "本地文件" };
    [JsonIgnore] public string Address => string.IsNullOrWhiteSpace(Url) ? Path : Url;
}

public sealed class HubConfig
{
    public int Version { get; set; } = 1;
    public List<string> Groups { get; set; } = ["常用服务", "开发网页", "直接打开"];
    public List<TaskEntry> Entries { get; set; } = [];
    public int RefreshSeconds { get; set; } = 8;
    public int DefaultWaitSeconds { get; set; } = 20;
}

public sealed class ProcessRecord
{
    public int Pid { get; set; }
    public long StartTicks { get; set; }
    public int? ServicePid { get; set; }
    public long ServiceStartTicks { get; set; }
    public RunStatus Status { get; set; }
    public string Message { get; set; } = "";
    public int? ExitCode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;
}

public sealed class HubState
{
    public string ThemePreference { get; set; } = "System";
    public bool SidebarCollapsed { get; set; }
    public string LastAutoStartSession { get; set; } = "";
    public bool DetailsVisible { get; set; } = true;
    public double Width { get; set; } = 1240;
    public double Height { get; set; } = 780;
    public double? Left { get; set; }
    public double? Top { get; set; }
    public string Group { get; set; } = "全部任务";
    public string Search { get; set; } = "";
    public string Filter { get; set; } = "全部状态";
    public Dictionary<Guid, ProcessRecord> Runs { get; set; } = [];
}

public record TaskStatus(RunStatus Status, string Detail = "", int? Pid = null, int? ExitCode = null)
{
    public string Text => Status switch
    {
        RunStatus.Stopped => "未启动", RunStatus.Starting => "启动中", RunStatus.Running => "运行中",
        RunStatus.Opened => "已打开", RunStatus.Completed => "已完成", RunStatus.Failed => "失败",
        RunStatus.Missing => "入口缺失", RunStatus.Occupied => "端口占用", _ => "已跳过"
    };
}
