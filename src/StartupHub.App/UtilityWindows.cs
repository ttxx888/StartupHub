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
        var list = new ListBox { ItemsSource = store.Config.Groups.ToArray(), MinHeight = 40 };
        ScrollEffects.SetEnabled(list, true); body.Children.Add(list);
        System.Windows.Automation.AutomationProperties.SetName(list, "已有分类");
        var text = new TextBox { Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(text);
        System.Windows.Automation.AutomationProperties.SetName(text, "分类名称");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
        list.SelectionChanged += (_, _) => text.Text = list.SelectedItem as string ?? "";
        var actions = new WrapPanel(); panel.Children.Add(actions);
        void Refresh(string? selected = null) { store.SaveConfig(); list.ItemsSource = store.Config.Groups.ToArray(); list.SelectedItem = selected; }
        void AddButton(string label, Action action) { var b = new Button { Content = label }; b.Click += (_, _) => { try { action(); } catch (Exception ex) { DialogExperience.Status(status, ex.Message, true); } }; actions.Children.Add(b); }
        bool ValidName(string name, string? current = null)
        {
            string? error = name.Length == 0 ? "请输入分类名称。" : name == "全部任务" ? "“全部任务”用于显示所有分类，请换一个名称。" : name != current && store.Config.Groups.Contains(name) ? "已存在同名分类。" : null;
            if (error == null) return true;
            DialogExperience.Status(status, error, true); text.Focus(); text.SelectAll(); return false;
        }
        AddButton("新增", () => { var name = text.Text.Trim(); if (!ValidName(name)) return; store.Config.Groups.Add(name); Refresh(name); DialogExperience.Status(status, "分类已添加。"); });
        AddButton("重命名", () =>
        {
            if (list.SelectedItem is not string old) { DialogExperience.Status(status, "请先选择要重命名的分类。", true); list.Focus(); return; }
            var name = text.Text.Trim(); if (!ValidName(name, old)) return;
            store.Config.Groups[store.Config.Groups.IndexOf(old)] = name; foreach (var e in store.Config.Entries.Where(x => x.Group == old)) e.Group = name; Refresh(name); DialogExperience.Status(status, "分类名称已更新。");
        });
        AddButton("删除", () =>
        {
            if (list.SelectedItem is not string name) { DialogExperience.Status(status, "请先选择要删除的分类。", true); list.Focus(); return; }
            if (store.Config.Entries.Any(x => x.Group == name)) { DialogExperience.Status(status, "请先将该分类中的任务移动到其他分类。", true); return; }
            if (!DialogExperience.Confirm(this, "删除分类", $"删除空分类“{name}”？", "删除分类")) return;
            store.Config.Groups.Remove(name); Refresh(); DialogExperience.Status(status, "分类已删除。");
        });
        panel.Children.Add(new TextBlock { Text = "编辑任务中的“分类”字段即可移动任务。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0) });
        panel.Children.Add(status);
        DialogExperience.Attach(this, () => text);
    }
}

public sealed class SettingsWindow : Window
{
    public SettingsWindow(DataStore store)
    {
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
        theme.SelectionChanged += (_, _) => { if (!syncingTheme && theme.SelectedIndex >= 0) ThemeManager.SetPreference(theme.SelectedIndex switch { 1 => "Light", 2 => "Dark", _ => "System" }); };
        void SyncTheme(object? sender, EventArgs args)
        {
            syncingTheme = true;
            try { theme.SelectedIndex = ThemeManager.Preference switch { "Light" => 1, "Dark" => 2, _ => 0 }; }
            finally { syncingTheme = false; }
        }
        ThemeManager.Changed += SyncTheme;
        Closed += (_, _) => ThemeManager.Changed -= SyncTheme;
        var login = new CheckBox { Content = new TextBlock { Text = "登录 Windows 后在托盘启动，并执行自启清单", TextWrapping = TextWrapping.Wrap }, IsChecked = StartupRegistration.Enabled };
        System.Windows.Automation.AutomationProperties.SetName(login, "登录 Windows 后在托盘启动，并执行自启清单");
        panel.Children.Add(login);
        var refresh = new TextBox { Text = store.Config.RefreshSeconds.ToString() }; EntryEditor.Add(panel, "状态刷新间隔（秒）", refresh);
        var wait = new TextBox { Text = store.Config.DefaultWaitSeconds.ToString() }; EntryEditor.Add(panel, "新增服务的默认等待时间（秒）", wait);
        System.Windows.Automation.AutomationProperties.SetName(refresh, "状态刷新间隔（秒）");
        System.Windows.Automation.AutomationProperties.SetName(wait, "新增服务的默认等待时间（秒）");
        var save = new Button { Content = "保存设置", Style = (Style)FindResource("PrimaryButton"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(save);
        save.Click += (_, _) =>
        {
            if (!int.TryParse(refresh.Text, out var r) || r < 1) { DialogExperience.Status(status, "状态刷新间隔必须填写正整数。", true); refresh.Focus(); refresh.SelectAll(); return; }
            if (!int.TryParse(wait.Text, out var w) || w < 1) { DialogExperience.Status(status, "默认等待时间必须填写正整数。", true); wait.Focus(); wait.SelectAll(); return; }
            try { store.Config.RefreshSeconds = r; store.Config.DefaultWaitSeconds = w; store.SaveConfig(); StartupRegistration.Set(login.IsChecked == true); DialogExperience.Status(status, "设置已保存。"); }
            catch (Exception ex) { DialogExperience.Status(status, ex.Message, true); }
        };
        panel.Children.Add(new TextBlock { Text = "配置迁移与备份", FontWeight = FontWeights.SemiBold, FontSize = 16, Margin = new Thickness(0, 12, 0, 12) });
        var actions = new WrapPanel(); panel.Children.Add(actions);
        void Button(string label, Action action) { var b = new Button { Content = label }; b.Click += (_, _) => { try { action(); } catch (Exception ex) { DialogExperience.Status(status, ex.Message, true); } }; actions.Children.Add(b); }
        void Import(bool legacy)
        {
            if (App.Engine.BatchRunning) { DialogExperience.Status(status, "请等待自启清单完成后再导入配置。", true); return; }
            var picker = new OpenFileDialog { Filter = "JSON 配置|*.json", Title = legacy ? "选择旧启动器的 launcher.json" : "选择启动台配置" };
            if (picker.ShowDialog(this) != true) return;
            var config = legacy ? DataStore.ImportLegacy(picker.FileName) : DataStore.ReadExport(picker.FileName);
            if (!DialogExperience.Confirm(this, "导入配置", $"导入 {config.Entries.Count} 个任务并替换当前清单？\n当前配置会先备份，运行中的程序继续运行。", "导入并替换")) return;
            store.Export(Path.Combine(store.Root, "config.before-import.json")); store.ReplaceConfig(config); Close();
        }
        Button("导入旧启动器", () => Import(true)); Button("导入配置", () => Import(false));
        Button("导出配置", () => { var picker = new SaveFileDialog { Filter = "JSON 配置|*.json", FileName = "启动台配置.json" }; if (picker.ShowDialog(this) == true) { store.Export(picker.FileName); DialogExperience.Status(status, "配置已导出。"); } });
        Button("打开数据目录", () => NativeProcess.Open(store.Root)?.Dispose());
        panel.Children.Add(new TextBlock { Text = "关闭窗口后收进托盘。需要退出时，右键托盘图标选择退出；已启动的服务继续运行。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 18, 0, 12) });
        var location = new TextBlock { Text = "数据目录\n" + store.Root, TextWrapping = TextWrapping.Wrap, FontSize = 12 }; location.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); panel.Children.Add(location);
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
        var folder = new Button {Content="查找 HTML 文件夹"};options.Children.Add(folder);
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
        preview = new Button { Content = "打开预览", IsEnabled = false }; actions.Children.Add(preview);
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

