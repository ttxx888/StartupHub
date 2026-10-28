using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using StartupHub.App;
using StartupHub.Core;

internal static class DragChecks
{
    internal static void Run(MainWindow window, MainViewModel vm, DataStore store, Action<bool, string> check)
    {
        var a = new TaskEntry { Name="共同甲", Path=Environment.ProcessPath!, Group="常用服务", AutoStart=true, Arguments=["--fixture", "中文 参数"] };
        var b = new TaskEntry { Name="隐藏乙", Path=Environment.ProcessPath!, Group="开发网页", DependsOn=a.Id };
        var c = new TaskEntry { Name="共同丙", Kind=TaskKind.Url, Url="http://127.0.0.1:54321/", Group="常用服务" };
        var d = new TaskEntry { Name="最后丁", Kind=TaskKind.Url, Url="http://127.0.0.1:54322/", Group="直接打开" };
        var e = new TaskEntry { Name="隐藏戊", Kind=TaskKind.Url, Url="http://127.0.0.1:54323/", Group="开发网页" };
        store.ReplaceConfig(new() { Entries=[a,b,c,d,e] }); vm.Reload();
        bool Order(params TaskEntry[] expected) => store.Config.Entries.Select(x=>x.Id).SequenceEqual(expected.Select(x=>x.Id));
        check(vm.ReorderTask(a.Id,c.Id,true) && Order(b,c,a,d,e),"向下拖到目标之后");
        check(vm.ReorderTask(d.Id,b.Id,false) && Order(d,b,c,a,e),"向上拖到目标之前");
        string saved = File.ReadAllText(Path.Combine(store.Root,"config.json"));
        check(!vm.ReorderTask(a.Id,a.Id,false) && File.ReadAllText(Path.Combine(store.Root,"config.json"))==saved,"拖到自身不更改配置");
        vm.Search="共同";
        check(vm.ReorderTask(a.Id,c.Id,false) && Order(d,b,a,c,e),"筛选后排序保留隐藏任务相对顺序");
        vm.Search=""; vm.SelectedGroup="常用服务";
        vm.Selected=vm.Rows.Single(x=>x.Entry.Id==c.Id);
        check(vm.MoveTaskToGroup(c.Id,"开发网页") && Order(d,b,a,e,c),"跨分类移动到目标分类末尾");
        check(vm.SelectedGroup=="常用服务" && vm.Selected?.Entry.Id==a.Id && vm.CategoryItems.Single(x=>x.Name=="开发网页").Count==3,"移动后保留当前分类并刷新数量和选择");
        check(!vm.MoveTaskToGroup(c.Id,"开发网页"),"拖到原分类不改变顺序");
        bool rejected=false; try { vm.MoveTaskToGroup(c.Id,"全部任务"); } catch(ArgumentException) { rejected=true; }
        check(rejected && c.Group=="开发网页","全部任务不作为分类目标");
        var reloaded=new DataStore(store.Root);
        check(reloaded.Config.Entries.Select(x=>x.Id).SequenceEqual(store.Config.Entries.Select(x=>x.Id)) && reloaded.Config.Entries.Single(x=>x.Id==c.Id).Group=="开发网页","重读配置保留顺序与分类");
        check(a.AutoStart && a.Arguments.SequenceEqual(new[]{"--fixture","中文 参数"}) && b.DependsOn==a.Id,"拖放保留自启参数和依赖 ID");

        vm.SelectedGroup="全部任务";
        window.ShowActivated=false; window.ShowInTaskbar=false; window.Show(); window.UpdateLayout();
        var list=(DataGrid)window.FindName("TaskList"); var categories=(ListBox)window.FindName("CategoryList");
        var category=vm.CategoryItems.Single(x=>x.Name=="直接打开");
        var item=(ListBoxItem)categories.ItemContainerGenerator.ContainerFromItem(category);
        var data=new DataObject("StartupHub.TaskId",b.Id.ToString());
        var over=Event(data,item,new Point(8,8),DragDrop.DragOverEvent); item.RaiseEvent(over);
        check(over.Effects==DragDropEffects.Move && (AdornerLayer.GetAdornerLayer(categories)?.GetAdorners(categories)?.Length??0)>0,"分类拖入事件显示可放置提示");
        var drop=Event(data,item,new Point(8,8),DragDrop.DropEvent); item.RaiseEvent(drop);
        check(drop.Effects==DragDropEffects.Move && b.Group=="直接打开" && (AdornerLayer.GetAdornerLayer(categories)?.GetAdorners(categories)?.Length??0)==0,"分类放置事件保存并清除提示");
        list.UpdateLayout();
        var row=(DataGridRow)list.ItemContainerGenerator.ContainerFromItem(vm.Rows.Single(x=>x.Entry.Id==d.Id));
        var point=row.TranslatePoint(new Point(12,row.ActualHeight*.75),list);
        data=new DataObject("StartupHub.TaskId",e.Id.ToString());
        var listOver=Event(data,list,point,DragDrop.DragOverEvent); list.RaiseEvent(listOver);
        check(listOver.Effects==DragDropEffects.Move && (AdornerLayer.GetAdornerLayer(list)?.GetAdorners(list)?.Length??0)>0,"列表拖入事件显示插入线");
        var listDrop=Event(data,list,point,DragDrop.DropEvent); list.RaiseEvent(listDrop);
        check(listDrop.Effects==DragDropEffects.Move && Order(d,e,b,a,c),"列表放置事件按上下半行定位");
        saved=File.ReadAllText(Path.Combine(store.Root,"config.json"));
        var foreign=Event(new DataObject(DataFormats.Text,"foreign"),list,point,DragDrop.DropEvent); list.RaiseEvent(foreign);
        check(foreign.Effects==DragDropEffects.None && File.ReadAllText(Path.Combine(store.Root,"config.json"))==saved,"外部拖入不修改任务");
        string tasksBefore=System.Text.Json.JsonSerializer.Serialize(store.Config.Entries,DataStore.Json);
        vm.SelectedGroup="开发网页";
        check(vm.ReorderCategory("直接打开","常用服务",false) && store.Config.Groups.SequenceEqual(new[]{"直接打开","常用服务","开发网页"}),"侧栏分类可向上排序");
        check(vm.SelectedGroup=="开发网页" && vm.CategoryItems.Select(x=>x.Name).SequenceEqual(new[]{"全部任务","直接打开","常用服务","开发网页"}),"分类排序保留当前视图并固定汇总项");
        check(vm.ReorderCategory("直接打开","开发网页",true) && store.Config.Groups.SequenceEqual(new[]{"常用服务","开发网页","直接打开"}),"侧栏分类可向下排序");
        check(new DataStore(store.Root).Config.Groups.SequenceEqual(store.Config.Groups),"分类顺序保存后可重读");
        check(!vm.ReorderCategory("开发网页","开发网页",false),"分类拖到自身不保存变更");
        rejected=false; try { vm.ReorderCategory("全部任务","常用服务",false); } catch(ArgumentException) { rejected=true; }
        check(rejected,"全部任务不能被重新排序");
        window.UpdateLayout(); categories.UpdateLayout();
        var targetCategory=vm.CategoryItems.Single(x=>x.Name=="常用服务");
        item=(ListBoxItem)categories.ItemContainerGenerator.ContainerFromItem(targetCategory);
        data=new DataObject("StartupHub.Category","直接打开");
        over=Event(data,item,new Point(8,item.ActualHeight*.75),DragDrop.DragOverEvent); item.RaiseEvent(over);
        check(over.Effects==DragDropEffects.Move && (AdornerLayer.GetAdornerLayer(categories)?.GetAdorners(categories)?.Length??0)>0,"分类排序拖入显示插入线");
        drop=Event(data,item,new Point(8,item.ActualHeight*.75),DragDrop.DropEvent); item.RaiseEvent(drop);
        check(drop.Effects==DragDropEffects.Move && store.Config.Groups.SequenceEqual(new[]{"常用服务","直接打开","开发网页"}),"分类排序放置事件按半行定位");
        check(System.Text.Json.JsonSerializer.Serialize(store.Config.Entries,DataStore.Json)==tasksBefore,"分类排序保留所有任务的顺序和归属");
        var cross=Event(data,list,new Point(10,10),DragDrop.DropEvent); list.RaiseEvent(cross);
        check(cross.Effects==DragDropEffects.None,"分类不能拖入任务列表");
        var others=store.Config.Entries.Where(x=>x.Id!=c.Id).Select(x=>x.Id).ToArray();
        vm.SelectedGroup="开发网页";vm.Search="共同";vm.Selected=vm.Rows.Single(x=>x.Entry.Id==c.Id);
        window.SetDetails("show");window.UpdateLayout();
        var more=VisualButtons(window).Single(x=>Equals(x.Content,"更多 ···"));
        more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var top=more.ContextMenu.Items.OfType<MenuItem>().Single(x=>Equals(x.Header,"置顶"));
        check(top.Command?.CanExecute(null)==true,"任务更多操作提供可执行的置顶入口");
        more.ContextMenu.UpdateLayout();
        var menuImage=new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(more.ContextMenu.ActualWidth),(int)Math.Ceiling(more.ContextMenu.ActualHeight),96,96,PixelFormats.Pbgra32);
        menuImage.Render(more.ContextMenu);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(menuImage));
        using(var output=File.Create(Path.Combine(store.Root,"move-to-top-menu.png"))) encoder.Save(output);
        top.Command!.Execute(null);more.ContextMenu.IsOpen=false;
        check(store.Config.Entries.First().Id==c.Id && vm.Selected?.Entry.Id==c.Id && vm.SelectedGroup=="开发网页" && vm.Search=="共同","筛选下置顶移到清单第一位并保留选择与筛选");
        check(store.Config.Entries.Skip(1).Select(x=>x.Id).SequenceEqual(others) && c.Group=="开发网页" && b.DependsOn==a.Id,"置顶保留其余相对顺序、分类和依赖");
        check(new DataStore(store.Root).Config.Entries.First().Id==c.Id,"重读配置保留置顶顺序");
        check(!vm.MoveToTopCommand.CanExecute(null),"已位于第一位时禁用置顶");
        var longList=Enumerable.Range(0,100).Select(i=>new TaskEntry {Name="滚动任务 "+i,Kind=TaskKind.Url,Url="https://example.com/"+i,Group="常用服务"}).ToList();
        store.ReplaceConfig(new HubConfig {Entries=longList});vm.Search="";vm.SelectedGroup="全部任务";vm.Reload();window.UpdateLayout();Pump(150);
        var scroll=VisualScrolls(list).First();
        var scrollData=new DataObject("StartupHub.TaskId",longList[50].Id.ToString());
        string beforeScroll=File.ReadAllText(Path.Combine(store.Root,"config.json"));
        Point DragPoint(Point position) {
            var origin=Event(scrollData,list,new Point(0,0),DragDrop.DragOverEvent).GetPosition(list);
            var unit=Event(scrollData,list,new Point(1,1),DragDrop.DragOverEvent).GetPosition(list);
            return new Point((position.X-origin.X)/(unit.X-origin.X),(position.Y-origin.Y)/(unit.Y-origin.Y));
        }
        double ScrollAt(double y) {
            scroll.ScrollToVerticalOffset(1600);Pump(60);window.UpdateLayout();double start=scroll.VerticalOffset;
            list.RaiseEvent(Event(scrollData,list,DragPoint(new Point(16,y)),DragDrop.DragOverEvent));Pump(280);
            list.RaiseEvent(Event(scrollData,list,DragPoint(new Point(16,list.ActualHeight/2)),DragDrop.DragOverEvent));window.UpdateLayout();
            return scroll.VerticalOffset-start;
        }
        double near=ScrollAt(1), outer=ScrollAt(44);
        check(near < -120 && Math.Abs(near)>Math.Abs(outer)*1.6,"拖到上边缘快速滚动且越贴边越快");
        double down=ScrollAt(list.ActualHeight-1);
        File.WriteAllText(Path.Combine(store.Root,"drag-scroll-metrics.json"),System.Text.Json.JsonSerializer.Serialize(new {UpperEdgePixels=-near,UpperZoneOuterPixels=-outer,LowerEdgePixels=down,DurationMs=280},DataStore.Json));
        check(down>120,"拖到下边缘快速滚动");
        double stopped=scroll.VerticalOffset;Pump(120);
        check(Math.Abs(scroll.VerticalOffset-stopped)<.5,"离开边缘后停止自动滚动");
        scroll.ScrollToVerticalOffset(0);window.UpdateLayout();list.RaiseEvent(Event(scrollData,list,DragPoint(new Point(16,1)),DragDrop.DragOverEvent));Pump(120);
        check(scroll.VerticalOffset==0,"到达列表顶部后保持边界");
        list.RaiseEvent(Event(scrollData,list,DragPoint(new Point(16,list.ActualHeight/2)),DragDrop.DragOverEvent));
        check(File.ReadAllText(Path.Combine(store.Root,"config.json"))==beforeScroll,"自动滚动不改变任务顺序与配置");
    }
    private static void Pump(int milliseconds)
    {
        var frame=new System.Windows.Threading.DispatcherFrame();var timer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromMilliseconds(milliseconds)};
        timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();System.Windows.Threading.Dispatcher.PushFrame(frame);
    }
    private static IEnumerable<ScrollViewer> VisualScrolls(DependencyObject root)
    {
        if(root is ScrollViewer viewer) yield return viewer;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var child in VisualScrolls(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    private static IEnumerable<Button> VisualButtons(DependencyObject root)
    {
        if(root is Button button) yield return button;
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
            foreach(var child in VisualButtons(VisualTreeHelper.GetChild(root,i))) yield return child;
    }
    private static DragEventArgs Event(IDataObject data, DependencyObject target, Point point, RoutedEvent routed)
    {
        var result=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public,null,[data,DragDropKeyStates.LeftMouseButton,DragDropEffects.Move,target,point],null)!;
        result.RoutedEvent=routed; return result;
    }
}
