using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StartupHub.App;
using StartupHub.Core;

internal static class BatchChecks
{
    internal static void Run(MainWindow window, MainViewModel vm, DataStore store, Action<bool,string> check, string root)
    {
        var a = new TaskEntry { Name = "共同甲服务", Path = Environment.ProcessPath!, Group = "常用服务", AutoStart = true };
        var b = new TaskEntry { Name = "共同乙服务", Path = Environment.ProcessPath!, Group = "开发网页", DependsOn = a.Id };
        var c = new TaskEntry { Name = "网页工具", Kind = TaskKind.Url, Url = "http://127.0.0.1:54321/", Group = "直接打开" };
        var d = new TaskEntry { Name = "缺失文件", Kind = TaskKind.File, Path = Path.Combine(root,"missing.html"), Group = "常用服务" };
        store.ReplaceConfig(new() { Entries = [a,b,c,d] }); vm.Reload(); vm.SelectedGroup = "全部任务";
        window.ShowActivated = false; window.ShowInTaskbar = false; window.Show(); window.UpdateLayout();
        var list = (DataGrid)window.FindName("TaskList"); var categories = (ListBox)window.FindName("CategoryList");
        TaskRow Row(Guid id) => vm.Rows.Single(x => x.Entry.Id == id);
        void Select(params Guid[] ids) => vm.SetSelection(ids.Select(Row));
        string Saved() => File.ReadAllText(Path.Combine(store.Root,"config.json"));
        check(list.SelectionMode == DataGridSelectionMode.Extended && list.SelectionUnit == DataGridSelectionUnit.FullRow, "主列表使用原生 Ctrl / Shift 整行多选");
        list.UnselectAll(); list.SelectedItems.Add(Row(a.Id)); list.SelectedItems.Add(Row(b.Id));
        check(vm.SelectionCount == 2 && vm.HasMultipleSelection && vm.DetailTitle == "已选 2 项", "界面多选同步数量和详情摘要");
        check(!vm.EditCommand.CanExecute(null) && !vm.StartCommand.CanExecute(null) && vm.DeleteCommand.CanExecute(null), "多选启用批量操作并禁用详情单项命令");
        check(ApplicationCommands.SelectAll.CanExecute(null,list), "列表提供 Ctrl+A 对应的全选命令");
        ApplicationCommands.SelectAll.Execute(null,list);
        check(list.SelectedItems.Count == 4 && vm.SelectionCount == 4, "全选命令选中当前可见项目");
        vm.Search = "共同";
        check(vm.SelectionCount == 0 && list.SelectedItems.Count == 0, "改变搜索清除旧选择");
        ApplicationCommands.SelectAll.Execute(null,list);
        check(vm.SelectionCount == 2 && vm.SelectedRows.All(x => x.Entry.Name.Contains("共同")), "筛选后的全选不包含隐藏项目");
        vm.Search = ""; Select(a.Id,b.Id);
        vm.Reload();
        check(vm.SelectionCount == 2 && list.SelectedItems.Count == 2, "配置刷新按稳定 ID 保留多选");
        vm.RefreshAsync().GetAwaiter().GetResult();
        window.Dispatcher.Invoke(() => {}, DispatcherPriority.ContextIdle);
        check(vm.SelectionCount == 2 && list.SelectedItems.Count == 2, "状态刷新保留仍可见的选择");
        list.UpdateLayout();
        var selectedContainer = (DataGridRow)list.ItemContainerGenerator.ContainerFromItem(Row(a.Id));
        var selectedName = Descendants<TextBlock>(selectedContainer).First(x => x.Text == a.Name);
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent, Source = selectedName };
        typeof(MainWindow).GetMethod("OnTaskMouseDown",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[list,down]);
        check(down.Handled && vm.SelectionCount == 2, "从多选中的行按下鼠标保留整组以便拖放");
        typeof(MainWindow).GetMethod("OnTaskMouseUp",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[list,down]);
        check(vm.SelectionCount == 1 && vm.Selected?.Entry.Id == a.Id, "普通点击已选行在松开后恢复单选");
        Select(a.Id,b.Id);
        var statusBefore = App.Engine.Status(a.Id);
        var startButton = new Button { DataContext = Row(d.Id) };
        typeof(MainWindow).GetMethod("OnRowStart",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,[startButton,new RoutedEventArgs(Button.ClickEvent)]);
        check(App.Engine.Status(d.Id).Status == RunStatus.Missing && App.Engine.Status(a.Id) == statusBefore && vm.SelectionCount == 2, "多选时行内启动只处理对应行并保留选择");
        vm.SelectedGroup = "常用服务";
        check(vm.SelectionCount == 0, "切换分类清除选择");
        Select(a.Id); vm.Reload();
        check(vm.SelectedGroup == "常用服务" && vm.SelectionCount == 1, "重新加载保留当前分类和可见选择");
        vm.StatusFilter = "登录自启";
        check(vm.SelectionCount == 0, "切换状态筛选清除选择");
        var selectedResponse = window.ExecuteCommandAsync(["ui","select",d.Id.ToString()]).GetAwaiter().GetResult();
        check(selectedResponse.Ok && vm.Selected?.Entry.Id == d.Id && vm.SelectedGroup == "全部任务" && vm.StatusFilter == "全部状态", "CLI 选择隐藏项目时切换到可见视图并保持单项行为");
        vm.ClearFiltersCommand.Execute(null); Select(a.Id,b.Id);
        string before = Saved();
        Interact<MoveTasksWindow>(() => vm.MoveSelectionCommand.Execute(null), dialog => dialog.Close());
        check(Saved() == before && vm.SelectionCount == 2, "取消分类选择不改配置或选择");
        Interact<MoveTasksWindow>(() => vm.MoveSelectionCommand.Execute(null), dialog =>
        {
            Descendants<ComboBox>(dialog).Single().SelectedItem = "直接打开";
            Click(dialog,"移动");
        });
        check(store.Config.Entries.Select(x => x.Id).SequenceEqual(new[] {c.Id,a.Id,b.Id,d.Id}) && new DataStore(store.Root).Config.Entries.Single(x => x.Id == b.Id).Group == "直接打开", "菜单批量移动保存分类并保持原相对顺序");
        check(vm.SelectionCount == 2 && list.SelectedItems.Count == 2, "批量移动后保留仍可见的多选");
        var category = vm.CategoryItems.Single(x => x.Name == "开发网页");
        categories.UpdateLayout();
        var item = (ListBoxItem)categories.ItemContainerGenerator.ContainerFromItem(category);
        var data = new DataObject("StartupHub.TaskIds",new[] {b.Id.ToString(),a.Id.ToString()});
        var over = Event(data,item,new Point(8,8),DragDrop.DragOverEvent); item.RaiseEvent(over);
        check(over.Effects == DragDropEffects.Move, "多项拖入分类提供可放置反馈");
        var drop = Event(data,item,new Point(8,8),DragDrop.DropEvent); item.RaiseEvent(drop);
        check(drop.Effects == DragDropEffects.Move && store.Config.Entries.Select(x => x.Id).SequenceEqual(new[] {c.Id,d.Id,a.Id,b.Id}), "多项拖到分类按清单原顺序移到末尾");
        before = Saved();
        var listDrop = Event(data,list,new Point(8,8),DragDrop.DropEvent); list.RaiseEvent(listDrop);
        check(listDrop.Effects == DragDropEffects.None && Saved() == before, "多项拖动不会意外触发单项排序");
        vm.SelectedGroup = "开发网页"; Select(a.Id,b.Id);
        check(vm.MoveTasksToGroup(new[] {a.Id,b.Id}, "常用服务") && vm.SelectionCount == 0 && list.SelectedItems.Count == 0, "移出当前分类后清除不可见的选择");
        vm.SelectedGroup = "全部任务"; Select(a.Id,c.Id);
        before = Saved();
        Interact<DeleteTasksWindow>(() => vm.DeleteCommand.Execute(null), dialog =>
        {
            check(Descendants<TextBlock>(dialog).Any(t => t.Text == "共同乙服务 → 共同甲服务"), "删除确认列出未选项目将解除的依赖");
            check(Descendants<Button>(dialog).Single(x => x.IsDefault).Content.ToString() == "取消", "删除确认默认操作为取消");
            Snapshot(dialog,root,"delete-preview.png"); dialog.Close();
        });
        check(Saved() == before && vm.SelectionCount == 2, "取消删除保留配置、依赖和多选");
        SnapshotMain(window,root,1240,"batch-wide.png");
        SnapshotMain(window,root,900,"batch-narrow.png");
        Interact<DeleteTasksWindow>(() => vm.DeleteCommand.Execute(null), dialog => Click(dialog,"删除 2 项"));
        check(store.Config.Entries.Select(x => x.Id).ToHashSet().SetEquals(new[] {b.Id,d.Id}) && store.Config.Entries.Single(x => x.Id == b.Id).DependsOn == null, "确认后仅删除所选项目并解除外部依赖");
        check(new DataStore(store.Root).Config.Entries.Single(x => x.Id == b.Id).DependsOn == null && vm.SelectionCount == 0, "删除结果可重读且不保留已删除选择");
        vm.SelectedGroup = "全部任务"; Select(d.Id);
        var network = new TaskEntry { Name="等待网络", Kind=TaskKind.Network, Url="http://127.0.0.1:1/", DirectProbeUrl="http://127.0.0.1:1/", ProxyUrl="http://127.0.0.1:1", ProxyProbeUrl="https://127.0.0.1:1/", WaitSeconds=1, AutoStart=true };
        store.Config.Entries.Add(network); store.SaveConfig();
        var batch = App.Engine.StartBatchAsync();
        before = Saved();
        check(!vm.CanArrange && !vm.DeleteCommand.CanExecute(null) && !vm.MoveSelectionCommand.CanExecute(null) && !vm.MoveTasksToGroup(new[] {d.Id},"开发网页") && Saved() == before, "自启清单执行中禁止删除和批量移动");
        App.Engine.CancelPending();
        PumpUntil(window,() => batch.IsCompleted); batch.GetAwaiter().GetResult();
    }

    private static void Interact<T>(Action invoke, Action<T> action) where T : Window
    {
        Exception? error = null;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            var dialog = Application.Current.Windows.OfType<T>().Single();
            try { action(dialog); } catch(Exception ex) { error = ex; dialog.Close(); }
        },DispatcherPriority.ApplicationIdle);
        invoke();
        if(error != null) throw error;
    }
    private static void Click(Window window, string text) => Descendants<Button>(window).Single(x => x.Content?.ToString() == text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
        {
            var child = VisualTreeHelper.GetChild(parent,i);
            if(child is T match) yield return match;
            foreach(var item in Descendants<T>(child)) yield return item;
        }
    }
    private static DragEventArgs Event(IDataObject data, DependencyObject target, Point point, RoutedEvent routed)
    {
        var e=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,[data,DragDropKeyStates.LeftMouseButton,DragDropEffects.Move,target,point],null)!;
        e.RoutedEvent=routed; return e;
    }
    private static void SnapshotMain(MainWindow window,string root,int width,string name)
    {
        double previousWidth=window.Width;
        window.Width=width; window.Dispatcher.Invoke(() => {},DispatcherPriority.ApplicationIdle); window.UpdateLayout(); Snapshot(window,root,name);
        window.Width=previousWidth; window.Dispatcher.Invoke(() => {},DispatcherPriority.ApplicationIdle); window.UpdateLayout();
    }
    private static void Snapshot(Window window,string root,string name)
    {
        window.UpdateLayout();
        var content=(FrameworkElement)window.Content;
        int width=(int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right), height=(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom);
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)Application.Current.FindResource("CanvasBrush"),null,new Rect(0,0,width,height));
            drawing.DrawRectangle(new VisualBrush(content) { ViewboxUnits=BrushMappingMode.Absolute, Viewbox=new Rect(0,0,width,height), Stretch=Stretch.Fill },null,new Rect(0,0,width,height));
        }
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream=File.Create(Path.Combine(root,name)); encoder.Save(stream);
    }
    private static void PumpUntil(Window window,Func<bool> done)
    {
        var until=DateTime.UtcNow.AddSeconds(8); var frame=new DispatcherFrame();
        var timer=new DispatcherTimer(DispatcherPriority.Background,window.Dispatcher) { Interval=TimeSpan.FromMilliseconds(20) };
        timer.Tick+=(_,_)=> { if(done() || DateTime.UtcNow>=until) frame.Continue=false; };
        timer.Start(); try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
        if(!done()) throw new TimeoutException("等待自启测试结束超时。");
    }
}
