using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using StartupHub.App;
using StartupHub.Core;

internal static class MainWindowChecks
{
    internal static void Run(MainWindow window, MainViewModel vm, DataStore store, TaskEngine engine, Action<bool,string> check, string root)
    {
        var legacy = JsonSerializer.Deserialize<HubState>("{\"Width\":1080,\"DetailsVisible\":false}", DataStore.Json)!;
        check(!legacy.SidebarCollapsed && legacy.Width == 1080 && !legacy.DetailsVisible, "旧界面状态默认展开侧栏并保留窗口与详情偏好");
        var entries = Enumerable.Range(1,33).Select(i => new TaskEntry
        {
            Name = $"界面测试任务 {i:00}", Kind = TaskKind.Url,
            Url = $"http://127.0.0.1:54321/fixture/{i}", Group = i <= 20 ? "常用测试分类" : "开发测试分类",
            AutoStart = i <= 4, Info = "独立界面测试，不执行启动"
        }).ToList();
        store.ReplaceConfig(new HubConfig { Entries = entries, Groups = ["常用测试分类", "开发测试分类"] });
        vm.Reload(); vm.Search = ""; vm.SelectedGroup = "全部任务";
        window.ShowActivated = false; window.ShowInTaskbar = false;
        window.Show(); Settle(window);
        var list = (DataGrid)window.FindName("TaskList");
        var categories = (ListBox)window.FindName("CategoryList");
        var column = (ColumnDefinition)window.FindName("SidebarColumn");
        var toggle = (Button)window.FindName("SidebarToggle");
        var api = new CommandApi(store,engine);
        string configBefore = File.ReadAllText(Path.Combine(store.Root,"config.json"));
        vm.Search = "测试"; vm.SelectedGroup = "常用测试分类";
        vm.SetSelection(vm.Rows.Where(x => x.Entry.Group == vm.SelectedGroup).Take(2));
        Guid[] selected = vm.SelectedRows.Select(x => x.Entry.Id).ToArray();
        var search = window.FindName("SearchBox");

        window.SetSidebar("hide"); Settle(window);
        check(window.SidebarCollapsed && Math.Abs(column.ActualWidth - 72) < 1, "折叠侧栏完成动画后保留 72 DIP 导航区域");
        check(store.State.SidebarCollapsed && new DataStore(store.Root).State.SidebarCollapsed, "侧栏折叠偏好立即保存并可重新读取");
        check(vm.Search == "测试" && vm.SelectedGroup == "常用测试分类" && selected.SequenceEqual(vm.SelectedRows.Select(x => x.Entry.Id)) && ReferenceEquals(search,window.FindName("SearchBox")), "折叠侧栏保留搜索、分类、多选和原有输入控件");
        check(File.ReadAllText(Path.Combine(store.Root,"config.json")) == configBefore, "侧栏折叠不修改任务配置");
        foreach (var category in vm.CategoryItems)
        {
            var container = (ListBoxItem)categories.ItemContainerGenerator.ContainerFromItem(category);
            check(container != null && container.IsVisible && container.Focusable && AutomationProperties.GetName(container) == category.Name && container.ToolTip?.ToString() == category.Description, "折叠分类可聚焦且保留完整辅助名称与数量提示：" + category.Name);
        }
        var development = vm.CategoryItems.Single(x => x.Name == "开发测试分类");
        categories.SelectedItem = development; Settle(window);
        check(vm.SelectedGroup == development.Name && list.Items.Count == 13, "折叠侧栏分类选择仍筛选对应任务");
        window.SetSidebar("show"); Settle(window);
        check(!window.SidebarCollapsed && Math.Abs(column.ActualWidth - 180) < 1 && !new DataStore(store.Root).State.SidebarCollapsed, "侧栏展开恢复 180 DIP 并保存偏好");
        toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Settle(window);
        check(window.SidebarCollapsed && new DataStore(store.Root).State.SidebarCollapsed, "侧栏按钮可切换并持久化折叠状态");

        var state = Await(UiCommands.ExecuteAsync(window,vm,api,["ui","state"]));
        check(state.Ok && JsonSerializer.SerializeToElement(state.Data,DataStore.Json).GetProperty("SidebarCollapsed").GetBoolean(), "ui state 返回侧栏实际折叠状态");
        var shown = Await(UiCommands.ExecuteAsync(window,vm,api,["ui","sidebar","show"])); Settle(window);
        check(shown.Ok && !window.SidebarCollapsed && !new DataStore(store.Root).State.SidebarCollapsed, "ui sidebar show 展开并保存侧栏");
        var hidden = Await(UiCommands.ExecuteAsync(window,vm,api,["ui","sidebar","hide"])); Settle(window);
        check(hidden.Ok && window.SidebarCollapsed, "ui sidebar hide 折叠侧栏");
        var toggled = Await(UiCommands.ExecuteAsync(window,vm,api,["ui","sidebar","toggle"])); Settle(window);
        check(toggled.Ok && !window.SidebarCollapsed, "ui sidebar toggle 切换侧栏");
        bool rejected = false;
        try { window.SetSidebar("invalid"); } catch (ArgumentException) { rejected = true; }
        check(rejected && !window.SidebarCollapsed && File.ReadAllText(Path.Combine(store.Root,"config.json")) == configBefore, "无效侧栏指令拒绝且不改变任务配置");

        string stateBefore = File.ReadAllText(Path.Combine(store.Root,"state.json"));
        string snapshotPath = Path.Combine(root,"sidebar-collapsed.png");
        var snapshot = Await(UiCommands.ExecuteAsync(window,vm,api,["ui","snapshot","--sidebar","hide","--output",snapshotPath])); Settle(window);
        check(snapshot.Ok && JsonSerializer.SerializeToElement(snapshot.Data,DataStore.Json).GetProperty("SidebarCollapsed").GetBoolean() && File.Exists(snapshotPath) && new FileInfo(snapshotPath).Length > 0 && !window.SidebarCollapsed && File.ReadAllText(Path.Combine(store.Root,"state.json")) == stateBefore, "临时折叠截图实际应用侧栏状态，完成后恢复且不写入临时偏好");
        window.SetSidebar("hide"); Settle(window);
        stateBefore = File.ReadAllText(Path.Combine(store.Root,"state.json"));
        snapshot = Await(UiCommands.ExecuteAsync(window,vm,api,["ui","snapshot","--sidebar","show","--output",Path.Combine(root,"sidebar-expanded.png")])); Settle(window);
        check(snapshot.Ok && !JsonSerializer.SerializeToElement(snapshot.Data,DataStore.Json).GetProperty("SidebarCollapsed").GetBoolean() && window.SidebarCollapsed && File.ReadAllText(Path.Combine(store.Root,"state.json")) == stateBefore, "临时展开截图实际应用侧栏状态，完成后恢复原有折叠状态及持久化内容");
        string invalidOutput = Path.Combine(root,"sidebar-output-directory"); Directory.CreateDirectory(invalidOutput);
        bool failed = false;
        try { Await(UiCommands.ExecuteAsync(window,vm,api,["ui","snapshot","--sidebar","show","--output",invalidOutput])); }
        catch (UnauthorizedAccessException) { failed = true; }
        catch (IOException) { failed = true; }
        Settle(window);
        check(failed && window.SidebarCollapsed && File.ReadAllText(Path.Combine(store.Root,"state.json")) == stateBefore, "截图输出失败仍恢复侧栏且不保存临时状态");

        vm.Search = ""; vm.SelectedGroup = "全部任务"; Settle(window);
        var target = (ListBoxItem)categories.ItemContainerGenerator.ContainerFromItem(development);
        Guid draggedId = entries[0].Id;
        var drag = new DataObject("StartupHub.TaskId",draggedId.ToString());
        var over = DragEvent(drag,target,new Point(8,8),DragDrop.DragOverEvent); target.RaiseEvent(over);
        check(over.Effects == DragDropEffects.Move && (AdornerLayer.GetAdornerLayer(categories)?.GetAdorners(categories)?.Length ?? 0) > 0, "折叠分类拖入事件显示可放置提示");
        var drop = DragEvent(drag,target,new Point(8,8),DragDrop.DropEvent); target.RaiseEvent(drop); Settle(window);
        string destination = new DataStore(store.Root).Config.Entries.Single(x => x.Id == draggedId).Group;
        int remainingMarkers = AdornerLayer.GetAdornerLayer(categories)?.GetAdorners(categories)?.Length ?? 0;
        check(drop.Effects == DragDropEffects.Move && destination == development.Name && remainingMarkers == 0, $"折叠分类放置任务保存归属并清除提示（效果={drop.Effects}，分类={destination}，提示数={remainingMarkers}，消息={vm.Message}）");
        check(store.Config.Entries.Count == 33 && store.Config.Entries.Count(x => x.AutoStart) == 4, "导航和拖放保留 33 个任务及 4 项自启配置");

        vm.SelectedGroup = "全部任务"; list.SelectAll(); Settle(window);
        check(list.SelectionMode == DataGridSelectionMode.Extended && list.SelectionUnit == DataGridSelectionUnit.FullRow && vm.SelectedRows.Count == 33, "任务列表保留扩展多选及全部 33 项选择");
        list.UnselectAll(); list.SelectedItems.Add(vm.Rows[0]); list.SelectedItems.Add(vm.Rows[2]); Settle(window);
        check(vm.SelectedRows.Select(x => x.Entry.Id).ToHashSet().SetEquals(new[] { vm.Rows[0].Entry.Id,vm.Rows[2].Entry.Id }), "列表不连续选择与视图模型同步");
        list.SelectedItem = vm.Rows[0];
        window.ApplyDetailsLayout(900); window.SetDetails("show"); Settle(window);
        check(window.DetailsOverlay && window.DetailsVisible && vm.Selected?.Entry.Id == vm.Rows[0].Entry.Id, "窄窗口侧栏折叠时详情覆盖仍显示选中任务");
        window.SetSidebar("show"); Settle(window);
        check(window.DetailsOverlay && window.DetailsVisible, "窄窗口展开侧栏不丢失详情覆盖状态");
        window.SetDetails("hide"); window.ApplyDetailsLayout(1240); window.SetDetails("show"); Settle(window);
        check(window.DetailsVisible && !window.DetailsOverlay && ((FrameworkElement)window.FindName("DetailPanel")).Width == 320, "返回宽窗口详情以 320 DIP 保留固定布局");
        check(list.EnableRowVirtualization && VirtualizingPanel.GetIsVirtualizing(list) && ScrollViewer.GetCanContentScroll(list), "任务列表保留行虚拟化及兼容虚拟化的内容滚动");
        var bindings = window.InputBindings.OfType<KeyBinding>().ToArray();
        check(bindings.Any(x => x.Key == Key.N && x.Modifiers == ModifierKeys.Control && ReferenceEquals(x.Command,vm.AddCommand)) && bindings.Any(x => x.Key == Key.E && x.Modifiers == ModifierKeys.Control && ReferenceEquals(x.Command,vm.EditCommand)) && bindings.Any(x => x.Key == Key.F5 && ReferenceEquals(x.Command,vm.RefreshCommand)), "Ctrl+N、Ctrl+E 和 F5 保留原有命令绑定");
        vm.ClearFiltersCommand.Execute(null);
        var statusBox=(ComboBox)window.FindName("StatusFilterBox");
        string filterConfig=File.ReadAllText(Path.Combine(store.Root,"config.json"));
        var statuses=new[]{RunStatus.Failed,RunStatus.Missing,RunStatus.Occupied,RunStatus.Skipped};
        for(int i=0;i<statuses.Length;i++)vm.Rows[i].Status=new StartupHub.Core.TaskStatus(statuses[i]);
        statusBox.SelectedItem=statusBox.Items.Cast<Choice<string>>().Single(x=>x.Label=="异常或已跳过");Settle(window);
        check(list.Items.Cast<TaskRow>().Select(x=>x.Entry.Id).ToHashSet().SetEquals(vm.Rows.Take(4).Select(x=>x.Entry.Id)),"异常筛选包含失败、缺失、占用和已跳过任务");
        statusBox.IsDropDownOpen=true;Settle(window);
        Capture((FrameworkElement)((Popup)statusBox.Template.FindName("PART_Popup",statusBox)).Child,Path.Combine(root,"task-filter-labels.png"));statusBox.IsDropDownOpen=false;
        statusBox.SelectedItem=statusBox.Items.Cast<Choice<string>>().Single(x=>x.Label=="已勾选登录自启");Settle(window);
        check(list.Items.Cast<TaskRow>().Select(x=>x.Entry.Id).ToHashSet().SetEquals(vm.Rows.Where(x=>x.AutoStart).Select(x=>x.Entry.Id)) && vm.StatusFilter=="登录自启","新自启筛选名称保留原有筛选值和勾选结果");
        check(File.ReadAllText(Path.Combine(store.Root,"config.json"))==filterConfig,"切换筛选不修改任务或登录自启配置");
        var menuButton=Buttons(window).Single(x=>Equals(x.Content,"清单操作"));menuButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Settle(window);
        Capture(menuButton.ContextMenu,Path.Combine(root,"list-action-labels.png"));menuButton.ContextMenu.IsOpen=false;
    }

    private static void Capture(FrameworkElement visual,string path)
    {
        visual.UpdateLayout();
        var image=new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth),(int)Math.Ceiling(visual.ActualHeight),96,96,PixelFormats.Pbgra32);image.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));using var stream=File.Create(path);encoder.Save(stream);
    }
    private static IEnumerable<Button> Buttons(DependencyObject root)
    {
        if(root is Button button)yield return button;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)foreach(var child in Buttons(VisualTreeHelper.GetChild(root,i)))yield return child;
    }
    private static DragEventArgs DragEvent(IDataObject data, DependencyObject target, Point point, RoutedEvent routed)
    {
        var result = (DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,null,[data,DragDropKeyStates.LeftMouseButton,DragDropEffects.Move,target,point],null)!;
        result.RoutedEvent = routed; return result;
    }
    private static void Settle(Window window)
    {
        Await(Task.Delay(320));
        window.Dispatcher.Invoke(() => window.UpdateLayout(),DispatcherPriority.ApplicationIdle);
    }
    private static void Await(Task task)
    {
        if (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            task.ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() => frame.Continue = false),TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        task.GetAwaiter().GetResult();
    }
    private static T Await<T>(Task<T> task) { Await((Task)task); return task.GetAwaiter().GetResult(); }
}
