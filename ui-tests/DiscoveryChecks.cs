using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StartupHub.App;
using StartupHub.Core;

internal static class DiscoveryChecks
{
    public static void RunLive(MainWindow window,MainViewModel vm,DataStore store,Action<bool,string> check,string fixturePath)
    {
        window.ShowFromTray();
        System.Threading.SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(window.Dispatcher));
        using var fixture=System.Text.Json.JsonDocument.Parse(File.ReadAllText(fixturePath));
        int html=fixture.RootElement.GetProperty("Html").GetInt32(), xhtml=fixture.RootElement.GetProperty("Xhtml").GetInt32();
        string filePath=Path.Combine(store.Root,"本地验收.html"),catalog=Path.Combine(store.Root,"reference.json");
        File.WriteAllText(filePath,"<html><title>本地 HTML 验收</title><img src='assets/icon.png'></html>");
        File.WriteAllText(catalog,System.Text.Json.JsonSerializer.Serialize(new HubConfig {Entries=[new() {Name="本地 HTML 验收",Kind=TaskKind.File,Path=filePath}]},DataStore.Json));
        App.Engine.DiscoveryCatalogPath=catalog;
        var method=typeof(MainViewModel).GetMethod("Discover",BindingFlags.Instance|BindingFlags.NonPublic)!;
        var scan=(Task)method.Invoke(vm,null)!;
        check(vm.IsDiscovering && !vm.DiscoverCommand.CanExecute(null) && vm.Message.Contains("扫描"),"扫描期间显示状态并禁用重复扫描");
        check(((Task)method.Invoke(vm,null)!).IsCompleted && ReferenceEquals(App.Engine.DiscoverAsync(),App.Engine.DiscoverAsync()),"界面重复操作被忽略且核心复用正在进行的扫描");
        Exception? error=null; bool handled=false;
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(30)};
        timer.Tick+=(_,_)=>
        {
            if(handled || Application.Current.Windows.OfType<DiscoverWindow>().FirstOrDefault(x=>x.IsVisible) is not {} dialog)return;
            handled=true;
            try
            {
                var list=(ListBox)typeof(DiscoverWindow).GetField("list",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(dialog)!;
                var pages=list.Items.Cast<DiscoveredPage>().Where(x=>x.Port==html || x.Port==xhtml).ToArray();
                check(pages.Length==2,"实际扫描结果包含 HTML 和 XHTML 测试页面");
                foreach(var page in pages)list.SelectedItems.Add(page);
                var local=list.Items.Cast<DiscoveredPage>().Single(x=>x.Kind==TaskKind.File&&x.Path==filePath);list.SelectedItems.Add(local);
                Descendants<ComboBox>(dialog).Single().SelectedItem="开发网页";
                Descendants<Button>(dialog).Single(x=>x.Content?.ToString()=="添加选中").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch(Exception ex){error=ex;dialog.Close();}
        };
        var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(40);
        var done=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(30)};
        done.Tick+=(_,_)=>{if(scan.IsCompleted || DateTime.UtcNow>deadline)frame.Continue=false;};
        timer.Start();done.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();done.Stop();}
        if(error!=null)throw error;
        if(!scan.IsCompleted)throw new TimeoutException("发现网页验收超时"); scan.GetAwaiter().GetResult();
        var saved=new DataStore(store.Root).Config.Entries.Where(x=>x.Port==html || x.Port==xhtml).ToArray();
        check(saved.Length==2 && saved.All(x=>!x.AutoStart && x.Kind==TaskKind.Url && x.Group=="开发网页"),"多选网页统一分类保存且默认不自启");
        var savedFile=new DataStore(store.Root).Config.Entries.Single(x=>x.Path==filePath);
        check(savedFile.Kind==TaskKind.File&&savedFile.Url==""&&!savedFile.AutoStart&&File.ReadAllText(filePath).Contains("assets/icon.png"),"混合添加的 HTML 保存为本地文件且保留相对资源引用");
        check(!vm.IsDiscovering && vm.DiscoverCommand.CanExecute(null) && vm.SelectionCount==3 && vm.SelectedRows.All(x=>vm.TasksView.Contains(x)),"扫描完成恢复按钮并显示新增任务");
    }
    public static void Run(MainWindow owner, Action<bool,string> check, string root)
    {
        owner.ShowFromTray();
        var pages=new List<DiscoveredPage> {
            new("本地项目管理","http://127.0.0.1:43210/",43210,1,"node"),
            new("用于核对长标题与长地址布局的本地网页服务入口","http://localhost:43211/workspace/projects/long-address/preview",43211,2,"python")
        };
        var dialog=new DiscoverWindow(pages,["常用服务","开发网页"],"开发网页") {Owner=owner};
        T Field<T>(string name)=>(T)typeof(DiscoverWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(dialog)!;
        Interact(dialog,()=>
        {
            var list=Field<ListBox>("list"); var add=Field<Button>("add"); var preview=Field<Button>("preview");
            check(list.SelectionMode==SelectionMode.Extended && !add.IsEnabled && !preview.IsEnabled,"初始未选择时禁用预览和添加");
            check(Descendants<ComboBox>(dialog).Single().SelectedItem?.ToString()=="开发网页","默认使用当前具体分类");
            list.SelectedIndex=0;
            check(add.IsEnabled && preview.IsEnabled,"单选可预览并添加");
            list.SelectAll();
            check(!preview.IsEnabled && add.IsEnabled && Field<TextBlock>("summary").Text.Contains("已选 2 个"),"多选显示数量且预览只接受单项");
            check(Descendants<TextBlock>(dialog).Any(t=>t.Text=="node") && Descendants<TextBlock>(dialog).Any(t=>t.Text==pages[0].Url),"结果同时显示所属程序与地址");
            Snapshot(dialog,root,"discovery-preview.png");
            dialog.Width=520;dialog.Height=420;dialog.UpdateLayout(); Snapshot(dialog,root,"discovery-narrow.png");
            var bottom=add.TransformToAncestor((Visual)dialog.Content).Transform(new Point(0,add.ActualHeight));
            check(bottom.Y<=((FrameworkElement)dialog.Content).ActualHeight && add.ActualWidth>0,"窄窗口底部添加按钮可见");
            list.UnselectAll(); check(!add.IsEnabled && !preview.IsEnabled,"清空选择恢复禁用状态");
            list.SelectAll();add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        });
        check(dialog.SelectedPages.SequenceEqual(pages) && dialog.Group=="开发网页","确认保留多选页面与分类");
        var cancel=new DiscoverWindow(pages,["常用服务"]);
        Interact(cancel,()=>cancel.Close()); check(cancel.SelectedPages.Count==0,"取消不返回添加项目");
        var empty=new DiscoverWindow([],["常用服务"]);
        Interact(empty,()=>
        {
            check(Descendants<TextBlock>(empty).Any(x=>x.Text.Contains("暂未发现可添加的网页")),"空结果给出明确提示");
            Snapshot(empty,root,"discovery-empty.png"); empty.Close();
        });
        string folderPath=Path.Combine(root,"html-folder");Directory.CreateDirectory(folderPath);File.WriteAllText(Path.Combine(folderPath,"页面.html"),"<html><title>文件夹页面</title></html>");
        var folderDialog=new DiscoverWindow([],["常用服务"],scanIssues:[new("https://localhost:47990/","需要证书确认：根证书不受信任")]);
        Exception? folderError=null;
        folderDialog.Dispatcher.BeginInvoke(async()=>
        {
            try
            {
                await folderDialog.AddFolderAsync(folderPath);
                var list=(ListBox)typeof(DiscoverWindow).GetField("list",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(folderDialog)!;
                check(list.Items.Count==1&&list.Items[0] is DiscoveredPage {Kind:TaskKind.File},"空结果中查找 HTML 文件夹后显示文件入口");
                list.SelectedIndex=0;
                var details=Descendants<Expander>(folderDialog).Single();details.IsExpanded=true;folderDialog.UpdateLayout();
                check(Descendants<TextBox>(folderDialog).Any(x=>x.Text.Contains("根证书不受信任")),"未加入结果的入口提供具体原因");
                Snapshot(folderDialog,root,"discovery-html.png");folderDialog.Width=520;folderDialog.Height=480;folderDialog.UpdateLayout();Snapshot(folderDialog,root,"discovery-html-narrow.png");
                Descendants<Button>(folderDialog).Single(x=>x.Content?.ToString()=="添加选中").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            catch(Exception ex){folderError=ex;folderDialog.Close();}
        },DispatcherPriority.ApplicationIdle);
        folderDialog.ShowDialog();if(folderError!=null)throw folderError;
        check(folderDialog.SelectedPages.Count==1&&folderDialog.SelectedPages[0].Kind==TaskKind.File,"目录查找结果支持确认添加");
    }
    private static void Interact(Window dialog,Action action)
    {
        Exception? error=null;
        dialog.Dispatcher.BeginInvoke(()=>{try{action();}catch(Exception ex){error=ex;dialog.Close();}},DispatcherPriority.ApplicationIdle);
        dialog.ShowDialog(); if(error!=null)throw error;
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T:DependencyObject
    {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T match)yield return match;foreach(var nested in Descendants<T>(child))yield return nested;}
    }
    private static void Snapshot(Window window,string root,string name)
    {
        window.UpdateLayout();var content=(FrameworkElement)window.Content;int width=(int)Math.Ceiling(content.ActualWidth+content.Margin.Left+content.Margin.Right),height=(int)Math.Ceiling(content.ActualHeight+content.Margin.Top+content.Margin.Bottom);
        var visual=new DrawingVisual();
        using(var drawing=visual.RenderOpen())
        {
            drawing.DrawRectangle((Brush)Application.Current.FindResource("CanvasBrush"),null,new Rect(0,0,width,height));
            var brush=new VisualBrush(content) {ViewboxUnits=BrushMappingMode.Absolute,Viewbox=new Rect(0,0,width,height),Stretch=Stretch.Fill};
            drawing.DrawRectangle(brush,null,new Rect(0,0,width,height));
        }
        var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(Path.Combine(root,name));encoder.Save(stream);
    }
}
