using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StartupHub.Core;
using TaskStatus = StartupHub.Core.TaskStatus;

namespace StartupHub.App;

public class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class ActionCommand(Func<Task> action, Func<bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? parameter) => can?.Invoke() ?? true;
    public async void Execute(object? parameter)
    {
        try { await action(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "启动台", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
}
public sealed class TaskRow(TaskEntry entry, Action save) : Observable
{
    public TaskEntry Entry { get; } = entry;
    private TaskStatus status = new(RunStatus.Stopped);
    public TaskStatus Status { get => status; set { status = value; Changed(); Changed(nameof(StatusBrush)); } }
    public bool AutoStart { get => Entry.AutoStart; set { if (Entry.AutoStart == value) return; Entry.AutoStart = value; save(); Changed(); } }
    public string Subtitle => Entry.KindText + (Entry.Port > 0 ? " · " + Entry.Port : "") + " · " + (Entry.Url.Length > 0 ? Entry.Url : Entry.Group);
    public string KindGlyph => Entry.Kind switch { TaskKind.Network => "\uE774", TaskKind.Service => "\uE968", TaskKind.Script => "\uE756", TaskKind.Desktop => "\uE7F4", TaskKind.Url => "\uE774", _ => "\uE8A5" };
    public void RefreshPlacement() { Changed(nameof(Entry)); Changed(nameof(Subtitle)); }
    public Brush StatusBrush => (Brush)Application.Current.FindResource(Status.Status switch { RunStatus.Running or RunStatus.Completed or RunStatus.Opened => "SuccessBrush", RunStatus.Failed or RunStatus.Missing or RunStatus.Occupied => "DangerBrush", RunStatus.Starting => "WaitingBrush", _ => "MutedBrush" });
}
public sealed class CategoryItem(string name, int count) : Observable
{
    public string Name { get; } = name;
    public string ShortName => Name == "全部任务" ? "全" : Name[..1].ToUpperInvariant();
    public string Description => $"{Name} · {Count} 项";
    private int itemCount = count;
    public int Count { get => itemCount; set { itemCount = value; Changed(); Changed(nameof(Description)); } }
}
public sealed record DetailField(string Label, string Value);

public sealed class MainViewModel : Observable
{
    private readonly DataStore store;
    private readonly TaskEngine engine;
    public ObservableCollection<TaskRow> Rows { get; } = [];
    public ObservableCollection<string> Groups { get; } = [];
    public ObservableCollection<CategoryItem> CategoryItems { get; } = [];
    public bool IsEmpty => TasksView.IsEmpty;
    public bool HasNoTasks => Rows.Count == 0;
    public bool HasTasks => Rows.Count > 0;
    public string EmptyTitle => HasNoTasks ? "从一个任务开始" : "没有找到匹配的任务";
    public string EmptyDescription => HasNoTasks ? "添加网页、程序或脚本，让每次开始都更从容。" : "试试其他关键词，或清除当前筛选。";
    public bool BatchActive => engine.BatchRunning;
    public bool CanArrange => !engine.BatchRunning;
    private bool discovering;
    public bool IsDiscovering { get => discovering; private set { discovering = value; Changed(); CommandManager.InvalidateRequerySuggested(); } }
    private TaskRow[] selection = [];
    private readonly System.Collections.Generic.Dictionary<Guid, TaskRow> rowIndex = [];
    private readonly DispatcherTimer stateSaveTimer;
    private bool loaded;
    private bool reloading;
    public System.Collections.Generic.IReadOnlyList<TaskRow> SelectedRows => selection;
    public int SelectionCount => selection.Length;
    public bool HasTaskSelection => selection.Length > 0;
    public bool HasSingleSelection => selection.Length == 1;
    public bool HasMultipleSelection => selection.Length > 1;
    public string SelectionSummary => $"已选 {SelectionCount} 项";
    public string DetailTitle => HasMultipleSelection ? SelectionSummary : Selected?.Entry.Name ?? "任务详情";
    public ICommand ClearFiltersCommand { get; }
    public DetailField[] DetailFields
    {
        get
        {
            if (HasMultipleSelection) return [new("所选任务", string.Join("\n", selection.Select(x => x.Entry.Name))), new("批量管理", "移动到分类，或从清单删除所选项目。")];
            if (Selected == null) return [new("任务详情", "选择一项任务，查看启动配置和运行日志。")];
            var e = Selected.Entry;
            var fields = new System.Collections.Generic.List<DetailField>();
            void Add(string label, string value) { if (!string.IsNullOrWhiteSpace(value)) fields.Add(new(label, value)); }
            Add("当前状态", Selected.Status.Detail); Add("说明", e.Info); Add("类型 / 分类", e.KindText + " · " + e.Group);
            Add("网页", e.Url); Add("程序 / 文件", e.Path); Add("脚本", e.Script); Add("工作目录", e.WorkingDir);
            Add("启动参数", string.Join("\n", e.Arguments));
            if (e.DependsOn.HasValue) Add("前置任务", store.Config.Entries.FirstOrDefault(x => x.Id == e.DependsOn)?.Name ?? "");
            if (e.StartDelaySeconds > 0) Add("连续就绪后延迟", e.StartDelaySeconds + " 秒");
            Add("Windows 应用标识", e.AppUserModelId);
            if (e.WaitForReady) Add("桌面就绪", $"进程运行，端口 {e.Port} 可用，最多等待 {e.WaitSeconds} 秒");
            if (e.Kind == TaskKind.Network) { Add("网络阶段上限", e.WaitSeconds + " 秒"); Add("直连检查", e.DirectProbeUrl.Length > 0 ? e.DirectProbeUrl : "Windows NCSI 连接测试"); Add("代理", e.ProxyUrl); Add("目标连通", e.ProxyProbeUrl); Add("认证操作", e.AutoLogin ? "尝试点击一次登录；无法识别则提示手动操作" : "手动登录"); }
            if (e.Kind == TaskKind.Service) { Add("等待就绪", e.WaitSeconds + " 秒"); Add("自动打开页面", e.OpenPageOnAutoStart ? "就绪后打开" : "保持后台运行"); }
            if (e.OpensOwnBrowser) Add("浏览器", "由程序自行打开");
            if (Selected.Status.Pid is { } pid) Add("进程 PID", pid.ToString());
            return fields.ToArray();
        }
    }
    private void UpdatePresentation() { Changed(nameof(IsEmpty)); Changed(nameof(HasNoTasks)); Changed(nameof(HasTasks)); Changed(nameof(EmptyTitle)); Changed(nameof(EmptyDescription)); Changed(nameof(BatchActive)); Changed(nameof(CanArrange)); Changed(nameof(DetailFields)); }
    public string[] Filters { get; } = ["全部状态", "运行中", "未启动", "需要处理", "登录自启"];
    public Choice<string>[] FilterOptions => Filters.Select(value => new Choice<string>(value, value switch
    {
        "需要处理" => "异常或已跳过",
        "登录自启" => "已勾选登录自启",
        _ => value
    })).ToArray();
    public ICollectionView TasksView { get; }
    private string search = "", group = "全部任务", filter = "全部状态", message = "手动打开不会执行自启清单。", logText = "请选择任务查看日志。";
    public string Search { get => search; set { if (search == value) return; search = value; Changed(); FilterChanged(); } }
    public string SelectedGroup { get => group; set { if (reloading) return; value ??= "全部任务"; if (group == value) return; group = value; Changed(); FilterChanged(); } }
    public string StatusFilter { get => filter; set { if (filter == value) return; filter = value; Changed(); FilterChanged(); } }
    public string Message { get => message; set { message = value; Changed(); } }
    public string LogText { get => logText; set { logText = value; Changed(); } }
    public TaskRow? Selected { get => selection.FirstOrDefault(); set => SetSelection(value == null ? [] : [value]); }
    public void SetSelection(System.Collections.Generic.IEnumerable<TaskRow> rows)
    {
        var ids = rows.Select(x => x.Entry.Id).ToHashSet();
        var next = TasksView.Cast<TaskRow>().Where(x => ids.Contains(x.Entry.Id)).ToArray();
        if (selection.SequenceEqual(next)) return;
        selection = next;
        Changed(nameof(SelectedRows)); Changed(nameof(Selected)); Changed(nameof(SelectionCount)); Changed(nameof(HasTaskSelection)); Changed(nameof(HasSingleSelection)); Changed(nameof(HasMultipleSelection));
        Changed(nameof(SelectionSummary)); Changed(nameof(DetailTitle)); Changed(nameof(DetailText)); Changed(nameof(DetailFields));
        RefreshLogs(); CommandManager.InvalidateRequerySuggested();
    }
    private void RefreshTaskView()
    {
        var kept = selection.ToArray();
        TasksView.Refresh();
        SetSelection(kept);
    }
    public string Summary => $"{Rows.Count} 个任务  ·  {Rows.Count(x => x.Status.Status == RunStatus.Running)} 项运行中  ·  {Rows.Count(x => x.AutoStart)} 项登录自启";
    public string DetailText
    {
        get
        {
            if (HasMultipleSelection) return string.Join("\n", selection.Select(x => x.Entry.Name));
            if (Selected == null) return "选择列表中的任务，查看状态和启动配置。";
            var e = Selected.Entry;
            var parent = e.DependsOn.HasValue ? store.Config.Entries.FirstOrDefault(x => x.Id == e.DependsOn)?.Name : null;
            return string.Join("\n\n", new[] { Selected.Status.Detail, e.Info, "类型：" + e.KindText + "\n分类：" + e.Group,
                e.Url.Length > 0 ? "网页\n" + e.Url : null, e.Path.Length > 0 ? "程序 / 文件\n" + e.Path : null,
                e.Script.Length > 0 ? "脚本\n" + e.Script : null, e.WorkingDir.Length > 0 ? "工作目录\n" + e.WorkingDir : null,
                e.Arguments.Count > 0 ? "参数\n" + string.Join("\n", e.Arguments) : null,
                parent != null ? "前置任务：" + parent : null,
                e.Kind == TaskKind.Service ? $"等待就绪：{e.WaitSeconds} 秒\n自动启动后打开网页：{(e.OpenPageOnAutoStart ? "是" : "否")}" : null,
                e.OpensOwnBrowser ? "浏览器由该程序自行打开。" : null,
                Selected.Status.Pid is { } pid ? "PID：" + pid : null }.Where(x => !string.IsNullOrEmpty(x)));
        }
    }
    public ICommand AddCommand { get; }
    public ICommand EditCommand { get; }
    public ICommand CopyCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand MoveSelectionCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand StartAutoCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand OpenPageCommand { get; }
    public ICommand OpenFolderCommand { get; }
    public ICommand OpenAllCommand { get; }
    public ICommand StopAllCommand { get; }
    public ICommand GroupsCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand DiscoverCommand { get; }
    public ICommand LogsCommand { get; }
    public ICommand LogFolderCommand { get; }
    public ICommand MoveToTopCommand { get; }
    public ICommand UpCommand { get; }
    public ICommand DownCommand { get; }
    private ActionCommand Command(Action action, Func<bool>? can = null) => new(() => { action(); return Task.CompletedTask; }, can);
    public MainViewModel(DataStore store, TaskEngine engine)
    {
        this.store = store; this.engine = engine;
        TasksView = CollectionViewSource.GetDefaultView(Rows);
        TasksView.Filter = Matches;
        stateSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        stateSaveTimer.Tick += (_, _) => { stateSaveTimer.Stop(); store.SaveState(); };
        search = store.State.Search; group = store.State.Group; filter = store.State.Filter;
        ClearFiltersCommand = Command(() => { Search = ""; StatusFilter = "全部状态"; SelectedGroup = "全部任务"; });
        AddCommand = Command(() => Edit(null)); EditCommand = Command(() => Edit(Selected!.Entry), HasSelection);
        CopyCommand = Command(Copy, HasSelection); DeleteCommand = Command(Delete, () => SelectionCount > 0 && CanArrange);
        MoveSelectionCommand = Command(MoveSelection, () => SelectionCount > 0 && CanArrange);
        StartCommand = new ActionCommand(async () => { await engine.StartAsync(Selected!.Entry); RefreshLogs(); }, HasSelection);
        StopCommand = new ActionCommand(async () => { await engine.StopAsync(Selected!.Entry); }, CanStop);
        RestartCommand = new ActionCommand(async () => { await engine.RestartAsync(Selected!.Entry); }, CanStop);
        StartAutoCommand = new ActionCommand(async () => { var task = engine.StartBatchAsync(); UpdatePresentation(); await task; UpdatePresentation(); }, () => !engine.BatchRunning); CancelCommand = Command(engine.CancelPending);
        RefreshCommand = new ActionCommand(RefreshAsync);
        OpenPageCommand = Command(() => engine.OpenPage(Selected!.Entry), () => HasSelection() && (Selected!.Entry.Url.Length > 0 || Selected.Entry.Kind == TaskKind.File));
        OpenFolderCommand = Command(OpenFolder, HasSelection);
        OpenAllCommand = new ActionCommand(OpenAll); StopAllCommand = new ActionCommand(StopAll);
        GroupsCommand = Command(() => { new GroupsWindow(store) { Owner = Application.Current.MainWindow }.ShowDialog(); Reload(); });
        SettingsCommand = Command(() => { new SettingsWindow(store) { Owner = Application.Current.MainWindow }.ShowDialog(); Reload(); });
        DiscoverCommand = new ActionCommand(Discover, () => !IsDiscovering);
        LogsCommand = Command(RefreshLogs); LogFolderCommand = Command(() => NativeProcess.Open(store.Logs)?.Dispose());
        MoveToTopCommand = Command(MoveToTop, () => HasSelection() && CanArrange && store.Config.Entries.FirstOrDefault()?.Id != Selected?.Entry.Id);
        UpCommand = Command(() => Move(-1), HasSelection); DownCommand = Command(() => Move(1), HasSelection);
        engine.StatusChanged += (id, status) => Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!rowIndex.TryGetValue(id, out var row)) return;
            var previous = row.Status.Status;
            row.Status = status; Changed(nameof(Summary)); Changed(nameof(DetailText));
            if (filter is "运行中" or "未启动" or "需要处理" && StatusPassesFilter(previous) != StatusPassesFilter(status.Status)) RefreshTaskView();
            UpdatePresentation();
        });
        engine.Activity += text => Application.Current.Dispatcher.BeginInvoke(() => { Message = text; UpdatePresentation(); CommandManager.InvalidateRequerySuggested(); });
        Reload();
    }
    private bool HasSelection() => SelectionCount == 1;
    private bool CanStop() => HasSelection() && (Selected?.Entry.Kind is TaskKind.Service or TaskKind.Script);
    private bool Matches(object item)
    {
        if (item is not TaskRow row) return false;
        if (group != "全部任务" && row.Entry.Group != group) return false;
        if (!string.IsNullOrWhiteSpace(search) && !(row.Entry.Name + " " + row.Entry.Info + " " + row.Entry.Address + " " + row.Entry.Script).Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        if (filter == "登录自启") return row.AutoStart;
        return StatusPassesFilter(row.Status.Status);
    }
    private bool StatusPassesFilter(RunStatus status) => filter switch
    {
        "运行中" => status is RunStatus.Running or RunStatus.Starting,
        "未启动" => status == RunStatus.Stopped,
        "需要处理" => status is RunStatus.Failed or RunStatus.Missing or RunStatus.Occupied or RunStatus.Skipped,
        _ => true
    };
    private void FilterChanged()
    {
        TasksView.Refresh();
        SetSelection([]);
        UpdatePresentation();
        store.State.Search = search; store.State.Group = group; store.State.Filter = filter;
        QueueStateSave();
    }
    private void QueueStateSave() { stateSaveTimer.Stop(); stateSaveTimer.Start(); }
    public void FlushPendingStateSave() { if (stateSaveTimer.IsEnabled) { stateSaveTimer.Stop(); store.SaveState(); } }
    public void Reload()
    {
        var ids = selection.Select(x => x.Entry.Id).ToHashSet();
        reloading = true;
        Rows.Clear(); rowIndex.Clear(); Groups.Clear(); CategoryItems.Clear(); Groups.Add("全部任务");
        foreach (var g in store.Config.Groups) Groups.Add(g);
        if (!Groups.Contains(group)) group = "全部任务";
        foreach (var entry in store.Config.Entries)
        {
            var row = new TaskRow(entry, () => { store.SaveConfig(); Changed(nameof(Summary)); if (filter == "登录自启") RefreshTaskView(); }) { Status = engine.Status(entry.Id) };
            Rows.Add(row); rowIndex[entry.Id] = row;
        }
        foreach (var name in Groups) CategoryItems.Add(new(name, name == "全部任务" ? Rows.Count : Rows.Count(x => x.Entry.Group == name)));
        reloading = false;
        Changed(nameof(SelectedGroup)); Changed(nameof(Summary)); TasksView.Refresh();
        SetSelection(!loaded ? TasksView.Cast<TaskRow>().Take(1) : Rows.Where(x => ids.Contains(x.Entry.Id)));
        loaded = true;
        UpdatePresentation();
    }
    public async Task RefreshAsync()
    {
        await engine.RefreshAsync(); RefreshLogs(); UpdatePresentation();
    }
    public async Task StartRowAsync(TaskRow row)
    {
        await engine.StartAsync(row.Entry); RefreshLogs();
    }
    public void ImportFiles(string[] paths)
    {
        if (!CanArrange) { Message="清单执行中，请完成后再添加任务。";return; }
        if (paths.Length == 0) return;
        string group = store.Config.Groups.Contains(SelectedGroup) ? SelectedGroup : store.Config.Groups.FirstOrDefault() ?? "未分类";
        var candidates = TaskImport.Prepare(paths,store.Config,group);
        System.Collections.Generic.IReadOnlyList<TaskEntry> imported;
        if (paths.Length == 1)
        {
            var candidate=candidates[0];
            if (candidate.Entry == null || candidate.Issue.Length > 0) {Message="未添加："+candidate.Issue;return;}
            var editor=new EntryEditor(store.Config,candidate.Entry,group,creating:true) {Owner=Application.Current.MainWindow};
            if(editor.ShowDialog()!=true) return;
            imported=[editor.Result];
        }
        else
        {
            var preview=new ImportTasksWindow(candidates,store.Config.Groups,group) {Owner=Application.Current.MainWindow};
            if(preview.ShowDialog()!=true) return;
            imported=preview.Imported;
        }
        if (!CanArrange) {Message="清单执行中，暂未添加任务。";return;}
        var result=TaskImport.Append(store.Config,imported);
        if(result.Added.Count>0)
        {
            store.ReplaceConfig(result.Config); Reload();
            var ids=result.Added.Select(x=>x.Id).ToHashSet();
            if(Rows.Where(x=>ids.Contains(x.Entry.Id)).Any(x=>!TasksView.Contains(x))) {Search="";StatusFilter="全部状态";SelectedGroup=result.Added[0].Group;}
            SetSelection(Rows.Where(x=>ids.Contains(x.Entry.Id)));
        }
        int skipped=candidates.Count(x=>x.Issue.Length>0)+result.Skipped.Count;
        Message=$"已添加 {result.Added.Count} 项"+(skipped>0?$"，跳过 {skipped} 项。":"。");
    }
    private void Edit(TaskEntry? entry)
    {
        if (engine.BatchRunning) { Message = "清单执行中，请完成或取消待启动任务后再编辑配置。"; return; }
        var window = new EntryEditor(store.Config, entry, SelectedGroup) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        if (entry != null) store.Config.Entries[store.Config.Entries.IndexOf(entry)] = window.Result;
        else store.Config.Entries.Add(window.Result);
        store.SaveConfig(); Reload(); Selected = Rows.First(x => x.Entry.Id == window.Result.Id);
        Message = "已保存：" + window.Result.Name;
        _ = RefreshAsync();
    }
    private void Copy()
    {
        var copy = Selected!.Entry.Copy(); copy.Id = Guid.NewGuid(); copy.Name += "（副本）"; copy.AutoStart = false;
        var window = new EntryEditor(store.Config, copy) { Owner = Application.Current.MainWindow };
        if (window.ShowDialog() != true) return;
        store.Config.Entries.Add(window.Result); store.SaveConfig(); Reload(); Selected = Rows.First(x => x.Entry.Id == copy.Id);
    }
    private void Delete()
    {
        if (engine.BatchRunning) { Message = "清单执行中，暂不能删除任务。"; return; }
        var entries = selection.Select(x => x.Entry).ToArray();
        var ids = entries.Select(x => x.Id).ToHashSet();
        var children = store.Config.Entries.Where(x => !ids.Contains(x.Id) && x.DependsOn is { } parent && ids.Contains(parent)).ToArray();
        var dialog = new DeleteTasksWindow(entries, children) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        if (!CanArrange) { Message = "清单执行中，暂不能删除任务。"; return; }
        store.ReplaceConfig(TaskBatch.Delete(store.Config, ids)); Reload();
        Message = $"已从清单删除 {entries.Length} 项，解除 {children.Length} 项前置依赖。";
    }
    private void MoveSelection()
    {
        var ids = selection.Select(x => x.Entry.Id).ToArray();
        var dialog = new MoveTasksWindow(store.Config.Groups, ids.Length) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() == true) MoveTasksToGroup(ids, dialog.Destination);
    }
    public bool MoveTasksToGroup(System.Collections.Generic.IEnumerable<Guid> ids, string destination)
    {
        if (!CanArrange) { Message = "自启清单执行中，请完成后再移动分类。"; return false; }
        var selectedIds = ids.ToHashSet();
        var candidate = TaskBatch.ChangeGroup(store.Config, selectedIds, destination);
        if (store.Config.Entries.Where(x => selectedIds.Contains(x.Id)).All(x => x.Group == destination)) return false;
        store.ReplaceConfig(candidate); Reload();
        Message = $"已将 {selectedIds.Count} 项移入“{destination}”。";
        return true;
    }
    private void MoveToTop()
    {
        if (Selected is not { } selected || store.Config.Entries.FirstOrDefault() is not { } first) return;
        if (ReorderTask(selected.Entry.Id, first.Id, false)) Message = "已置顶：" + selected.Entry.Name;
    }
    private void Move(int offset)
    {
        var entry = Selected!.Entry; var i = store.Config.Entries.IndexOf(entry); var next = i + offset;
        if (next < 0 || next >= store.Config.Entries.Count) return;
        ReorderTask(entry.Id, store.Config.Entries[next].Id, offset > 0);
    }
    public bool ReorderTask(Guid id, Guid target, bool after)
    {
        if (!CanArrange) { Message = "自启清单执行中，请完成后再调整任务。"; return false; }
        if (!TaskPlacement.Reorder(store.Config, id, target, after)) return false;
        SavePlacement(id);
        Message = "任务顺序已保存。";
        return true;
    }
    public bool MoveTaskToGroup(Guid id, string destination)
    {
        if (!CanArrange) { Message = "自启清单执行中，请完成后再移动分类。"; return false; }
        if (!TaskPlacement.ChangeGroup(store.Config, id, destination)) return false;
        SavePlacement(id);
        Message = "已移入“" + destination + "”。";
        return true;
    }
    public bool ReorderCategory(string source, string target, bool after)
    {
        if (!CanArrange) { Message = "自启清单执行中，请完成后再调整分类。"; return false; }
        if (!TaskPlacement.ReorderGroup(store.Config, source, target, after)) return false;
        store.SaveConfig();
        int index = store.Config.Groups.IndexOf(source) + 1;
        CategoryItems.Move(CategoryItems.IndexOf(CategoryItems.First(x => x.Name == source)), index);
        Groups.Move(Groups.IndexOf(source), index);
        Message = "分类顺序已保存。";
        return true;
    }
    private void SavePlacement(Guid id)
    {
        store.SaveConfig();
        var moved = Rows.First(x => x.Entry.Id == id);
        moved.RefreshPlacement();
        int index = store.Config.Entries.FindIndex(x => x.Id == id);
        Rows.Move(Rows.IndexOf(moved), index);
        foreach (var category in CategoryItems) category.Count = category.Name == "全部任务" ? Rows.Count : Rows.Count(x => x.Entry.Group == category.Name);
        RefreshTaskView();
        Selected = TasksView.Contains(moved) ? moved : TasksView.Cast<TaskRow>().FirstOrDefault();
        UpdatePresentation();
    }
    private void OpenFolder()
    {
        var e = Selected!.Entry; var folder = e.WorkingDir.Length > 0 ? e.WorkingDir : Path.GetDirectoryName(e.Path);
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) throw new DirectoryNotFoundException("此任务没有可用的本地目录。");
        NativeProcess.Open(folder)?.Dispose();
    }
    private async Task OpenAll()
    {
        await RefreshAsync();
        var entries = Rows.Where(x => x.Entry.Kind == TaskKind.File && TaskEngine.Missing(x.Entry) == null || x.Entry.Url.Length > 0 && (x.Entry.Kind == TaskKind.Url || x.Status.Status == RunStatus.Running)).Select(x => x.Entry).ToArray();
        if (entries.Length == 0) { Message = "清单中没有可打开的网页或文件。"; return; }
        if (!DialogExperience.Confirm(Application.Current.MainWindow, "打开全部网页和文件", $"将使用默认应用打开清单中 {entries.Length} 个网页或文件，包含其他分类和筛选隐藏的任务。", "全部打开")) return;
        foreach (var e in entries) engine.OpenPage(e);
        Message = $"已打开 {entries.Length} 个网页或文件。";
    }
    private async Task StopAll()
    {
        var entries = store.Config.Entries.Where(x => x.Kind is TaskKind.Service or TaskKind.Script).ToArray();
        int count = 0;
        foreach (var e in entries) if (await engine.StopAsync(e)) count++;
        Message = $"已停止 {count} 项受管理任务。";
    }
    private async Task Discover()
    {
        if (IsDiscovering) return;
        IsDiscovering = true;
        try
        {
            Message = "正在扫描本机网页…";
            var pages = await engine.DiscoverAsync(new Progress<DiscoveryProgress>(p => Message = $"正在扫描本机网页 · {p.Completed}/{p.Total}"));
            var window = new DiscoverWindow(pages, store.Config.Groups, SelectedGroup,engine.DiscoveryIssues,store.Config.Entries) { Owner = Application.Current.MainWindow };
            if (window.ShowDialog() == true)
            {
                if (!CanArrange) throw new InvalidOperationException("自启清单执行中，请完成后添加任务。");
                var selected = LocalPageDiscovery.ExcludeExisting(window.SelectedPages, store.Config.Entries);
                var result = TaskImport.Append(store.Config, selected.Select(page => page.ToEntry(window.Group)));
                if (result.Added.Count > 0)
                {
                    store.ReplaceConfig(result.Config); Reload();
                    var ids = result.Added.Select(x=>x.Id).ToHashSet();
                    if (Rows.Where(x=>ids.Contains(x.Entry.Id)).Any(x=>!TasksView.Contains(x))) { Search=""; StatusFilter="全部状态"; SelectedGroup=window.Group; }
                    SetSelection(Rows.Where(x=>ids.Contains(x.Entry.Id)));
                }
                Message = $"已添加 {result.Added.Count} 个网页 · 跳过 {window.SelectedPages.Count - result.Added.Count} 个已有入口。";
            }
            else Message = pages.Count == 0 ? "暂未发现可添加的本地网页。" : $"发现 {pages.Count} 个本地网页。";
        }
        finally { IsDiscovering = false; }
    }
    private void RefreshLogs() => LogText = HasMultipleSelection ? "多选时不合并日志，请选择单个任务查看。" : Selected == null ? "请选择任务查看日志。" : engine.ReadLogs(Selected.Entry);
}
