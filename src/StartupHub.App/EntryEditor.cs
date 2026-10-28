using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;
using StartupHub.Core;

namespace StartupHub.App;

public sealed class ArgumentRow { public string Value { get; set; } = ""; }
public sealed class EnvironmentRow { public string Name { get; set; } = ""; public string Value { get; set; } = ""; }
public sealed record Choice<T>(T Value, string Label);

public sealed class EntryEditor : Window
{
    public TaskEntry Result { get; private set; }
    private readonly HubConfig config;
    private readonly TextBox name = new(), path = new(), script = new(), cwd = new(), url = new(), info = new(), port = new(), checkUrl = new(), wait = new(), codes = new(), pidFile = new(), match = new(), externalLogs = new();
    private readonly ComboBox kind = new(), group = new() { IsEditable = true };
    private readonly ListBox dependency = new() { MaxHeight = 220 };
    private Guid? selectedDependency;
    private DialogLayout layout = null!;
    private string initialForm = "";
    private readonly TextBlock dependencySummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock delayLabel = new();
    private readonly CheckBox auto = new() { Content = "登录 Windows 时自动执行" }, open = new() { Content = "服务就绪后自动打开网页" }, visible = new() { Content = "显示控制台窗口（需要输入时启用）" }, ownBrowser = new() { Content = "程序自行打开浏览器" };
    private readonly CheckBox suppress = new() { Content = "控制台输出含临时登录链接，不写入日志" };
    private readonly TextBox appId = new(), delay = new(), directProbe = new(), expected = new(), proxy = new(), target = new();
    private readonly CheckBox ready = new() { Content = "等待桌面程序与端口就绪后再执行后续任务" }, autoLogin = new() { Content = "使用 Edge 已保存凭据，尝试点击一次登录" };
    private readonly ObservableCollection<ArgumentRow> arguments;
    private readonly ObservableCollection<EnvironmentRow> environment;
    private readonly DataGrid argumentGrid, environmentGrid;
    private readonly TextBlock validation = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24, 6, 24, 6), Visibility = Visibility.Collapsed };
    private readonly TextBlock detected = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel typePanel = new(), networkPanel = new(), servicePanel = new(), serviceFields = new(), launchPanel = new(), scriptPanel = new(), desktopPanel = new(), outputPanel = new(), healthPanel = new(), networkAdvanced = new();
    private readonly TextBlock inputLabel = new() { Text = "入口", Margin = new Thickness(0, 16, 0, 6) };
    private bool nameEdited, suggestingName;
    private TaskKind SelectedKind => kind.SelectedValue is TaskKind selected ? selected : TaskEntrance.Parse(path.Text).Kind;

    public EntryEditor(HubConfig config, TaskEntry? entry, string? initialGroup = null, bool creating = false)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        this.config = config;
        Result = entry?.Copy() ?? new() { Kind = TaskKind.File, Name = "", WaitSeconds = config.DefaultWaitSeconds, Group = initialGroup != null && config.Groups.Contains(initialGroup) ? initialGroup : config.Groups.FirstOrDefault() ?? "未分类" };
        Title = entry == null || creating ? "添加任务" : "编辑任务";
        Width = 650; Height = Math.Min(600, SystemParameters.WorkArea.Height - 40); MinWidth = 520; MinHeight = 430; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel(); Content = dock;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(20, 10, 20, 10) };
        var save = new Button { Content = "保存任务", IsDefault = true, Style = (Style)FindResource("PrimaryButton") }; save.Click += (_, _) => Save();
        var cancel = new Button { Content = "取消", IsCancel = true }; buttons.Children.Add(cancel); buttons.Children.Add(save);
        DockPanel.SetDock(buttons, Dock.Bottom); dock.Children.Add(buttons);
        validation.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush"); DockPanel.SetDock(validation, Dock.Bottom); dock.Children.Add(validation);
        var panel = new StackPanel { Margin = new Thickness(24, 16, 24, 12) };
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        ScrollEffects.SetEnabled(scroll, true); dock.Children.Add(scroll);
        Heading(panel, Title, "选择程序、文件，或粘贴网页地址。", true);
        panel.Children.Add(inputLabel);
        path.Text = Result.Kind is TaskKind.Url or TaskKind.Network ? Result.Url : Result.Path;
        AutomationProperties.SetName(path, "任务入口"); panel.Children.Add(Browse(path, false));
        var detection = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        var changeType = new Button { Content = "更改类型", Style = (Style)FindResource("QuietButton"), FontSize = 12, MinHeight = 26, Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(8, 0, 0, 0) };
        changeType.Click += (_, _) => { typePanel.Visibility = typePanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; changeType.Content = typePanel.Visibility == Visibility.Visible ? "收起类型" : "更改类型"; };
        DockPanel.SetDock(changeType, Dock.Right); detection.Children.Add(changeType);
        detected.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); detection.Children.Add(detected); panel.Children.Add(detection);
        kind.ItemsSource = new[] { new Choice<TaskKind?>(null, "自动识别"), new(TaskKind.Desktop, "桌面程序"), new(TaskKind.File, "本地文件"), new(TaskKind.Url, "网址"), new(TaskKind.Script, "一次性脚本"), new(TaskKind.Service, "本地网页服务"), new(TaskKind.Network, "网络登录与检查") };
        kind.DisplayMemberPath = "Label"; kind.SelectedValuePath = "Value";
        if (entry == null) kind.SelectedIndex = 0; else kind.SelectedValue = Result.Kind;
        Add(typePanel, "任务类型", kind); typePanel.Visibility = Visibility.Collapsed; panel.Children.Add(typePanel);
        name.Text = Result.Name; nameEdited = entry != null;
        group.ItemsSource = config.Groups; group.Text = Result.Group;
        AutomationProperties.SetName(name, "任务名称"); AutomationProperties.SetName(group, "任务分类");
        Add(panel, "名称", name); Add(panel, "分类", group);
        auto.IsChecked = Result.AutoStart; auto.Margin = new Thickness(0, 16, 0, 0); panel.Children.Add(auto);
        name.TextChanged += (_, _) => { if (!suggestingName) nameEdited = true; };

        proxy.Text = Result.ProxyUrl; target.Text = Result.ProxyProbeUrl; autoLogin.IsChecked = Result.AutoLogin;
        Heading(networkPanel, "网络登录与检查"); Add(networkPanel, "HTTP 代理地址", proxy); Add(networkPanel, "经代理检查的目标 HTTPS 地址", target); networkPanel.Children.Add(autoLogin); panel.Children.Add(networkPanel);
        Heading(servicePanel, "运行检查");
        port.Text = Result.Port.ToString(); checkUrl.Text = Result.CheckUrl; wait.Text = Result.WaitSeconds.ToString(); codes.Text = string.Join(", ", Result.HealthCodes);
        Add(serviceFields, "监听端口（0 表示只检查进程或地址）", port); Add(serviceFields, "就绪检查地址（可留空）", checkUrl); servicePanel.Children.Add(serviceFields);
        Add(servicePanel, "等待上限（秒；网络检查最多 300 秒）", wait); panel.Children.Add(servicePanel);

        var more = new StackPanel();
        info.Text = Result.Info; info.AcceptsReturn = true; info.Height = 64; Add(more, "说明", info);
        var choices = new List<Choice<Guid?>>();
        choices.AddRange(config.Entries.Where(x => (x.Kind is TaskKind.Service or TaskKind.Network || x.Kind == TaskKind.Desktop && x.WaitForReady) && x.Id != Result.Id && !CreatesCycle(x)).Select(x => new Choice<Guid?>(x.Id, x.Name + " · " + x.KindText + " · " + x.Group)));
        dependency.ItemsSource = choices; dependency.DisplayMemberPath = "Label"; dependency.SelectedValuePath = "Value"; dependency.SelectedIndex = choices.FindIndex(x => x.Value == Result.DependsOn);
        Add(more, "前置任务", dependency); delay.Text = Result.StartDelaySeconds.ToString(); Add(more, "启动延迟（秒）", delay);
        cwd.Text = Result.WorkingDir; script.Text = Result.Script; url.Text = Result.Url;
        Heading(launchPanel, "启动设置"); Add(launchPanel, "工作目录", Browse(cwd, true));
        Add(scriptPanel, "脚本路径（入口为 Python / Node 解释器时填写）", Browse(script, false)); launchPanel.Children.Add(scriptPanel);
        arguments = new(Result.Arguments.Select(x => new ArgumentRow { Value = x }));
        argumentGrid = new DataGrid { ItemsSource = arguments, AutoGenerateColumns = false, CanUserAddRows = true, CanUserDeleteRows = true, Height = 110, HeadersVisibility = DataGridHeadersVisibility.Column };
        argumentGrid.Columns.Add(new DataGridTextColumn { Header = "每行一个参数，无需外层引号", Binding = new Binding("Value"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) }); Add(launchPanel, "启动参数", argumentGrid);
        Add(launchPanel, "关联网页地址（可留空）", url);
        environment = new(Result.Environment.Select(x => new EnvironmentRow { Name = x.Key, Value = x.Value }));
        environmentGrid = new DataGrid { ItemsSource = environment, AutoGenerateColumns = false, CanUserAddRows = true, CanUserDeleteRows = true, Height = 100 };
        environmentGrid.Columns.Add(new DataGridTextColumn { Header = "环境变量名", Binding = new Binding("Name"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        environmentGrid.Columns.Add(new DataGridTextColumn { Header = "值", Binding = new Binding("Value"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) }); Add(launchPanel, "此任务的环境变量", environmentGrid);
        pidFile.Text = Result.ManagedPidFile; match.Text = Result.ProcessMatch; externalLogs.Text = string.Join(Environment.NewLine, Result.ExternalLogs); externalLogs.AcceptsReturn = true; externalLogs.Height = 64;
        Add(launchPanel, "服务维护的 PID 文件（可选）", Browse(pidFile, false)); Add(launchPanel, "进程识别标识（可选）", match); Add(launchPanel, "外部日志（每行一个路径）", externalLogs); more.Children.Add(launchPanel);
        appId.Text = Result.AppUserModelId; ready.IsChecked = Result.WaitForReady;
        Heading(desktopPanel, "桌面程序"); Add(desktopPanel, "Windows 包应用标识 AUMID（可替代入口路径）", appId); desktopPanel.Children.Add(ready); more.Children.Add(desktopPanel);
        visible.IsChecked = Result.VisibleConsole; ownBrowser.IsChecked = Result.OpensOwnBrowser; suppress.IsChecked = Result.SuppressOutput; open.IsChecked = Result.OpenPageOnAutoStart;
        Heading(outputPanel, "运行方式"); outputPanel.Children.Add(visible); outputPanel.Children.Add(ownBrowser); outputPanel.Children.Add(suppress); outputPanel.Children.Add(open); more.Children.Add(outputPanel);
        Add(healthPanel, "就绪检查接受的 HTTP 状态码（逗号分隔）", codes); more.Children.Add(healthPanel);
        directProbe.Text = Result.DirectProbeUrl; expected.Text = Result.DirectExpectedContent;
        Add(networkAdvanced, "直连检查地址（留空读取 Windows 设置）", directProbe); Add(networkAdvanced, "直连检查预期正文", expected); more.Children.Add(networkAdvanced);
        kind.SelectionChanged += (_, _) => UpdateFields();
        ready.Checked += (_, _) => UpdateFields(); ready.Unchecked += (_, _) => UpdateFields();
        ownBrowser.Checked += (_, _) => UpdateFields(); ownBrowser.Unchecked += (_, _) => UpdateFields();
        path.TextChanged += (_, _) => UpdateEntrance();
        path.AllowDrop = true;
        path.PreviewDragOver += (_, e) => { e.Effects = e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        path.PreviewDrop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files) path.Text = files[0]; e.Handled = true; };
        Loaded += (_, _) => { path.Focus(); path.SelectAll(); };
        DialogExperience.Attach(this, () => path);
        BuildSections(panel, more, buttons, cancel);
        UpdateEntrance();
        initialForm = DialogLayout.FormState(this)+"|"+selectedDependency;
        new DraftClose(this,cancel,()=>initialForm != DialogLayout.FormState(this)+"|"+selectedDependency,()=> { Save(); return DialogResult==true; });
    }

    private bool CreatesCycle(TaskEntry candidate)
    {
        var seen = new HashSet<Guid>();
        for(TaskEntry? current=candidate; current!=null && seen.Add(current.Id); current=config.Entries.FirstOrDefault(x=>x.Id==current.DependsOn))
            if(current.DependsOn==Result.Id) return true;
        return false;
    }
    internal static void Move(UIElement control, Panel destination)
    {
        UIElement item=control;
        var parent=LogicalTreeHelper.GetParent(item);
        if(parent is DockPanel dock && control is TextBox) { item=dock; parent=LogicalTreeHelper.GetParent(item); }
        if(parent is not Panel source) return;
        int index=source.Children.IndexOf(item);
        if(index>0 && source.Children[index-1] is TextBlock label && label.FontSize<18) { source.Children.Remove(label); destination.Children.Add(label); }
        source.Children.Remove(item); destination.Children.Add(item);
    }
    private void BuildSections(StackPanel basic, StackPanel more, StackPanel buttons, Button cancel)
    {
        ((ScrollViewer)basic.Parent).Content=null;
        basic.Margin=new Thickness(0);
        basic.Children.Remove(networkPanel); basic.Children.Remove(servicePanel);
        layout=new DialogLayout(this); Content=layout;
        foreach(var button in buttons.Children.Cast<UIElement>().ToArray()) {buttons.Children.Remove(button);layout.Actions.Children.Add(button);}
        ((Panel)validation.Parent).Children.Remove(validation); DockPanel.SetDock(validation,Dock.Bottom); layout.Children.Insert(0,validation);
        Move(info,basic);
        var summary=new StackPanel {Margin=new Thickness(0,20,0,0)}; basic.Children.Add(summary); summary.Children.Add(dependencySummary);
        var row=new WrapPanel(); summary.Children.Add(row);
        basic.Children.Remove(summary); basic.Children.Insert(basic.Children.IndexOf(info)-1,summary);
        var change=new Button {Content=Result.DependsOn.HasValue?"更改前置任务":"选择前置任务"};
        change.Click+=(_,_)=>{layout.ShowSection("启动顺序");dependency.Focus();}; row.Children.Add(change);
        var clear=new Button {Content="解除前置关系",ToolTip="解除前置关系并清零延迟，保存后生效。"};
        clear.Click+=(_,_)=>{selectedDependency=null;dependency.SelectedIndex=-1;delay.Text="0";UpdateDependency();change.Content="选择前置任务";
            foreach(var button in DialogLayout.Descendants(this).OfType<Button>().Where(x=>Equals(x.Content,"解除前置关系"))) button.IsEnabled=false;};row.Children.Add(clear);
        var sequence=new StackPanel(); Heading(sequence,"启动顺序",null,true);
        var search=new TextBox(); AutomationProperties.SetName(search,"搜索前置任务"); Add(sequence,"搜索前置任务",search);
        Move(dependency,sequence);
        var remove=new Button {Content="解除前置关系",HorizontalAlignment=HorizontalAlignment.Left,ToolTip=clear.ToolTip}; remove.Click+=(_,_)=>clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); sequence.Children.Add(remove);
        Move(delay,sequence);
        var oldLabel=sequence.Children[sequence.Children.IndexOf(delay)-1]; int labelIndex=sequence.Children.IndexOf(oldLabel);sequence.Children.Remove(oldLabel);sequence.Children.Insert(labelIndex,delayLabel);delayLabel.Margin=new Thickness(0,12,0,6);
        var choices=((IEnumerable<Choice<Guid?>>)dependency.ItemsSource).ToArray();
        selectedDependency=Result.DependsOn;
        search.TextChanged+=(_,_)=>{ var selected=selectedDependency;dependency.ItemsSource=choices.Where(x=>x.Label.Contains(search.Text,StringComparison.OrdinalIgnoreCase)).ToArray(); selectedDependency=selected;UpdateDependency();};
        dependency.SelectionChanged+=(_,_)=>{if(dependency.SelectedItem is Choice<Guid?> choice) {selectedDependency=choice.Value;UpdateDependency();}};
        dependency.SelectionChanged+=(_,_)=>{remove.IsEnabled=clear.IsEnabled=selectedDependency.HasValue;change.Content=selectedDependency.HasValue?"更改前置任务":"选择前置任务";};
        remove.IsEnabled=clear.IsEnabled=selectedDependency.HasValue;
        var launch=new StackPanel(); Heading(launch,"启动方式",null,true); Move(launchPanel,launch);Move(desktopPanel,launch);Move(outputPanel,launch);
        var checks=new StackPanel();Heading(checks,"运行检查",null,true);Move(networkPanel,checks);Move(servicePanel,checks);Move(healthPanel,checks);Move(networkAdvanced,checks);Move(pidFile,checks);Move(match,checks);
        var logs=new StackPanel();Heading(logs,"日志与环境",null,true);Move(environmentGrid,logs);Move(externalLogs,logs);Move(suppress,logs);
        AddRows(launchPanel,argumentGrid,()=>arguments.Add(new()),()=>{if(argumentGrid.SelectedItem is ArgumentRow value) arguments.Remove(value);},"参数");
        AddRows(logs,environmentGrid,()=>environment.Add(new()),()=>{if(environmentGrid.SelectedItem is EnvironmentRow value) environment.Remove(value);},"环境变量");
        layout.AddSection("基本信息",basic);layout.AddSection("启动顺序",sequence);layout.AddSection("启动方式",launch);layout.AddSection("运行检查",checks);layout.AddSection("日志与环境",logs);
        delay.TextChanged+=(_,_)=>UpdateDependency();
    }
    private void UpdateDependency()
    {
        var parent=config.Entries.FirstOrDefault(x=>x.Id==selectedDependency);
        dependencySummary.Text=(parent==null?"无前置任务":"前置任务："+parent.Name)+(SelectedKind!=TaskKind.Network && delay.Text!="0"?" · 等待 "+delay.Text+" 秒":"");
        delayLabel.Text=parent?.Kind==TaskKind.Network?"网络连续就绪时间（秒）":"启动延迟（秒）";
    }
    private static void AddRows(Panel panel,DataGrid grid,Action add,Action remove,string noun)
    {
        grid.CanUserAddRows=false;
        var row=new WrapPanel();var plus=new Button {Content="添加"+noun};var minus=new Button {Content="删除所选"+noun};
        plus.Click+=(_,_)=>add();minus.Click+=(_,_)=>remove();row.Children.Add(plus);row.Children.Add(minus);panel.Children.Insert(panel.Children.IndexOf(grid)+1,row);
    }

    private void UpdateEntrance()
    {
        if (!nameEdited)
        {
            suggestingName = true;
            try { name.Text = TaskEntrance.Parse(path.Text).SuggestedName; } finally { suggestingName = false; }
        }
        UpdateFields();
    }
    private void UpdateFields()
    {
        var k = SelectedKind;
        bool launch = k is TaskKind.Service or TaskKind.Script or TaskKind.Desktop;
        launchPanel.Visibility = launch ? Visibility.Visible : Visibility.Collapsed;
        scriptPanel.Visibility = k is TaskKind.Service or TaskKind.Script ? Visibility.Visible : Visibility.Collapsed;
        networkPanel.Visibility = k == TaskKind.Network ? Visibility.Visible : Visibility.Collapsed;
        networkAdvanced.Visibility = networkPanel.Visibility;
        desktopPanel.Visibility = k == TaskKind.Desktop ? Visibility.Visible : Visibility.Collapsed;
        bool checks = k == TaskKind.Service || k == TaskKind.Desktop && ready.IsChecked == true;
        servicePanel.Visibility = checks || k == TaskKind.Network ? Visibility.Visible : Visibility.Collapsed;
        serviceFields.Visibility = checks ? Visibility.Visible : Visibility.Collapsed;
        healthPanel.Visibility = serviceFields.Visibility;
        outputPanel.Visibility = launch ? Visibility.Visible : Visibility.Collapsed;
        visible.Visibility = suppress.Visibility = k is TaskKind.Service or TaskKind.Script ? Visibility.Visible : Visibility.Collapsed;
        open.Visibility = k == TaskKind.Service ? Visibility.Visible : Visibility.Collapsed;
        open.IsEnabled = ownBrowser.IsChecked != true;
        open.ToolTip = ownBrowser.IsChecked == true ? "程序自行打开网页，启动台不再重复打开。" : "服务就绪后由启动台打开关联网页。";
        detected.Text = path.Text.Trim().Length == 0 && kind.SelectedValue == null ? "自动识别程序、文件和网址" : "任务类型：" + new TaskEntry { Kind = k }.KindText;
        inputLabel.Text = k == TaskKind.Network ? "认证网页入口" : "入口";
        if(layout!=null) {
            layout.SetAvailable("启动方式",launch); layout.SetAvailable("日志与环境",launch);
            layout.SetAvailable("运行检查", k is TaskKind.Service or TaskKind.Network or TaskKind.Desktop);
            delay.Visibility = delayLabel.Visibility = k==TaskKind.Network ? Visibility.Collapsed : Visibility.Visible;
            UpdateDependency();
        }
    }
    internal static void Add(Panel panel, string title, UIElement input)
    {
        if (input is Control && string.IsNullOrEmpty(AutomationProperties.GetName(input))) AutomationProperties.SetName(input, title);
        panel.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 12, 0, 6), TextWrapping = TextWrapping.Wrap }); panel.Children.Add(input);
    }
    internal static void Heading(Panel panel, string title, string? description = null, bool page = false)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = page ? 24 : 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, page ? 0 : 24, 0, 8) });
        if (description != null) { var text = new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) }; text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(text); }
    }
    internal static UIElement Browse(TextBox text, bool folder)
    {
        text.SetBinding(ToolTipProperty, new Binding("Text") { Source = text });
        var dock = new DockPanel(); var button = new Button { Content = "浏览…", Margin = new Thickness(8, 0, 0, 0) }; DockPanel.SetDock(button, Dock.Right); dock.Children.Add(button); dock.Children.Add(text);
        button.Click += (_, _) =>
        {
            var owner = GetWindow(text);
            if (folder) { var picker = new OpenFolderDialog(); if (picker.ShowDialog(owner) == true) text.Text = picker.FolderName; }
            else { var picker = new OpenFileDialog(); if (File.Exists(text.Text)) picker.FileName = text.Text; if (picker.ShowDialog(owner) == true) text.Text = picker.FileName; }
            text.Focus();
        };
        return dock;
    }
    private void Save()
    {
        if (InputComposition.IsActive(this)) return;
        int Number(TextBox field)
        {
            if (int.TryParse(field.Text, out int value)) {
                string? error = field == port && (value < 0 || value > 65535) ? "监听端口应在 0 至 65535 之间。" :
                    field == wait && value < 1 ? "等待时间必须大于零。" :
                    field == wait && SelectedKind == TaskKind.Network && value > 300 ? "网络等待上限不能超过 300 秒。" :
                    field == delay && (value < 0 || value > 3600) ? "启动延迟应在 0 至 3600 秒之间。" : null;
                if (error != null) { layout.Reveal(field); throw new InvalidDataException(error); }
                return value;
            }
            layout.Reveal(field);
            field.Focus(); field.SelectAll(); field.BringIntoView();
            throw new FormatException();
        }
        try
        {
            var e = Result.Copy(); var k = SelectedKind;
            e.Name = name.Text.Trim(); e.Group = group.Text.Trim(); if (e.Group.Length == 0) e.Group = "未分类";
            e.Kind = k; e.Info = info.Text; e.AutoStart = auto.IsChecked == true;
            e.DependsOn = selectedDependency; e.StartDelaySeconds = k==TaskKind.Network ? Result.StartDelaySeconds : Number(delay);
            var entrance = TaskEntrance.Parse(path.Text);
            if (k is TaskKind.Url or TaskKind.Network) e.Url = entrance.Target;
            else e.Path = path.Text.Trim().Trim('"');
            if (k is TaskKind.Service or TaskKind.Script or TaskKind.Desktop)
            {
                argumentGrid.CommitEdit(DataGridEditingUnit.Cell, true); argumentGrid.CommitEdit(DataGridEditingUnit.Row, true);
                environmentGrid.CommitEdit(DataGridEditingUnit.Cell, true); environmentGrid.CommitEdit(DataGridEditingUnit.Row, true);
                e.WorkingDir = cwd.Text.Trim(); e.Arguments = arguments.Select(x => x.Value ?? "").ToList(); e.Url = url.Text.Trim();
                e.Environment = environment.Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToDictionary(x => x.Name.Trim(), x => x.Value ?? "", StringComparer.OrdinalIgnoreCase);
                e.ManagedPidFile = pidFile.Text.Trim(); e.ProcessMatch = match.Text.Trim(); e.ExternalLogs = externalLogs.Text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries).ToList();
                e.OpensOwnBrowser = ownBrowser.IsChecked == true;
                if (k is TaskKind.Service or TaskKind.Script)
                {
                    e.Script = script.Text.Trim().Trim('"'); e.VisibleConsole = visible.IsChecked == true; e.SuppressOutput = suppress.IsChecked == true;
                }
                if (k == TaskKind.Desktop) { e.AppUserModelId = appId.Text.Trim(); e.WaitForReady = ready.IsChecked == true; }
            }
            if (k == TaskKind.Service || k == TaskKind.Network || k == TaskKind.Desktop && e.WaitForReady) e.WaitSeconds = Number(wait);
            if (k == TaskKind.Service || k == TaskKind.Desktop && e.WaitForReady)
            {
                e.Port = Number(port); e.CheckUrl = checkUrl.Text.Trim();
                var tokens=codes.Text.Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries);
                if(tokens.Any(x=>!int.TryParse(x,out _))) {layout.Reveal(codes);throw new FormatException();}
                e.HealthCodes = tokens.Select(int.Parse).Distinct().ToList();
            }
            if (k == TaskKind.Service) e.OpenPageOnAutoStart = !e.OpensOwnBrowser && open.IsChecked == true;
            if (k == TaskKind.Network) { e.DirectProbeUrl = directProbe.Text.Trim(); e.DirectExpectedContent = expected.Text; e.ProxyUrl = proxy.Text.Trim(); e.ProxyProbeUrl = target.Text.Trim(); e.AutoLogin = autoLogin.IsChecked == true; }
            if (e.Environment.Keys.Any(x => x.Contains('=') || x.Contains('\0'))) throw new InvalidDataException("环境变量名不能包含等号。");
            if (e.HealthCodes.Any(x => x < 100 || x > 599)) throw new InvalidDataException("HTTP 状态码应为 100 至 599。");
            var candidate = new HubConfig { Version = config.Version, RefreshSeconds = config.RefreshSeconds, DefaultWaitSeconds = config.DefaultWaitSeconds, Groups = config.Groups.ToList(), Entries = config.Entries.Where(x => x.Id != e.Id).Append(e).ToList() };
            var addresses = k==TaskKind.Network ? new[]{path,proxy,target,directProbe} : k==TaskKind.Service || k==TaskKind.Desktop && e.WaitForReady ? new[]{url,checkUrl} : k==TaskKind.Url ? new[]{path} : Array.Empty<TextBox>();
            foreach(var field in addresses.Where(x=>!string.IsNullOrWhiteSpace(x.Text))) {
                if(!Uri.TryCreate(field.Text.Trim(),UriKind.Absolute,out var address) || address.Scheme is not ("http" or "https")) {validation.Text="网页地址需要使用完整的 HTTP 或 HTTPS 地址。"; validation.Visibility=Visibility.Visible;layout.Reveal(field);return;}
            }
            DataStore.Validate(candidate); Result = e; DialogResult = true;
        }
        catch (Exception ex)
        {
            validation.Text = ex is FormatException or OverflowException ? "端口、等待秒数、延迟和状态码需要填写有效整数。" : ex.Message;
            validation.Visibility = Visibility.Visible;
            if (string.IsNullOrWhiteSpace(name.Text)) layout.Reveal(name);
            else if(ex.Message.Contains("前置") || ex.Message.Contains("循环")) layout.Reveal(dependency);
            else if(ex.Message.Contains("环境变量") || ex.Message.Contains("same key")) layout.Reveal(environmentGrid);
            else if(ex.Message.Contains("状态码")) layout.Reveal(codes);
            else if(ex.Message.Contains("端口")) layout.Reveal(port);
            else if(ex.Message.Contains("延迟")) layout.Reveal(delay);
            else if(ex.Message.Contains("等待")) layout.Reveal(wait);
            else if(ex is not FormatException) layout.Reveal(path);
        }
    }
}
