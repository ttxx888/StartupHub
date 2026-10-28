using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using StartupHub.Core;

namespace StartupHub.App;

public sealed class GroupsWindow : Window
{
    public GroupsWindow(DataStore store)
    {
        var draft = System.Text.Json.JsonSerializer.Deserialize<HubConfig>(System.Text.Json.JsonSerializer.Serialize(store.Config,DataStore.Json),DataStore.Json)!;
        string baseline=System.Text.Json.JsonSerializer.Serialize(draft,DataStore.Json);
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "管理分类"; Width = 440; Height = 520; MinWidth = 380; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new Thickness(24) }; Content = dock;
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var close = new Button { Content = "关闭", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        var body = new DockPanel(); dock.Children.Add(body);
        var heading = new StackPanel();
        EntryEditor.Heading(heading, "管理分类", "按工作方式整理任务，保持清晰的工作空间。", true);
        DockPanel.SetDock(heading, Dock.Top); body.Children.Add(heading);
        var panel = new StackPanel(); DockPanel.SetDock(panel, Dock.Bottom); body.Children.Add(panel);
        var list = new ListBox { ItemsSource = draft.Groups.ToArray(), MinHeight = 40 };
        ScrollEffects.SetEnabled(list, true); body.Children.Add(list);
        System.Windows.Automation.AutomationProperties.SetName(list, "已有分类");
        var text = new TextBox { Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(text);
        System.Windows.Automation.AutomationProperties.SetName(text, "分类名称");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        bool switching=false;
        list.SelectionChanged += (_, e) => {
            if(switching) return;
            if(e.RemovedItems.OfType<string>().FirstOrDefault() is string previous && draft.Groups.Contains(previous) && text.Text.Trim()!=previous) {
                var next=text.Text.Trim();
                if(!ValidName(next,previous)) {switching=true;list.SelectedItem=previous;switching=false;return;}
                draft.Groups[draft.Groups.IndexOf(previous)]=next;foreach(var task in draft.Entries.Where(x=>x.Group==previous))task.Group=next;
                var selected=list.SelectedItem;switching=true;list.ItemsSource=draft.Groups.ToArray();list.SelectedItem=selected;switching=false;
            }
            text.Text = list.SelectedItem as string ?? "";
        };
        var actions = new WrapPanel(); panel.Children.Add(actions);
        void Refresh(string? selected = null) { list.ItemsSource = draft.Groups.ToArray(); list.SelectedItem = selected; }
        void AddButton(string label, Action action) { var b = new Button { Content = label }; b.Click += (_, _) => { try { action(); } catch (Exception ex) { DialogExperience.Status(status, ex.Message, true); } }; actions.Children.Add(b); }
        bool ValidName(string name, string? current = null)
        {
            string? error = name.Length == 0 ? "请输入分类名称。" : name == "全部任务" ? "“全部任务”用于显示所有分类，请换一个名称。" : name != current && draft.Groups.Contains(name) ? "已存在同名分类。" : null;
            if (error == null) return true;
            DialogExperience.Status(status, error, true); text.Focus(); text.SelectAll(); return false;
        }
        AddButton("新增", () => { var name = text.Text.Trim(); if (!ValidName(name)) return; draft.Groups.Add(name); Refresh(name); DialogExperience.Status(status, "分类已添加。"); });
        AddButton("重命名", () =>
        {
            if (list.SelectedItem is not string old) { DialogExperience.Status(status, "请先选择要重命名的分类。", true); list.Focus(); return; }
            var name = text.Text.Trim(); if (!ValidName(name, old)) return;
            draft.Groups[draft.Groups.IndexOf(old)] = name; foreach (var e in draft.Entries.Where(x => x.Group == old)) e.Group = name; Refresh(name); DialogExperience.Status(status, "分类名称已更新。");
        });
        AddButton("删除", () =>
        {
            if (list.SelectedItem is not string name) { DialogExperience.Status(status, "请先选择要删除的分类。", true); list.Focus(); return; }
            if (draft.Entries.Any(x => x.Group == name)) { DialogExperience.Status(status, "请先将该分类中的任务移动到其他分类。", true); return; }

            draft.Groups.Remove(name); Refresh(); DialogExperience.Status(status, "分类已删除。");
        });
        panel.Children.Add(new TextBlock { Text = "编辑任务中的“分类”字段即可移动任务。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(status);
        var layout=new DialogLayout(this); Content=layout; layout.Navigation.Visibility=Visibility.Collapsed;
        body.Children.Remove(heading); body.Children.Remove(panel); body.Children.Remove(list);
        var columns=new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(180)}); columns.ColumnDefinitions.Add(new ColumnDefinition());
        list.Margin=new Thickness(0,0,20,0); columns.Children.Add(list); Grid.SetColumn(panel,1); columns.Children.Add(panel);
        var page=new StackPanel(); EntryEditor.Heading(page,"管理分类",null,true);
        // The list and editor share the available height.
        var container=new DockPanel(); DockPanel.SetDock(page,Dock.Top);container.Children.Add(page);container.Children.Add(columns);
        layout.Children.Add(container); layout.Navigation.Visibility=Visibility.Collapsed;
        bool SaveGroups() {
            if(InputComposition.IsActive(this)) return false;
            if(list.SelectedItem is string current && text.Text.Trim()!=current) {
                var value=text.Text.Trim(); if(!ValidName(value,current)) return false;
                draft.Groups[draft.Groups.IndexOf(current)]=value; foreach(var task in draft.Entries.Where(x=>x.Group==current))task.Group=value; Refresh(value);
            }
            try { store.ReplaceConfig(draft); DialogResult=true; return true; } catch(Exception ex) {DialogExperience.Status(status,ex.Message,true);return false;}
        }
        var cancel=new Button {Content="取消",IsCancel=true}; var save=new Button {Content="保存分类",IsDefault=true,Style=(Style)FindResource("PrimaryButton")};save.Click+=(_,_)=>SaveGroups();layout.Actions.Children.Add(cancel);layout.Actions.Children.Add(save);
        new DraftClose(this,cancel,()=>baseline!=System.Text.Json.JsonSerializer.Serialize(draft,DataStore.Json) || (list.SelectedItem is string selected && text.Text.Trim()!=selected),SaveGroups);
        DialogExperience.Attach(this, () => text);
    }
}

public sealed class SettingsWindow : Window
{
    public SettingsWindow(DataStore store, Action<bool>? register = null, bool? registered = null)
    {
        string originalTheme=ThemeManager.Preference;
        bool originalLogin=registered ?? StartupRegistration.Enabled;
        bool committed=false;
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "设置与配置"; Width = 620; Height = 570; MinWidth = 440; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new Thickness(24) }; Content = dock;
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var close = new Button { Content = "关闭", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0) };
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); footer.Children.Add(close);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0), Visibility = Visibility.Collapsed }; footer.Children.Add(status);
        DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        var panel = new StackPanel();
        var scroll = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        ScrollEffects.SetEnabled(scroll, true); dock.Children.Add(scroll);
        EntryEditor.Heading(panel, "设置与配置", "启动习惯、运行状态和数据备份。", true);
        var theme = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = ThemeManager.Preference switch { "Light" => 1, "Dark" => 2, _ => 0 } };
        System.Windows.Automation.AutomationProperties.SetName(theme, "外观主题");
        EntryEditor.Add(panel, "外观主题", theme);
        bool syncingTheme = false;
        theme.SelectionChanged += (_, _) => { if (!syncingTheme && theme.SelectedIndex >= 0) ThemeManager.Apply(theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" }); };
        void SyncTheme(object? sender, EventArgs args)
        {
            syncingTheme = true;
            try { theme.SelectedIndex = ThemeManager.Preference switch { "Light" => 1, "Dark" => 2, _ => 0 }; }
            finally { syncingTheme = false; }
        }
        ThemeManager.Changed += SyncTheme;
        Closed += (_, _) => ThemeManager.Changed -= SyncTheme;
        var login = new CheckBox { Content = new TextBlock { Text = "登录 Windows 后在托盘启动，并执行自启清单", TextWrapping = TextWrapping.Wrap }, IsChecked = originalLogin };
        System.Windows.Automation.AutomationProperties.SetName(login, "登录 Windows 后在托盘启动，并执行自启清单");
        panel.Children.Add(login);
        var refresh = new TextBox { Text = store.Config.RefreshSeconds.ToString() }; EntryEditor.Add(panel, "状态刷新间隔（秒）", refresh);
        var wait = new TextBox { Text = store.Config.DefaultWaitSeconds.ToString() }; EntryEditor.Add(panel, "新增服务的默认等待时间（秒）", wait);
        System.Windows.Automation.AutomationProperties.SetName(refresh, "状态刷新间隔（秒）");
        System.Windows.Automation.AutomationProperties.SetName(wait, "新增服务的默认等待时间（秒）");
        var save = new Button { Content = "保存设置", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(save);
        DialogLayout layout=null!;
        string initial="";
        bool SaveSettings(bool closeWindow) {
            if(InputComposition.IsActive(this)) return false;
            if(!int.TryParse(refresh.Text,out int r)||r<1) { DialogExperience.Status(status,"状态刷新间隔必须填写正整数。",true);layout.Reveal(refresh);return false; }
            if(!int.TryParse(wait.Text,out int w)||w<1) { DialogExperience.Status(status,"默认等待时间必须填写正整数。",true);layout.Reveal(wait);return false; }
            var old=store.Config; bool changedLogin=originalLogin!=(login.IsChecked==true);
            try {
                var candidate=System.Text.Json.JsonSerializer.Deserialize<HubConfig>(System.Text.Json.JsonSerializer.Serialize(old,DataStore.Json),DataStore.Json)!;
                candidate.RefreshSeconds=r;candidate.DefaultWaitSeconds=w;DataStore.Validate(candidate);
                if(changedLogin) (register ?? StartupRegistration.Set)(login.IsChecked==true);
                try {store.ReplaceConfig(candidate);} catch { if(changedLogin)(register ?? StartupRegistration.Set)(originalLogin); throw; }
                ThemeManager.SetPreference(ThemeManager.Preference); originalTheme=ThemeManager.Preference;originalLogin=login.IsChecked==true;committed=true;
                initial=DialogLayout.FormState(this);
                if(closeWindow) DialogResult=true;
                return true;
            } catch(Exception ex) { DialogExperience.Status(status,ex.Message,true);return false; }
        }
        save.Click+=(_,_)=>SaveSettings(true);
        panel.Children.Add(new TextBlock { Text = "配置迁移与备份", FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 12) });
        var actions = new WrapPanel(); panel.Children.Add(actions);
        void Button(string label, Action action) { var b = new Button { Content = label }; b.Click += (_, _) => { try { action(); } catch (Exception ex) { DialogExperience.Status(status, ex.Message, true); } }; actions.Children.Add(b); }
        DraftClose guard=null!;
        void Import(bool legacy)
        {
            if (App.Engine.BatchRunning) { DialogExperience.Status(status, "请等待自启清单完成后再导入配置。", true); return; }
            if(!guard.Resolve()) return;
            if(guard.Discarded) {ThemeManager.Apply(originalTheme);refresh.Text=store.Config.RefreshSeconds.ToString();wait.Text=store.Config.DefaultWaitSeconds.ToString();login.IsChecked=originalLogin;initial=DialogLayout.FormState(this);}
            var picker = new OpenFileDialog { Filter = "JSON 配置|*.json", Title = legacy ? "选择旧启动器的 launcher.json" : "选择启动台配置" };
            if (picker.ShowDialog(this) != true) return;
            var config = legacy ? DataStore.ImportLegacy(picker.FileName) : DataStore.ReadExport(picker.FileName);
            if (!DialogExperience.Confirm(this, "导入配置", $"导入 {config.Entries.Count} 个任务并替换当前清单？\n当前配置会先备份，运行中的程序继续运行。", "导入并替换")) return;
            store.Export(Path.Combine(store.Root, "config.before-import.json")); store.ReplaceConfig(config); initial=DialogLayout.FormState(this); Close();
        }
        Button("导入旧启动器", () => Import(true)); Button("导入并替换配置…", () => Import(false));
        Button("导出已保存配置…", () => { var picker = new SaveFileDialog { Filter = "JSON 配置|*.json", FileName = "启动台配置.json" }; if (picker.ShowDialog(this) == true) { store.Export(picker.FileName); DialogExperience.Status(status, "配置已导出。"); } });
        Button("打开数据目录", () => NativeProcess.Open(store.Root)?.Dispose());
        panel.Children.Add(new TextBlock { Text = "关闭窗口后收进托盘。需要退出时，右键托盘图标选择退出；已启动的服务继续运行。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 12) });
        var location = new TextBlock { Text = "数据目录\n" + store.Root, TextWrapping = TextWrapping.Wrap, FontSize = 12 }; location.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(location);
        layout=new DialogLayout(this); Content=layout;
        var appearance=new StackPanel();EntryEditor.Heading(appearance,"外观",null,true);EntryEditor.Move(theme,appearance);
        var runtime=new StackPanel();EntryEditor.Heading(runtime,"启动与运行",null,true);EntryEditor.Move(login,runtime);EntryEditor.Move(refresh,runtime);EntryEditor.Move(wait,runtime);
        panel.Children.Remove(save);
        // Remaining controls are configuration import, export and data actions.
        if(panel.Children.Count>1) {panel.Children.RemoveAt(0);panel.Children.RemoveAt(0);}
        panel.Children.Remove(status); footer.Children.Remove(status);
        DockPanel.SetDock(status,Dock.Bottom);layout.Children.Insert(0,status);
        ((ScrollViewer)panel.Parent).Content=null;
        layout.AddSection("外观",appearance);layout.AddSection("启动与运行",runtime);layout.AddSection("配置与数据",panel);
        var cancel=new Button {Content="取消",IsCancel=true};layout.Actions.Children.Add(cancel);layout.Actions.Children.Add(save);save.IsDefault=true;save.Margin=new Thickness(8,0,0,0);
        initial=DialogLayout.FormState(this);
        guard=new DraftClose(this,cancel,()=>initial!=DialogLayout.FormState(this),()=>SaveSettings(false));
        Closed+=(_,_)=> { if(!committed || ThemeManager.Preference!=originalTheme) ThemeManager.Apply(originalTheme); };
        DialogExperience.Attach(this, () => theme);
    }
}

public sealed class DiscoverWindow : Window
{
    public List<DiscoveredPage> SelectedPages { get; private set; } = [];
    public string Group { get; private set; } = "常用服务";
    private readonly ListBox list;
    private readonly Button add;
    private readonly Button preview;
    private readonly TextBlock summary;
    private readonly List<DiscoveredPage> discovered;
    private readonly List<DiscoveryIssue> issues;
    private readonly System.Collections.Generic.IEnumerable<TaskEntry> existing;
    private readonly Expander diagnostics;
    private readonly TextBox issueText;
    private readonly TextBlock empty;
    private readonly Button selectAll;
    private readonly Button clearSelection;
    public DiscoverWindow(List<DiscoveredPage> pages, List<string> groups, string? initialGroup = null, System.Collections.Generic.IEnumerable<DiscoveryIssue>? scanIssues=null, System.Collections.Generic.IEnumerable<TaskEntry>? existingEntries=null)
    {
        discovered=pages.ToList();issues=(scanIssues??[]).ToList();existing=existingEntries??[];
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "发现本地网页"; Width = 800; Height = Math.Min(620, SystemParameters.WorkArea.Height - 40); MinWidth = 520; MinHeight = 500; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var dock = new DockPanel { Margin = new Thickness(24) }; Content = dock;
        var title = new StackPanel { Margin = new Thickness(0, 0, 0, 12) }; EntryEditor.Heading(title, "发现本地网页", "选择要保留的网页，可用 Ctrl / Shift 多选。添加后默认不自启。", true); DockPanel.SetDock(title, Dock.Top); dock.Children.Add(title);
        var options = new WrapPanel(); title.Children.Add(options);
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 8) };
        selectAll = new Button { Content = "全选", IsEnabled = pages.Count > 0 }; selectAll.Click += (_,_) => list!.SelectAll(); options.Children.Add(selectAll);
        clearSelection = new Button { Content = "清除选择", IsEnabled = pages.Count > 0 }; clearSelection.Click += (_,_) => list!.UnselectAll(); options.Children.Add(clearSelection);
        var folder = new Button {Content="查找网页文件…", ToolTip="选择文件夹，查找其中的 HTML 网页文件。"};options.Children.Add(folder);
        folder.Click+=async (_,_)=>
        {
            var picker=new OpenFolderDialog {Title="选择包含 HTML 页面的文件夹"};
            if(picker.ShowDialog(this)!=true)return;
            folder.IsEnabled=false;
            try{await AddFolderAsync(picker.FolderName);}
            catch(Exception ex){DialogExperience.Status(feedback, ex.Message, true); UpdateSummary();}
            finally{folder.IsEnabled=true;}
        };
        summary = new TextBlock { Text = $"发现 {pages.Count} 个 · 已选 0 个", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8,0,0,0) }; options.Children.Add(summary);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); dock.Children.Add(footer);
        footer.Children.Add(feedback);
        issueText=new TextBox {IsReadOnly=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MaxHeight=80};
        System.Windows.Automation.AutomationProperties.SetName(issueText, "未加入结果的入口详情");
        diagnostics=new Expander {Content=issueText,Margin=new Thickness(0,8,0,0)};UpdateIssues();
        var groupRow = new DockPanel { Margin = new Thickness(0,0,0,12) }; footer.Children.Add(groupRow);
        groupRow.Children.Add(new TextBlock { Text = "添加到分类", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0,0,12,0) });
        var group = new ComboBox { ItemsSource = groups, SelectedItem = groups.Contains(initialGroup ?? "") ? initialGroup : groups.FirstOrDefault(), MinWidth = 160 }; groupRow.Children.Add(group);
        System.Windows.Automation.AutomationProperties.SetName(group,"添加到分类");
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right }; footer.Children.Add(actions);
        var cancel = new Button { Content = "取消", IsCancel = true }; actions.Children.Add(cancel);
        preview = new Button { Content = "打开所选网页", ToolTip="使用默认应用打开所选网页或 HTML 文件。", IsEnabled = false }; actions.Children.Add(preview);
        preview.Click += (_,_) => { if (list!.SelectedItem is DiscoveredPage page) try { NativeProcess.Open(page.Entrance)?.Dispose(); } catch (Exception ex) { DialogExperience.Status(feedback, "无法打开预览：" + ex.Message, true); } };
        list = new ListBox { SelectionMode = SelectionMode.Extended, ItemsSource = discovered, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        ScrollViewer.SetHorizontalScrollBarVisibility(list,ScrollBarVisibility.Disabled);
        ScrollEffects.SetEnabled(list, true);
        System.Windows.Automation.AutomationProperties.SetName(list,"发现的本地网页");
        var template = new DataTemplate(); var stack = new FrameworkElementFactory(typeof(StackPanel)); stack.SetValue(StackPanel.MarginProperty, new Thickness(6));
        void Text(string field, bool bold)
        {
            var text = new FrameworkElementFactory(typeof(TextBlock)); text.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding(field)); text.SetBinding(FrameworkElement.ToolTipProperty,new System.Windows.Data.Binding(field));
            text.SetValue(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis);
            if (bold) text.SetValue(TextBlock.FontWeightProperty,FontWeights.SemiBold);
            else text.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
            stack.AppendChild(text);
        }
        Text("Name",true); Text("SourceLabel",false); Text("Entrance",false); template.VisualTree = stack; list.ItemTemplate = template;
        add = new Button { Content = "添加选中", Style = (Style)FindResource("PrimaryButton"), IsEnabled = false };
        add.Click += (_, _) => { SelectedPages = list.SelectedItems.Cast<DiscoveredPage>().ToList(); Group = group.SelectedItem as string ?? "常用服务"; DialogResult = true; }; actions.Children.Add(add);
        list.SelectionChanged += (_, _) => UpdateSummary();
        var body=new Grid();body.RowDefinitions.Add(new RowDefinition());body.RowDefinitions.Add(new RowDefinition {Height=GridLength.Auto});dock.Children.Add(body);body.Children.Add(list);
        Grid.SetRow(diagnostics,1);body.Children.Add(diagnostics);
        empty=new TextBlock {Text="暂未发现可添加的网页\n启动本地服务，或选择 HTML 文件夹。",TextAlignment=TextAlignment.Center,VerticalAlignment=VerticalAlignment.Center,TextWrapping=TextWrapping.Wrap,LineHeight=28,IsHitTestVisible=false};empty.SetResourceReference(TextBlock.ForegroundProperty,"MutedBrush");body.Children.Add(empty);
        void UpdateEmpty()=>empty.Visibility=discovered.Count>0?Visibility.Collapsed:Visibility.Visible;
        list.Items.CurrentChanged+=(_,_)=>UpdateEmpty();UpdateEmpty();
        DialogExperience.Attach(this, () => discovered.Count > 0 ? list : folder);
    }
    private void UpdateSummary(){int count=list.SelectedItems.Count;add.IsEnabled=count>0;preview.IsEnabled=count==1;summary.Text=$"发现 {discovered.Count} 个 · 已选 {count} 个";}
    private void UpdateIssues(){diagnostics.Header=$"未加入结果的入口（{issues.Count}）";diagnostics.Visibility=issues.Count>0?Visibility.Visible:Visibility.Collapsed;issueText.Text=string.Join("\n\n",issues.Select(i=>i.Address+"\n"+i.Reason));}
    public async System.Threading.Tasks.Task AddFolderAsync(string path)
    {
        summary.Text="正在查找 HTML 文件…";
        var report=await System.Threading.Tasks.Task.Run(()=>LocalHtmlDiscovery.Scan([],existing,[path]));
        discovered.AddRange(report.Pages.Where(p=>!discovered.Any(e=>e.Kind==TaskKind.File&&e.Path.Equals(p.Path,StringComparison.OrdinalIgnoreCase))));
        issues.AddRange(report.Issues);list.Items.Refresh();UpdateIssues();UpdateSummary();
        selectAll.IsEnabled=clearSelection.IsEnabled=discovered.Count>0;
        empty.Visibility=discovered.Count>0?Visibility.Collapsed:Visibility.Visible;
    }
}

