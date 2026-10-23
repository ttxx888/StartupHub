using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using StartupHub.App;
using StartupHub.Core;

internal static class ImportChecks
{
    internal static void Run(MainWindow window,MainViewModel vm,DataStore store,Action<bool,string> check,string root)
    {
        var first=Path.Combine(root,"文档甲.txt");var second=Path.Combine(root,"文档乙.txt");var url=Path.Combine(root,"示例网址.url");var link=Path.Combine(root,"演示程序.lnk");var missing=Path.Combine(root,"不存在.txt");
        File.WriteAllText(first,"first");File.WriteAllText(second,"second");File.WriteAllText(url,"[InternetShortcut]\nURL=https://example.org/import\n");
        object shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;object shortcut=((dynamic)shell).CreateShortcut(link);
        try {((dynamic)shortcut).TargetPath=Environment.ProcessPath!;((dynamic)shortcut).Arguments="--fixture \"中文 参数\"";((dynamic)shortcut).WorkingDirectory=root;((dynamic)shortcut).Save();}
        finally {Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell);}
        store.ReplaceConfig(new HubConfig());vm.Reload();vm.SelectedGroup="开发网页";
        window.ShowActivated=false;window.ShowInTaskbar=false;window.Show();window.UpdateLayout();
        string Saved()=>File.ReadAllText(Path.Combine(store.Root,"config.json"));
        T Field<T>(object obj,string name)=>(T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(obj)!;
        var before=Saved();
        Interact<EntryEditor>(()=>vm.ImportFiles([first]),e=>{check(e.Title=="添加任务" && Field<TextBox>(e,"name").Text=="文档甲","单项拖入打开预填的新建表单");e.Close();});
        check(Saved()==before,"取消单项导入不修改配置");
        Interact<EntryEditor>(()=>vm.ImportFiles([first]),e=>{Field<TextBox>(e,"name").Text="我的文档";Click(e,"保存任务");});
        check(store.Config.Entries.Count==1 && store.Config.Entries[0].Name=="我的文档" && store.Config.Entries[0].Group=="开发网页" && !store.Config.Entries[0].AutoStart,"单项导入可编辑名称并继承当前分类，默认不自启");
        before=Saved();vm.ImportFiles([first]);check(Saved()==before && vm.Message.Contains("重复入口"),"单项重复入口不进入添加表单或修改配置");
        Interact<ImportTasksWindow>(()=>vm.ImportFiles([second,link,url,first,missing]),e=>
        {
            var rows=Field<ImportTaskRow[]>(e,"rows");check(rows.Length==5 && rows.Count(x=>x.CanSelect)==3 && rows.Count(x=>x.Selected)==3,"多项预览展示有效、重复和无效项并默认只选择可添加项");
            check(rows.Single(x=>x.Source==missing).Issue.Length>0 && !rows.Single(x=>x.Source==first).Selected,"重复和无效项有说明且不能选入");
            Snapshot(e,root,"import-preview.png");e.Width=650;e.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);Snapshot(e,root,"import-narrow.png");e.Close();
        });
        check(Saved()==before,"取消批量预览不修改配置");
        Interact<ImportTasksWindow>(()=>vm.ImportFiles([second,link,url,first,missing]),e=>
        {
            var rows=Field<ImportTaskRow[]>(e,"rows");rows.Single(x=>x.Source==second).Name="批量改名";rows.Single(x=>x.Source==url).Selected=false;
            var renamed=rows.Single(x=>x.Source==second);renamed.Name="";Click(e,"添加所选");
            check(Field<TextBlock>(e,"validation").Text.Length>0 && e.IsVisible,"批量名称为空时提示并保留预览输入");
            renamed.Name="批量改名";Field<ComboBox>(e,"group").SelectedItem="常用服务";Click(e,"添加所选");
        });
        check(store.Config.Entries.Count==3 && store.Config.Entries.Any(x=>x.Name=="批量改名") && store.Config.Entries.All(x=>x.Url!="https://example.org/import"),"批量添加支持修改名称和取消部分项目");
        check(store.Config.Entries.Where(x=>x.Name!="我的文档").All(x=>x.Group=="常用服务" && !x.AutoStart) && vm.SelectedGroup=="常用服务" && vm.SelectionCount==2,"批量添加统一分类并显示、选中新项目");
        check(store.Config.Entries.Single(x=>x.Name=="演示程序").Path==link && store.Config.Entries.Single(x=>x.Name=="演示程序").Arguments.Count==0,"快捷方式导入保持原入口且不重复传递参数");
        check(store.State.Runs.Count==0 && File.Exists(first) && File.Exists(second) && File.Exists(link),"导入不启动程序、不移动或删除源文件");
        var reloaded=new DataStore(store.Root);check(reloaded.Config.Entries.Count==3,"批量导入配置可重读");
        store.ReplaceConfig(new HubConfig());vm.Reload();window.UpdateLayout();
        var panel=(Border)window.FindName("ListPanel");
        var data=new DataObject(DataFormats.FileDrop,new[] {first});var over=Event(data,panel,DragDrop.DragOverEvent);panel.RaiseEvent(over);
        check(over.Effects==DragDropEffects.Copy,"空清单面板接受外部文件并显示复制效果");
        Exception? dropError=null;
        window.Dispatcher.BeginInvoke(()=>
        {
            var editor=Application.Current.Windows.OfType<EntryEditor>().Single();
            try {Click(editor,"保存任务");}catch(Exception ex){dropError=ex;editor.Close();}
        },DispatcherPriority.ApplicationIdle);
        var drop=Event(data,panel,DragDrop.DropEvent);panel.RaiseEvent(drop);window.Dispatcher.Invoke(()=>{},DispatcherPriority.ContextIdle);
        if(dropError!=null)throw dropError;
        check(drop.Effects==DragDropEffects.Copy && store.Config.Entries.Count==1,"空清单文件放置事件在拖放结束后进入添加流程");
        var foreign=Event(new DataObject(DataFormats.Text,"foreign"),panel,DragDrop.DropEvent);panel.RaiseEvent(foreign);
        check(foreign.Effects==DragDropEffects.None && store.Config.Entries.Count==1,"普通文本拖入不会误触发导入或排序");
        before=Saved();
        Interact<ImportTasksWindow>(()=>vm.ImportFiles([missing,missing]),e=>
        {
            check(!Descendants<Button>(e).Single(x=>x.Content?.ToString()=="添加所选").IsEnabled,"全部无效时禁用添加并保留问题说明");e.Close();
        });
        check(Saved()==before,"全部无效的导入不修改配置");
        var network=new TaskEntry {Name="等待网络",Kind=TaskKind.Network,Url="http://127.0.0.1:1/",DirectProbeUrl="http://127.0.0.1:1/",ProxyUrl="http://127.0.0.1:1",ProxyProbeUrl="https://127.0.0.1:1/",WaitSeconds=1,AutoStart=true};
        store.Config.Entries.Add(network);store.SaveConfig();var batch=App.Engine.StartBatchAsync();before=Saved();
        vm.ImportFiles([second]);var blocked=Event(data,panel,DragDrop.DragOverEvent);panel.RaiseEvent(blocked);
        check(Saved()==before && blocked.Effects==DragDropEffects.None,"自启清单执行中拒绝文件拖入且不修改配置");
        App.Engine.CancelPending();var frame=new DispatcherFrame();var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};var deadline=DateTime.UtcNow.AddSeconds(8);
        timer.Tick+=(_,_)=>{if(batch.IsCompleted || DateTime.UtcNow>deadline)frame.Continue=false;};timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
        if(!batch.IsCompleted)throw new TimeoutException("等待导入测试清单结束超时。");batch.GetAwaiter().GetResult();
    }
    private static void Interact<T>(Action invoke,Action<T> action) where T:Window
    {
        Exception? error=null;
        Application.Current.Dispatcher.BeginInvoke(()=>{var dialog=Application.Current.Windows.OfType<T>().Single();try{action(dialog);}catch(Exception ex){error=ex;dialog.Close();}},DispatcherPriority.ApplicationIdle);
        invoke();if(error!=null)throw error;
    }
    private static void Click(Window window,string text)=>Descendants<Button>(window).Single(x=>x.Content?.ToString()==text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T match)yield return match;foreach(var nested in Descendants<T>(child))yield return nested;}
    }
    private static DragEventArgs Event(IDataObject data,DependencyObject target,RoutedEvent routed)
    {
        var e=(DragEventArgs)Activator.CreateInstance(typeof(DragEventArgs),BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,null,[data,DragDropKeyStates.LeftMouseButton,DragDropEffects.Copy,target,new Point(8,8)],null)!;e.RoutedEvent=routed;return e;
    }
    private static void Snapshot(Window window,string root,string name)
    {
        window.UpdateLayout();var content=(FrameworkElement)window.Content;int width=(int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right),height=(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom);
        var visual=new DrawingVisual();using(var drawing=visual.RenderOpen()) {drawing.DrawRectangle((Brush)Application.Current.FindResource("CanvasBrush"),null,new Rect(0,0,width,height));drawing.DrawRectangle(new VisualBrush(content){ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,width,height),Stretch=Stretch.Fill},null,new Rect(0,0,width,height));}
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(root,name));encoder.Save(stream);
    }
}
